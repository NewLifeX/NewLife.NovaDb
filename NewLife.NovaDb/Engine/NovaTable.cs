using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using NewLife.Buffers;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using NewLife.NovaDb.Tx;
using NewLife.NovaDb.Utilities;
using NewLife.NovaDb.WAL;

namespace NewLife.NovaDb.Engine;

/// <summary>Nova 表实例，支持 MVCC 的单表引擎</summary>
public partial class NovaTable : IDisposable
{
    private readonly TableSchema _schema;
    private readonly String _dbPath;
    private readonly DbOptions _options;
    private readonly TransactionManager _txManager;
    private readonly IDataCodec _codec;
    private readonly TableFileManager _fileManager;

    // 主键索引：MMF 哈希字典（等值）+ 磁盘有序段（范围/排序），启动时打开或重建
    private NovaPrimaryIndex _primaryIndex = null!;

    // 未提交写集：主键 → 尚未提交的行版本（回滚或提交后清理）
    private readonly SkipList<ComparableObject, List<RowVersion>> _pendingIndex = new();

    // 每事务待提交的主键变更
    private readonly Dictionary<UInt64, List<PrimaryCommit>> _txCommits = [];

    // 已注册提交动作的事务，避免同一事务重复注册
    private readonly HashSet<UInt64> _txCommitRegistered = [];

    // 主键索引写入失败标记：置位后拒绝后续写入，重启时整表重建
    private Boolean _primaryIndexBroken;
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif
    private Boolean _disposed;

    // 二级索引：索引名 → 磁盘哈希字典 + 惰性内存有序表
    private readonly Dictionary<String, NovaSecondaryIndex> _secondaryIndexes = new(StringComparer.OrdinalIgnoreCase);

    // 二级索引键所在列序号，避免每行写入重复解析列名
    private readonly Dictionary<String, Int32[]> _columnOrdinalCache = new(StringComparer.OrdinalIgnoreCase);

    // 二级索引增量按事务聚合：提交时一次性追加 .idxl 并推进哈希偏移，避免逐行刷盘
    private readonly Dictionary<UInt64, List<SecondaryIndexDeltaEntry>> _txIndexDeltas = [];
    private readonly HashSet<UInt64> _txIndexDeltasRegistered = [];

    // 全文索引：索引名 → FullTextIndex（P2，内存驻留）
    private readonly Dictionary<String, FullTextIndex> _fullTextIndexes = new(StringComparer.OrdinalIgnoreCase);

    // 冷热分离索引管理器
    private readonly HotIndexManager _hotIndexManager;

    // 分片管理器
    private readonly ShardManager _shardManager;

    // 默认分片 ID
    private const Int32 DefaultShardId = 0;

    // 取消检查间隔：扫描大表时每 N 行检查一次取消令牌，兼顾响应速度与检查开销
    private const Int32 CancellationCheckInterval = 1024;
    private const Int32 CancellationCheckMask = CancellationCheckInterval - 1;

    // 二级索引文件格式版本。2 起索引键中的字符串统一大写，与 SQL 大小写不敏感比较语义一致
    private const Byte SecondaryIndexFileVersion = 2;

    // 联合索引前缀扫描的分块大小：每次最多取这么多索引条目，避免一次性复制整个前缀区间
    private const Int32 SecondaryIndexPrefixScanChunkSize = 512;

    /// <summary>表架构</summary>
    public TableSchema Schema => _schema;

    /// <summary>数据库目录路径</summary>
    public String DbPath => _dbPath;

    /// <summary>表文件管理器</summary>
    public TableFileManager FileManager => _fileManager;

    /// <summary>热索引管理器</summary>
    public HotIndexManager HotIndex => _hotIndexManager;

    /// <summary>分片管理器</summary>
    public ShardManager Shards => _shardManager;

    /// <summary>全文索引集合</summary>
    public IReadOnlyDictionary<String, FullTextIndex> FullTextIndexes => _fullTextIndexes;

    /// <summary>数据版本号。每次写操作（Insert/Update/Delete）提交时递增，供物化视图增量捕获</summary>
    public Int64 DataVersion { get; private set; }

    /// <summary>主键索引条目数（含未提交与历史版本的行，仅用于执行计划的粗略估算）</summary>
    public Int32 RowCount => (Int32)(_primaryIndex.KeyCount + _pendingIndex.Count);

    /// <summary>数据变更事件。写操作提交后触发，参数为本次变更的主键</summary>
    public event Action<Object>? OnDataChanged;

    /// <summary>创建 NovaTable 实例</summary>
    /// <param name="schema">表架构</param>
    /// <param name="dbPath">数据库目录路径</param>
    /// <param name="options">数据库选项</param>
    /// <param name="txManager">事务管理器</param>
    public NovaTable(TableSchema schema, String dbPath, DbOptions options, TransactionManager txManager)
    {
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _txManager = txManager ?? throw new ArgumentNullException(nameof(txManager));
        _codec = new DefaultDataCodec();

        if (!_schema.PrimaryKeyIndex.HasValue)
            throw new NovaException(ErrorCode.InvalidArgument, "Table must have a primary key");

        // 确保数据库目录存在
        if (!Directory.Exists(_dbPath))
            Directory.CreateDirectory(_dbPath);

        // 使用 TableFileManager 生成文件路径（表文件平铺在数据库目录下）
        _fileManager = new TableFileManager(_dbPath, schema.TableName, _options);

        // 初始化冷热分离索引管理器
        _hotIndexManager = new HotIndexManager(new HotSegmentConfig());

        // 初始化分片管理器
        _shardManager = new ShardManager(_options, _dbPath);
        _shardManager.AddShard(new ShardInfo
        {
            ShardId = DefaultShardId,
            DataFilePath = _fileManager.GetRowLogFilePath(),
            CreatedAt = DateTime.UtcNow
        });

        // 打开行日志并恢复数据
        OpenRowLog();

        // 打开主键索引（缺失或损坏时按行日志重建）
        OpenPrimaryIndex();

        // 恢复二级索引（优先 .idx 文件，缺失或损坏时全表重建）
        RestoreSecondaryIndexes();
    }

    /// <summary>插入行</summary>
    /// <param name="tx">事务</param>
    /// <param name="row">行数据（按列序号排列的值数组）</param>
    public void Insert(Transaction tx, Object?[] row)
    {
        if (tx == null)
            throw new ArgumentNullException(nameof(tx));
        if (row == null)
            throw new ArgumentNullException(nameof(row));
        if (row.Length != _schema.Columns.Count)
            throw new NovaException(ErrorCode.InvalidArgument, "Row has {0} columns, expected {1}", row.Length, _schema.Columns.Count);

        lock (_lock)
        {
            RefreshStorage();

            // 获取主键值
            var pkColumn = _schema.GetPrimaryKeyColumn()!;
            var pkValue = row[pkColumn.Ordinal];

            if (pkValue == null)
                throw new NovaException(ErrorCode.InvalidArgument, "Primary key cannot be null");

            // 序列化行数据
            var payload = SerializeRow(row);

            // 创建行版本
            var rowVersion = new RowVersion(tx.TxId, pkValue, payload);

            // 包装主键为可比较对象
            var comparableKey = new ComparableObject(pkValue);

            // 检查主键冲突（含未提交写集）
            if (TryGetVisibleVersion(tx, pkValue, out _))
                throw new NovaException(ErrorCode.PrimaryKeyConflict, "Primary key '{0}' already exists", pkValue);

            // 唯一性预检查：在任何修改之前检测二级唯一索引冲突，保证异常路径零副作用（INSERT IGNORE 依赖此语义）
            if (_secondaryIndexes.Count > 0)
                CheckUniqueIndexConflicts(row, comparableKey);

            // 添加未提交版本
            AddPendingVersion(comparableKey, rowVersion);
            AddCommit(tx, new PrimaryCommit(pkValue, rowVersion, null, tx.TxId));

            // 维护二级索引
            if (_secondaryIndexes.Count > 0)
                InsertIntoSecondaryIndexes(row, comparableKey, GetIndexDeltas(tx));

            // 维护全文索引（内存驻留，P2）：提交时才更新，回滚时保持一致
            if (_fullTextIndexes.Count > 0)
            {
                var ftDocId = pkValue;
                tx.RegisterCommitAction(() => MaintainFullTextIndexes(ftDocId, row));
            }

            // 更新分片统计
            _shardManager.RecordWrite(DefaultShardId, payload.Length);

            // 注册提交动作：事务提交时写行日志并更新主键索引
            RegisterPrimaryCommit(tx);
            RegisterIndexDeltaCommit(tx);

            // 注册提交动作：递增数据版本号并触发变更事件（物化视图增量捕获）
            var changedPk = pkValue;
            tx.RegisterCommitAction(() =>
            {
                DataVersion++;
                OnDataChanged?.Invoke(changedPk);
            });

            // 注册回滚动作
            tx.RegisterRollbackAction(() =>
            {
                lock (_lock)
                {
                    RemovePendingVersion(comparableKey, rowVersion);

                    // 回滚时从二级索引移除
                    if (_secondaryIndexes.Count > 0)
                        RemoveFromSecondaryIndexes(row, comparableKey);
                }
            });
        }
    }

    /// <summary>根据主键查询行</summary>
    /// <param name="tx">事务</param>
    /// <param name="key">主键值</param>
    /// <returns>行数据（如果存在），否则返回 null</returns>
    public Object?[]? Get(Transaction tx, Object key) => Get(tx, key, null);

    /// <summary>根据主键查询行</summary>
    /// <param name="tx">事务</param>
    /// <param name="key">主键值</param>
    /// <param name="neededColumns">需要反序列化的列掩码，null 表示全部列</param>
    /// <returns>行数据（如果存在），否则返回 null</returns>
    public Object?[]? Get(Transaction tx, Object key, Boolean[]? neededColumns)
    {
        if (tx == null)
            throw new ArgumentNullException(nameof(tx));
        if (key == null)
            throw new ArgumentNullException(nameof(key));

        lock (_lock)
        {
            RefreshStorage();

            if (!TryGetVisibleVersion(tx, key, out var visibleVersion))
                return null;

            // 记录热度访问
            _hotIndexManager.AccessKey(key);

            // 反序列化行数据
            return DeserializeVersion(visibleVersion, neededColumns);
        }
    }

    /// <summary>根据主键更新行</summary>
    /// <param name="tx">事务</param>
    /// <param name="key">主键值</param>
    /// <param name="newRow">新行数据</param>
    /// <returns>是否更新成功</returns>
    public Boolean Update(Transaction tx, Object key, Object?[] newRow)
    {
        if (tx == null)
            throw new ArgumentNullException(nameof(tx));
        if (key == null)
            throw new ArgumentNullException(nameof(key));
        if (newRow == null)
            throw new ArgumentNullException(nameof(newRow));
        if (newRow.Length != _schema.Columns.Count)
            throw new NovaException(ErrorCode.InvalidArgument, "Row has {0} columns, expected {1}", newRow.Length, _schema.Columns.Count);

        lock (_lock)
        {
            RefreshStorage();

            var comparableKey = new ComparableObject(key);

            if (!TryGetVisibleVersion(tx, key, out var visibleVersion))
                return false;

            // 唯一性预检查：在任何修改之前检测二级唯一索引冲突，保证异常路径零副作用
            if (_secondaryIndexes.Count > 0)
                CheckUniqueIndexConflicts(newRow, comparableKey);

            // 更新二级索引：先移除旧值，后面再插入新值
            Object?[]? oldRow = null;
            if (_secondaryIndexes.Count > 0 || _fullTextIndexes.Count > 0)
            {
                oldRow = DeserializeVersion(visibleVersion);
                if (_secondaryIndexes.Count > 0)
                    RemoveFromSecondaryIndexes(oldRow, comparableKey, GetIndexDeltas(tx), keepRow: newRow);
            }

            // 创建新版本
            var payload = SerializeRow(newRow);
            var newVersion = new RowVersion(tx.TxId, key, payload);

            // 旧版本处理：已提交版本在提交时打删除标记；未提交版本撤掉同事务的插入项
            PrimaryCommit commit;
            var oldDeletedByTx = visibleVersion.DeletedByTx;
            if (visibleVersion.IndexValueOffset > 0)
            {
                commit = new PrimaryCommit(key, newVersion, visibleVersion, tx.TxId);
            }
            else
            {
                // 同一事务内先插入再更新：撤掉上一版插入，避免一次提交写出两条 Put
                RemovePendingPut(tx.TxId, visibleVersion);
                visibleVersion.MarkDeleted(tx.TxId);
                commit = new PrimaryCommit(key, newVersion, null, tx.TxId);

                var rollbackVersion = visibleVersion;
                tx.RegisterRollbackAction(() => rollbackVersion.DeletedByTx = oldDeletedByTx);
            }

            AddPendingVersion(comparableKey, newVersion);
            AddCommit(tx, commit);

            // 维护二级索引：插入新值
            if (_secondaryIndexes.Count > 0)
                InsertIntoSecondaryIndexes(newRow, comparableKey, GetIndexDeltas(tx), skipRow: oldRow);

            // 维护全文索引（内存驻留，P2）：提交时先移除旧文档再插入新文档
            if (_fullTextIndexes.Count > 0)
            {
                var ftKey = key;
                var ftOldRow = oldRow;
                var ftNewRow = newRow;
                tx.RegisterCommitAction(() =>
                {
                    if (ftOldRow != null) MaintainFullTextIndexes(ftKey, ftOldRow, true);
                    MaintainFullTextIndexes(ftKey, ftNewRow);
                });
            }

            // 更新分片统计
            _shardManager.RecordWrite(DefaultShardId, payload.Length);

            // 注册提交动作：事务提交时写行日志并更新主键索引
            RegisterPrimaryCommit(tx);
            RegisterIndexDeltaCommit(tx);

            // 注册提交动作：递增数据版本号并触发变更事件（物化视图增量捕获）
            var changedPk = key;
            tx.RegisterCommitAction(() =>
            {
                DataVersion++;
                OnDataChanged?.Invoke(changedPk);
            });

            // 注册回滚动作
            var rollbackOldRow = oldRow;
            tx.RegisterRollbackAction(() =>
            {
                lock (_lock)
                {
                    visibleVersion.DeletedByTx = oldDeletedByTx;
                    RemovePendingVersion(comparableKey, newVersion);

                    // 回滚二级索引
                    if (_secondaryIndexes.Count > 0)
                    {
                        RemoveFromSecondaryIndexes(newRow, comparableKey);
                        if (rollbackOldRow != null)
                            InsertIntoSecondaryIndexes(rollbackOldRow, comparableKey);
                    }
                }
            });

            return true;
        }
    }

    /// <summary>根据主键删除行</summary>
    /// <param name="tx">事务</param>
    /// <param name="key">主键值</param>
    /// <returns>是否删除成功</returns>
    public Boolean Delete(Transaction tx, Object key)
    {
        if (tx == null)
            throw new ArgumentNullException(nameof(tx));
        if (key == null)
            throw new ArgumentNullException(nameof(key));

        lock (_lock)
        {
            RefreshStorage();

            var comparableKey = new ComparableObject(key);

            if (!TryGetVisibleVersion(tx, key, out var visibleVersion))
                return false;

            // 从二级索引移除
            Object?[]? deletedRow = null;
            if (_secondaryIndexes.Count > 0)
            {
                deletedRow = DeserializeVersion(visibleVersion);
                RemoveFromSecondaryIndexes(deletedRow, comparableKey, GetIndexDeltas(tx));
            }

            // 旧版本处理：已提交版本在提交时打删除标记；未提交版本直接从写集移除
            var oldDeletedByTx = visibleVersion.DeletedByTx;
            if (visibleVersion.IndexValueOffset > 0)
            {
                var tombstone = new RowVersion(tx.TxId, key, null) { DeletedByTx = tx.TxId };
                AddPendingVersion(comparableKey, tombstone);
                AddCommit(tx, new PrimaryCommit(key, null, visibleVersion, tx.TxId));
                RegisterPrimaryCommit(tx);

                var rollbackTombstone = tombstone;
                tx.RegisterRollbackAction(() =>
                {
                    lock (_lock) RemovePendingVersion(comparableKey, rollbackTombstone);
                });
            }
            else
            {
                // 同事务内先插入再删除：撤掉插入项，不写任何记录
                RemovePendingPut(tx.TxId, visibleVersion);
                RemovePendingVersion(comparableKey, visibleVersion);
                visibleVersion.MarkDeleted(tx.TxId);
            }

            // 二级索引增量的提交动作排在主键提交之后，保证先写行日志再推进索引偏移
            RegisterIndexDeltaCommit(tx);

            // 维护全文索引（内存驻留，P2）：提交时移除文档
            if (_fullTextIndexes.Count > 0)
            {
                var ftKey = key;
                tx.RegisterCommitAction(() =>
                {
                    foreach (var idx in _fullTextIndexes.Values)
                    {
                        idx.Remove(ftKey);
                    }
                });
            }

            // 注册提交动作：递增数据版本号并触发变更事件（物化视图增量捕获）
            var changedPk = key;
            tx.RegisterCommitAction(() =>
            {
                DataVersion++;
                OnDataChanged?.Invoke(changedPk);
            });

            // 注册回滚动作
            var rollbackDeletedRow = deletedRow;
            tx.RegisterRollbackAction(() =>
            {
                lock (_lock)
                {
                    visibleVersion.DeletedByTx = oldDeletedByTx;
                    // 回滚时重新插入二级索引
                    if (_secondaryIndexes.Count > 0 && rollbackDeletedRow != null)
                        InsertIntoSecondaryIndexes(rollbackDeletedRow, comparableKey);
                }
            });

            return true;
        }
    }

    /// <summary>获取所有可见行</summary>
    /// <param name="tx">事务</param>
    /// <returns>行数据列表</returns>
    public List<Object?[]> GetAll(Transaction tx) => GetAll(tx, CancellationToken.None);

    /// <summary>获取所有可见行，支持协作式取消</summary>
    /// <param name="tx">事务</param>
    /// <param name="cancellationToken">取消令牌，每 <see cref="CancellationCheckInterval"/> 行检查一次</param>
    /// <returns>行数据列表</returns>
    public List<Object?[]> GetAll(Transaction tx, CancellationToken cancellationToken) => GetAll(tx, cancellationToken, null);

    /// <summary>获取所有可见行，支持协作式取消与列裁剪</summary>
    /// <param name="tx">事务</param>
    /// <param name="cancellationToken">取消令牌，每 <see cref="CancellationCheckInterval"/> 行检查一次</param>
    /// <param name="neededColumns">需要反序列化的列掩码，null 表示全部列</param>
    /// <returns>行数据列表</returns>
    public List<Object?[]> GetAll(Transaction tx, CancellationToken cancellationToken, Boolean[]? neededColumns)
    {
        if (tx == null)
            throw new ArgumentNullException(nameof(tx));

        lock (_lock)
        {
            RefreshStorage();

            return ScanRows(tx, null, true, null, true, false, 0, cancellationToken, neededColumns);
        }
    }

    /// <summary>按主键范围获取可见行，只反序列化范围内的行</summary>
    /// <param name="tx">事务</param>
    /// <param name="from">下界主键值，null 表示不限</param>
    /// <param name="includeFrom">是否包含下界</param>
    /// <param name="to">上界主键值，null 表示不限</param>
    /// <param name="includeTo">是否包含上界</param>
    /// <param name="maxRows">最大返回行数，0 表示不限制</param>
    /// <returns>按主键升序排列的行数据列表</returns>
    public List<Object?[]> GetRange(Transaction tx, Object? from, Boolean includeFrom, Object? to, Boolean includeTo, Int32 maxRows = 0)
        => GetRange(tx, from, includeFrom, to, includeTo, maxRows, CancellationToken.None);

    /// <summary>按主键范围获取可见行，只反序列化范围内的行，支持协作式取消</summary>
    /// <param name="tx">事务</param>
    /// <param name="from">下界主键值，null 表示不限</param>
    /// <param name="includeFrom">是否包含下界</param>
    /// <param name="to">上界主键值，null 表示不限</param>
    /// <param name="includeTo">是否包含上界</param>
    /// <param name="maxRows">最大返回行数，0 表示不限制</param>
    /// <param name="cancellationToken">取消令牌，每 <see cref="CancellationCheckInterval"/> 行检查一次</param>
    /// <param name="descending">是否按主键降序返回；边界语义不变，只是扫描方向相反</param>
    /// <param name="neededColumns">需要反序列化的列掩码，null 表示全部列</param>
    /// <returns>按主键升序（或 <paramref name="descending"/> 为 true 时降序）排列的行数据列表</returns>
    public List<Object?[]> GetRange(Transaction tx, Object? from, Boolean includeFrom, Object? to, Boolean includeTo, Int32 maxRows, CancellationToken cancellationToken, Boolean descending = false, Boolean[]? neededColumns = null)
    {
        if (tx == null)
            throw new ArgumentNullException(nameof(tx));

        lock (_lock)
        {
            RefreshStorage();

            return ScanRows(tx, from, includeFrom, to, includeTo, descending, maxRows, cancellationToken, neededColumns);
        }
    }

    /// <summary>扫描可见行并反序列化。无未提交写集时直接按载荷位置读取，省掉中间版本列表</summary>
    private List<Object?[]> ScanRows(Transaction tx, Object? from, Boolean includeFrom, Object? to, Boolean includeTo, Boolean descending, Int32 maxRows, CancellationToken cancellationToken, Boolean[]? neededColumns)
    {
        var result = new List<Object?[]>();
        var count = 0;

        if (_pendingIndex.Count == 0)
        {
            _primaryIndex.ScanOffsets(from, includeFrom, to, includeTo, descending, maxRows, (offset, length) =>
            {
                if ((++count & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();

                result.Add(DeserializeAt(offset, length, neededColumns));

                return maxRows <= 0 || result.Count < maxRows;
            });

            return result;
        }

        foreach (var (_, version) in CollectVisibleVersions(tx, from, includeFrom, to, includeTo, descending))
        {
            if ((++count & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();

            result.Add(DeserializeVersion(version, neededColumns));

            if (maxRows > 0 && result.Count >= maxRows) break;
        }

        return result;
    }

    /// <summary>尝试快速统计可见行数：无未提交写集且无并发写事务时直接数主键索引的存活条目</summary>
    /// <param name="tx">事务</param>
    /// <param name="count">可见行数</param>
    /// <returns>可以精确统计时返回 true；存在未提交写入或并发事务时返回 false</returns>
    public Boolean TryCountRows(Transaction tx, out Int64 count)
    {
        if (tx == null) throw new ArgumentNullException(nameof(tx));

        lock (_lock)
        {
            RefreshStorage();

            count = 0;
            if (_pendingIndex.Count > 0) return false;
            if (_txManager.HasOtherActiveTransactions(tx.TxId)) return false;

            // 主键哈希字典维护了存活值计数：插入 +1、更新净 0、删除 -1，O(1) 取数
            count = _primaryIndex.LiveCount;
            return true;
        }
    }

    #region 主键索引维护

    /// <summary>查找对指定事务可见的行版本（先写集、后磁盘索引）</summary>
    /// <param name="tx">事务</param>
    /// <param name="key">主键值</param>
    /// <param name="version">命中的版本</param>
    /// <returns>是否存在可见版本</returns>
    private Boolean TryGetVisibleVersion(Transaction tx, Object key, out RowVersion version)
    {
        // 无未提交写集时直接查磁盘索引，省掉包装对象与跳表查找
        if (_pendingIndex.Count > 0)
        {
            var comparableKey = new ComparableObject(key);
            if (_pendingIndex.TryGetValue(comparableKey, out var versions) && versions != null)
            {
                var deletedBySelf = false;
                for (var i = versions.Count - 1; i >= 0; i--)
                {
                    var item = versions[i];
                    if (item.IsVisible(_txManager, tx.TxId))
                    {
                        version = item;
                        return true;
                    }

                    if (item.CreatedByTx == tx.TxId && item.DeletedByTx == tx.TxId) deletedBySelf = true;
                }

                // 本事务已删除，不能回落到磁盘索引里的旧版本
                if (deletedBySelf)
                {
                    version = null!;
                    return false;
                }
            }
        }

        return _primaryIndex.TryGetCommitted(key, out version);
    }

    /// <summary>把版本放入未提交写集</summary>
    private void AddPendingVersion(ComparableObject key, RowVersion version)
    {
        if (!_pendingIndex.TryGetValue(key, out var versions))
        {
            versions = [];
            _pendingIndex.Insert(key, versions);
        }

        versions!.Add(version);
    }

    /// <summary>从写集移除版本，键下无版本时摘除键</summary>
    private void RemovePendingVersion(ComparableObject key, RowVersion version)
    {
        if (!_pendingIndex.TryGetValue(key, out var versions) || versions == null) return;

        versions.Remove(version);
        if (versions.Count == 0) _pendingIndex.Remove(key);
    }

    /// <summary>登记一次待提交的主键变更</summary>
    private void AddCommit(Transaction tx, PrimaryCommit commit)
    {
        if (!_txCommits.TryGetValue(tx.TxId, out var commits))
        {
            commits = [];
            _txCommits[tx.TxId] = commits;
        }

        commits.Add(commit);
    }

    /// <summary>撤掉同事务中某个版本的插入项（先插入再更新/删除时使用）</summary>
    private void RemovePendingPut(UInt64 txId, RowVersion version)
    {
        if (!_txCommits.TryGetValue(txId, out var commits)) return;

        for (var i = commits.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(commits[i].Put, version))
            {
                commits.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>注册主键提交动作：同一事务只注册一次</summary>
    private void RegisterPrimaryCommit(Transaction tx)
    {
        if (!_txCommitRegistered.Add(tx.TxId)) return;

        tx.RegisterCommitAction(() => CommitPrimaryWrites(tx.TxId));
        tx.RegisterRollbackAction(() => DiscardPrimaryWrites(tx.TxId));

        // 顺序：先刷行日志，再刷二级索引增量，最后刷主键索引，保证索引的已应用偏移永远不超前
        // 提交时只做一次 flush：逐行写入时每次提交一行会让千行导入慢上百倍
        // Full 模式强制行日志 fsync，保证已提交数据在断电后不丢
        tx.RegisterPostCommitAction(() =>
        {
            _rowLogStream?.Flush(_options.WalMode == WalMode.Full);
            FlushSecondaryIndexDeltas();
        });
        // 索引是派生数据：提交时只推进内存偏移，按间隔成批刷盘；崩溃时按行日志重建
        tx.RegisterPostCommitAction(() => FlushIndexesIfDue());
    }

    /// <summary>提交事务的主键变更：写行日志 → 更新索引 → 清理写集</summary>
    /// <param name="txId">事务 ID</param>
    private void CommitPrimaryWrites(UInt64 txId)
    {
        lock (_lock)
        {
            _txCommitRegistered.Remove(txId);

            RefreshStorage();

            if (_primaryIndexBroken)
            {
                // 索引写入曾失败：先尝试按行日志修复，修复失败才放弃本次提交
                RepairPrimaryIndex();
                if (_primaryIndexBroken) return;
            }
            if (!_txCommits.TryGetValue(txId, out var commits)) return;

            _txCommits.Remove(txId);

            try
            {
                var pkCol = _schema.GetPrimaryKeyColumn()!;

                // 1) 写行日志：Put 记录写完整载荷并回填偏移
                foreach (var commit in commits)
                {
                    if (commit.Put != null)
                    {
                        var payload = commit.Put.Payload ?? [];
                        var offset = WriteRecord(RecordType_Put, payload);
                        commit.Put.DataOffset = offset;
                        commit.Put.DataLength = payload.Length;
                        commit.Put.Payload = null;
                    }
                    else
                    {
                        WriteRecord(RecordType_Delete, _codec.Encode(commit.Key, pkCol.DataType));
                    }
                }

                // 2) 应用主键索引，已应用偏移覆盖本批写入
                _primaryIndex.Apply(commits, _rowLogLength);
                _indexDirty = true;

                // 3) 清理写集中本事务的版本
                foreach (var commit in commits)
                    ClearPendingForKey(commit.Key, txId);
            }
            catch
            {
                // 索引与行日志可能不一致，置脏后拒绝后续写入，重启时整表重建
                _primaryIndexBroken = true;
                throw;
            }
        }
    }

    /// <summary>回滚时丢弃事务的待提交变更</summary>
    /// <param name="txId">事务 ID</param>
    private void DiscardPrimaryWrites(UInt64 txId)
    {
        lock (_lock)
        {
            _txCommitRegistered.Remove(txId);
            _txCommits.Remove(txId);
        }
    }

    /// <summary>清理某个键上属于指定事务的写集版本</summary>
    private void ClearPendingForKey(Object key, UInt64 txId)
    {
        var comparableKey = new ComparableObject(key);
        if (!_pendingIndex.TryGetValue(comparableKey, out var versions) || versions == null) return;

        for (var i = versions.Count - 1; i >= 0; i--)
        {
            var version = versions[i];
            if (version.CreatedByTx == txId || version.DeletedByTx == txId) versions.RemoveAt(i);
        }

        if (versions.Count == 0) _pendingIndex.Remove(comparableKey);
    }

    /// <summary>收集可见版本：无未提交写入时直接顺序扫描磁盘索引</summary>
    private List<(Object Key, RowVersion Version)> CollectVisibleVersions(Transaction tx, Object? from, Boolean includeFrom, Object? to, Boolean includeTo, Boolean descending)
    {
        var result = new List<(Object, RowVersion)>();

        if (_pendingIndex.Count == 0)
        {
            _primaryIndex.Scan(from, includeFrom, to, includeTo, descending, 0, (key, version) =>
            {
                result.Add((key, version));
                return true;
            });

            return result;
        }

        // 有未提交写集：本事务的新版本覆盖磁盘版本，本事务的删除隐藏磁盘版本
        var pending = new Dictionary<ComparableObject, RowVersion?>();
        foreach (var entry in _pendingIndex.GetAll())
        {
            RowVersion? visible = null;
            var deletedBySelf = false;
            var versions = entry.Value;
            for (var i = versions.Count - 1; i >= 0; i--)
            {
                var item = versions[i];
                if (item.IsVisible(_txManager, tx.TxId))
                {
                    visible = item;
                    break;
                }

                if (item.CreatedByTx == tx.TxId && item.DeletedByTx == tx.TxId) deletedBySelf = true;
            }

            if (visible != null)
                pending[entry.Key] = visible;
            else if (deletedBySelf)
                pending[entry.Key] = null;
        }

        var seen = new HashSet<ComparableObject>();
        _primaryIndex.Scan(from, includeFrom, to, includeTo, descending, 0, (key, version) =>
        {
            var comparableKey = new ComparableObject(key);
            seen.Add(comparableKey);

            if (pending.TryGetValue(comparableKey, out var pendingVersion))
            {
                if (pendingVersion != null) result.Add((key, pendingVersion));
            }
            else
            {
                result.Add((key, version));
            }

            return true;
        });

        // 本事务新增、磁盘索引中尚不存在的键
        foreach (var kv in pending)
        {
            if (kv.Value == null || seen.Contains(kv.Key)) continue;
            if (!InPrimaryRange(kv.Key.Value, from, includeFrom, to, includeTo)) continue;

            result.Add((kv.Key.Value, kv.Value));
        }

        result.Sort((a, b) =>
        {
            var cmp = ComparableObject.CompareValues(a.Item1, b.Item1);
            return descending ? -cmp : cmp;
        });

        return result;
    }

    /// <summary>判断主键是否在范围内</summary>
    private static Boolean InPrimaryRange(Object key, Object? from, Boolean includeFrom, Object? to, Boolean includeTo)
    {
        if (from != null)
        {
            var cmp = ComparableObject.CompareValues(key, from);
            if (cmp < 0 || (cmp == 0 && !includeFrom)) return false;
        }

        if (to != null)
        {
            var cmp = ComparableObject.CompareValues(key, to);
            if (cmp > 0 || (cmp == 0 && !includeTo)) return false;
        }

        return true;
    }

    #endregion

    /// <summary>主键列类型变化后按新类型重建主键索引</summary>
    /// <remarks>索引键按列声明类型编码，类型变了旧键字节就不能再用于查找</remarks>
    internal void ReopenPrimaryIndexForSchemaChange()
    {
        var pkCol = _schema.GetPrimaryKeyColumn();
        if (pkCol == null) return;
        if (_primaryIndex.PrimaryKeyType == pkCol.DataType) return;

        lock (_lock)
        {
            if (_primaryIndex.PrimaryKeyType == pkCol.DataType) return;

            _primaryIndex.Dispose();
            _primaryIndex = NovaPrimaryIndex.Recreate(
                _fileManager.GetPrimaryHashIndexFilePath(),
                _fileManager.GetPrimaryOrderedIndexFilePath(),
                _codec,
                pkCol.DataType);
            RebuildPrimaryIndex();
        }
    }

    /// <summary>列类型变化后按新类型重建受影响的二级索引</summary>
    /// <remarks>索引键按列声明类型编码，类型变了旧键字节就不能再用于查找</remarks>
    internal void ReopenSecondaryIndexesForSchemaChange()
    {
        lock (_lock)
        {
            // 列序号可能整体位移，先丢弃缓存
            _columnOrdinalCache.Clear();

            if (_secondaryIndexes.Count == 0) return;
            var pkCol = _schema.GetPrimaryKeyColumn();
            if (pkCol == null) return;

            var stale = new List<IndexDefinition>();
            foreach (var indexDef in _schema.Indexes)
            {
                if (!_secondaryIndexes.TryGetValue(indexDef.IndexName, out var idx)) continue;
                if (idx.MatchesTypes(GetSecondaryIndexKeyTypes(indexDef), pkCol.DataType)) continue;

                stale.Add(indexDef);
            }

            foreach (var indexDef in stale)
            {
                var name = indexDef.IndexName;
                if (_secondaryIndexes.TryGetValue(name, out var old))
                {
                    old.Dispose();
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
                _secondaryIndexes[name] = idx;

                // 旧键编码已失效，直接从主键索引重建
                RebuildSecondaryIndex(indexDef, idx);
                idx.Commit(_rowLogLength);
            }
        }
    }

    /// <summary>清空表中所有数据，保留表结构</summary>
    public void Truncate()
    {
        lock (_lock)
        {
            _primaryIndex.Clear();
            _pendingIndex.Clear();
            _txCommits.Clear();
            _txCommitRegistered.Clear();
            _hotIndexManager.Clear();
            TruncateRowLog();

            // 清空所有二级索引，并删除基线/增量文件；行日志已清空，重启后从空表重建不会残留旧条目
            var indexNames = new List<String>(_secondaryIndexes.Keys);
            foreach (var name in indexNames)
            {
                _secondaryIndexes[name].Clear();
                DeleteSecondaryIndexFile(name);
                ResetSecondaryIndexDelta(name);
                _secondaryIndexBaselineCounts[name] = 0;
            }
        }
    }

    #region 二级索引

    /// <summary>创建二级索引并为已有数据构建索引</summary>
    /// <param name="indexDef">索引定义</param>
    /// <param name="tx">事务</param>
    public void CreateSecondaryIndex(IndexDefinition indexDef, Transaction tx)
    {
        if (indexDef == null) throw new ArgumentNullException(nameof(indexDef));
        if (tx == null) throw new ArgumentNullException(nameof(tx));

        lock (_lock)
        {
            if (_secondaryIndexes.ContainsKey(indexDef.IndexName))
                throw new NovaException(ErrorCode.InvalidArgument, "Index '{0}' already exists", indexDef.IndexName);

            var idx = NovaSecondaryIndex.Recreate(
                _fileManager.GetSecondaryHashIndexFilePath(indexDef.IndexName),
                indexDef,
                _codec,
                GetSecondaryIndexKeyTypes(indexDef),
                _schema.GetPrimaryKeyColumn()!.DataType);
            _secondaryIndexes[indexDef.IndexName] = idx;

            var colOrdinals = GetColumnOrdinals(indexDef);

            var pkCol = _schema.GetPrimaryKeyColumn()!;

            // 为已有数据构建索引
            foreach (var (_, ver) in CollectVisibleVersions(tx, null, true, null, true, false))
            {
                var row = DeserializeVersion(ver);

                // 索引键列含 NULL 时跳过该行（MySQL 语义）
                if (HasNullColumn(row, colOrdinals)) continue;

                var indexKey = BuildIndexKey(row, colOrdinals);

                if (indexDef.IsUnique && idx.CountKey(indexKey.Value) > 0)
                    throw new NovaException(ErrorCode.UniqueConstraintViolation, "Duplicate key in unique index '{0}'", indexDef.IndexName);

                idx.Add(indexKey.Value, row[pkCol.Ordinal]!);
            }

            // 注册提交动作：事务提交时全量重写基线（新建索引时基线就是全量数据，顺带清空增量）
            var persistName = indexDef.IndexName;
            tx.RegisterCommitAction(() => CompactSecondaryIndex(persistName));
        }
    }

    /// <summary>删除二级索引</summary>
    /// <param name="indexName">索引名</param>
    public void DropSecondaryIndex(String indexName)
    {
        lock (_lock)
        {
            if (_secondaryIndexes.TryGetValue(indexName, out var index))
            {
                index.Dispose();
                _secondaryIndexes.Remove(indexName);
            }
        }

        // 删除索引文件与增量日志
        DeleteSecondaryIndexFile(indexName);
        ResetSecondaryIndexDelta(indexName);
        _secondaryIndexBaselineCounts.Remove(indexName);
    }

    /// <summary>判断二级索引当前是否已注册（文件缺失时会注销，查询应回退全表扫描）</summary>
    /// <param name="indexName">索引名</param>
    /// <returns>已注册返回 true</returns>
    public Boolean HasSecondaryIndex(String indexName)
    {
        if (String.IsNullOrEmpty(indexName)) return false;

        lock (_lock)
        {
            return _secondaryIndexes.ContainsKey(indexName);
        }
    }

    /// <summary>判断二级索引是否覆盖全部存活行（即索引列不含 NULL）</summary>
    /// <param name="indexName">索引名</param>
    /// <returns>索引条目数与存活行数一致时返回 true</returns>
    /// <remarks>可空列的 ORDER BY 只有在确认没有 NULL 时才能改走索引顺序，否则会漏掉 NULL 行</remarks>
    public Boolean IsIndexComplete(String indexName)
    {
        if (String.IsNullOrEmpty(indexName)) return false;

        lock (_lock)
        {
            return _secondaryIndexes.TryGetValue(indexName, out var idx) && idx.LiveCount == _primaryIndex.LiveCount;
        }
    }

    /// <summary>通过二级索引查询匹配的主键列表</summary>
    /// <param name="indexName">索引名</param>
    /// <param name="indexKeyValues">索引键值</param>
    /// <returns>匹配的主键值列表</returns>
    public List<Object>? LookupByIndex(String indexName, Object?[] indexKeyValues)
    {
        lock (_lock)
        {
            RefreshStorage();

            if (!_secondaryIndexes.TryGetValue(indexName, out var idx))
                return null;

            var values = new Object?[indexKeyValues.Length];
            for (var i = 0; i < indexKeyValues.Length; i++)
                values[i] = NormalizeIndexKeyPart(indexKeyValues[i]);

            var indexKey = values.Length == 1 ? values[0]! : (Object)values;

            return idx.TryGet(indexKey);
        }
    }

    /// <summary>按索引键前缀查询匹配的主键列表，支持联合索引最左前缀</summary>
    /// <param name="indexName">索引名</param>
    /// <param name="prefixValues">前缀列值，长度必须小于索引列数</param>
    /// <param name="lower">下一列范围下界，null 表示不限</param>
    /// <param name="hasLower">是否有下界</param>
    /// <param name="includeLower">是否包含下界</param>
    /// <param name="upper">下一列范围上界，null 表示不限</param>
    /// <param name="hasUpper">是否有上界</param>
    /// <param name="includeUpper">是否包含上界</param>
    /// <param name="maxMatches">最大返回主键数，0 表示不限制</param>
    /// <returns>匹配的主键值列表；索引不存在或不是联合索引时返回 null</returns>
    /// <remarks>索引键按数组元素有序，前缀相等时下一列连续排列，因此按分块顺序扫描到前缀变化即可停止</remarks>
    public List<Object>? LookupByIndexPrefix(String indexName, Object?[] prefixValues, Object? lower, Boolean hasLower, Boolean includeLower, Object? upper, Boolean hasUpper, Boolean includeUpper, Int32 maxMatches)
    {
        lock (_lock)
        {
            RefreshStorage();

            if (!_secondaryIndexes.TryGetValue(indexName, out var idx))
                return null;

            var indexDef = _schema.GetIndex(indexName);
            if (indexDef == null || prefixValues == null || prefixValues.Length == 0 || indexDef.Columns.Count <= prefixValues.Length) return null;

            // 索引键列含 NULL 时不进索引，带 NULL 的前缀不可能命中
            var prefix = new Object?[prefixValues.Length];
            for (var i = 0; i < prefix.Length; i++)
            {
                prefix[i] = NormalizeIndexValue(prefixValues[i]);
                if (prefix[i] == null) return [];
            }

            var lowerKey = hasLower ? NormalizeIndexValue(lower) : null;
            var upperKey = hasUpper ? NormalizeIndexValue(upper) : null;

            // 范围/前缀扫描需要有序结构：首次使用时从哈希字典重建
            var ordered = idx.EnsureOrdered();

            var result = new List<Object>();
            var fromKey = new ComparableObject(prefix);
            var includeFrom = true;

            while (true)
            {
                var entries = ordered.GetRange(fromKey, includeFrom, null, true, SecondaryIndexPrefixScanChunkSize);
                var exhausted = entries.Count < SecondaryIndexPrefixScanChunkSize;
                var stop = false;

                foreach (var entry in entries)
                {
                    // 单列索引键不是数组，与联合索引键不可比较，直接结束
                    if (entry.Key.Value is not Object?[] keyValues || !MatchIndexPrefix(keyValues, prefix))
                    {
                        stop = true;
                        break;
                    }

                    if (keyValues.Length > prefix.Length)
                    {
                        var next = keyValues[prefix.Length];
                        if (hasLower)
                        {
                            var cmp = ComparableObject.CompareValues(next, lowerKey);
                            if (cmp < 0 || (cmp == 0 && !includeLower)) continue;
                        }
                        if (hasUpper)
                        {
                            var cmp = ComparableObject.CompareValues(next, upperKey);
                            if (cmp > 0 || (cmp == 0 && !includeUpper))
                            {
                                stop = true;
                                break;
                            }
                        }
                    }

                    foreach (var pk in entry.Value)
                        result.Add(pk.Value!);

                    if (maxMatches > 0 && result.Count >= maxMatches) return result;
                }

                if (stop || exhausted) break;

                // 继续扫描下一块，跳过已处理的那条
                fromKey = entries[^1].Key;
                includeFrom = false;
            }

            return result;
        }
    }

    /// <summary>统计二级索引键命中的主键数量（不回表）</summary>
    /// <param name="indexName">索引名</param>
    /// <param name="key">索引键值</param>
    /// <returns>命中主键数</returns>
    /// <remarks>仅在无并发写事务时可用：此时二级索引条目与可见行一一对应</remarks>
    public Int64 CountIndexKey(String indexName, Object key)
    {
        lock (_lock)
        {
            if (!_secondaryIndexes.TryGetValue(indexName, out var idx)) return 0;

            var normalized = NormalizeIndexValue(key);
            if (normalized == null) return 0;

            return idx.CountKey(normalized);
        }
    }

    /// <summary>统计联合索引前缀范围内命中的主键数量（不回表）</summary>
    /// <param name="indexName">索引名</param>
    /// <param name="prefixValues">前缀等值条件</param>
    /// <param name="lower">下一列下界，null 表示不限</param>
    /// <param name="hasLower">是否有下界</param>
    /// <param name="includeLower">是否包含下界</param>
    /// <param name="upper">下一列上界，null 表示不限</param>
    /// <param name="hasUpper">是否有上界</param>
    /// <param name="includeUpper">是否包含上界</param>
    /// <returns>命中主键数</returns>
    /// <remarks>仅在无并发写事务时可用</remarks>
    public Int64 CountIndexPrefix(String indexName, Object?[] prefixValues, Object? lower, Boolean hasLower, Boolean includeLower, Object? upper, Boolean hasUpper, Boolean includeUpper)
    {
        lock (_lock)
        {
            if (!_secondaryIndexes.TryGetValue(indexName, out var idx)) return 0;

            var indexDef = _schema.GetIndex(indexName);
            if (indexDef == null || prefixValues == null || prefixValues.Length == 0 || indexDef.Columns.Count <= prefixValues.Length) return 0;

            // 索引键列含 NULL 时不进索引，带 NULL 的前缀不可能命中
            var prefix = new Object?[prefixValues.Length];
            for (var i = 0; i < prefix.Length; i++)
            {
                prefix[i] = NormalizeIndexValue(prefixValues[i]);
                if (prefix[i] == null) return 0;
            }

            var lowerKey = hasLower ? NormalizeIndexValue(lower) : null;
            var upperKey = hasUpper ? NormalizeIndexValue(upper) : null;

            // 范围/前缀统计需要有序结构：首次使用时从哈希字典重建
            var ordered = idx.EnsureOrdered();

            var count = 0L;
            var fromKey = new ComparableObject(prefix);
            var includeFrom = true;

            while (true)
            {
                var entries = ordered.GetRange(fromKey, includeFrom, null, true, SecondaryIndexPrefixScanChunkSize);
                var exhausted = entries.Count < SecondaryIndexPrefixScanChunkSize;
                var stop = false;

                foreach (var entry in entries)
                {
                    if (entry.Key.Value is not Object?[] keyValues || !MatchIndexPrefix(keyValues, prefix))
                    {
                        stop = true;
                        break;
                    }

                    if (keyValues.Length > prefix.Length)
                    {
                        var next = keyValues[prefix.Length];
                        if (hasLower)
                        {
                            var cmp = ComparableObject.CompareValues(next, lowerKey);
                            if (cmp < 0 || (cmp == 0 && !includeLower)) continue;
                        }
                        if (hasUpper)
                        {
                            var cmp = ComparableObject.CompareValues(next, upperKey);
                            if (cmp > 0 || (cmp == 0 && !includeUpper))
                            {
                                stop = true;
                                break;
                            }
                        }
                    }

                    count += entry.Value.Count;
                }

                if (stop || exhausted) break;

                fromKey = entries[^1].Key;
                includeFrom = false;
            }

            return count;
        }
    }

    /// <summary>按二级索引键顺序扫描并回表，产出与索引键同序的可见行</summary>
    /// <param name="tx">事务</param>
    /// <param name="indexName">索引名</param>
    /// <param name="prefixValues">联合索引前缀等值条件，单列索引传 null</param>
    /// <param name="lower">单列索引键下界，null 表示不限</param>
    /// <param name="hasLower">是否有下界</param>
    /// <param name="includeLower">是否包含下界</param>
    /// <param name="upper">单列索引键上界，null 表示不限</param>
    /// <param name="hasUpper">是否有上界</param>
    /// <param name="includeUpper">是否包含上界</param>
    /// <param name="descending">是否按索引键降序扫描</param>
    /// <param name="maxRows">最大返回行数，0 表示不限制</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="neededColumns">需要反序列化的列掩码，null 表示全部列</param>
    /// <returns>按索引键顺序排列的可见行；索引不存在时返回空列表</returns>
    /// <remarks>
    /// 同一索引键下的主键始终按升序回表，与“全表扫描后按同一列稳定排序”的并列行顺序一致；
    /// 索引键列含 NULL 的行不进索引，调用方需保证排序列不可空。
    /// </remarks>
    public List<Object?[]> ScanIndexOrder(Transaction tx, String indexName, Object?[]? prefixValues, Object? lower, Boolean hasLower, Boolean includeLower, Object? upper, Boolean hasUpper, Boolean includeUpper, Boolean descending, Int32 maxRows, CancellationToken cancellationToken, Boolean[]? neededColumns = null)
    {
        if (tx == null) throw new ArgumentNullException(nameof(tx));
        if (indexName == null) throw new ArgumentNullException(nameof(indexName));

        lock (_lock)
        {
            RefreshStorage();

            if (!_secondaryIndexes.TryGetValue(indexName, out var idx)) return [];

            // 顺序扫描需要有序结构：首次使用时从哈希字典重建
            var ordered = idx.EnsureOrdered();

            var result = new List<Object?[]>();

            Object?[]? prefix = null;
            if (prefixValues != null && prefixValues.Length > 0)
            {
                prefix = new Object?[prefixValues.Length];
                for (var i = 0; i < prefix.Length; i++)
                {
                    prefix[i] = NormalizeIndexValue(prefixValues[i]);

                    // 索引键列含 NULL 时不进索引，带 NULL 的前缀不可能命中
                    if (prefix[i] == null) return result;
                }
            }

            if (prefix == null)
            {
                var fromKey = hasLower && lower != null ? new ComparableObject(NormalizeIndexValue(lower)!) : null;
                var includeFrom = !hasLower || includeLower;
                var toKey = hasUpper && upper != null ? new ComparableObject(NormalizeIndexValue(upper)!) : null;
                var includeTo = !hasUpper || includeUpper;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var entries = ordered.GetRange(fromKey, includeFrom, toKey, includeTo, SecondaryIndexPrefixScanChunkSize, descending);
                    foreach (var entry in entries)
                    {
                        if (AppendIndexEntryRows(tx, entry.Value, result, maxRows, cancellationToken, neededColumns)) return result;
                    }

                    if (entries.Count < SecondaryIndexPrefixScanChunkSize) break;

                    // 继续下一块：正向抬高下界，反向压低上界（已取过的键不再包含）
                    if (descending)
                    {
                        toKey = entries[^1].Key;
                        includeTo = false;
                    }
                    else
                    {
                        fromKey = entries[^1].Key;
                        includeFrom = false;
                    }
                }

                return result;
            }

            // 联合索引前缀等值：正向分块扫描到前缀变化即停；反向需先收集前缀分组再逆序回表
            var collected = new List<List<ComparableObject>>();
            var scanFrom = new ComparableObject(prefix);
            var scanIncludeFrom = true;
            var stop = false;
            while (!stop)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entries = ordered.GetRange(scanFrom, scanIncludeFrom, null, true, SecondaryIndexPrefixScanChunkSize);
                foreach (var entry in entries)
                {
                    // 单列索引键不是数组，与联合索引键不可比较，直接结束
                    if (entry.Key.Value is not Object?[] keyValues || !MatchIndexPrefix(keyValues, prefix))
                    {
                        stop = true;
                        break;
                    }

                    if (descending) collected.Add(entry.Value);
                    else if (AppendIndexEntryRows(tx, entry.Value, result, maxRows, cancellationToken, neededColumns)) return result;
                }

                if (stop || entries.Count < SecondaryIndexPrefixScanChunkSize) break;

                scanFrom = entries[^1].Key;
                scanIncludeFrom = false;
            }

            for (var i = collected.Count - 1; i >= 0; i--)
            {
                if (AppendIndexEntryRows(tx, collected[i], result, maxRows, cancellationToken, neededColumns)) return result;
            }

            return result;
        }
    }

    /// <summary>把同一索引键下的主键按升序回表追加可见行</summary>
    /// <param name="tx">事务</param>
    /// <param name="primaryKeys">索引键下的主键列表</param>
    /// <param name="rows">结果行列表</param>
    /// <param name="maxRows">最大返回行数，0 表示不限制</param>
    /// <param name="cancellationToken">取消令牌，每 <see cref="CancellationCheckInterval"/> 行检查一次</param>
    /// <param name="neededColumns">需要反序列化的列掩码，null 表示全部列</param>
    /// <returns>是否已达到行数上限</returns>
    private Boolean AppendIndexEntryRows(Transaction tx, List<ComparableObject> primaryKeys, List<Object?[]> rows, Int32 maxRows, CancellationToken cancellationToken, Boolean[]? neededColumns = null)
    {
        var count = 0;
        foreach (var pk in primaryKeys)
        {
            if ((++count & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();

            var row = Get(tx, pk.Value!, neededColumns);
            if (row == null) continue;

            rows.Add(row);
            if (maxRows > 0 && rows.Count >= maxRows) return true;
        }

        return false;
    }

    /// <summary>统计指定主键中当前事务可见的行数，只检查行版本可见性，不反序列化行数据</summary>
    /// <param name="tx">事务</param>
    /// <param name="primaryKeys">候选主键，调用方需保证已去重</param>
    /// <returns>可见行数</returns>
    public Int32 CountVisibleRows(Transaction tx, IList<Object> primaryKeys)
    {
        if (tx == null) throw new ArgumentNullException(nameof(tx));
        if (primaryKeys == null || primaryKeys.Count == 0) return 0;

        lock (_lock)
        {
            RefreshStorage();

            var count = 0;
            foreach (var pk in primaryKeys)
            {
                if (pk == null) continue;
                if (TryGetVisibleVersion(tx, pk, out _)) count++;
            }

            return count;
        }
    }

    /// <summary>判断联合索引键是否以指定前缀开始</summary>
    private static Boolean MatchIndexPrefix(Object?[] keyValues, Object?[] prefix)
    {
        if (keyValues.Length < prefix.Length) return false;

        for (var i = 0; i < prefix.Length; i++)
        {
            if (ComparableObject.CompareValues(keyValues[i], prefix[i]) != 0) return false;
        }

        return true;
    }

    /// <summary>规范化索引键的组成部分。联合索引键是数组，需逐元素规范化后再参与比较</summary>
    private static Object? NormalizeIndexKeyPart(Object? value)
    {
        if (value is Object?[] parts)
        {
            var copy = new Object?[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                copy[i] = NormalizeIndexValue(parts[i]);

            return copy;
        }

        return NormalizeIndexValue(value);
    }

    #region 全文索引（P2）

    /// <summary>创建全文索引并为已有数据构建索引</summary>
    /// <param name="indexName">索引名</param>
    /// <param name="columns">被索引的列名</param>
    public void CreateFullTextIndex(String indexName, String[] columns)
    {
        if (String.IsNullOrEmpty(indexName)) throw new ArgumentNullException(nameof(indexName));
        if (columns == null || columns.Length == 0) throw new ArgumentNullException(nameof(columns));

        // 校验列存在
        foreach (var col in columns)
        {
            _schema.GetColumnIndex(col);
        }

        lock (_lock)
        {
            if (_fullTextIndexes.ContainsKey(indexName))
                throw new NovaException(ErrorCode.InvalidArgument, "Full-text index '{0}' already exists", indexName);

            var idx = new FullTextIndex(indexName, columns);
            _fullTextIndexes[indexName] = idx;

            var colOrdinals = columns.Select(c => _schema.GetColumnIndex(c)).ToArray();
            var pkCol = _schema.GetPrimaryKeyColumn()!;

            // 为已有数据构建索引
            using var tx = _txManager.BeginTransaction();
            foreach (var (_, ver) in CollectVisibleVersions(tx, null, true, null, true, false))
            {
                var row = DeserializeVersion(ver);
                var docId = row[pkCol.Ordinal]!;
                var text = BuildFullText(row, colOrdinals);
                idx.Add(docId, text);
            }
            tx.Commit();
        }
    }

    /// <summary>删除全文索引</summary>
    /// <param name="indexName">索引名</param>
    public void DropFullTextIndex(String indexName)
    {
        lock (_lock)
        {
            _fullTextIndexes.Remove(indexName);
        }
    }

    /// <summary>全文检索，返回按 BM25 分数降序的 (主键, 分数) 列表</summary>
    /// <param name="indexName">索引名</param>
    /// <param name="query">查询文本</param>
    /// <param name="topK">返回数量上限</param>
    /// <returns>匹配的 (主键值, 分数) 列表</returns>
    public List<(Object PrimaryKey, Double Score)> SearchFullText(String indexName, String query, Int32 topK = 10)
    {
        lock (_lock)
        {
            if (!_fullTextIndexes.TryGetValue(indexName, out var idx))
                throw new NovaException(ErrorCode.InvalidArgument, "Full-text index '{0}' not found", indexName);

            var pkCol = _schema.GetPrimaryKeyColumn()!;
            var result = new List<(Object, Double)>();

            using var tx = _txManager.BeginTransaction();
            foreach (var (docId, score) in idx.Search(query, topK))
            {
                var row = Get(tx, docId);
                if (row != null)
                    result.Add((row[pkCol.Ordinal]!, score));
            }
            tx.Commit();

            return result;
        }
    }

    /// <summary>索引维护：插入/更新行时更新全文索引</summary>
    /// <param name="docId">文档 ID（主键值）</param>
    /// <param name="row">行数据</param>
    /// <param name="remove">是否仅移除旧文档（更新时先移除再插入）</param>
    private void MaintainFullTextIndexes(Object docId, Object?[]? row, Boolean remove = false)
    {
        if (_fullTextIndexes.Count == 0) return;

        foreach (var idx in _fullTextIndexes.Values)
        {
            if (remove)
            {
                // 仅移除旧文档，不写入内容
                idx.Remove(docId);
                continue;
            }

            var colOrdinals = idx.Columns.Select(c => _schema.GetColumnIndex(c)).ToArray();
            var text = BuildFullText(row!, colOrdinals);
            idx.Add(docId, text);
        }
    }

    /// <summary>构建全文索引文本（多列拼接）</summary>
    /// <param name="row">行数据</param>
    /// <param name="colOrdinals">列序号</param>
    /// <returns>拼接文本</returns>
    private static String BuildFullText(Object?[] row, Int32[] colOrdinals)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ord in colOrdinals)
        {
            if (row[ord] != null)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Convert.ToString(row[ord], CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    #endregion

    /// <summary>获取索引定义的列序号数组</summary>
    /// <param name="indexDef">索引定义</param>
    /// <returns>列序号数组</returns>
    /// <remarks>每行写入都会用到，按索引缓存；表结构变更时清空缓存</remarks>
    private Int32[] GetColumnOrdinals(IndexDefinition indexDef)
    {
        if (_columnOrdinalCache.TryGetValue(indexDef.IndexName, out var cached)) return cached;

        var colOrdinals = new Int32[indexDef.Columns.Count];
        for (var i = 0; i < indexDef.Columns.Count; i++)
            colOrdinals[i] = _schema.GetColumnIndex(indexDef.Columns[i]);

        _columnOrdinalCache[indexDef.IndexName] = colOrdinals;

        return colOrdinals;
    }

    /// <summary>获取索引键的编码类型（单列索引用列类型，组合索引编码为字符串）</summary>
    /// <param name="indexDef">索引定义</param>
    /// <returns>索引键数据类型</returns>
    private DataType GetSecondaryIndexKeyType(IndexDefinition indexDef)
    {
        var colOrdinals = GetColumnOrdinals(indexDef);

        return colOrdinals.Length == 1 ? _schema.Columns[colOrdinals[0]].DataType : DataType.String;
    }

    /// <summary>获取索引键每一列的数据类型（哈希字典的键编码用）</summary>
    /// <param name="indexDef">索引定义</param>
    /// <returns>列类型数组</returns>
    private DataType[] GetSecondaryIndexKeyTypes(IndexDefinition indexDef)
    {
        var colOrdinals = GetColumnOrdinals(indexDef);
        var types = new DataType[colOrdinals.Length];
        for (var i = 0; i < colOrdinals.Length; i++) types[i] = _schema.Columns[colOrdinals[i]].DataType;

        return types;
    }

    /// <summary>构建索引键</summary>
    private static ComparableObject BuildIndexKey(Object?[] row, Int32[] colOrdinals)
    {
        if (colOrdinals.Length == 1)
            return new ComparableObject(NormalizeIndexValue(row[colOrdinals[0]]!)!);

        // 联合索引：用数组作为键
        var keyValues = new Object?[colOrdinals.Length];
        for (var i = 0; i < colOrdinals.Length; i++)
            keyValues[i] = NormalizeIndexValue(row[colOrdinals[i]]);

        return new ComparableObject(keyValues);
    }

    /// <summary>规范化索引键值。字符串统一大写，使索引等值与 SQL 的大小写不敏感比较语义一致</summary>
    /// <param name="value">原始值</param>
    /// <returns>规范化后的值</returns>
    private static Object? NormalizeIndexValue(Object? value) => value is String str ? str.ToUpperInvariant() : value;

    /// <summary>唯一性预检查：检测行在唯一二级索引中的冲突（只读，无副作用）</summary>
    /// <param name="row">待插入的行数据</param>
    /// <param name="pk">行主键。同主键的既有条目不视为冲突（Update 唯一值未变场景）</param>
    /// <exception cref="NovaException">唯一键冲突时抛出 UniqueConstraintViolation</exception>
    private void CheckUniqueIndexConflicts(Object?[] row, ComparableObject pk)
    {
        foreach (var kvp in _secondaryIndexes)
        {
            var indexDef = _schema.GetIndex(kvp.Key);
            if (indexDef == null || !indexDef.IsUnique) continue;

            var colOrdinals = GetColumnOrdinals(indexDef);

            // 索引键列含 NULL 时不参与唯一性检查（MySQL 语义）
            if (HasNullColumn(row, colOrdinals)) continue;

            var indexKey = BuildIndexKey(row, colOrdinals);
            var existing = kvp.Value.TryGet(indexKey.Value);
            if (existing != null && existing.Count > 0)
            {
                // 若既有条目均属于当前主键（Update 场景唯一值未变），不视为冲突
                foreach (var existingPk in existing)
                {
                    if (ComparableObject.CompareValues(existingPk, pk.Value) != 0)
                        throw new NovaException(ErrorCode.UniqueConstraintViolation, "Duplicate key in unique index '{0}'", indexDef.IndexName);
                }
            }
        }
    }

    /// <summary>向所有二级索引插入条目</summary>
    /// <param name="row">行数据</param>
    /// <param name="pk">主键</param>
    /// <param name="deltas">增量收集器，为空表示只维护内存索引（回滚路径）</param>
    /// <param name="skipRow">需要跳过的旧行（更新时索引键未变化，无需重复维护）</param>
    /// <remarks>唯一性冲突必须在此前通过 <see cref="CheckUniqueIndexConflicts"/> 预检查，本方法不再抛出唯一性异常</remarks>
    private void InsertIntoSecondaryIndexes(Object?[] row, ComparableObject pk, List<SecondaryIndexDeltaEntry>? deltas = null, Object?[]? skipRow = null)
    {
        foreach (var kvp in _secondaryIndexes)
        {
            var indexDef = _schema.GetIndex(kvp.Key);
            if (indexDef == null) continue;

            var colOrdinals = GetColumnOrdinals(indexDef);

            // 索引键列含 NULL 时跳过该行（MySQL 语义：NULL 值不进二级索引）
            if (HasNullColumn(row, colOrdinals)) continue;

            var indexKey = BuildIndexKey(row, colOrdinals);

            // 更新时索引键未变：条目已存在，跳过删除与插入两侧
            if (skipRow != null && !HasNullColumn(skipRow, colOrdinals) &&
                ComparableObject.CompareValues(BuildIndexKey(skipRow, colOrdinals).Value, indexKey.Value) == 0)
                continue;

            kvp.Value.Add(indexKey.Value, pk.Value!);

            // 联合索引不落盘，跳过增量记录，避免每条 DML 都走一次注定失败的编码
            if (colOrdinals.Length == 1)
                deltas?.Add(new SecondaryIndexDeltaEntry(kvp.Key, true, indexKey.Value, pk.Value!));
        }
    }

    /// <summary>从所有二级索引删除条目</summary>
    /// <param name="row">行数据</param>
    /// <param name="pk">主键</param>
    /// <param name="deltas">增量收集器，为空表示只维护内存索引（回滚路径）</param>
    /// <param name="keepRow">需要保留的新行（更新时索引键未变化，无需重复维护）</param>
    private void RemoveFromSecondaryIndexes(Object?[] row, ComparableObject pk, List<SecondaryIndexDeltaEntry>? deltas = null, Object?[]? keepRow = null)
    {
        foreach (var kvp in _secondaryIndexes)
        {
            var indexDef = _schema.GetIndex(kvp.Key);
            if (indexDef == null) continue;

            var colOrdinals = GetColumnOrdinals(indexDef);

            // 索引键列含 NULL 时该行未入索引，直接跳过
            if (HasNullColumn(row, colOrdinals)) continue;

            var indexKey = BuildIndexKey(row, colOrdinals);

            // 更新时索引键未变：不做「先删后插」，避免在长值链上白走一趟（低基数列一条链可达数千个主键）
            if (keepRow != null && !HasNullColumn(keepRow, colOrdinals) &&
                ComparableObject.CompareValues(BuildIndexKey(keepRow, colOrdinals).Value, indexKey.Value) == 0)
                continue;

            kvp.Value.Remove(indexKey.Value, pk.Value!);

            // 联合索引不落盘，跳过增量记录
            if (colOrdinals.Length == 1)
                deltas?.Add(new SecondaryIndexDeltaEntry(kvp.Key, false, indexKey.Value, pk.Value!));
        }
    }

    /// <summary>判断行在指定列序号上是否含 NULL 值</summary>
    private static Boolean HasNullColumn(Object?[] row, Int32[] colOrdinals)
    {
        foreach (var ord in colOrdinals)
        {
            if (row[ord] == null) return true;
        }
        return false;
    }

    /// <summary>恢复二级索引：优先复用已是最新的 .hidx，否则用 .idx 基线 + .idxl 回放填充，最后回退主键索引重建</summary>
    private void RestoreSecondaryIndexes()
    {
        var pkType = _schema.GetPrimaryKeyColumn()!.DataType;
        var readOnly = _options.ReadOnly;

        foreach (var indexDef in _schema.Indexes)
        {
            var path = _fileManager.GetSecondaryHashIndexFilePath(indexDef.IndexName);
            var keyTypes = GetSecondaryIndexKeyTypes(indexDef);

            NovaSecondaryIndex idx;
            try
            {
                idx = NovaSecondaryIndex.Open(path, indexDef, _codec, keyTypes, pkType, readOnly);
                if (idx.AppliedOffset == _rowLogLength)
                {
                    // 哈希字典已包含增量里的全部条目，增量文件不再需要（损坏的残留也一并清掉）
                    if (!readOnly) ResetSecondaryIndexDelta(indexDef.IndexName);

                    _secondaryIndexes[indexDef.IndexName] = idx;
                _secondaryIndexBaselineCounts[indexDef.IndexName] = (Int32)idx.LiveCount;
                    continue;
                }

                idx.Dispose();

                // 只读模式没有权限重建索引文件，此时不注册该索引，查询自动退回全表扫描
                if (readOnly) continue;
            }
            catch (Exception ex) when (ex is NovaException or IOException or InvalidOperationException)
            {
                // 文件缺失或损坏：走重建
                if (readOnly) continue;
            }

            idx = NovaSecondaryIndex.Recreate(path, indexDef, _codec, keyTypes, pkType);

            // 联合索引旧文件格式不支持基线，直接从主键索引重建
            // .idx 基线只在增量已清空时直接加载；增量非空时本应回放，而回放要逐条值链去重，
            // 低基数列上是 O(N²)（百万行分钟级），改为直接从主键索引重建（O(N)，秒级）
            var loaded = indexDef.Columns.Count == 1 && TryLoadSecondaryIndexFile(indexDef, idx) && !HasSecondaryIndexDelta(indexDef);
            if (!loaded)
            {
                ResetSecondaryIndexDelta(indexDef.IndexName);
                RebuildSecondaryIndex(indexDef, idx);
            }

            idx.Commit(_rowLogLength);
            _secondaryIndexes[indexDef.IndexName] = idx;
            _secondaryIndexBaselineCounts[indexDef.IndexName] = (Int32)idx.LiveCount;
        }
    }

    /// <summary>判断二级索引是否还有待回放的增量记录</summary>
    /// <param name="indexDef">索引定义</param>
    /// <returns>增量文件存在且含有记录时返回 true</returns>
    private Boolean HasSecondaryIndexDelta(IndexDefinition indexDef)
    {
        var path = _fileManager.GetSecondaryIndexDeltaFilePath(indexDef.IndexName);

        try
        {
            return File.Exists(path) && new FileInfo(path).Length > FileHeader.HeaderSize;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>把 .idx 基线加载进哈希字典</summary>
    /// <param name="indexDef">索引定义</param>
    /// <param name="idx">目标索引</param>
    /// <returns>加载成功返回 true；文件缺失、版本不符或损坏返回 false</returns>
    private Boolean TryLoadSecondaryIndexFile(IndexDefinition indexDef, NovaSecondaryIndex idx)
    {
        var filePath = _fileManager.GetSecondaryIndexFilePath(indexDef.IndexName);
        if (!File.Exists(filePath)) return false;

        var indexKeyType = GetSecondaryIndexKeyType(indexDef);

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < FileHeader.HeaderSize + 4) return false;

            // 版本 1 的索引键未做字符串规范化，与新键不兼容，直接从行数据重建
            var header = FileHeader.Read(new ReadOnlySpan<Byte>(bytes, 0, FileHeader.HeaderSize));
            if (header.Version != SecondaryIndexFileVersion) return false;

            var reader = new SpanReader(bytes, FileHeader.HeaderSize);
            var count = reader.ReadInt32();

            // SpanReader.Position 相对构造时指定的起始偏移，解码时需加回文件头长度
            const Int32 baseOffset = FileHeader.HeaderSize;

            for (var i = 0; i < count; i++)
            {
                var keyLen = reader.ReadInt32();
                var key = _codec.Decode(bytes, baseOffset + reader.Position, indexKeyType);
                reader.Advance(keyLen);

                var pkCount = reader.ReadInt32();
                for (var j = 0; j < pkCount; j++)
                {
                    var pkLen = reader.ReadInt32();
                    var pk = _codec.Decode(bytes, baseOffset + reader.Position, _schema.GetPrimaryKeyColumn()!.DataType);
                    reader.Advance(pkLen);

                    // 基线由 EnumerateAll 写出，(键, 主键) 组合天然唯一，不需要值链去重；
                    // 低基数列值链可达上万个主键，逐条去重会把加载退化成 O(N²)（百万行约 7 分钟）
                    if (key != null && pk != null) idx.Add(key, pk);
                }
            }

            return true;
        }
        catch
        {
            // 索引文件损坏时回退到全表重建，保证表可加载
            return false;
        }
    }

    /// <summary>从主键索引全表扫描重建二级索引</summary>
    /// <param name="indexDef">索引定义</param>
    /// <param name="idx">目标索引</param>
    private void RebuildSecondaryIndex(IndexDefinition indexDef, NovaSecondaryIndex idx)
    {
        var pkCol = _schema.GetPrimaryKeyColumn()!;
        var colOrdinals = GetColumnOrdinals(indexDef);

        // 启动恢复后，主键索引中每个主键仅有一个已提交版本
        _primaryIndex.Scan(null, true, null, true, false, 0, (_, ver) =>
        {
            if (ver.Payload == null && ver.DataOffset <= 0) return true;

            var row = DeserializeVersion(ver);

            // 索引键列含 NULL 时跳过该行（MySQL 语义）
            if (HasNullColumn(row, colOrdinals)) return true;

            var indexKey = BuildIndexKey(row, colOrdinals);

            // 历史数据可能违反唯一约束（如仅大小写不同的重复值），这里全部保留，保证索引查询与全表扫描结果一致
            idx.Add(indexKey.Value, row[pkCol.Ordinal]!);

            return true;
        });
    }

    /// <summary>将二级索引持久化到 .idx 文件（全量重写）</summary>
    /// <param name="indexName">索引名称</param>
    /// <remarks>联合索引键是数组，当前文件格式只支持标量键，因此联合索引不落盘，启动时按行日志重建</remarks>
    private void PersistSecondaryIndex(String indexName)
    {
        var idxDef = _schema.GetIndex(indexName);
        if (idxDef == null) return;
        if (idxDef.Columns.Count > 1) return;

        lock (_lock)
        {
            if (!_secondaryIndexes.TryGetValue(indexName, out var idx)) return;

            var filePath = _fileManager.GetSecondaryIndexFilePath(indexName);
            var pkCol = _schema.GetPrimaryKeyColumn()!;

            // 索引键数据类型（单列索引用列类型，组合索引编码为字符串）
            var indexKeyType = GetSecondaryIndexKeyType(idxDef);

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);

            // 写入文件头
            var header = new FileHeader
            {
                Version = SecondaryIndexFileVersion,
                FileType = FileType.Index,
                PageSize = (UInt32)_options.PageSize,
                CreateTime = DateTime.Now
            };
            using var headerPk = header.ToPacket();
            if (headerPk.TryGetArray(out var segment))
                fs.Write(segment.Array!, segment.Offset, segment.Count);

            // 使用 SpanWriter + 流模式写入索引条目，避免 BinaryWriter 分配
            Span<Byte> spanBuf = stackalloc Byte[4096];
            var writer = new SpanWriter(spanBuf, fs);

            // 哈希字典按键的取值链连续，这里聚成 [键, 主键列表] 后写入
            var groups = new List<(Object Key, List<Object> Pks)>();
            foreach (var (key, pk) in idx.EnumerateAll())
            {
                if (groups.Count > 0 && ComparableObject.CompareValues(groups[groups.Count - 1].Key, key) == 0)
                    groups[groups.Count - 1].Pks.Add(pk);
                else
                    groups.Add((key, [pk]));
            }

            writer.Write(groups.Count);

            foreach (var (key, pkList) in groups)
            {
                // 编码索引键
                var keyBytes = _codec.Encode(key, indexKeyType);
                writer.Write(keyBytes.Length);
                writer.Write(keyBytes);

                // 写入主键列表
                writer.Write(pkList.Count);
                foreach (var pk in pkList)
                {
                    var pkBytes = _codec.Encode(pk, pkCol.DataType);
                    writer.Write(pkBytes.Length);
                    writer.Write(pkBytes);
                }
            }

            writer.Flush();
        }
    }

    /// <summary>删除二级索引的 .idx 基线与 .hidx 哈希文件</summary>
    /// <param name="indexName">索引名称</param>
    private void DeleteSecondaryIndexFile(String indexName)
    {
        foreach (var filePath in new[] { _fileManager.GetSecondaryIndexFilePath(indexName), _fileManager.GetSecondaryHashIndexFilePath(indexName) })
        {
            if (!File.Exists(filePath)) continue;

            try { File.Delete(filePath); }
            catch { }
        }
    }

    #endregion

    #region 辅助

    /// <summary>序列化行数据</summary>
    private Byte[] SerializeRow(Object?[] row)
    {
        // 估一个常用初始容量，减少扩容次数。
        // 暂定256字节，实际根据列数和数据类型可能需要调整。
        using var w = new PooledBufferWriter(initialCapacity: 256);

        // 写入列数（Int32，小端）
        w.WriteInt32(row.Length);

        // 写入每列的值
        for (var i = 0; i < row.Length; i++)
        {
            var colDef = _schema.Columns[i];
            var encodedLength = _codec.GetEncodedLength(row[i], colDef.DataType);
            w.WriteInt32(encodedLength);

            var segment = w.GetWritableSegment(encodedLength);
            _codec.Encode(row[i], colDef.DataType, segment.Array!, segment.Offset);
        }

        return w.Buffer.AsSpan(0, w.WrittenCount).ToArray();
    }

    /// <summary>反序列化指定版本的行数据</summary>
    /// <param name="version">行版本</param>
    /// <param name="neededColumns">需要反序列化的列掩码，null 表示全部列；未选中的列返回 null</param>
    /// <returns>行数据</returns>
    private Object?[] DeserializeVersion(RowVersion version, Boolean[]? neededColumns = null)
    {
        // 未提交或未开启行日志时载荷仍在内存
        if (version.Payload != null) return DeserializeRow(version.Payload, 0, version.Payload.Length, neededColumns);

        if (version.DataOffset <= 0 || version.DataLength <= 0 || _rowStore == null)
            throw new NovaException(ErrorCode.FileCorrupted, "Row payload of key '{0}' is missing", version.Key);

        // 借池化缓冲区读取，避免逐行分配；载荷自描述，边界由 DeserializeRow 校验
        var buffer = ArrayPool<Byte>.Shared.Rent(version.DataLength);
        try
        {
            _rowStore.Read(version.DataOffset, buffer, version.DataLength);
            return DeserializeRow(buffer, 0, version.DataLength, neededColumns);
        }
        finally
        {
            ArrayPool<Byte>.Shared.Return(buffer);
        }
    }

    /// <summary>按载荷位置读取并反序列化行数据</summary>
    /// <param name="offset">载荷在行日志中的起始偏移</param>
    /// <param name="length">载荷字节数</param>
    /// <param name="neededColumns">需要反序列化的列掩码，null 表示全部列</param>
    /// <returns>行数据</returns>
    private Object?[] DeserializeAt(Int64 offset, Int32 length, Boolean[]? neededColumns)
    {
        if (_rowStore == null || offset <= 0 || length <= 0)
            throw new NovaException(ErrorCode.FileCorrupted, "Row payload [{0},{1}) is missing", offset, length);

        var buffer = ArrayPool<Byte>.Shared.Rent(length);
        try
        {
            _rowStore.Read(offset, buffer, length);
            return DeserializeRow(buffer, 0, length, neededColumns);
        }
        finally
        {
            ArrayPool<Byte>.Shared.Return(buffer);
        }
    }

    /// <summary>反序列化行数据</summary>
    private Object?[] DeserializeRow(Byte[] payload) => DeserializeRow(payload, payload.Length);

    /// <summary>反序列化行数据（缓冲区可能大于载荷）</summary>
    /// <param name="payload">载荷所在的缓冲区</param>
    /// <param name="payloadLength">载荷有效字节数</param>
    /// <returns>行数据</returns>
    private Object?[] DeserializeRow(Byte[] payload, Int32 payloadLength) => DeserializeRow(payload, 0, payloadLength, null);

    /// <summary>反序列化行数据（缓冲区可能大于载荷）</summary>
    /// <param name="payload">载荷所在的缓冲区</param>
    /// <param name="offset">载荷起始位置</param>
    /// <param name="payloadLength">载荷有效字节数</param>
    /// <param name="neededColumns">需要反序列化的列掩码，null 表示全部列；未选中的列直接跳过解码并返回 null</param>
    /// <returns>行数据</returns>
    private Object?[] DeserializeRow(Byte[] payload, Int32 offset, Int32 payloadLength, Boolean[]? neededColumns)
    {
        var pos = offset;
        var end = offset + payloadLength;
        var colCount = ReadInt32(payload, ref pos, end);
        var row = new Object?[colCount];

        for (var i = 0; i < colCount; i++)
        {
            var length = ReadInt32(payload, ref pos, end);
            if (length < 0 || pos + length > end)
                throw new NovaException(ErrorCode.FileCorrupted, "Row payload column {0} is out of range", i);

            // 未引用的列跳过解码：省掉字符串/二进制复制与装箱，这是全表扫描的主要开销
            if (neededColumns != null && i < neededColumns.Length && !neededColumns[i])
            {
                pos += length;
                continue;
            }

            var colDef = _schema.Columns[i];
            if (length == 1 && payload[pos] == 0x00)
                row[i] = null;
            else
                row[i] = _codec.Decode(payload, pos, colDef.DataType);
            pos += length;
        }

        return row;
    }

    private static Int32 ReadInt32(Byte[] buffer, ref Int32 pos, Int32 limit)
    {
        if (pos + 4 > limit)
            throw new NovaException(ErrorCode.FileCorrupted, "Row payload is truncated");

        var value = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(pos, 4));
        pos += 4;
        return value;
    }

    #endregion

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_lock)
        {
            if (_disposed)
                return;

            // 关闭前把二级索引增量合并进基线，避免下次启动回放；失败时保留增量文件，下次启动仍可回放或重建
            try { CompactSecondaryIndexesOnClose(); }
            catch { }

            foreach (var idx in _secondaryIndexes.Values)
            {
                try { idx.Dispose(); } catch { }
            }

            _primaryIndex.Dispose();
            _rowStore?.Dispose();
            _rowLogStream?.Dispose();
            _disposed = true;
        }
    }
}
