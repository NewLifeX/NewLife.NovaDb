using System.Diagnostics;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Storage;
using NewLife.NovaDb.Tx;
using NewLife.NovaDb.WAL;

namespace NewLife.NovaDb.Sql;

/// <summary>SQL 执行引擎，连接 SQL 解析器与表引擎</summary>
public partial class SqlEngine : IDisposable
{
    #region 属性
    private readonly String _dbPath;
    private readonly DbOptions _options;
    private readonly TransactionManager _txManager;
    private readonly Dictionary<String, NovaTable> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<String, TableSchema> _schemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly MetadataLock _metaLock = new();
    private Boolean _disposed;
    private Int32 _lastAffectedRows;

    /// <summary>最近一次 INSERT 自增主键值（MySQL LAST_INSERT_ID 行为）</summary>
    internal Int64 LastInsertId;

    /// <summary>事务管理器</summary>
    public TransactionManager TxManager => _txManager;

    /// <summary>数据库路径</summary>
    public String DbPath => _dbPath;

    /// <summary>运行时指标</summary>
    public NovaMetrics Metrics { get; }

    /// <summary>慢查询日志</summary>
    public SlowQueryLog SlowQuery { get; }

    /// <summary>Binlog 写入器（可选，启用后记录已提交的 SQL 变更）</summary>
    public BinlogWriter? Binlog { get; set; }

    /// <summary>获取所有表名</summary>
    public IReadOnlyCollection<String> TableNames
    {
        get
        {
            using var _ = _metaLock.AcquireRead();
            return _schemas.Keys.ToList().AsReadOnly();
        }
    }

    /// <summary>获取单张表的统计信息</summary>
    /// <param name="tableName">表名</param>
    /// <returns>表统计信息，不存在时返回 null</returns>
    public TableStats? GetTableStats(String tableName)
    {
        using var _ = _metaLock.AcquireRead();
        return GetTableStatsNoLock(tableName);
    }

    /// <summary>获取单张表的统计信息（调用方已持有读锁）</summary>
    private TableStats? GetTableStatsNoLock(String tableName)
    {
        if (!_schemas.TryGetValue(tableName, out var schema))
            return null;

        var stats = new TableStats
        {
            ColumnCount = schema.Columns.Count,
            // 主键算一个索引，加上二级索引
            IndexCount = (schema.PrimaryKeyIndex.HasValue ? 1 : 0) + schema.Indexes.Count,
            Comment = schema.Comment,
            EngineName = schema.EngineName,
        };

        if (_tables.TryGetValue(tableName, out var table))
        {
            var shards = table.Shards.GetAllShards();
            foreach (var shard in shards)
            {
                stats.TableRows += shard.RowCount;
                stats.DataLength += shard.SizeBytes;
            }
            if (shards.Count > 0)
                stats.CreateTime = shards.Min(s => s.CreatedAt);
        }

        return stats;
    }
    #endregion

    #region 构造
    /// <summary>创建 SQL 执行引擎</summary>
    /// <param name="dbPath">数据库路径</param>
    /// <param name="options">数据库选项</param>
    public SqlEngine(String dbPath, DbOptions? options = null)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _options = options ?? new DbOptions { Path = dbPath };
        _txManager = new TransactionManager();
        Metrics = new NovaMetrics { StartTime = DateTime.Now };
        SlowQuery = new SlowQueryLog();

        // 只读模式下不自动创建目录和元数据
        if (!_options.ReadOnly)
        {
            if (!Directory.Exists(_dbPath))
                Directory.CreateDirectory(_dbPath);

            // 确保数据库元数据文件 nova.db 存在
            var metaPath = Path.Combine(_dbPath, "nova.db");
            if (!File.Exists(metaPath))
            {
                var header = new FileHeader
                {
                    Version = 1,
                    FileType = FileType.Data,
                    PageSize = (UInt32)_options.PageSize,
                    CreateTime = DateTime.Now
                };

                using var pk = header.ToPacket();
                using var fs = new FileStream(metaPath, FileMode.Create, FileAccess.Write);
                pk.CopyTo(fs);
            }
        }

        // 加载已持久化的表架构，使表元数据可跨连接/重启复用
        LoadPersistedSchemas();
    }

    /// <summary>从磁盘加载持久化的表架构，并为每张表打开对应的 <see cref="NovaTable"/> 实例</summary>
    private void LoadPersistedSchemas()
    {
        if (!Directory.Exists(_dbPath)) return;

        var schemas = SchemaPersister.LoadAll(_dbPath);
        foreach (var schema in schemas)
        {
            if (_schemas.ContainsKey(schema.TableName)) continue;

            try
            {
                var table = new NovaTable(schema, _dbPath, _options, _txManager);
                _schemas[schema.TableName] = schema;
                _tables[schema.TableName] = table;
            }
            catch
            {
                // 单个表加载失败不影响其它表
            }
        }
    }
    #endregion

    #region 方法
    /// <summary>执行 SQL 语句并返回结果</summary>
    /// <param name="sql">SQL 文本</param>
    /// <param name="parameters">参数字典</param>
    /// <returns>执行结果</returns>
    public SqlResult Execute(String sql, Dictionary<String, Object?>? parameters = null)
    {
        if (sql == null) throw new ArgumentNullException(nameof(sql));

        var sw = Stopwatch.StartNew();

        var parser = new SqlParser(sql);
        var stmt = parser.Parse();

        // 只读模式下拦截所有写操作
        if (_options.ReadOnly && stmt is not SelectStatement)
            throw new NovaException(ErrorCode.ReadOnlyViolation, "Database is opened in read-only mode, write operations are not allowed");

        var result = stmt switch
        {
            // DDL 语句
            CreateDatabaseStatement createDb => TrackDdl(ExecuteCreateDatabase(createDb), sql),
            DropDatabaseStatement dropDb => TrackDdl(ExecuteDropDatabase(dropDb), sql),
            CreateTableStatement create => TrackDdl(ExecuteCreateTable(create), sql),
            DropTableStatement drop => TrackDdl(ExecuteDropTable(drop), sql),
            AlterTableStatement alter => TrackDdl(ExecuteAlterTable(alter), sql),
            TruncateTableStatement truncate => TrackDdl(ExecuteTruncateTable(truncate), sql),
            CreateIndexStatement createIdx => TrackDdl(ExecuteCreateIndex(createIdx), sql),
            DropIndexStatement dropIdx => TrackDdl(ExecuteDropIndex(dropIdx), sql),

            // DML 语句
            InsertStatement insert => TrackInsert(ExecuteInsert(insert, parameters), sql),
            UpsertStatement upsert => TrackInsert(ExecuteUpsert(upsert, parameters), sql),
            MergeStatement merge => TrackInsert(ExecuteMerge(merge, parameters), sql),
            UpdateStatement update => TrackUpdate(ExecuteUpdate(update, parameters), sql),
            DeleteStatement delete => TrackDelete(ExecuteDelete(delete, parameters), sql),

            // 查询语句
            SelectStatement select => TrackQuery(ExecuteSelect(select, parameters)),

            // 查询计划
            ExplainStatement explain => ExecuteExplain(explain, parameters),

            // SHOW 命令
            ShowStatement show => ExecuteShow(show),

            _ => throw new NovaException(ErrorCode.NotSupported, $"Unsupported statement type: {stmt.StatementType}")
        };

        sw.Stop();

        // 记录慢查询
        SlowQuery.Record(sql, sw.ElapsedMilliseconds, result.AffectedRows);

        return result;
    }

    private SqlResult TrackDdl(SqlResult result, String sql) { Metrics.ExecuteCount++; Metrics.DdlCount++; _lastAffectedRows = result.AffectedRows; Binlog?.Write(BinlogEventType.Ddl, sql, result.AffectedRows); return result; }
    private SqlResult TrackQuery(SqlResult result) { Metrics.ExecuteCount++; Metrics.QueryCount++; _lastAffectedRows = result.AffectedRows; return result; }
    private SqlResult TrackInsert(SqlResult result, String sql) { Metrics.ExecuteCount++; Metrics.InsertCount++; _lastAffectedRows = result.AffectedRows; Binlog?.Write(BinlogEventType.Insert, sql, result.AffectedRows); return result; }
    private SqlResult TrackUpdate(SqlResult result, String sql) { Metrics.ExecuteCount++; Metrics.UpdateCount++; _lastAffectedRows = result.AffectedRows; Binlog?.Write(BinlogEventType.Update, sql, result.AffectedRows); return result; }
    private SqlResult TrackDelete(SqlResult result, String sql) { Metrics.ExecuteCount++; Metrics.DeleteCount++; _lastAffectedRows = result.AffectedRows; Binlog?.Write(BinlogEventType.Delete, sql, result.AffectedRows); return result; }

    #region 释放
    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;

        using var _ = _metaLock.AcquireWrite();
        foreach (var table in _tables.Values)
        {
            table.Dispose();
        }
        _tables.Clear();
        _schemas.Clear();

        _disposed = true;
    }
    #endregion

    #endregion
}
