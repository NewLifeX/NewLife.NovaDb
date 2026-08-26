using System.Diagnostics;
using System.Threading;
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

    /// <summary>SQL 跟踪写文件同步锁（仅诊断用）</summary>
    private static readonly Object TraceSync = new();

    /// <summary>最近一次 INSERT 自增主键值（MySQL LAST_INSERT_ID 行为）</summary>
    internal Int64 LastInsertId;

    /// <summary>事务管理器</summary>
    public TransactionManager TxManager => _txManager;

    /// <summary>SQL 层会话事务上下文。BEGIN 开启后存入，COMMIT/ROLLBACK 结束后清空</summary>
    /// <remarks>
    /// 使用 AsyncLocal 按异步流隔离，支持同一引擎多连接并发（连接池场景）。
    /// net45 目标框架的 reference assemblies 缺失 AsyncLocal 类型，降级为 ThreadLocal（不跨异步流）。
    /// </remarks>
#if NET45
    private readonly ThreadLocal<Transaction?> _sessionTransaction = new();
#else
    private readonly AsyncLocal<Transaction?> _sessionTransaction = new();
#endif

    /// <summary>当前 SQL 会话事务（BEGIN 开启后有效）</summary>
    public Transaction? SessionTransaction => _sessionTransaction.Value;

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

    /// <summary>获取指定表的数据版本号（物化视图增量捕获用）</summary>
    /// <param name="tableName">表名</param>
    /// <returns>数据版本号，表不存在时返回 0</returns>
    public Int64 GetTableDataVersion(String tableName)
    {
        using var _ = _metaLock.AcquireRead();
        return _tables.TryGetValue(tableName, out var table) ? table.DataVersion : 0;
    }

    /// <summary>获取所有表的数据版本快照（物化视图增量捕获用）</summary>
    /// <returns>表名 → 数据版本号</returns>
    public Dictionary<String, Int64> GetDataVersionSnapshot()
    {
        using var _ = _metaLock.AcquireRead();
        var snapshot = new Dictionary<String, Int64>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _tables)
        {
            snapshot[kvp.Key] = kvp.Value.DataVersion;
        }
        return snapshot;
    }

    /// <summary>创建全文索引并为已有数据构建索引</summary>
    /// <param name="tableName">表名</param>
    /// <param name="indexName">索引名</param>
    /// <param name="columns">被索引的列名</param>
    public void CreateFullTextIndex(String tableName, String indexName, String[] columns)
    {
        using var _ = _metaLock.AcquireRead();
        if (!_tables.TryGetValue(tableName, out var table))
            throw new NovaException(ErrorCode.TableNotFound, $"Table '{tableName}' not found");

        table.CreateFullTextIndex(indexName, columns);
    }

    /// <summary>删除全文索引</summary>
    /// <param name="tableName">表名</param>
    /// <param name="indexName">索引名</param>
    public void DropFullTextIndex(String tableName, String indexName)
    {
        using var _ = _metaLock.AcquireRead();
        if (!_tables.TryGetValue(tableName, out var table))
            throw new NovaException(ErrorCode.TableNotFound, $"Table '{tableName}' not found");

        table.DropFullTextIndex(indexName);
    }

    /// <summary>全文检索，返回按 BM25 分数降序的 (主键, 分数) 列表</summary>
    /// <remarks>用于 RAG 混合检索：与向量检索结果融合排序。</remarks>
    /// <param name="tableName">表名</param>
    /// <param name="indexName">全文索引名</param>
    /// <param name="query">查询文本</param>
    /// <param name="topK">返回数量上限，默认 10</param>
    /// <returns>匹配的 (主键值, 分数) 列表</returns>
    public List<(Object PrimaryKey, Double Score)> SearchFullText(String tableName, String indexName, String query, Int32 topK = 10)
    {
        using var _ = _metaLock.AcquireRead();
        if (!_tables.TryGetValue(tableName, out var table))
            throw new NovaException(ErrorCode.TableNotFound, $"Table '{tableName}' not found");

        return table.SearchFullText(indexName, query, topK);
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
    /// <param name="tx">外部事务。为空时使用 SQL 会话事务或自动提交</param>
    /// <returns>执行结果</returns>
    public SqlResult Execute(String sql, Dictionary<String, Object?>? parameters = null, Transaction? tx = null)
    {
        if (sql == null) throw new ArgumentNullException(nameof(sql));

        var sw = Stopwatch.StartNew();

        // 开启环境变量 NovaDb_TraceSql=1 时，把每条 SQL 追加到临时跟踪文件，便于诊断
        if (Environment.GetEnvironmentVariable("NovaDb_TraceSql") == "1")
        {
            lock (TraceSync)
            {
                var tracePath = Path.Combine(Path.GetTempPath(), $"novadb_sql_trace_{System.Diagnostics.Process.GetCurrentProcess().Id}.txt");
                File.AppendAllText(tracePath, $"[{DateTime.Now:HH:mm:ss.fff}] {sql}{Environment.NewLine}");
            }
        }

        SqlParser parser;
        try
        {
            parser = new SqlParser(sql);
        }
        catch (Exception ex)
        {
            throw ParseError(sql, ex);
        }

        SqlResult? result = null;
        var statementIndex = 0;
        while (true)
        {
            SqlStatement stmt;
            try
            {
                stmt = parser.Parse();
            }
            catch (Exception ex)
            {
                throw ParseError(sql, ex);
            }

            result = ExecuteOne(stmt, sql, parameters, tx);

            // 多语句支持：分号分隔的后续语句顺序执行，返回最后一条语句结果（MySQL 语义）
            statementIndex++;
            var nextPos = parser.GetNextStatementPosition();
            if (nextPos < 0) break;

            sql = sql.Substring(nextPos);
            parser = new SqlParser(sql);
        }

        sw.Stop();

        // 记录慢查询
        SlowQuery.Record(sql, sw.ElapsedMilliseconds, result.AffectedRows);

        return result;
    }

    /// <summary>构造解析错误（可选转储 SQL）</summary>
    /// <param name="sql">SQL 文本</param>
    /// <param name="ex">原始异常</param>
    /// <returns>新异常</returns>
    private static NovaException ParseError(String sql, Exception ex)
    {
        // 解析失败时转储 SQL 便于诊断（仅当环境变量开启）
        if (Environment.GetEnvironmentVariable("NovaDb_DumpSql") == "1")
        {
            var dumpPath = Path.Combine(Path.GetTempPath(), $"novadb_sql_dump_{Guid.NewGuid():N}.txt");
            File.WriteAllText(dumpPath, sql);
            System.Diagnostics.Debug.WriteLine($"SQL dumped to {dumpPath}");
            return new NovaException(ErrorCode.SyntaxError, $"{ex.Message} (sql dumped to {dumpPath})", ex);
        }

        return ex as NovaException ?? new NovaException(ErrorCode.SyntaxError, ex.Message, ex);
    }

    /// <summary>执行单条 SQL 语句</summary>
    /// <param name="stmt">解析后的语句</param>
    /// <param name="sql">原始 SQL 文本</param>
    /// <param name="parameters">参数字典</param>
    /// <param name="tx">外部事务</param>
    /// <returns>执行结果</returns>
    private SqlResult ExecuteOne(SqlStatement stmt, String sql, Dictionary<String, Object?>? parameters, Transaction? tx)
    {
        // 事务控制语句
        if (stmt is TransactionStatement transactionStmt)
            return ExecuteTransactionStatement(transactionStmt);

        // Binlog 管理语句
        if (stmt is BinlogStatement binlogStmt)
        {
            return binlogStmt.StatementType switch
            {
                SqlStatementType.PurgeBinlogs => ExecutePurgeBinlogs(binlogStmt),
                SqlStatementType.FlushBinlogs => ExecuteFlushBinlogs(),
                _ => throw new NovaException(ErrorCode.NotSupported, $"Unsupported binlog statement: {binlogStmt.StatementType}")
            };
        }

        // 只读模式下拦截所有写操作
        if (_options.ReadOnly && stmt is not SelectStatement)
            throw new NovaException(ErrorCode.ReadOnlyViolation, "Database is opened in read-only mode, write operations are not allowed");

        // 未显式传入事务时，使用 SQL 会话事务（BEGIN 开启的）
        tx ??= _sessionTransaction.Value;

        return stmt switch
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
            InsertStatement insert => TrackInsert(ExecuteInsert(insert, parameters, tx), sql),
            ReplaceStatement replace => TrackInsert(ExecuteReplace(replace, parameters, tx), sql),
            UpsertStatement upsert => TrackInsert(ExecuteUpsert(upsert, parameters, tx), sql),
            MergeStatement merge => TrackInsert(ExecuteMerge(merge, parameters, tx), sql),
            UpdateStatement update => TrackUpdate(ExecuteUpdate(update, parameters, tx), sql),
            DeleteStatement delete => TrackDelete(ExecuteDelete(delete, parameters, tx), sql),

            // 查询语句
            SelectStatement select => TrackQuery(ExecuteSelect(select, parameters)),

            // 查询计划
            ExplainStatement explain => ExecuteExplain(explain, parameters),

            // SHOW 命令
            ShowStatement show => ExecuteShow(show),

            _ => throw new NovaException(ErrorCode.NotSupported, $"Unsupported statement type: {stmt.StatementType}")
        };
    }

    /// <summary>执行事务控制语句（BEGIN/COMMIT/ROLLBACK）</summary>
    /// <param name="stmt">事务语句</param>
    /// <returns>执行结果</returns>
    private SqlResult ExecuteTransactionStatement(TransactionStatement stmt)
    {
        switch (stmt.StatementType)
        {
            case SqlStatementType.BeginTransaction:
                if (_sessionTransaction.Value != null)
                    throw new NovaException(ErrorCode.TransactionError, "Transaction already in progress");
                _sessionTransaction.Value = _txManager.BeginTransaction();
                break;

            case SqlStatementType.CommitTransaction:
                var commitTx = _sessionTransaction.Value;
                if (commitTx == null)
                    throw new NovaException(ErrorCode.TransactionError, "No active transaction to commit");
                commitTx.Commit();
                _sessionTransaction.Value = null;
                Metrics.CommitCount++;
                break;

            case SqlStatementType.RollbackTransaction:
                var rollbackTx = _sessionTransaction.Value;
                if (rollbackTx == null)
                    throw new NovaException(ErrorCode.TransactionError, "No active transaction to rollback");
                rollbackTx.Rollback();
                _sessionTransaction.Value = null;
                Metrics.RollbackCount++;
                break;

            default:
                throw new NovaException(ErrorCode.NotSupported, $"Unsupported transaction statement: {stmt.StatementType}");
        }

        return new SqlResult { AffectedRows = 0 };
    }

    private SqlResult TrackDdl(SqlResult result, String sql) { Metrics.ExecuteCount++; Metrics.DdlCount++; _lastAffectedRows = result.AffectedRows; Binlog?.Write(BinlogEventType.Ddl, sql, result.AffectedRows); return result; }
    private SqlResult TrackQuery(SqlResult result) { Metrics.ExecuteCount++; Metrics.QueryCount++; _lastAffectedRows = result.AffectedRows; return result; }
    private SqlResult TrackInsert(SqlResult result, String sql) { Metrics.ExecuteCount++; Metrics.InsertCount++; Metrics.AddTotalRows(result.AffectedRows); _lastAffectedRows = result.AffectedRows; Binlog?.Write(BinlogEventType.Insert, sql, result.AffectedRows); return result; }
    private SqlResult TrackUpdate(SqlResult result, String sql) { Metrics.ExecuteCount++; Metrics.UpdateCount++; _lastAffectedRows = result.AffectedRows; Binlog?.Write(BinlogEventType.Update, sql, result.AffectedRows); return result; }
    private SqlResult TrackDelete(SqlResult result, String sql) { Metrics.ExecuteCount++; Metrics.DeleteCount++; Metrics.AddTotalRows(-result.AffectedRows); _lastAffectedRows = result.AffectedRows; Binlog?.Write(BinlogEventType.Delete, sql, result.AffectedRows); return result; }

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
