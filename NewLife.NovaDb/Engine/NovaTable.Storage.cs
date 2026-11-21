using NewLife;
using NewLife.Log;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;

namespace NewLife.NovaDb.Engine;

/// <summary>表文件角色</summary>
public enum TableFileRole
{
    /// <summary>行数据（.rlog）</summary>
    Data,

    /// <summary>主键哈希索引（.hidx）</summary>
    PrimaryHash,

    /// <summary>主键有序索引（.oidx）</summary>
    PrimaryOrder,

    /// <summary>二级索引哈希字典（{表}_{索引}.hidx）</summary>
    SecondaryIndex,

    /// <summary>二级索引全量基线（{表}_{索引}.idx）</summary>
    IndexBaseline,

    /// <summary>二级索引增量日志（{表}_{索引}.idxl）</summary>
    IndexDelta,

    /// <summary>表结构（{表}.schema）</summary>
    Schema,

    /// <summary>预写日志（.wal）</summary>
    Wal,
}

/// <summary>表文件状态快照</summary>
public sealed class TableFileState
{
    /// <summary>表名</summary>
    public String TableName { get; set; } = String.Empty;

    /// <summary>文件名</summary>
    public String FileName { get; set; } = String.Empty;

    /// <summary>文件完整路径</summary>
    public String FilePath { get; set; } = String.Empty;

    /// <summary>文件角色</summary>
    public TableFileRole Role { get; set; }

    /// <summary>文件当前是否存在</summary>
    public Boolean Exists { get; set; }

    /// <summary>文件大小（字节），文件不存在时为 0</summary>
    public Int64 Size { get; set; }

    /// <summary>是否处于降级状态（文件缺失，引擎已按缺失语义继续运行）</summary>
    public Boolean Degraded { get; set; }

    /// <summary>文件角色名称（小写下划线形式，供系统表输出）</summary>
    public String RoleName => Role switch
    {
        TableFileRole.Data => "data",
        TableFileRole.PrimaryHash => "primary_hash",
        TableFileRole.PrimaryOrder => "primary_order",
        TableFileRole.SecondaryIndex => "secondary_index",
        TableFileRole.IndexBaseline => "index_baseline",
        TableFileRole.IndexDelta => "index_delta",
        TableFileRole.Schema => "schema",
        TableFileRole.Wal => "wal",
        _ => "unknown",
    };
}

/// <summary>NovaTable 存储文件巡检与降级</summary>
/// <remarks>
/// 运维可以在运行期直接删除（Linux）或改名移走（Windows）某张表的单个文件，引擎按文件角色分级降级：
/// <list type="bullet">
/// <item>行数据 .rlog 缺失：视为空表，重建空日志并重置主键与二级索引，继续服务。</item>
/// <item>主键索引 .hidx/.oidx 缺失：立即按行日志整表重建，重建期间阻塞该表访问。</item>
/// <item>二级索引 .hidx 缺失：注销该索引，查询回退全表扫描；重启后自动重建。</item>
/// <item>WAL 缺失：重建 WAL 文件；已提交数据在行日志中不受影响。</item>
/// </list>
/// 文件句柄统一带 <see cref="FileShare.Delete"/>，Windows 上即使存在映射视图也能把文件改名移走。
/// 只读模式不做任何降级动作，避免修改磁盘。
/// </remarks>
public partial class NovaTable
{
    private Int64 _lastStorageCheck;
    private Int64 _rowLogObservedLength;
    private Int64 _externalFileLossCount;
    private readonly HashSet<String> _degradedIndexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>外部移除文件的累计次数</summary>
    public Int64 ExternalFileLossCount => Interlocked.Read(ref _externalFileLossCount);

    /// <summary>已降级的二级索引名称</summary>
    public IReadOnlyCollection<String> DegradedIndexes
    {
        get
        {
            lock (_lock)
            {
                return _degradedIndexes.ToArray();
            }
        }
    }

    /// <summary>检查受管文件是否仍存在，必要时降级。返回是否发生了降级</summary>
    /// <param name="force">是否忽略巡检间隔立即检查</param>
    public Boolean RefreshStorage(Boolean force = false)
    {
        if (_options.ReadOnly || _disposed) return false;

        lock (_lock)
        {
            if (_disposed) return false;

            var interval = _options.FileWatchIntervalMs;
            if (!force && interval > 0 && Runtime.TickCount64 - _lastStorageCheck < interval) return false;

            _lastStorageCheck = Runtime.TickCount64;

            // 数据文件是唯一事实源：它没了，索引即使存在也没有意义
            if (IsRowLogUnavailable())
            {
                ResetRowLogAfterRemoval();
                return true;
            }

            var changed = false;

            if (!_primaryIndexBroken && (IsFileMissing(_primaryIndex.HashPath) || IsFileMissing(_primaryIndex.OrderedPath)))
            {
                RepairPrimaryIndex();
                changed = true;
            }

            if (_secondaryIndexes.Count > 0)
            {
                foreach (var name in _secondaryIndexes.Keys.ToArray())
                {
                    if (IsFileMissing(_fileManager.GetSecondaryHashIndexFilePath(name)))
                    {
                        DegradeSecondaryIndex(name);
                        changed = true;
                    }
                }
            }

            if (_walWriter != null && IsFileMissing(_fileManager.GetWalFilePath()))
            {
                ReopenWal();
                changed = true;
            }

            return changed;
        }
    }

    /// <summary>获取所有表文件的当前状态</summary>
    public IList<TableFileState> GetFileStates()
    {
        lock (_lock)
        {
            var list = new List<TableFileState>();

            void Add(String path, TableFileRole role, Boolean degraded = false, Boolean optional = false)
            {
                var exists = File.Exists(path);
                list.Add(new TableFileState
                {
                    TableName = _schema.TableName,
                    FileName = Path.GetFileName(path),
                    FilePath = path,
                    Role = role,
                    Exists = exists,
                    Size = exists ? new FileInfo(path).Length : 0,
                    Degraded = degraded || (!exists && !optional),
                });
            }

            Add(GetRowLogPath(), TableFileRole.Data);
            Add(_primaryIndex.HashPath, TableFileRole.PrimaryHash);
            Add(_primaryIndex.OrderedPath, TableFileRole.PrimaryOrder);

            foreach (var indexDef in _schema.Indexes)
            {
                var name = indexDef.IndexName;
                Add(_fileManager.GetSecondaryHashIndexFilePath(name), TableFileRole.SecondaryIndex, _degradedIndexes.Contains(name));

                // 基线与增量是可选文件：尚未压缩过或已合并时本就不存在
                Add(_fileManager.GetSecondaryIndexFilePath(name), TableFileRole.IndexBaseline, optional: true);
                Add(_fileManager.GetSecondaryIndexDeltaFilePath(name), TableFileRole.IndexDelta, optional: true);
            }

            Add(SchemaPersister.GetSchemaFilePath(_dbPath, _schema.TableName), TableFileRole.Schema);

            if (_options.WalMode != WalMode.None)
                Add(_fileManager.GetWalFilePath(), TableFileRole.Wal);

            return list;
        }
    }

    #region 降级处理

    /// <summary>判断行日志是否已不可用（文件被删除/移走，或被外部截断）</summary>
    private Boolean IsRowLogUnavailable()
    {
        if (_rowLogStream == null) return false;
        if (!File.Exists(GetRowLogPath())) return true;

        // 行日志只追加，物理长度不会自行变小；变小说明被外部截断，已有偏移全部失效
        var physical = _rowLogStream.Length;
        if (physical < _rowLogObservedLength) return true;

        _rowLogObservedLength = physical;

        return false;
    }

    /// <summary>按"数据文件已缺失"重置整张表：重建空行日志，清空主键与二级索引</summary>
    private void ResetRowLogAfterRemoval()
    {
        XTrace.WriteLine("[NovaDb] Row log of table '{0}' disappeared, resetting table to empty", _schema.TableName);

        // 释放映射与句柄：有映射视图时 Windows 无法删除文件，释放后运维才能清理被移走的那份
        _rowStore?.Dispose();
        _rowStore = null;
        _rowLogStream?.Dispose();
        _rowLogStream = null;

        // 未提交写集全部失效：数据文件已不存在，事务无法再落到磁盘
        _pendingIndex.Clear();
        _txCommits.Clear();
        _txCommitRegistered.Clear();
        _txIndexDeltas.Clear();
        _txIndexDeltasRegistered.Clear();
        _hotIndexManager.Clear();
        foreach (var ft in _fullTextIndexes.Values) ft.Clear();

        var path = GetRowLogPath();
        _rowLogStream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
        WriteRowLogHeader();
        _rowLogObservedLength = _rowLogLength;
        _rowStore = new RowStore(_rowLogStream, () => _rowLogLength);

        // 主键索引重建为空
        _primaryIndex.Dispose();
        var pkCol = _schema.GetPrimaryKeyColumn()!;
        _primaryIndex = NovaPrimaryIndex.Recreate(
            _fileManager.GetPrimaryHashIndexFilePath(),
            _fileManager.GetPrimaryOrderedIndexFilePath(),
            _codec,
            pkCol.DataType);
        _primaryIndex.MarkApplied(_rowLogLength);
        _primaryIndexBroken = false;

        // 二级索引：删除基线、增量与哈希文件后重建为空，避免重启时用旧基线恢复出指向已消失行的条目
        foreach (var indexDef in _schema.Indexes)
        {
            var name = indexDef.IndexName;
            if (_secondaryIndexes.TryGetValue(name, out var old))
            {
                try { old.Dispose(); } catch { }
                _secondaryIndexes.Remove(name);
            }

            DeleteSecondaryIndexFile(name);
            ResetSecondaryIndexDelta(name);
            _secondaryIndexBaselineCounts[name] = 0;

            var idx = NovaSecondaryIndex.Recreate(
                _fileManager.GetSecondaryHashIndexFilePath(name),
                indexDef,
                _codec,
                GetSecondaryIndexKeyTypes(indexDef),
                pkCol.DataType);
            idx.Commit(_rowLogLength);
            _secondaryIndexes[name] = idx;

            _degradedIndexes.Remove(name);
        }

        Interlocked.Increment(ref _externalFileLossCount);
    }

    /// <summary>主键索引文件缺失时按行日志整表重建</summary>
    private void RepairPrimaryIndex()
    {
        XTrace.WriteLine("[NovaDb] Primary index of table '{0}' disappeared, rebuilding from row log", _schema.TableName);

        try
        {
            _primaryIndex.Dispose();

            var pkCol = _schema.GetPrimaryKeyColumn()!;
            _primaryIndex = NovaPrimaryIndex.Recreate(
                _fileManager.GetPrimaryHashIndexFilePath(),
                _fileManager.GetPrimaryOrderedIndexFilePath(),
                _codec,
                pkCol.DataType);
            _primaryIndexBroken = false;

            RebuildPrimaryIndex();
        }
        catch (Exception ex)
        {
            _primaryIndexBroken = true;
            XTrace.WriteLine("[NovaDb] Failed to rebuild primary index of table '{0}': {1}", _schema.TableName, ex.Message);
            throw;
        }

        Interlocked.Increment(ref _externalFileLossCount);
    }

    /// <summary>二级索引文件缺失时注销该索引，查询回退全表扫描</summary>
    /// <param name="indexName">索引名称</param>
    private void DegradeSecondaryIndex(String indexName)
    {
        XTrace.WriteLine("[NovaDb] Secondary index '{0}.{1}' disappeared, queries fall back to full scan and the unique constraint is not enforced until restart",
            _schema.TableName, indexName);

        if (_secondaryIndexes.TryGetValue(indexName, out var idx))
        {
            try { idx.Dispose(); } catch { }
            _secondaryIndexes.Remove(indexName);
        }

        // 基线与增量是基于当时行日志的局部快照，离开哈希字典后不再可信；删除后重启会按主键索引重建
        DeleteSecondaryIndexFile(indexName);
        ResetSecondaryIndexDelta(indexName);
        _secondaryIndexBaselineCounts.Remove(indexName);

        _degradedIndexes.Add(indexName);

        Interlocked.Increment(ref _externalFileLossCount);
    }

    /// <summary>WAL 文件缺失时重新打开</summary>
    private void ReopenWal()
    {
        XTrace.WriteLine("[NovaDb] WAL of table '{0}' disappeared, recreating it", _schema.TableName);

        try
        {
            _walWriter!.Reopen();
        }
        catch (Exception ex)
        {
            XTrace.WriteLine("[NovaDb] Failed to reopen WAL of table '{0}': {1}", _schema.TableName, ex.Message);
        }

        Interlocked.Increment(ref _externalFileLossCount);
    }

    private static Boolean IsFileMissing(String path) => !File.Exists(path);

    #endregion
}
