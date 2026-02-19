using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Tx;

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
    private readonly Object _lock = new();
    private Boolean _disposed;
    private Int32 _lastAffectedRows;

    /// <summary>事务管理器</summary>
    public TransactionManager TxManager => _txManager;

    /// <summary>数据库路径</summary>
    public String DbPath => _dbPath;

    /// <summary>运行时指标</summary>
    public NovaMetrics Metrics { get; }

    /// <summary>获取所有表名</summary>
    public IReadOnlyCollection<String> TableNames
    {
        get
        {
            lock (_lock)
            {
                return _schemas.Keys.ToList().AsReadOnly();
            }
        }
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

        // 只读模式下不自动创建目录
        if (!_options.ReadOnly && !Directory.Exists(_dbPath))
            Directory.CreateDirectory(_dbPath);
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

        var parser = new SqlParser(sql);
        var stmt = parser.Parse();

        // 只读模式下拦截所有写操作
        if (_options.ReadOnly && stmt is not SelectStatement)
            throw new NovaException(ErrorCode.ReadOnlyViolation, "Database is opened in read-only mode, write operations are not allowed");

        return stmt switch
        {
            // DDL 语句
            CreateDatabaseStatement createDb => TrackDdl(ExecuteCreateDatabase(createDb)),
            DropDatabaseStatement dropDb => TrackDdl(ExecuteDropDatabase(dropDb)),
            CreateTableStatement create => TrackDdl(ExecuteCreateTable(create)),
            DropTableStatement drop => TrackDdl(ExecuteDropTable(drop)),
            AlterTableStatement alter => TrackDdl(ExecuteAlterTable(alter)),
            TruncateTableStatement truncate => TrackDdl(ExecuteTruncateTable(truncate)),
            CreateIndexStatement createIdx => TrackDdl(ExecuteCreateIndex(createIdx)),
            DropIndexStatement dropIdx => TrackDdl(ExecuteDropIndex(dropIdx)),

            // DML 语句
            InsertStatement insert => TrackInsert(ExecuteInsert(insert, parameters)),
            UpsertStatement upsert => TrackInsert(ExecuteUpsert(upsert, parameters)),
            UpdateStatement update => TrackUpdate(ExecuteUpdate(update, parameters)),
            DeleteStatement delete => TrackDelete(ExecuteDelete(delete, parameters)),

            // 查询语句
            SelectStatement select => TrackQuery(ExecuteSelect(select, parameters)),

            _ => throw new NovaException(ErrorCode.NotSupported, $"Unsupported statement type: {stmt.StatementType}")
        };
    }

    private SqlResult TrackDdl(SqlResult result) { Metrics.ExecuteCount++; Metrics.DdlCount++; _lastAffectedRows = result.AffectedRows; return result; }
    private SqlResult TrackQuery(SqlResult result) { Metrics.ExecuteCount++; Metrics.QueryCount++; _lastAffectedRows = result.AffectedRows; return result; }
    private SqlResult TrackInsert(SqlResult result) { Metrics.ExecuteCount++; Metrics.InsertCount++; _lastAffectedRows = result.AffectedRows; return result; }
    private SqlResult TrackUpdate(SqlResult result) { Metrics.ExecuteCount++; Metrics.UpdateCount++; _lastAffectedRows = result.AffectedRows; return result; }
    private SqlResult TrackDelete(SqlResult result) { Metrics.ExecuteCount++; Metrics.DeleteCount++; _lastAffectedRows = result.AffectedRows; return result; }

    #region 释放
    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;

        lock (_lock)
        {
            foreach (var table in _tables.Values)
            {
                table.Dispose();
            }
            _tables.Clear();
            _schemas.Clear();
        }

        _disposed = true;
    }
    #endregion

    #endregion
}
