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
    private readonly WalWriter? _walWriter;
    private readonly MmfPager _dataPager;
    private readonly IDataCodec _codec;
    private readonly TableFileManager _fileManager;

    // 主键索引（内存 SkipList）
    private readonly SkipList<ComparableObject, List<RowVersion>> _primaryIndex;
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif
    private Boolean _disposed;

    // 二级索引：索引名 → SkipList<索引键, 主键列表>
    private readonly Dictionary<String, SkipList<ComparableObject, List<ComparableObject>>> _secondaryIndexes = new(StringComparer.OrdinalIgnoreCase);

    // 冷热分离索引管理器
    private readonly HotIndexManager _hotIndexManager;

    // 分片管理器
    private readonly ShardManager _shardManager;

    // 默认分片 ID
    private const Int32 DefaultShardId = 0;

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

        // 初始化数据文件（新建时写入 FileHeader）
        var dataPath = _fileManager.GetDataFilePath();
        _dataPager = new MmfPager(dataPath, _options.PageSize);
        var dataHeader = new FileHeader
        {
            Version = 1,
            FileType = FileType.Data,
            PageSize = (UInt32)_options.PageSize,
            CreateTime = DateTime.Now,
        };
        _dataPager.Open(dataHeader);

        // 初始化 WAL
        if (_options.WalMode != WalMode.None)
        {
            var walPath = _fileManager.GetWalFilePath();
            _walWriter = new WalWriter(walPath, _options.WalMode);
            _walWriter.Open();
        }

        // 初始化主键索引
        _primaryIndex = new SkipList<ComparableObject, List<RowVersion>>();

        // 初始化冷热分离索引管理器
        _hotIndexManager = new HotIndexManager(new HotSegmentConfig());

        // 初始化分片管理器
        _shardManager = new ShardManager(_options, _dbPath);
        _shardManager.AddShard(new ShardInfo
        {
            ShardId = DefaultShardId,
            DataFilePath = _fileManager.GetDataFilePath(),
            CreatedAt = DateTime.UtcNow
        });

        // 打开行日志并恢复数据
        OpenRowLog();
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
            throw new NovaException(ErrorCode.InvalidArgument, $"Row has {row.Length} columns, expected {_schema.Columns.Count}");

        lock (_lock)
        {
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

            // 检查主键冲突
            if (!_primaryIndex.TryGetValue(comparableKey, out var versions))
            {
                versions = [];
                _primaryIndex.Insert(comparableKey, versions);
            }
            else
            {
                // 检查是否有可见的版本（主键冲突）
                foreach (var ver in versions!)
                {
                    if (ver.IsVisible(_txManager, tx.TxId))
                        throw new NovaException(ErrorCode.PrimaryKeyConflict, $"Primary key '{pkValue}' already exists");
                }
            }

            // 添加新版本
            versions.Add(rowVersion);

            // 维护二级索引
            if (_secondaryIndexes.Count > 0)
            {
                InsertIntoSecondaryIndexes(row, comparableKey);

                // 注册提交动作：事务提交时持久化索引文件
                foreach (var idxName in _secondaryIndexes.Keys)
                {
                    var name = idxName;
                    tx.RegisterCommitAction(() => PersistSecondaryIndex(name));
                }
            }

            // 更新分片统计
            _shardManager.RecordWrite(DefaultShardId, payload.Length);

            // 注册提交动作：事务提交时才持久化到行日志
            var persistPayload = payload;
            tx.RegisterCommitAction(() => PersistPut(persistPayload));

            // 记录 WAL（如果启用）
            if (_walWriter != null)
            {
                var record = new WalRecord
                {
                    RecordType = WalRecordType.UpdatePage,
                    TxId = tx.TxId,
                    PageId = 0,
                    Data = payload
                };
                _walWriter.Write(record);
            }

            // 注册回滚动作
            tx.RegisterRollbackAction(() =>
            {
                lock (_lock)
                {
                    if (_primaryIndex.TryGetValue(comparableKey, out var vers))
                    {
                        vers!.Remove(rowVersion);
                    }
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
    public Object?[]? Get(Transaction tx, Object key)
    {
        if (tx == null)
            throw new ArgumentNullException(nameof(tx));
        if (key == null)
            throw new ArgumentNullException(nameof(key));

        lock (_lock)
        {
            var comparableKey = new ComparableObject(key);

            if (!_primaryIndex.TryGetValue(comparableKey, out var versions))
                return null;

            // 查找对当前事务可见的最新版本
            RowVersion? visibleVersion = null;
            foreach (var ver in versions!)
            {
                if (ver.IsVisible(_txManager, tx.TxId))
                {
                    visibleVersion = ver;
                    break;
                }
            }

            if (visibleVersion == null)
                return null;

            // 记录热度访问
            _hotIndexManager.AccessKey(key);

            // 反序列化行数据
            return DeserializeRow(visibleVersion.Payload!);
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
            throw new NovaException(ErrorCode.InvalidArgument, $"Row has {newRow.Length} columns, expected {_schema.Columns.Count}");

        lock (_lock)
        {
            var comparableKey = new ComparableObject(key);

            if (!_primaryIndex.TryGetValue(comparableKey, out var versions))
                return false;

            // 查找对当前事务可见的版本
            RowVersion? visibleVersion = null;
            foreach (var ver in versions!)
            {
                if (ver.IsVisible(_txManager, tx.TxId))
                {
                    visibleVersion = ver;
                    break;
                }
            }

            if (visibleVersion == null)
                return false;

            // 更新二级索引：先移除旧值，后面再插入新值
            Object?[]? oldRow = null;
            if (_secondaryIndexes.Count > 0)
            {
                oldRow = DeserializeRow(visibleVersion.Payload!);
                RemoveFromSecondaryIndexes(oldRow, comparableKey);
            }

            // 标记旧版本为已删除
            var oldDeletedByTx = visibleVersion.DeletedByTx;
            visibleVersion.MarkDeleted(tx.TxId);

            // 创建新版本
            var payload = SerializeRow(newRow);
            var newVersion = new RowVersion(tx.TxId, key, payload);
            versions.Add(newVersion);

            // 维护二级索引：插入新值
            if (_secondaryIndexes.Count > 0)
            {
                InsertIntoSecondaryIndexes(newRow, comparableKey);

                // 注册提交动作：事务提交时持久化索引文件
                foreach (var idxName in _secondaryIndexes.Keys)
                {
                    var name = idxName;
                    tx.RegisterCommitAction(() => PersistSecondaryIndex(name));
                }
            }

            // 更新分片统计
            _shardManager.RecordWrite(DefaultShardId, payload.Length);

            // 注册提交动作：事务提交时才持久化新版本
            var persistPayload = payload;
            tx.RegisterCommitAction(() => PersistPut(persistPayload));

            // 记录 WAL
            if (_walWriter != null)
            {
                var record = new WalRecord
                {
                    RecordType = WalRecordType.UpdatePage,
                    TxId = tx.TxId,
                    PageId = 0,
                    Data = payload
                };
                _walWriter.Write(record);
            }

            // 注册回滚动作
            var rollbackOldRow = oldRow;
            tx.RegisterRollbackAction(() =>
            {
                lock (_lock)
                {
                    visibleVersion.DeletedByTx = oldDeletedByTx;
                    if (_primaryIndex.TryGetValue(comparableKey, out var vers))
                    {
                        vers!.Remove(newVersion);
                    }
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
            var comparableKey = new ComparableObject(key);

            if (!_primaryIndex.TryGetValue(comparableKey, out var versions))
                return false;

            // 查找对当前事务可见的版本
            RowVersion? visibleVersion = null;
            foreach (var ver in versions!)
            {
                if (ver.IsVisible(_txManager, tx.TxId))
                {
                    visibleVersion = ver;
                    break;
                }
            }

            if (visibleVersion == null)
                return false;

            // 从二级索引移除
            Object?[]? deletedRow = null;
            if (_secondaryIndexes.Count > 0)
            {
                deletedRow = DeserializeRow(visibleVersion.Payload!);
                RemoveFromSecondaryIndexes(deletedRow, comparableKey);

                // 注册提交动作：事务提交时持久化索引文件
                foreach (var idxName in _secondaryIndexes.Keys)
                {
                    var name = idxName;
                    tx.RegisterCommitAction(() => PersistSecondaryIndex(name));
                }
            }

            // 标记为已删除
            var oldDeletedByTx = visibleVersion.DeletedByTx;
            visibleVersion.MarkDeleted(tx.TxId);

            // 注册提交动作：事务提交时才持久化删除记录
            var persistKey = key;
            tx.RegisterCommitAction(() => PersistDelete(persistKey));

            // 记录 WAL
            if (_walWriter != null)
            {
                var record = new WalRecord
                {
                    RecordType = WalRecordType.UpdatePage,
                    TxId = tx.TxId,
                    PageId = 0,
                    Data = []
                };
                _walWriter.Write(record);
            }

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
    public List<Object?[]> GetAll(Transaction tx)
    {
        if (tx == null)
            throw new ArgumentNullException(nameof(tx));

        lock (_lock)
        {
            var result = new List<Object?[]>();
            var allEntries = _primaryIndex.GetAll();

            foreach (var entry in allEntries)
            {
                var versions = entry.Value;
                foreach (var ver in versions)
                {
                    if (ver.IsVisible(_txManager, tx.TxId))
                    {
                        var row = DeserializeRow(ver.Payload!);
                        result.Add(row);
                        break;
                    }
                }
            }

            return result;
        }
    }

    /// <summary>清空表中所有数据，保留表结构</summary>
    public void Truncate()
    {
        lock (_lock)
        {
            _primaryIndex.Clear();
            _hotIndexManager.Clear();
            TruncateRowLog();

            // 清空所有二级索引
            foreach (var idx in _secondaryIndexes.Values)
                idx.Clear();
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
                throw new NovaException(ErrorCode.InvalidArgument, $"Index '{indexDef.IndexName}' already exists");

            var idx = new SkipList<ComparableObject, List<ComparableObject>>();
            _secondaryIndexes[indexDef.IndexName] = idx;

            var colOrdinals = GetColumnOrdinals(indexDef);

            var pkCol = _schema.GetPrimaryKeyColumn()!;

            // 为已有数据构建索引
            var allEntries = _primaryIndex.GetAll();
            foreach (var entry in allEntries)
            {
                var versions = entry.Value;
                foreach (var ver in versions)
                {
                    if (ver.IsVisible(_txManager, tx.TxId))
                    {
                        var row = DeserializeRow(ver.Payload!);
                        var indexKey = BuildIndexKey(row, colOrdinals);
                        var pk = new ComparableObject(row[pkCol.Ordinal]!);

                        if (!idx.TryGetValue(indexKey, out var pkList))
                        {
                            pkList = [];
                            idx.Insert(indexKey, pkList);
                        }
                        else if (indexDef.IsUnique && pkList!.Count > 0)
                        {
                            throw new NovaException(ErrorCode.UniqueConstraintViolation, $"Duplicate key in unique index '{indexDef.IndexName}'");
                        }

                        pkList!.Add(pk);
                        break;
                    }
                }
            }

            // 注册提交动作：事务提交时持久化索引文件
            var persistName = indexDef.IndexName;
            tx.RegisterCommitAction(() => PersistSecondaryIndex(persistName));
        }
    }

    /// <summary>删除二级索引</summary>
    /// <param name="indexName">索引名</param>
    public void DropSecondaryIndex(String indexName)
    {
        lock (_lock)
        {
            _secondaryIndexes.Remove(indexName);
        }

        // 删除索引文件
        DeleteSecondaryIndexFile(indexName);
    }

    /// <summary>通过二级索引查询匹配的主键列表</summary>
    /// <param name="indexName">索引名</param>
    /// <param name="indexKeyValues">索引键值</param>
    /// <returns>匹配的主键值列表</returns>
    public List<Object>? LookupByIndex(String indexName, Object?[] indexKeyValues)
    {
        lock (_lock)
        {
            if (!_secondaryIndexes.TryGetValue(indexName, out var idx))
                return null;

            var indexKey = new ComparableObject(indexKeyValues.Length == 1 ? indexKeyValues[0]! : indexKeyValues);
            if (!idx.TryGetValue(indexKey, out var pkList))
                return null;

            var result = new List<Object>();
            foreach (var pk in pkList!)
                result.Add(pk.Value!);

            return result;
        }
    }

    /// <summary>获取索引定义的列序号数组</summary>
    private Int32[] GetColumnOrdinals(IndexDefinition indexDef)
    {
        var colOrdinals = new Int32[indexDef.Columns.Count];
        for (var i = 0; i < indexDef.Columns.Count; i++)
            colOrdinals[i] = _schema.GetColumnIndex(indexDef.Columns[i]);
        return colOrdinals;
    }

    /// <summary>构建索引键</summary>
    private static ComparableObject BuildIndexKey(Object?[] row, Int32[] colOrdinals)
    {
        if (colOrdinals.Length == 1)
            return new ComparableObject(row[colOrdinals[0]]!);

        // 联合索引：用数组作为键
        var keyValues = new Object?[colOrdinals.Length];
        for (var i = 0; i < colOrdinals.Length; i++)
            keyValues[i] = row[colOrdinals[i]];

        return new ComparableObject(keyValues);
    }

    /// <summary>向所有二级索引插入条目</summary>
    private void InsertIntoSecondaryIndexes(Object?[] row, ComparableObject pk)
    {
        foreach (var kvp in _secondaryIndexes)
        {
            var indexDef = _schema.GetIndex(kvp.Key);
            if (indexDef == null) continue;

            var colOrdinals = GetColumnOrdinals(indexDef);
            var indexKey = BuildIndexKey(row, colOrdinals);
            var idx = kvp.Value;

            if (!idx.TryGetValue(indexKey, out var pkList))
            {
                pkList = [];
                idx.Insert(indexKey, pkList);
            }
            else if (indexDef.IsUnique && pkList!.Count > 0)
            {
                throw new NovaException(ErrorCode.UniqueConstraintViolation, $"Duplicate key in unique index '{indexDef.IndexName}'");
            }

            pkList!.Add(pk);
        }
    }

    /// <summary>从所有二级索引删除条目</summary>
    private void RemoveFromSecondaryIndexes(Object?[] row, ComparableObject pk)
    {
        foreach (var kvp in _secondaryIndexes)
        {
            var indexDef = _schema.GetIndex(kvp.Key);
            if (indexDef == null) continue;

            var colOrdinals = GetColumnOrdinals(indexDef);
            var indexKey = BuildIndexKey(row, colOrdinals);
            var idx = kvp.Value;

            if (idx.TryGetValue(indexKey, out var pkList))
            {
                for (var i = pkList!.Count - 1; i >= 0; i--)
                {
                    if (CompareKeys(pkList[i], pk) == 0)
                    {
                        pkList.RemoveAt(i);
                        break;
                    }
                }

                if (pkList.Count == 0)
                    idx.Remove(indexKey);
            }
        }
    }

    /// <summary>比较两个索引键</summary>
    private static Int32 CompareKeys(ComparableObject a, ComparableObject b) => a.CompareTo(b);

    /// <summary>将二级索引持久化到 .idx 文件（全量重写）</summary>
    /// <param name="indexName">索引名称</param>
    private void PersistSecondaryIndex(String indexName)
    {
        var idxDef = _schema.GetIndex(indexName);
        if (idxDef == null) return;

        lock (_lock)
        {
            if (!_secondaryIndexes.TryGetValue(indexName, out var idx)) return;

            var filePath = _fileManager.GetSecondaryIndexFilePath(indexName);
            var colOrdinals = GetColumnOrdinals(idxDef);
            var pkCol = _schema.GetPrimaryKeyColumn()!;

            // 确定索引键数据类型（单列索引用列类型，组合索引编码为字符串）
            var indexKeyType = colOrdinals.Length == 1
                ? _schema.Columns[colOrdinals[0]].DataType
                : DataType.String;

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);

            // 写入文件头
            var header = new FileHeader
            {
                Version = 1,
                FileType = FileType.Index,
                PageSize = (UInt32)_options.PageSize,
                CreateTime = DateTime.Now
            };
            using var headerPk = header.ToPacket();
            if (headerPk.TryGetArray(out var segment))
                fs.Write(segment.Array!, segment.Offset, segment.Count);

            // 写入索引条目
            using var bw = new BinaryWriter(fs, System.Text.Encoding.UTF8, leaveOpen: true);

            var allEntries = idx.GetAll();
            bw.Write(allEntries.Count);

            foreach (var entry in allEntries)
            {
                // 编码索引键
                var keyBytes = _codec.Encode(entry.Key.Value, indexKeyType);
                bw.Write(keyBytes.Length);
                bw.Write(keyBytes);

                // 写入主键列表
                var pkList = entry.Value;
                bw.Write(pkList.Count);
                foreach (var pk in pkList)
                {
                    var pkBytes = _codec.Encode(pk.Value, pkCol.DataType);
                    bw.Write(pkBytes.Length);
                    bw.Write(pkBytes);
                }
            }
        }
    }

    /// <summary>删除二级索引的 .idx 文件</summary>
    /// <param name="indexName">索引名称</param>
    private void DeleteSecondaryIndexFile(String indexName)
    {
        var filePath = _fileManager.GetSecondaryIndexFilePath(indexName);
        if (File.Exists(filePath))
        {
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

        var len = w.WrittenCount;
        var result = new Byte[len];
        Buffer.BlockCopy(w.Buffer, 0, result, 0, len);
        return result;
    }

    /// <summary>反序列化行数据</summary>
    private Object?[] DeserializeRow(Byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var br = new BinaryReader(ms);

        var colCount = br.ReadInt32();
        var row = new Object?[colCount];

        for (var i = 0; i < colCount; i++)
        {
            var length = br.ReadInt32();
            var encoded = br.ReadBytes(length);
            var colDef = _schema.Columns[i];
            row[i] = _codec.Decode(encoded, 0, colDef.DataType);
        }

        return row;
    }

    #endregion

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _walWriter?.Dispose();
        _dataPager?.Dispose();
        _rowLogStream?.Dispose();
        _disposed = true;
    }
}
