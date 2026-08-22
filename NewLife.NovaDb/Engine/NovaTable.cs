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

    // 全文索引：索引名 → FullTextIndex（P2，内存驻留）
    private readonly Dictionary<String, FullTextIndex> _fullTextIndexes = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>全文索引集合</summary>
    public IReadOnlyDictionary<String, FullTextIndex> FullTextIndexes => _fullTextIndexes;

    /// <summary>数据版本号。每次写操作（Insert/Update/Delete）提交时递增，供物化视图增量捕获</summary>
    public Int64 DataVersion { get; private set; }

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

            // 唯一性预检查：在任何修改之前检测二级唯一索引冲突，保证异常路径零副作用（INSERT IGNORE 依赖此语义）
            if (_secondaryIndexes.Count > 0)
                CheckUniqueIndexConflicts(row, comparableKey);

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

            // 维护全文索引（内存驻留，P2）：提交时才更新，回滚时保持一致
            if (_fullTextIndexes.Count > 0)
            {
                var ftDocId = pkValue;
                tx.RegisterCommitAction(() => MaintainFullTextIndexes(ftDocId, row));
            }

            // 更新分片统计
            _shardManager.RecordWrite(DefaultShardId, payload.Length);

            // 注册提交动作：事务提交时才持久化到行日志
            var persistPayload = payload;
            tx.RegisterCommitAction(() => PersistPut(persistPayload));

            // 注册提交动作：递增数据版本号并触发变更事件（物化视图增量捕获）
            var changedPk = pkValue;
            tx.RegisterCommitAction(() =>
            {
                DataVersion++;
                OnDataChanged?.Invoke(changedPk);
            });

            // 记录 WAL（如果启用）
            if (_walWriter != null)
            {
                var record = new WalRecord
                {
                    RecordType = WalRecordType.UpdatePage,
                    TxId = tx.TxId,
                    PageId = 0,
                };
                _walWriter.Write(record, payload);
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

            // 唯一性预检查：在任何修改之前检测二级唯一索引冲突，保证异常路径零副作用
            if (_secondaryIndexes.Count > 0)
                CheckUniqueIndexConflicts(newRow, comparableKey);

            // 更新二级索引：先移除旧值，后面再插入新值
            Object?[]? oldRow = null;
            if (_secondaryIndexes.Count > 0 || _fullTextIndexes.Count > 0)
            {
                oldRow = DeserializeRow(visibleVersion.Payload!);
                if (_secondaryIndexes.Count > 0)
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

            // 注册提交动作：事务提交时才持久化新版本
            var persistPayload = payload;
            tx.RegisterCommitAction(() => PersistPut(persistPayload));

            // 注册提交动作：递增数据版本号并触发变更事件（物化视图增量捕获）
            var changedPk = key;
            tx.RegisterCommitAction(() =>
            {
                DataVersion++;
                OnDataChanged?.Invoke(changedPk);
            });

            // 记录 WAL
            if (_walWriter != null)
            {
                var record = new WalRecord
                {
                    RecordType = WalRecordType.UpdatePage,
                    TxId = tx.TxId,
                    PageId = 0,
                };
                _walWriter.Write(record, payload);
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

            // 注册提交动作：事务提交时才持久化删除记录
            var persistKey = key;
            tx.RegisterCommitAction(() => PersistDelete(persistKey));

            // 注册提交动作：递增数据版本号并触发变更事件（物化视图增量捕获）
            var changedPk = key;
            tx.RegisterCommitAction(() =>
            {
                DataVersion++;
                OnDataChanged?.Invoke(changedPk);
            });

            // 记录 WAL
            if (_walWriter != null)
            {
                var record = new WalRecord
                {
                    RecordType = WalRecordType.UpdatePage,
                    TxId = tx.TxId,
                    PageId = 0,
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

                        // 索引键列含 NULL 时跳过该行（MySQL 语义）
                        if (HasNullColumn(row, colOrdinals)) break;

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
                throw new NovaException(ErrorCode.InvalidArgument, $"Full-text index '{indexName}' already exists");

            var idx = new FullTextIndex(indexName, columns);
            _fullTextIndexes[indexName] = idx;

            var colOrdinals = columns.Select(c => _schema.GetColumnIndex(c)).ToArray();
            var pkCol = _schema.GetPrimaryKeyColumn()!;

            // 为已有数据构建索引
            using var tx = _txManager.BeginTransaction();
            foreach (var entry in _primaryIndex.GetAll())
            {
                foreach (var ver in entry.Value)
                {
                    if (ver.IsVisible(_txManager, tx.TxId))
                    {
                        var row = DeserializeRow(ver.Payload!);
                        var docId = row[pkCol.Ordinal]!;
                        var text = BuildFullText(row, colOrdinals);
                        idx.Add(docId, text);
                        break;
                    }
                }
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
                throw new NovaException(ErrorCode.InvalidArgument, $"Full-text index '{indexName}' not found");

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
                sb.Append(Convert.ToString(row[ord]));
            }
        }
        return sb.ToString();
    }

    #endregion

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
            if (kvp.Value.TryGetValue(indexKey, out var pkList) && pkList!.Count > 0)
            {
                // 若既有条目均属于当前主键（Update 场景唯一值未变），不视为冲突
                foreach (var existingPk in pkList)
                {
                    if (CompareKeys(existingPk, pk) != 0)
                        throw new NovaException(ErrorCode.UniqueConstraintViolation, $"Duplicate key in unique index '{indexDef.IndexName}'");
                }
            }
        }
    }

    /// <summary>向所有二级索引插入条目</summary>
    /// <remarks>唯一性冲突必须在此前通过 <see cref="CheckUniqueIndexConflicts"/> 预检查，本方法不再抛出唯一性异常</remarks>
    private void InsertIntoSecondaryIndexes(Object?[] row, ComparableObject pk)
    {
        foreach (var kvp in _secondaryIndexes)
        {
            var indexDef = _schema.GetIndex(kvp.Key);
            if (indexDef == null) continue;

            var colOrdinals = GetColumnOrdinals(indexDef);

            // 索引键列含 NULL 时跳过该行（MySQL 语义：NULL 值不进二级索引）
            if (HasNullColumn(row, colOrdinals)) continue;

            var indexKey = BuildIndexKey(row, colOrdinals);
            var idx = kvp.Value;

            if (!idx.TryGetValue(indexKey, out var pkList))
            {
                pkList = [];
                idx.Insert(indexKey, pkList);
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

            // 索引键列含 NULL 时该行未入索引，直接跳过
            if (HasNullColumn(row, colOrdinals)) continue;

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

    /// <summary>判断行在指定列序号上是否含 NULL 值</summary>
    private static Boolean HasNullColumn(Object?[] row, Int32[] colOrdinals)
    {
        foreach (var ord in colOrdinals)
        {
            if (row[ord] == null) return true;
        }
        return false;
    }

    /// <summary>比较两个索引键</summary>
    private static Int32 CompareKeys(ComparableObject a, ComparableObject b) => a.CompareTo(b);

    /// <summary>恢复二级索引（优先 .idx 文件，缺失或损坏时全表重建）</summary>
    /// <remarks>引擎启动加载已有表时调用，保证二级索引跨连接/重启可用</remarks>
    private void RestoreSecondaryIndexes()
    {
        foreach (var indexDef in _schema.Indexes)
        {
            var idx = TryLoadSecondaryIndexFile(indexDef) ?? RebuildSecondaryIndex(indexDef);
            if (idx != null)
                _secondaryIndexes[indexDef.IndexName] = idx;
        }
    }

    /// <summary>尝试从 .idx 文件加载二级索引</summary>
    /// <param name="indexDef">索引定义</param>
    /// <returns>加载成功的索引，文件缺失或损坏时返回 null</returns>
    private SkipList<ComparableObject, List<ComparableObject>>? TryLoadSecondaryIndexFile(IndexDefinition indexDef)
    {
        var filePath = _fileManager.GetSecondaryIndexFilePath(indexDef.IndexName);
        if (!File.Exists(filePath)) return null;

        var pkCol = _schema.GetPrimaryKeyColumn()!;
        var colOrdinals = GetColumnOrdinals(indexDef);
        var indexKeyType = colOrdinals.Length == 1
            ? _schema.Columns[colOrdinals[0]].DataType
            : DataType.String;

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < FileHeader.HeaderSize + 4) return null;

            var reader = new SpanReader(bytes, FileHeader.HeaderSize);
            var count = reader.ReadInt32();
            var idx = new SkipList<ComparableObject, List<ComparableObject>>();

            for (var i = 0; i < count; i++)
            {
                var keyLen = reader.ReadInt32();
                var key = _codec.Decode(bytes, reader.Position, indexKeyType);
                reader.Advance(keyLen);

                var pkCount = reader.ReadInt32();
                var pkList = new List<ComparableObject>();
                for (var j = 0; j < pkCount; j++)
                {
                    var pkLen = reader.ReadInt32();
                    var pk = _codec.Decode(bytes, reader.Position, pkCol.DataType);
                    reader.Advance(pkLen);
                    pkList.Add(new ComparableObject(pk!));
                }

                idx.Insert(new ComparableObject(key!), pkList);
            }

            return idx;
        }
        catch
        {
            // 索引文件损坏时回退到全表重建，保证表可加载
            return null;
        }
    }

    /// <summary>从主键索引全表扫描重建二级索引</summary>
    /// <param name="indexDef">索引定义</param>
    /// <returns>重建后的索引</returns>
    private SkipList<ComparableObject, List<ComparableObject>> RebuildSecondaryIndex(IndexDefinition indexDef)
    {
        var pkCol = _schema.GetPrimaryKeyColumn()!;
        var colOrdinals = GetColumnOrdinals(indexDef);
        var idx = new SkipList<ComparableObject, List<ComparableObject>>();

        // 启动恢复后，主键索引中每个主键仅有一个已提交版本
        foreach (var entry in _primaryIndex.GetAll())
        {
            foreach (var ver in entry.Value)
            {
                if (ver.Payload == null) continue;

                var row = DeserializeRow(ver.Payload);

                // 索引键列含 NULL 时跳过该行（MySQL 语义）
                if (HasNullColumn(row, colOrdinals)) break;

                var indexKey = BuildIndexKey(row, colOrdinals);
                var pk = new ComparableObject(row[pkCol.Ordinal]!);

                if (!idx.TryGetValue(indexKey, out var pkList))
                {
                    pkList = [];
                    idx.Insert(indexKey, pkList);
                }
                else if (indexDef.IsUnique)
                {
                    // 重建时重复键静默跳过（历史数据可能违反唯一约束），保证表可加载
                    break;
                }

                pkList!.Add(pk);
                break;
            }
        }

        return idx;
    }

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

            // 使用 SpanWriter + 流模式写入索引条目，避免 BinaryWriter 分配
            Span<Byte> spanBuf = stackalloc Byte[4096];
            var writer = new SpanWriter(spanBuf, fs);

            var allEntries = idx.GetAll();
            writer.Write(allEntries.Count);

            foreach (var entry in allEntries)
            {
                // 编码索引键
                var keyBytes = _codec.Encode(entry.Key.Value, indexKeyType);
                writer.Write(keyBytes.Length);
                writer.Write(keyBytes);

                // 写入主键列表
                var pkList = entry.Value;
                writer.Write(pkList.Count);
                foreach (var pk in pkList)
                {
                    var pkBytes = _codec.Encode(pk.Value, pkCol.DataType);
                    writer.Write(pkBytes.Length);
                    writer.Write(pkBytes);
                }
            }

            writer.Flush();
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

        return w.Buffer.AsSpan(0, w.WrittenCount).ToArray();
    }

    /// <summary>反序列化行数据</summary>
    private Object?[] DeserializeRow(Byte[] payload)
    {
        var reader = new SpanReader(payload);

        var colCount = reader.ReadInt32();
        var row = new Object?[colCount];

        for (var i = 0; i < colCount; i++)
        {
            var length = reader.ReadInt32();
            var colDef = _schema.Columns[i];
            // 检查 null 标记：长度为 1 且首字节为 NullFlag
            if (length == 1 && payload[reader.Position] == 0x00)
                row[i] = null;
            else
                row[i] = _codec.Decode(payload, reader.Position, colDef.DataType);
            reader.Advance(length);
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
