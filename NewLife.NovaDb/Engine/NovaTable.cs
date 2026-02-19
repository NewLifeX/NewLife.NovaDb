using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using NewLife.NovaDb.Tx;
using NewLife.NovaDb.WAL;

namespace NewLife.NovaDb.Engine;

/// <summary>Nova 表实例，支持 MVCC 的单表引擎</summary>
public class NovaTable : IDisposable
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
    private readonly Object _lock = new();
    private Boolean _disposed;

    /// <summary>表架构</summary>
    public TableSchema Schema => _schema;

    /// <summary>数据库目录路径</summary>
    public String DbPath => _dbPath;

    /// <summary>表文件管理器</summary>
    public TableFileManager FileManager => _fileManager;

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

        // 初始化数据文件
        var dataPath = _fileManager.GetDataFilePath();
        _dataPager = new MmfPager(dataPath, _options.PageSize);
        _dataPager.Open();

        // 初始化 WAL
        if (_options.WalMode != WalMode.None)
        {
            var walPath = _fileManager.GetWalFilePath();
            _walWriter = new WalWriter(walPath, _options.WalMode);
            _walWriter.Open();
        }

        // 初始化主键索引
        _primaryIndex = new SkipList<ComparableObject, List<RowVersion>>();
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
                foreach (var ver in versions)
                {
                    if (ver.IsVisible(_txManager, tx.TxId))
                        throw new NovaException(ErrorCode.PrimaryKeyConflict, $"Primary key '{pkValue}' already exists");
                }
            }

            // 添加新版本
            versions.Add(rowVersion);

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
                        vers.Remove(rowVersion);
                    }
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
            foreach (var ver in versions)
            {
                if (ver.IsVisible(_txManager, tx.TxId))
                {
                    visibleVersion = ver;
                    break;
                }
            }

            if (visibleVersion == null)
                return null;

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
            foreach (var ver in versions)
            {
                if (ver.IsVisible(_txManager, tx.TxId))
                {
                    visibleVersion = ver;
                    break;
                }
            }

            if (visibleVersion == null)
                return false;

            // 标记旧版本为已删除
            var oldDeletedByTx = visibleVersion.DeletedByTx;
            visibleVersion.MarkDeleted(tx.TxId);

            // 创建新版本
            var payload = SerializeRow(newRow);
            var newVersion = new RowVersion(tx.TxId, key, payload);
            versions.Add(newVersion);

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
            tx.RegisterRollbackAction(() =>
            {
                lock (_lock)
                {
                    visibleVersion.DeletedByTx = oldDeletedByTx;
                    if (_primaryIndex.TryGetValue(comparableKey, out var vers))
                    {
                        vers.Remove(newVersion);
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
            foreach (var ver in versions)
            {
                if (ver.IsVisible(_txManager, tx.TxId))
                {
                    visibleVersion = ver;
                    break;
                }
            }

            if (visibleVersion == null)
                return false;

            // 标记为已删除
            var oldDeletedByTx = visibleVersion.DeletedByTx;
            visibleVersion.MarkDeleted(tx.TxId);

            // 记录 WAL
            if (_walWriter != null)
            {
                var record = new WalRecord
                {
                    RecordType = WalRecordType.UpdatePage,
                    TxId = tx.TxId,
                    PageId = 0,
                    Data = new Byte[0]
                };
                _walWriter.Write(record);
            }

            // 注册回滚动作
            tx.RegisterRollbackAction(() =>
            {
                lock (_lock)
                {
                    visibleVersion.DeletedByTx = oldDeletedByTx;
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
        }
    }

    #region 辅助

    /// <summary>序列化行数据</summary>
    private Byte[] SerializeRow(Object?[] row)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // 写入列数
        bw.Write(row.Length);

        // 写入每列的值
        for (var i = 0; i < row.Length; i++)
        {
            var colDef = _schema.Columns[i];
            var encoded = _codec.Encode(row[i], colDef.DataType);
            bw.Write(encoded.Length);
            bw.Write(encoded);
        }

        return ms.ToArray();
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
        _disposed = true;
    }
}
