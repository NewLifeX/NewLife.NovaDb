using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Storage;
using NewLife.NovaDb.Tx;

namespace NewLife.NovaDb.Sql;

/// <summary>SQL 执行引擎，连接 SQL 解析器与表引擎</summary>
public class SqlEngine : IDisposable
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
            CreateTableStatement create => TrackDdl(ExecuteCreateTable(create)),
            DropTableStatement drop => TrackDdl(ExecuteDropTable(drop)),
            CreateIndexStatement createIdx => TrackDdl(ExecuteCreateIndex(createIdx)),
            DropIndexStatement dropIdx => TrackDdl(ExecuteDropIndex(dropIdx)),
            CreateDatabaseStatement createDb => TrackDdl(ExecuteCreateDatabase(createDb)),
            DropDatabaseStatement dropDb => TrackDdl(ExecuteDropDatabase(dropDb)),
            AlterTableStatement alter => TrackDdl(ExecuteAlterTable(alter)),
            TruncateTableStatement truncate => TrackDdl(ExecuteTruncateTable(truncate)),
            InsertStatement insert => TrackInsert(ExecuteInsert(insert, parameters)),
            UpdateStatement update => TrackUpdate(ExecuteUpdate(update, parameters)),
            DeleteStatement delete => TrackDelete(ExecuteDelete(delete, parameters)),
            SelectStatement select => TrackQuery(ExecuteSelect(select, parameters)),
            _ => throw new NovaException(ErrorCode.NotSupported, $"Unsupported statement type: {stmt.StatementType}")
        };
    }

    private SqlResult TrackDdl(SqlResult result) { Metrics.ExecuteCount++; Metrics.DdlCount++; _lastAffectedRows = result.AffectedRows; return result; }
    private SqlResult TrackQuery(SqlResult result) { Metrics.ExecuteCount++; Metrics.QueryCount++; _lastAffectedRows = result.AffectedRows; return result; }
    private SqlResult TrackInsert(SqlResult result) { Metrics.ExecuteCount++; Metrics.InsertCount++; _lastAffectedRows = result.AffectedRows; return result; }
    private SqlResult TrackUpdate(SqlResult result) { Metrics.ExecuteCount++; Metrics.UpdateCount++; _lastAffectedRows = result.AffectedRows; return result; }
    private SqlResult TrackDelete(SqlResult result) { Metrics.ExecuteCount++; Metrics.DeleteCount++; _lastAffectedRows = result.AffectedRows; return result; }

    #region DDL 执行

    private SqlResult ExecuteCreateTable(CreateTableStatement stmt)
    {
        lock (_lock)
        {
            if (_schemas.ContainsKey(stmt.TableName))
            {
                if (stmt.IfNotExists) return new SqlResult { AffectedRows = 0 };
                throw new NovaException(ErrorCode.TableExists, $"Table '{stmt.TableName}' already exists");
            }

            var schema = new TableSchema(stmt.TableName);

            // 设置引擎名称，未指定时默认为 Nova
            schema.EngineName = stmt.EngineName ?? "Nova";

            // 设置表注释
            if (stmt.Comment != null)
                schema.Comment = stmt.Comment;

            foreach (var colDef in stmt.Columns)
            {
                var dataType = ParseDataType(colDef.DataTypeName);
                var column = new ColumnDefinition(colDef.Name, dataType, !colDef.NotNull, colDef.IsPrimaryKey);
                if (colDef.Comment != null)
                    column.Comment = colDef.Comment;
                schema.AddColumn(column);
            }

            var table = new NovaTable(schema, _dbPath, _options, _txManager);

            _schemas[stmt.TableName] = schema;
            _tables[stmt.TableName] = table;

            return new SqlResult { AffectedRows = 0 };
        }
    }

    private SqlResult ExecuteDropTable(DropTableStatement stmt)
    {
        lock (_lock)
        {
            if (!_schemas.ContainsKey(stmt.TableName))
            {
                if (stmt.IfExists) return new SqlResult { AffectedRows = 0 };
                throw new NovaException(ErrorCode.TableNotFound, $"Table '{stmt.TableName}' not found");
            }

            if (_tables.TryGetValue(stmt.TableName, out var table))
            {
                table.Dispose();
                _tables.Remove(stmt.TableName);
            }

            _schemas.Remove(stmt.TableName);

            // 使用 TableFileManager 删除表的所有文件（平铺在数据库目录下）
            var fileManager = new TableFileManager(_dbPath, stmt.TableName, _options);
            try
            {
                fileManager.DeleteAllFiles();
            }
            catch
            {
                // 忽略文件系统错误
            }

            return new SqlResult { AffectedRows = 0 };
        }
    }

    private SqlResult ExecuteCreateIndex(CreateIndexStatement stmt)
    {
        lock (_lock)
        {
            if (!_schemas.ContainsKey(stmt.TableName))
                throw new NovaException(ErrorCode.TableNotFound, $"Table '{stmt.TableName}' not found");

            // 当前实现仅记录索引元数据，实际索引由 NovaTable 的 SkipList 主键索引处理
            return new SqlResult { AffectedRows = 0 };
        }
    }

    private SqlResult ExecuteDropIndex(DropIndexStatement stmt)
    {
        lock (_lock)
        {
            if (!_schemas.ContainsKey(stmt.TableName))
                throw new NovaException(ErrorCode.TableNotFound, $"Table '{stmt.TableName}' not found");

            return new SqlResult { AffectedRows = 0 };
        }
    }

    private SqlResult ExecuteCreateDatabase(CreateDatabaseStatement stmt)
    {
        var dbPath = Path.Combine(Path.GetDirectoryName(_dbPath) ?? ".", stmt.DatabaseName);
        if (Directory.Exists(dbPath))
        {
            if (stmt.IfNotExists) return new SqlResult { AffectedRows = 0 };
            throw new NovaException(ErrorCode.DatabaseExists, $"Database '{stmt.DatabaseName}' already exists");
        }

        Directory.CreateDirectory(dbPath);
        return new SqlResult { AffectedRows = 0 };
    }

    private SqlResult ExecuteDropDatabase(DropDatabaseStatement stmt)
    {
        var dbPath = Path.Combine(Path.GetDirectoryName(_dbPath) ?? ".", stmt.DatabaseName);
        if (!Directory.Exists(dbPath))
        {
            if (stmt.IfExists) return new SqlResult { AffectedRows = 0 };
            throw new NovaException(ErrorCode.DatabaseNotFound, $"Database '{stmt.DatabaseName}' not found");
        }

        Directory.Delete(dbPath, recursive: true);
        return new SqlResult { AffectedRows = 0 };
    }

    private SqlResult ExecuteAlterTable(AlterTableStatement stmt)
    {
        lock (_lock)
        {
            if (!_schemas.TryGetValue(stmt.TableName, out var schema))
                throw new NovaException(ErrorCode.TableNotFound, $"Table '{stmt.TableName}' not found");

            switch (stmt.Action)
            {
                case AlterTableAction.AddColumn:
                    {
                        var colDef = stmt.ColumnDef!;
                        var dataType = ParseDataType(colDef.DataTypeName);
                        var column = new ColumnDefinition(colDef.Name, dataType, !colDef.NotNull, colDef.IsPrimaryKey);
                        if (colDef.Comment != null)
                            column.Comment = colDef.Comment;
                        schema.AddColumn(column);
                        break;
                    }

                case AlterTableAction.ModifyColumn:
                    {
                        var colDef = stmt.ColumnDef!;
                        var dataType = ParseDataType(colDef.DataTypeName);
                        schema.ModifyColumn(colDef.Name, dataType, !colDef.NotNull, colDef.Comment);
                        break;
                    }

                case AlterTableAction.DropColumn:
                    schema.RemoveColumn(stmt.ColumnName!);
                    break;

                case AlterTableAction.AddTableComment:
                    schema.Comment = stmt.Comment;
                    break;

                case AlterTableAction.AddColumnComment:
                    {
                        var col = schema.GetColumn(stmt.ColumnName!);
                        col.Comment = stmt.Comment;
                        break;
                    }

                default:
                    throw new NovaException(ErrorCode.NotSupported, $"Unsupported ALTER TABLE action: {stmt.Action}");
            }

            return new SqlResult { AffectedRows = 0 };
        }
    }

    private SqlResult ExecuteTruncateTable(TruncateTableStatement stmt)
    {
        var table = GetTable(stmt.TableName);

        // 直接清空表数据，比逐行 DELETE 更快
        table.Truncate();

        return new SqlResult { AffectedRows = 0 };
    }

    #endregion

    #region DML 执行

    private SqlResult ExecuteInsert(InsertStatement stmt, Dictionary<String, Object?>? parameters)
    {
        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);

        using var tx = _txManager.BeginTransaction();
        var affectedRows = 0;

        foreach (var values in stmt.ValuesList)
        {
            var row = new Object?[schema.Columns.Count];

            if (stmt.Columns != null)
            {
                // 按指定列名填充
                for (var i = 0; i < stmt.Columns.Count; i++)
                {
                    var colIdx = schema.GetColumnIndex(stmt.Columns[i]);
                    row[colIdx] = EvaluateExpression(values[i], null, schema, parameters);
                }
            }
            else
            {
                // 按列序号填充
                if (values.Count != schema.Columns.Count)
                    throw new NovaException(ErrorCode.InvalidArgument,
                        $"INSERT values count ({values.Count}) does not match column count ({schema.Columns.Count})");

                for (var i = 0; i < values.Count; i++)
                {
                    row[i] = EvaluateExpression(values[i], null, schema, parameters);
                }
            }

            // 类型转换
            ConvertRowTypes(row, schema);

            table.Insert(tx, row);
            affectedRows++;
        }

        tx.Commit();
        return new SqlResult { AffectedRows = affectedRows };
    }

    private SqlResult ExecuteUpdate(UpdateStatement stmt, Dictionary<String, Object?>? parameters)
    {
        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);

        using var tx = _txManager.BeginTransaction();
        var allRows = table.GetAll(tx);
        var affectedRows = 0;

        foreach (var row in allRows)
        {
            if (stmt.Where != null && !EvaluateCondition(stmt.Where, row, schema, parameters))
                continue;

            // 构建新行
            var newRow = new Object?[schema.Columns.Count];
            Array.Copy(row, newRow, row.Length);

            // 应用 SET 子句
            foreach (var (column, value) in stmt.SetClauses)
            {
                var colIdx = schema.GetColumnIndex(column);
                newRow[colIdx] = EvaluateExpression(value, row, schema, parameters);
            }

            ConvertRowTypes(newRow, schema);

            // 获取主键值
            var pkCol = schema.GetPrimaryKeyColumn()!;
            var pkValue = row[pkCol.Ordinal]!;
            table.Update(tx, pkValue, newRow);
            affectedRows++;
        }

        tx.Commit();
        return new SqlResult { AffectedRows = affectedRows };
    }

    private SqlResult ExecuteDelete(DeleteStatement stmt, Dictionary<String, Object?>? parameters)
    {
        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);

        using var tx = _txManager.BeginTransaction();
        var allRows = table.GetAll(tx);
        var affectedRows = 0;

        foreach (var row in allRows)
        {
            if (stmt.Where != null && !EvaluateCondition(stmt.Where, row, schema, parameters))
                continue;

            var pkCol = schema.GetPrimaryKeyColumn()!;
            var pkValue = row[pkCol.Ordinal]!;
            if (table.Delete(tx, pkValue))
                affectedRows++;
        }

        tx.Commit();
        return new SqlResult { AffectedRows = affectedRows };
    }

    #endregion

    #region SELECT 执行

    private SqlResult ExecuteSelect(SelectStatement stmt, Dictionary<String, Object?>? parameters)
    {
        // 无表的 SELECT（如 SELECT 1）
        if (stmt.TableName == null)
        {
            return ExecuteSelectNoTable(stmt, parameters);
        }

        // 系统表查询
        if (stmt.TableName.StartsWith("_sys.", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteSystemTableQuery(stmt, parameters);
        }

        // JOIN 查询
        if (stmt.HasJoin)
        {
            return ExecuteSelectWithJoin(stmt, parameters);
        }

        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);

        using var tx = _txManager.BeginTransaction();

        // 1. 获取所有行
        var rows = table.GetAll(tx);

        // 2. WHERE 过滤
        if (stmt.Where != null)
        {
            rows = rows.Where(row => EvaluateCondition(stmt.Where, row, schema, parameters)).ToList();
        }

        // 3. GROUP BY
        if (stmt.GroupBy != null && stmt.GroupBy.Count > 0)
        {
            return ExecuteGroupBy(stmt, rows, schema, parameters);
        }

        // 4. 检查是否有聚合函数（无 GROUP BY）
        if (HasAggregateFunction(stmt))
        {
            return ExecuteAggregate(stmt, rows, schema, parameters);
        }

        // 5. ORDER BY
        if (stmt.OrderBy != null)
        {
            rows = ApplyOrderBy(rows, stmt.OrderBy, schema);
        }

        // 6. OFFSET / LIMIT
        if (stmt.OffsetValue.HasValue)
        {
            rows = rows.Skip(stmt.OffsetValue.Value).ToList();
        }
        if (stmt.Limit.HasValue)
        {
            rows = rows.Take(stmt.Limit.Value).ToList();
        }

        // 7. 投影
        return BuildSelectResult(stmt, rows, schema, parameters);
    }

    #region 系统表查询

    /// <summary>系统表前缀</summary>
    private const String SystemTablePrefix = "_sys.";

    private SqlResult ExecuteSystemTableQuery(SelectStatement stmt, Dictionary<String, Object?>? parameters)
    {
        var sysTableName = stmt.TableName!.Substring(SystemTablePrefix.Length).ToLower();

        // 构建虚拟 Schema 和行数据
        var (schema, rows) = sysTableName switch
        {
            "tables" => BuildSysTablesData(),
            "columns" => BuildSysColumnsData(),
            "indexes" => BuildSysIndexesData(),
            "metrics" => BuildSysMetricsData(),
            "version" => BuildSysVersionData(),
            _ => throw new NovaException(ErrorCode.TableNotFound, $"System table '{stmt.TableName}' not found")
        };

        // WHERE 过滤
        if (stmt.Where != null)
        {
            rows = rows.Where(row => EvaluateCondition(stmt.Where, row, schema, parameters)).ToList();
        }

        // ORDER BY
        if (stmt.OrderBy != null)
        {
            rows = ApplyOrderBy(rows, stmt.OrderBy, schema);
        }

        // OFFSET / LIMIT
        if (stmt.OffsetValue.HasValue)
        {
            rows = rows.Skip(stmt.OffsetValue.Value).ToList();
        }
        if (stmt.Limit.HasValue)
        {
            rows = rows.Take(stmt.Limit.Value).ToList();
        }

        // 投影
        return BuildSelectResult(stmt, rows, schema, parameters);
    }

    private (TableSchema Schema, List<Object?[]> Rows) BuildSysTablesData()
    {
        var schema = new TableSchema("_sys.tables");
        schema.AddColumn(new ColumnDefinition("name", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("column_count", DataType.Int32, false));
        schema.AddColumn(new ColumnDefinition("primary_key", DataType.String, true));
        schema.AddColumn(new ColumnDefinition("row_count", DataType.Int32, false));

        var rows = new List<Object?[]>();
        lock (_lock)
        {
            foreach (var kvp in _schemas)
            {
                var tableName = kvp.Key;
                var tableSchema = kvp.Value;
                var pkCol = tableSchema.GetPrimaryKeyColumn();

                var rowCount = 0;
                if (_tables.TryGetValue(tableName, out var table))
                {
                    using var tx = _txManager.BeginTransaction();
                    rowCount = table.GetAll(tx).Count;
                    tx.Commit();
                }

                rows.Add(new Object?[] { tableName, tableSchema.Columns.Count, pkCol?.Name, rowCount });
            }
        }

        return (schema, rows);
    }

    private (TableSchema Schema, List<Object?[]> Rows) BuildSysColumnsData()
    {
        var schema = new TableSchema("_sys.columns");
        schema.AddColumn(new ColumnDefinition("table_name", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("column_name", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("data_type", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("is_nullable", DataType.Boolean, false));
        schema.AddColumn(new ColumnDefinition("is_primary_key", DataType.Boolean, false));
        schema.AddColumn(new ColumnDefinition("ordinal_position", DataType.Int32, false));

        var rows = new List<Object?[]>();
        lock (_lock)
        {
            foreach (var kvp in _schemas)
            {
                var tableName = kvp.Key;
                var tableSchema = kvp.Value;

                foreach (var col in tableSchema.Columns)
                {
                    rows.Add(new Object?[]
                    {
                        tableName, col.Name, col.DataType.ToString(), col.Nullable, col.IsPrimaryKey, col.Ordinal
                    });
                }
            }
        }

        return (schema, rows);
    }

    private (TableSchema Schema, List<Object?[]> Rows) BuildSysIndexesData()
    {
        var schema = new TableSchema("_sys.indexes");
        schema.AddColumn(new ColumnDefinition("table_name", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("index_name", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("is_unique", DataType.Boolean, false));
        schema.AddColumn(new ColumnDefinition("columns", DataType.String, false));

        var rows = new List<Object?[]>();
        lock (_lock)
        {
            foreach (var kvp in _schemas)
            {
                var tableName = kvp.Key;
                var tableSchema = kvp.Value;
                var pkCol = tableSchema.GetPrimaryKeyColumn();

                // 主键索引始终存在
                if (pkCol != null)
                {
                    rows.Add(new Object?[]
                    {
                        tableName, $"pk_{tableName}", true, pkCol.Name
                    });
                }
            }
        }

        return (schema, rows);
    }

    private (TableSchema Schema, List<Object?[]> Rows) BuildSysMetricsData()
    {
        var schema = new TableSchema("_sys.metrics");
        schema.AddColumn(new ColumnDefinition("metric", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("value", DataType.String, false));

        // 更新表计数
        lock (_lock)
        {
            Metrics.TableCount = _schemas.Count;
        }

        var rows = new List<Object?[]>
        {
            new Object?[] { "table_count", Metrics.TableCount.ToString() },
            new Object?[] { "total_rows", Metrics.TotalRows.ToString() },
            new Object?[] { "execute_count", Metrics.ExecuteCount.ToString() },
            new Object?[] { "query_count", Metrics.QueryCount.ToString() },
            new Object?[] { "insert_count", Metrics.InsertCount.ToString() },
            new Object?[] { "update_count", Metrics.UpdateCount.ToString() },
            new Object?[] { "delete_count", Metrics.DeleteCount.ToString() },
            new Object?[] { "ddl_count", Metrics.DdlCount.ToString() },
            new Object?[] { "commit_count", Metrics.CommitCount.ToString() },
            new Object?[] { "rollback_count", Metrics.RollbackCount.ToString() },
            new Object?[] { "start_time", Metrics.StartTime.ToString("o") },
            new Object?[] { "uptime_seconds", Metrics.Uptime.TotalSeconds.ToString("F0") }
        };

        return (schema, rows);
    }

    private (TableSchema Schema, List<Object?[]> Rows) BuildSysVersionData()
    {
        var schema = new TableSchema("_sys.version");
        schema.AddColumn(new ColumnDefinition("version", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("platform", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("start_time", DataType.String, false));

        var version = NovaDbTool.GetVersion();
        var platform = Environment.Version.ToString();
        var startTime = Metrics.StartTime.ToString("o");

        var rows = new List<Object?[]>
        {
            new Object?[] { version, platform, startTime }
        };

        return (schema, rows);
    }

    #endregion

    private SqlResult ExecuteSelectNoTable(SelectStatement stmt, Dictionary<String, Object?>? parameters)
    {
        var result = new SqlResult();
        var colNames = new List<String>();
        var values = new List<Object?>();

        for (var i = 0; i < stmt.Columns.Count; i++)
        {
            var col = stmt.Columns[i];
            var val = EvaluateExpression(col.Expression, null, null, parameters);
            colNames.Add(col.Alias ?? $"col{i}");
            values.Add(val);
        }

        result.ColumnNames = colNames.ToArray();
        result.Rows.Add(values.ToArray());
        return result;
    }

    private SqlResult ExecuteSelectWithJoin(SelectStatement stmt, Dictionary<String, Object?>? parameters)
    {
        using var tx = _txManager.BeginTransaction();

        // 构建合并 Schema：左表 + 所有 JOIN 右表
        var leftSchema = GetSchema(stmt.TableName!);
        var leftTable = GetTable(stmt.TableName!);
        var leftAlias = stmt.TableAlias ?? stmt.TableName!;

        var leftRows = leftTable.GetAll(tx);

        // 表别名 → Schema 的映射
        var aliasSchemas = new Dictionary<String, TableSchema>(StringComparer.OrdinalIgnoreCase)
        {
            [leftAlias] = leftSchema,
            [stmt.TableName!] = leftSchema
        };

        // 合并 Schema 列名列表（含表前缀）
        var mergedColumns = new List<(String Alias, String Column, Int32 TableIndex, Int32 ColIndex)>();
        for (var i = 0; i < leftSchema.Columns.Count; i++)
            mergedColumns.Add((leftAlias, leftSchema.Columns[i].Name, 0, i));

        // 合并行数据：当前结果集
        var currentRows = leftRows.Select(r => r.ToArray() as Object?[]).ToList();

        // 依次处理每个 JOIN
        for (var joinIdx = 0; joinIdx < stmt.Joins!.Count; joinIdx++)
        {
            var join = stmt.Joins[joinIdx];
            var rightSchema = GetSchema(join.TableName);
            var rightTable = GetTable(join.TableName);
            var rightAlias = join.Alias ?? join.TableName;
            var rightRows = rightTable.GetAll(tx);

            aliasSchemas[rightAlias] = rightSchema;
            aliasSchemas[join.TableName] = rightSchema;

            var rightColStart = mergedColumns.Count;
            for (var i = 0; i < rightSchema.Columns.Count; i++)
                mergedColumns.Add((rightAlias, rightSchema.Columns[i].Name, joinIdx + 1, i));

            // Nested Loop Join
            var joinedRows = new List<Object?[]>();

            foreach (var leftRow in currentRows)
            {
                var matched = false;

                foreach (var rightRow in rightRows)
                {
                    // 合并为一行
                    var combined = new Object?[leftRow.Length + rightRow.Length];
                    Array.Copy(leftRow, 0, combined, 0, leftRow.Length);
                    Array.Copy(rightRow, 0, combined, leftRow.Length, rightRow.Length);

                    // 在合并行上求值 ON 条件
                    if (EvaluateJoinCondition(join.Condition, combined, mergedColumns, parameters))
                    {
                        joinedRows.Add(combined);
                        matched = true;
                    }
                }

                // LEFT JOIN：左行无匹配时补 NULL
                if (!matched && join.Type == JoinType.Left)
                {
                    var combined = new Object?[leftRow.Length + rightSchema.Columns.Count];
                    Array.Copy(leftRow, 0, combined, 0, leftRow.Length);
                    joinedRows.Add(combined);
                }
            }

            // RIGHT JOIN：右行无匹配时补 NULL
            if (join.Type == JoinType.Right)
            {
                var leftWidth = currentRows.Count > 0 ? currentRows[0].Length - rightSchema.Columns.Count : 0;
                foreach (var rightRow in rightRows)
                {
                    var hasMatch = false;
                    foreach (var leftRow in currentRows)
                    {
                        var combined = new Object?[leftRow.Length + rightRow.Length];
                        Array.Copy(leftRow, 0, combined, 0, leftRow.Length);
                        Array.Copy(rightRow, 0, combined, leftRow.Length, rightRow.Length);

                        if (EvaluateJoinCondition(join.Condition, combined, mergedColumns, parameters))
                        {
                            hasMatch = true;
                            break;
                        }
                    }

                    if (!hasMatch)
                    {
                        var combined = new Object?[leftWidth + rightRow.Length];
                        Array.Copy(rightRow, 0, combined, leftWidth, rightRow.Length);
                        joinedRows.Add(combined);
                    }
                }
            }

            currentRows = joinedRows;
        }

        // WHERE 过滤
        if (stmt.Where != null)
        {
            currentRows = currentRows.Where(row => EvaluateJoinCondition(stmt.Where, row, mergedColumns, parameters)).ToList();
        }

        // ORDER BY
        if (stmt.OrderBy != null)
        {
            currentRows = ApplyJoinOrderBy(currentRows, stmt.OrderBy, mergedColumns);
        }

        // OFFSET / LIMIT
        if (stmt.OffsetValue.HasValue)
            currentRows = currentRows.Skip(stmt.OffsetValue.Value).ToList();
        if (stmt.Limit.HasValue)
            currentRows = currentRows.Take(stmt.Limit.Value).ToList();

        // 投影
        return BuildJoinSelectResult(stmt, currentRows, mergedColumns, parameters);
    }

    private SqlResult ExecuteGroupBy(SelectStatement stmt, List<Object?[]> rows, TableSchema schema, Dictionary<String, Object?>? parameters)
    {
        // 按 GROUP BY 列分组
        var groups = new Dictionary<String, List<Object?[]>>();
        foreach (var row in rows)
        {
            var key = BuildGroupKey(row, stmt.GroupBy!, schema);
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
            }
            group.Add(row);
        }

        // HAVING 过滤
        var filteredGroups = groups.AsEnumerable();
        if (stmt.Having != null)
        {
            filteredGroups = filteredGroups.Where(g =>
            {
                var representative = g.Value[0];
                return EvaluateGroupCondition(stmt.Having, g.Value, representative, schema, parameters);
            });
        }

        // 构建结果
        var result = new SqlResult();
        var colNames = BuildColumnNames(stmt, schema);
        result.ColumnNames = colNames;

        foreach (var group in filteredGroups)
        {
            var representative = group.Value[0];
            var outputRow = new Object?[stmt.Columns.Count];

            for (var i = 0; i < stmt.Columns.Count; i++)
            {
                var col = stmt.Columns[i];
                outputRow[i] = EvaluateSelectExpression(col.Expression, group.Value, representative, schema, parameters);
            }

            result.Rows.Add(outputRow);
        }

        return result;
    }

    private SqlResult ExecuteAggregate(SelectStatement stmt, List<Object?[]> rows, TableSchema schema, Dictionary<String, Object?>? parameters)
    {
        var result = new SqlResult();
        var colNames = BuildColumnNames(stmt, schema);
        result.ColumnNames = colNames;

        var outputRow = new Object?[stmt.Columns.Count];
        for (var i = 0; i < stmt.Columns.Count; i++)
        {
            var col = stmt.Columns[i];
            outputRow[i] = EvaluateSelectExpression(col.Expression, rows, rows.Count > 0 ? rows[0] : null, schema, parameters);
        }

        result.Rows.Add(outputRow);
        return result;
    }

    private SqlResult BuildSelectResult(SelectStatement stmt, List<Object?[]> rows, TableSchema schema, Dictionary<String, Object?>? parameters)
    {
        var result = new SqlResult();

        if (stmt.IsSelectAll)
        {
            result.ColumnNames = schema.Columns.Select(c => c.Name).ToArray();
            result.Rows = rows;
        }
        else
        {
            result.ColumnNames = BuildColumnNames(stmt, schema);

            foreach (var row in rows)
            {
                var outputRow = new Object?[stmt.Columns.Count];
                for (var i = 0; i < stmt.Columns.Count; i++)
                {
                    var col = stmt.Columns[i];
                    outputRow[i] = EvaluateExpression(col.Expression, row, schema, parameters);
                }
                result.Rows.Add(outputRow);
            }
        }

        return result;
    }

    #endregion

    #region 表达式求值

    private Object? EvaluateExpression(SqlExpression expr, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        switch (expr)
        {
            case LiteralExpression lit:
                return lit.Value;

            case ColumnRefExpression colRef:
                if (row == null || schema == null)
                    throw new NovaException(ErrorCode.InvalidArgument, $"Column '{colRef.ColumnName}' cannot be evaluated without a row context");
                var idx = schema.GetColumnIndex(colRef.ColumnName);
                return row[idx];

            case ParameterExpression param:
                if (parameters == null || !parameters.TryGetValue(param.ParameterName, out var paramValue))
                    throw new NovaException(ErrorCode.InvalidArgument, $"Parameter '{param.ParameterName}' not found");
                return paramValue;

            case BinaryExpression binary:
                return EvaluateBinary(binary, row, schema, parameters);

            case UnaryExpression unary:
                return EvaluateUnary(unary, row, schema, parameters);

            case FunctionExpression func when func.IsAggregate:
                // 单行上下文中不应出现聚合函数
                throw new NovaException(ErrorCode.SyntaxError, $"Aggregate function {func.FunctionName} not allowed in this context");

            case FunctionExpression func when !func.IsAggregate:
                return EvaluateScalarFunction(func, row, schema, parameters);

            case CaseExpression caseExpr:
                return EvaluateCaseExpression(caseExpr, row, schema, parameters);

            case CastExpression castExpr:
                return EvaluateCastExpression(castExpr, row, schema, parameters);

            case IsNullExpression isNull:
                var operandVal = EvaluateExpression(isNull.Operand, row, schema, parameters);
                return isNull.IsNot ? operandVal != null : operandVal == null;

            default:
                throw new NovaException(ErrorCode.NotSupported, $"Unsupported expression type: {expr.ExprType}");
        }
    }

    private Object? EvaluateSelectExpression(SqlExpression expr, List<Object?[]> groupRows, Object?[]? representative, TableSchema schema, Dictionary<String, Object?>? parameters)
    {
        if (expr is FunctionExpression func && func.IsAggregate)
            return EvaluateAggregateFunction(func, groupRows, schema, parameters);

        return EvaluateExpression(expr, representative, schema, parameters);
    }

    private Object? EvaluateBinary(BinaryExpression binary, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        // 短路求值
        if (binary.Operator == BinaryOperator.And)
        {
            var leftVal = EvaluateExpression(binary.Left, row, schema, parameters);
            if (leftVal is Boolean lb && !lb) return false;
            var rightVal = EvaluateExpression(binary.Right, row, schema, parameters);
            return Convert.ToBoolean(leftVal) && Convert.ToBoolean(rightVal);
        }

        if (binary.Operator == BinaryOperator.Or)
        {
            var leftVal = EvaluateExpression(binary.Left, row, schema, parameters);
            if (leftVal is Boolean lb && lb) return true;
            var rightVal = EvaluateExpression(binary.Right, row, schema, parameters);
            return Convert.ToBoolean(leftVal) || Convert.ToBoolean(rightVal);
        }

        var left = EvaluateExpression(binary.Left, row, schema, parameters);
        var right = EvaluateExpression(binary.Right, row, schema, parameters);

        return binary.Operator switch
        {
            BinaryOperator.Equal => CompareValues(left, right) == 0,
            BinaryOperator.NotEqual => CompareValues(left, right) != 0,
            BinaryOperator.LessThan => CompareValues(left, right) < 0,
            BinaryOperator.GreaterThan => CompareValues(left, right) > 0,
            BinaryOperator.LessOrEqual => CompareValues(left, right) <= 0,
            BinaryOperator.GreaterOrEqual => CompareValues(left, right) >= 0,
            BinaryOperator.Add => ArithmeticOp(left, right, (a, b) => a + b),
            BinaryOperator.Subtract => ArithmeticOp(left, right, (a, b) => a - b),
            BinaryOperator.Multiply => ArithmeticOp(left, right, (a, b) => a * b),
            BinaryOperator.Divide => ArithmeticOp(left, right, (a, b) => b != 0 ? a / b : throw new DivideByZeroException()),
            BinaryOperator.Modulo => ArithmeticOp(left, right, (a, b) => b != 0 ? a % b : throw new DivideByZeroException()),
            BinaryOperator.Like => EvaluateLike(left, right),
            _ => throw new NovaException(ErrorCode.NotSupported, $"Unsupported operator: {binary.Operator}")
        };
    }

    private Object? EvaluateUnary(UnaryExpression unary, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        var operand = EvaluateExpression(unary.Operand, row, schema, parameters);

        return unary.Operator switch
        {
            "NOT" => !(Convert.ToBoolean(operand)),
            "-" => ArithmeticNegate(operand),
            _ => throw new NovaException(ErrorCode.NotSupported, $"Unsupported unary operator: {unary.Operator}")
        };
    }

    private Object? EvaluateAggregateFunction(FunctionExpression func, List<Object?[]> rows, TableSchema schema, Dictionary<String, Object?>? parameters)
    {
        switch (func.FunctionName)
        {
            case "COUNT":
                if (func.Arguments.Count > 0 && func.Arguments[0] is ColumnRefExpression colRef && colRef.ColumnName == "*")
                    return rows.Count;
                return rows.Count(r =>
                {
                    var val = EvaluateExpression(func.Arguments[0], r, schema, parameters);
                    return val != null;
                });

            case "SUM":
                var sum = 0.0;
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null) sum += Convert.ToDouble(val);
                }
                return sum;

            case "AVG":
                var total = 0.0;
                var count = 0;
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null)
                    {
                        total += Convert.ToDouble(val);
                        count++;
                    }
                }
                return count > 0 ? total / count : null;

            case "MIN":
                Object? minVal = null;
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null && (minVal == null || CompareValues(val, minVal) < 0))
                        minVal = val;
                }
                return minVal;

            case "MAX":
                Object? maxVal = null;
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null && (maxVal == null || CompareValues(val, maxVal) > 0))
                        maxVal = val;
                }
                return maxVal;

            case "STRING_AGG":
                var separator = func.Arguments.Count > 1
                    ? Convert.ToString(EvaluateExpression(func.Arguments[1], rows.Count > 0 ? rows[0] : null, schema, parameters)) ?? ","
                    : ",";
                var parts = new List<String>();
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null) parts.Add(Convert.ToString(val)!);
                }
                return String.Join(separator, parts);

            case "GROUP_CONCAT":
                var gcParts = new List<String>();
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null) gcParts.Add(Convert.ToString(val)!);
                }
                return String.Join(",", gcParts);

            case "STDDEV":
                var stdValues = new List<Double>();
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null) stdValues.Add(Convert.ToDouble(val));
                }
                if (stdValues.Count == 0) return null;
                var stdMean = stdValues.Average();
                var stdVariance = stdValues.Sum(v => (v - stdMean) * (v - stdMean)) / stdValues.Count;
                return Math.Sqrt(stdVariance);

            case "VARIANCE":
                var varValues = new List<Double>();
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null) varValues.Add(Convert.ToDouble(val));
                }
                if (varValues.Count == 0) return null;
                var varMean = varValues.Average();
                return varValues.Sum(v => (v - varMean) * (v - varMean)) / varValues.Count;

            default:
                throw new NovaException(ErrorCode.NotSupported, $"Unsupported aggregate function: {func.FunctionName}");
        }
    }

    private Boolean EvaluateCondition(SqlExpression expr, Object?[]? row, TableSchema schema, Dictionary<String, Object?>? parameters)
    {
        var result = EvaluateExpression(expr, row, schema, parameters);
        return result is Boolean b && b;
    }

    private Boolean EvaluateGroupCondition(SqlExpression expr, List<Object?[]> groupRows, Object?[]? representative, TableSchema schema, Dictionary<String, Object?>? parameters)
    {
        // 对 HAVING 中的聚合函数进行求值
        if (expr is BinaryExpression binary)
        {
            if (binary.Operator is BinaryOperator.And or BinaryOperator.Or)
            {
                var leftResult = EvaluateGroupCondition(binary.Left, groupRows, representative, schema, parameters);
                var rightResult = EvaluateGroupCondition(binary.Right, groupRows, representative, schema, parameters);
                return binary.Operator == BinaryOperator.And ? leftResult && rightResult : leftResult || rightResult;
            }

            var left = EvaluateSelectExpression(binary.Left, groupRows, representative, schema, parameters);
            var right = EvaluateSelectExpression(binary.Right, groupRows, representative, schema, parameters);

            return binary.Operator switch
            {
                BinaryOperator.Equal => CompareValues(left, right) == 0,
                BinaryOperator.NotEqual => CompareValues(left, right) != 0,
                BinaryOperator.LessThan => CompareValues(left, right) < 0,
                BinaryOperator.GreaterThan => CompareValues(left, right) > 0,
                BinaryOperator.LessOrEqual => CompareValues(left, right) <= 0,
                BinaryOperator.GreaterOrEqual => CompareValues(left, right) >= 0,
                _ => false
            };
        }

        return EvaluateCondition(expr, representative, schema, parameters);
    }

    /// <summary>求值标量函数</summary>
    private Object? EvaluateScalarFunction(FunctionExpression func, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        var args = new List<Object?>();
        foreach (var arg in func.Arguments)
        {
            args.Add(EvaluateExpression(arg, row, schema, parameters));
        }

        switch (func.FunctionName)
        {
            // 字符串函数
            case "CONCAT":
                return String.Concat(args.Select(a => Convert.ToString(a) ?? String.Empty));

            case "LENGTH" or "LEN":
                return args.Count > 0 && args[0] != null ? Convert.ToString(args[0])!.Length : (Object?)null;

            case "SUBSTRING" or "SUBSTR":
                if (args.Count < 2 || args[0] == null) return null;
                var subStr = Convert.ToString(args[0])!;
                var startPos = Convert.ToInt32(args[1]) - 1; // SQL 1-based
                if (startPos < 0) startPos = 0;
                if (startPos >= subStr.Length) return String.Empty;
                if (args.Count >= 3 && args[2] != null)
                {
                    var len = Convert.ToInt32(args[2]);
                    if (startPos + len > subStr.Length) len = subStr.Length - startPos;
                    return subStr.Substring(startPos, len);
                }
                return subStr.Substring(startPos);

            case "UPPER":
                return args.Count > 0 && args[0] != null ? Convert.ToString(args[0])!.ToUpper() : null;

            case "LOWER":
                return args.Count > 0 && args[0] != null ? Convert.ToString(args[0])!.ToLower() : null;

            case "TRIM":
                return args.Count > 0 && args[0] != null ? Convert.ToString(args[0])!.Trim() : null;

            case "LTRIM":
                return args.Count > 0 && args[0] != null ? Convert.ToString(args[0])!.TrimStart() : null;

            case "RTRIM":
                return args.Count > 0 && args[0] != null ? Convert.ToString(args[0])!.TrimEnd() : null;

            case "REPLACE":
                if (args.Count < 3 || args[0] == null) return args.Count > 0 ? args[0] : null;
                return Convert.ToString(args[0])!.Replace(
                    Convert.ToString(args[1]) ?? String.Empty,
                    Convert.ToString(args[2]) ?? String.Empty);

            case "LEFT":
                if (args.Count < 2 || args[0] == null) return null;
                var leftStr = Convert.ToString(args[0])!;
                var leftLen = Convert.ToInt32(args[1]);
                return leftLen >= leftStr.Length ? leftStr : leftStr.Substring(0, leftLen);

            case "RIGHT":
                if (args.Count < 2 || args[0] == null) return null;
                var rightStr = Convert.ToString(args[0])!;
                var rightLen = Convert.ToInt32(args[1]);
                return rightLen >= rightStr.Length ? rightStr : rightStr.Substring(rightStr.Length - rightLen);

            case "CHARINDEX" or "INSTR":
                if (args.Count < 2 || args[0] == null || args[1] == null) return 0;
                var ciNeedle = Convert.ToString(args[0])!;
                var ciHaystack = Convert.ToString(args[1])!;
                var ciIdx = ciHaystack.IndexOf(ciNeedle, StringComparison.OrdinalIgnoreCase);
                return ciIdx >= 0 ? ciIdx + 1 : 0; // 1-based

            case "REVERSE":
                if (args.Count < 1 || args[0] == null) return null;
                var revChars = Convert.ToString(args[0])!.ToCharArray();
                Array.Reverse(revChars);
                return new String(revChars);

            case "LPAD":
                if (args.Count < 2 || args[0] == null) return null;
                var lpadStr = Convert.ToString(args[0])!;
                var lpadLen = Convert.ToInt32(args[1]);
                var lpadChar = args.Count >= 3 && args[2] != null ? Convert.ToString(args[2])![0] : ' ';
                return lpadStr.PadLeft(lpadLen, lpadChar);

            case "RPAD":
                if (args.Count < 2 || args[0] == null) return null;
                var rpadStr = Convert.ToString(args[0])!;
                var rpadLen = Convert.ToInt32(args[1]);
                var rpadChar = args.Count >= 3 && args[2] != null ? Convert.ToString(args[2])![0] : ' ';
                return rpadStr.PadRight(rpadLen, rpadChar);

            // 数值函数
            case "ABS":
                return args.Count > 0 && args[0] != null ? Math.Abs(Convert.ToDouble(args[0])) : (Object?)null;

            case "ROUND":
                if (args.Count < 1 || args[0] == null) return null;
                var roundDecimals = args.Count >= 2 && args[1] != null ? Convert.ToInt32(args[1]) : 0;
                return Math.Round(Convert.ToDouble(args[0]), roundDecimals, MidpointRounding.AwayFromZero);

            case "CEILING" or "CEIL":
                return args.Count > 0 && args[0] != null ? Math.Ceiling(Convert.ToDouble(args[0])) : (Object?)null;

            case "FLOOR":
                return args.Count > 0 && args[0] != null ? Math.Floor(Convert.ToDouble(args[0])) : (Object?)null;

            case "MOD":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                var modB = Convert.ToDouble(args[1]);
                if (modB == 0) throw new DivideByZeroException();
                return Convert.ToDouble(args[0]) % modB;

            case "POWER" or "POW":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                return Math.Pow(Convert.ToDouble(args[0]), Convert.ToDouble(args[1]));

            case "SQRT":
                return args.Count > 0 && args[0] != null ? Math.Sqrt(Convert.ToDouble(args[0])) : (Object?)null;

            case "RAND" or "RANDOM":
                return new Random().NextDouble();

            case "SIGN":
                return args.Count > 0 && args[0] != null ? (Object)Math.Sign(Convert.ToDouble(args[0])) : null;

            case "TRUNCATE" or "TRUNC":
                if (args.Count < 1 || args[0] == null) return null;
                var truncDecimals = args.Count >= 2 && args[1] != null ? Convert.ToInt32(args[1]) : 0;
                var truncFactor = Math.Pow(10, truncDecimals);
                return Math.Truncate(Convert.ToDouble(args[0]) * truncFactor) / truncFactor;

            case "PI":
                return Math.PI;

            case "EXP":
                return args.Count > 0 && args[0] != null ? Math.Exp(Convert.ToDouble(args[0])) : (Object?)null;

            case "LOG" or "LN":
                return args.Count > 0 && args[0] != null ? Math.Log(Convert.ToDouble(args[0])) : (Object?)null;

            case "LOG10":
                return args.Count > 0 && args[0] != null ? Math.Log10(Convert.ToDouble(args[0])) : (Object?)null;

            // 日期时间函数
            case "NOW" or "GETDATE" or "CURRENT_TIMESTAMP":
                return DateTime.Now;

            case "YEAR":
                return args.Count > 0 && args[0] != null ? Convert.ToDateTime(args[0]).Year : (Object?)null;

            case "MONTH":
                return args.Count > 0 && args[0] != null ? Convert.ToDateTime(args[0]).Month : (Object?)null;

            case "DAY":
                return args.Count > 0 && args[0] != null ? Convert.ToDateTime(args[0]).Day : (Object?)null;

            case "HOUR":
                return args.Count > 0 && args[0] != null ? Convert.ToDateTime(args[0]).Hour : (Object?)null;

            case "MINUTE":
                return args.Count > 0 && args[0] != null ? Convert.ToDateTime(args[0]).Minute : (Object?)null;

            case "SECOND":
                return args.Count > 0 && args[0] != null ? Convert.ToDateTime(args[0]).Second : (Object?)null;

            case "DATEDIFF":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                return (Convert.ToDateTime(args[0]) - Convert.ToDateTime(args[1])).Days;

            case "DATEADD":
                if (args.Count < 3 || args[0] == null || args[1] == null || args[2] == null) return null;
                var daInterval = Convert.ToString(args[0])!.ToUpper();
                var daAmount = Convert.ToInt32(args[1]);
                var daDate = Convert.ToDateTime(args[2]);
                return daInterval switch
                {
                    "YEAR" => daDate.AddYears(daAmount),
                    "MONTH" => daDate.AddMonths(daAmount),
                    "DAY" => daDate.AddDays(daAmount),
                    "HOUR" => daDate.AddHours(daAmount),
                    "MINUTE" => daDate.AddMinutes(daAmount),
                    "SECOND" => daDate.AddSeconds(daAmount),
                    _ => throw new NovaException(ErrorCode.InvalidArgument, $"Unknown DATEADD interval: {daInterval}")
                };

            case "DATEPART":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                var dpPart = Convert.ToString(args[0])!.ToUpper();
                var dpDate = Convert.ToDateTime(args[1]);
                return dpPart switch
                {
                    "YEAR" => dpDate.Year,
                    "MONTH" => dpDate.Month,
                    "DAY" => dpDate.Day,
                    "HOUR" => dpDate.Hour,
                    "MINUTE" => dpDate.Minute,
                    "SECOND" => dpDate.Second,
                    "WEEKDAY" or "DAYOFWEEK" => (Int32)dpDate.DayOfWeek + 1,
                    _ => throw new NovaException(ErrorCode.InvalidArgument, $"Unknown DATEPART part: {dpPart}")
                };

            case "WEEKDAY" or "DAYOFWEEK":
                return args.Count > 0 && args[0] != null ? (Int32)Convert.ToDateTime(args[0]).DayOfWeek + 1 : (Object?)null;

            case "LAST_DAY":
                if (args.Count < 1 || args[0] == null) return null;
                var ldDate = Convert.ToDateTime(args[0]);
                return new DateTime(ldDate.Year, ldDate.Month, DateTime.DaysInMonth(ldDate.Year, ldDate.Month));

            case "DATE_FORMAT":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                var dfDate = Convert.ToDateTime(args[0]);
                var dfFormat = Convert.ToString(args[1])!
                    .Replace("%Y", "yyyy").Replace("%m", "MM").Replace("%d", "dd")
                    .Replace("%H", "HH").Replace("%i", "mm").Replace("%s", "ss")
                    .Replace("%M", "MMMM").Replace("%W", "dddd");
                return dfDate.ToString(dfFormat);

            case "TIMESTAMPDIFF":
                if (args.Count < 3 || args[0] == null || args[1] == null || args[2] == null) return null;
                var tdUnit = Convert.ToString(args[0])!.ToUpper();
                var tdDate1 = Convert.ToDateTime(args[1]);
                var tdDate2 = Convert.ToDateTime(args[2]);
                var tdDiff = tdDate2 - tdDate1;
                return tdUnit switch
                {
                    "YEAR" => tdDate2.Year - tdDate1.Year,
                    "MONTH" => (tdDate2.Year - tdDate1.Year) * 12 + (tdDate2.Month - tdDate1.Month),
                    "DAY" => (Int32)tdDiff.TotalDays,
                    "HOUR" => (Int64)tdDiff.TotalHours,
                    "MINUTE" => (Int64)tdDiff.TotalMinutes,
                    "SECOND" => (Int64)tdDiff.TotalSeconds,
                    _ => throw new NovaException(ErrorCode.InvalidArgument, $"Unknown TIMESTAMPDIFF unit: {tdUnit}")
                };

            // 类型转换函数
            case "CONVERT":
                if (args.Count < 2) return null;
                var convTypeName = Convert.ToString(args[0]) ?? String.Empty;
                return CastValue(args[1], convTypeName);

            case "COALESCE":
                foreach (var a in args)
                {
                    if (a != null) return a;
                }
                return null;

            case "ISNULL" or "IFNULL":
                if (args.Count < 2) return args.Count > 0 ? args[0] : null;
                return args[0] ?? args[1];

            case "NULLIF":
                if (args.Count < 2) return args.Count > 0 ? args[0] : null;
                return CompareValues(args[0], args[1]) == 0 ? null : args[0];

            // 条件函数
            case "IF" or "IIF":
                if (args.Count < 3) return null;
                var condResult = args[0] is Boolean bv ? bv : Convert.ToBoolean(args[0]);
                return condResult ? args[1] : args[2];

            // 系统函数
            case "DATABASE" or "CURRENT_DATABASE":
                return Path.GetFileName(_dbPath);

            case "VERSION":
                return "NovaDb 1.0";

            case "USER" or "CURRENT_USER":
                return "nova";

            case "CONNECTION_ID":
                return 0;

            case "ROW_COUNT":
                return _lastAffectedRows;

            case "LAST_INSERT_ID":
                return 0; // NovaDb 不支持自增主键，返回 0

            // 哈希函数
            case "MD5":
                if (args.Count < 1 || args[0] == null) return null;
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(Convert.ToString(args[0])!);
                    var hash = md5.ComputeHash(bytes);
                    return BitConverter.ToString(hash).Replace("-", String.Empty).ToLower();
                }

            case "SHA1":
                if (args.Count < 1 || args[0] == null) return null;
                using (var sha1 = System.Security.Cryptography.SHA1.Create())
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(Convert.ToString(args[0])!);
                    var hash = sha1.ComputeHash(bytes);
                    return BitConverter.ToString(hash).Replace("-", String.Empty).ToLower();
                }

            case "SHA2":
                if (args.Count < 1 || args[0] == null) return null;
                var sha2Bits = args.Count >= 2 && args[1] != null ? Convert.ToInt32(args[1]) : 256;
                using (var sha2 = sha2Bits switch
                {
                    384 => (System.Security.Cryptography.HashAlgorithm)System.Security.Cryptography.SHA384.Create(),
                    512 => System.Security.Cryptography.SHA512.Create(),
                    _ => System.Security.Cryptography.SHA256.Create()
                })
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(Convert.ToString(args[0])!);
                    var hash = sha2.ComputeHash(bytes);
                    return BitConverter.ToString(hash).Replace("-", String.Empty).ToLower();
                }

            default:
                throw new NovaException(ErrorCode.NotSupported, $"Unsupported function: {func.FunctionName}");

            // GeoPoint 函数
            case "GEOPOINT":
                if (args.Count < 2) throw new NovaException(ErrorCode.InvalidArgument, "GEOPOINT requires 2 arguments (lat, lon)");
                return new GeoPoint(Convert.ToDouble(args[0]), Convert.ToDouble(args[1]));

            case "DISTANCE":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                return ((GeoPoint)args[0]!).Distance((GeoPoint)args[1]!);

            case "WITHIN_RADIUS":
                if (args.Count < 3 || args[0] == null || args[1] == null || args[2] == null) return null;
                return ((GeoPoint)args[0]!).WithinRadius((GeoPoint)args[1]!, Convert.ToDouble(args[2]));

            // Vector 函数
            case "VECTOR":
                var vec = new Single[args.Count];
                for (var i = 0; i < args.Count; i++)
                {
                    vec[i] = Convert.ToSingle(args[i]);
                }
                return vec;

            case "COSINE_SIMILARITY":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                return CosineSimilarity((Single[])args[0]!, (Single[])args[1]!);

            case "EUCLIDEAN_DISTANCE":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                return EuclideanDistance((Single[])args[0]!, (Single[])args[1]!);

            case "DOT_PRODUCT":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                return DotProduct((Single[])args[0]!, (Single[])args[1]!);
        }
    }

    /// <summary>求值 CASE WHEN 表达式</summary>
    private Object? EvaluateCaseExpression(CaseExpression caseExpr, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        foreach (var (whenExpr, thenExpr) in caseExpr.WhenClauses)
        {
            var whenResult = EvaluateExpression(whenExpr, row, schema, parameters);
            if (whenResult is Boolean b && b)
                return EvaluateExpression(thenExpr, row, schema, parameters);
        }

        return caseExpr.ElseExpression != null
            ? EvaluateExpression(caseExpr.ElseExpression, row, schema, parameters)
            : null;
    }

    /// <summary>求值 CAST 表达式</summary>
    private Object? EvaluateCastExpression(CastExpression castExpr, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        var value = EvaluateExpression(castExpr.Operand, row, schema, parameters);
        return CastValue(value, castExpr.TargetTypeName);
    }

    /// <summary>将值转换为指定类型</summary>
    private static Object? CastValue(Object? value, String typeName)
    {
        if (value == null) return null;

        return typeName.ToUpper() switch
        {
            "INT" or "INT32" or "INTEGER" => Convert.ToInt32(value),
            "BIGINT" or "INT64" or "LONG" => Convert.ToInt64(value),
            "FLOAT" or "DOUBLE" or "REAL" => Convert.ToDouble(value),
            "DECIMAL" or "NUMERIC" => Convert.ToDecimal(value),
            "VARCHAR" or "TEXT" or "STRING" or "NVARCHAR" or "CHAR" => Convert.ToString(value),
            "BOOL" or "BOOLEAN" => Convert.ToBoolean(value),
            "DATETIME" or "TIMESTAMP" or "DATE" => Convert.ToDateTime(value),
            _ => throw new NovaException(ErrorCode.InvalidArgument, $"Unknown CAST target type: {typeName}")
        };
    }

    #endregion

    #region JOIN 辅助

    /// <summary>在合并行上对 JOIN 条件求值</summary>
    private Boolean EvaluateJoinCondition(SqlExpression expr, Object?[] combinedRow,
        List<(String Alias, String Column, Int32 TableIndex, Int32 ColIndex)> columns,
        Dictionary<String, Object?>? parameters)
    {
        var result = EvaluateJoinExpression(expr, combinedRow, columns, parameters);
        return result is Boolean b && b;
    }

    /// <summary>在合并行上对表达式求值（支持 table.column 前缀解析）</summary>
    private Object? EvaluateJoinExpression(SqlExpression expr, Object?[] combinedRow,
        List<(String Alias, String Column, Int32 TableIndex, Int32 ColIndex)> columns,
        Dictionary<String, Object?>? parameters)
    {
        switch (expr)
        {
            case LiteralExpression lit:
                return lit.Value;

            case ColumnRefExpression colRef:
                var colIdx = ResolveJoinColumnIndex(colRef, columns);
                return combinedRow[colIdx];

            case ParameterExpression param:
                if (parameters == null || !parameters.TryGetValue(param.ParameterName, out var paramValue))
                    throw new NovaException(ErrorCode.InvalidArgument, $"Parameter '{param.ParameterName}' not found");
                return paramValue;

            case BinaryExpression binary:
                // 短路求值
                if (binary.Operator == BinaryOperator.And)
                {
                    var lv = EvaluateJoinExpression(binary.Left, combinedRow, columns, parameters);
                    if (lv is Boolean lb && !lb) return false;
                    var rv = EvaluateJoinExpression(binary.Right, combinedRow, columns, parameters);
                    return Convert.ToBoolean(lv) && Convert.ToBoolean(rv);
                }
                if (binary.Operator == BinaryOperator.Or)
                {
                    var lv = EvaluateJoinExpression(binary.Left, combinedRow, columns, parameters);
                    if (lv is Boolean lb && lb) return true;
                    var rv = EvaluateJoinExpression(binary.Right, combinedRow, columns, parameters);
                    return Convert.ToBoolean(lv) || Convert.ToBoolean(rv);
                }

                var left = EvaluateJoinExpression(binary.Left, combinedRow, columns, parameters);
                var right = EvaluateJoinExpression(binary.Right, combinedRow, columns, parameters);

                return binary.Operator switch
                {
                    BinaryOperator.Equal => CompareValues(left, right) == 0,
                    BinaryOperator.NotEqual => CompareValues(left, right) != 0,
                    BinaryOperator.LessThan => CompareValues(left, right) < 0,
                    BinaryOperator.GreaterThan => CompareValues(left, right) > 0,
                    BinaryOperator.LessOrEqual => CompareValues(left, right) <= 0,
                    BinaryOperator.GreaterOrEqual => CompareValues(left, right) >= 0,
                    BinaryOperator.Add => ArithmeticOp(left, right, (a, b) => a + b),
                    BinaryOperator.Subtract => ArithmeticOp(left, right, (a, b) => a - b),
                    BinaryOperator.Multiply => ArithmeticOp(left, right, (a, b) => a * b),
                    BinaryOperator.Divide => ArithmeticOp(left, right, (a, b) => b != 0 ? a / b : throw new DivideByZeroException()),
                    BinaryOperator.Modulo => ArithmeticOp(left, right, (a, b) => b != 0 ? a % b : throw new DivideByZeroException()),
                    BinaryOperator.Like => EvaluateLike(left, right),
                    _ => throw new NovaException(ErrorCode.NotSupported, $"Unsupported operator: {binary.Operator}")
                };

            case UnaryExpression unary:
                var operand = EvaluateJoinExpression(unary.Operand, combinedRow, columns, parameters);
                return unary.Operator switch
                {
                    "NOT" => !(Convert.ToBoolean(operand)),
                    "-" => ArithmeticNegate(operand),
                    _ => throw new NovaException(ErrorCode.NotSupported, $"Unsupported unary operator: {unary.Operator}")
                };

            case IsNullExpression isNull:
                var val = EvaluateJoinExpression(isNull.Operand, combinedRow, columns, parameters);
                return isNull.IsNot ? val != null : val == null;

            default:
                throw new NovaException(ErrorCode.NotSupported, $"Unsupported expression type in JOIN: {expr.ExprType}");
        }
    }

    /// <summary>解析 JOIN 中的列引用（支持 table.column 和 无前缀）</summary>
    private static Int32 ResolveJoinColumnIndex(ColumnRefExpression colRef,
        List<(String Alias, String Column, Int32 TableIndex, Int32 ColIndex)> columns)
    {
        if (colRef.TablePrefix != null)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (String.Equals(columns[i].Alias, colRef.TablePrefix, StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(columns[i].Column, colRef.ColumnName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            throw new NovaException(ErrorCode.InvalidArgument, $"Column '{colRef.TablePrefix}.{colRef.ColumnName}' not found");
        }

        // 无表前缀：按列名匹配（如有歧义取第一个）
        for (var i = 0; i < columns.Count; i++)
        {
            if (String.Equals(columns[i].Column, colRef.ColumnName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        throw new NovaException(ErrorCode.InvalidArgument, $"Column '{colRef.ColumnName}' not found");
    }

    /// <summary>构建 JOIN 查询的结果投影</summary>
    private SqlResult BuildJoinSelectResult(SelectStatement stmt, List<Object?[]> rows,
        List<(String Alias, String Column, Int32 TableIndex, Int32 ColIndex)> columns,
        Dictionary<String, Object?>? parameters)
    {
        var result = new SqlResult();

        if (stmt.IsSelectAll)
        {
            result.ColumnNames = columns.Select(c => c.Column).ToArray();
            result.Rows = rows;
        }
        else
        {
            var colNames = new String[stmt.Columns.Count];
            for (var i = 0; i < stmt.Columns.Count; i++)
            {
                var col = stmt.Columns[i];
                if (col.Alias != null)
                    colNames[i] = col.Alias;
                else if (col.Expression is ColumnRefExpression cr)
                    colNames[i] = cr.ColumnName;
                else
                    colNames[i] = $"col{i}";
            }
            result.ColumnNames = colNames;

            foreach (var row in rows)
            {
                var outputRow = new Object?[stmt.Columns.Count];
                for (var i = 0; i < stmt.Columns.Count; i++)
                {
                    var col = stmt.Columns[i];
                    outputRow[i] = EvaluateJoinExpression(col.Expression, row, columns, parameters);
                }
                result.Rows.Add(outputRow);
            }
        }

        return result;
    }

    /// <summary>JOIN 结果的 ORDER BY</summary>
    private static List<Object?[]> ApplyJoinOrderBy(List<Object?[]> rows, List<OrderByClause> orderBy,
        List<(String Alias, String Column, Int32 TableIndex, Int32 ColIndex)> columns)
    {
        return rows.OrderBy(r => 0, Comparer<Int32>.Default)
            .ThenBy(r => r, new JoinOrderByComparer(orderBy, columns))
            .ToList();
    }

    #endregion

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

    #region 辅助

    private NovaTable GetTable(String tableName)
    {
        lock (_lock)
        {
            if (!_tables.TryGetValue(tableName, out var table))
                throw new NovaException(ErrorCode.TableNotFound, $"Table '{tableName}' not found");
            return table;
        }
    }

    private TableSchema GetSchema(String tableName)
    {
        lock (_lock)
        {
            if (!_schemas.TryGetValue(tableName, out var schema))
                throw new NovaException(ErrorCode.TableNotFound, $"Table '{tableName}' not found");
            return schema;
        }
    }

    /// <summary>获取表的架构定义</summary>
    /// <param name="tableName">表名</param>
    /// <returns>架构定义，不存在时返回 null</returns>
    public TableSchema? GetTableSchema(String tableName)
    {
        lock (_lock)
        {
            _schemas.TryGetValue(tableName, out var schema);
            return schema;
        }
    }

    private static DataType ParseDataType(String typeName)
    {
        return typeName.ToUpper() switch
        {
            "BOOL" or "BOOLEAN" => DataType.Boolean,
            "INT" or "INT32" or "INTEGER" => DataType.Int32,
            "BIGINT" or "INT64" or "LONG" => DataType.Int64,
            "FLOAT" or "DOUBLE" or "REAL" => DataType.Double,
            "DECIMAL" or "NUMERIC" => DataType.Decimal,
            "VARCHAR" or "TEXT" or "STRING" or "NVARCHAR" => DataType.String,
            "BINARY" or "VARBINARY" or "BYTES" or "BLOB" => DataType.Binary,
            "DATETIME" or "TIMESTAMP" or "DATE" => DataType.DateTime,
            "GEOPOINT" => DataType.GeoPoint,
            "VECTOR" => DataType.Vector,
            _ => throw new NovaException(ErrorCode.SyntaxError, $"Unknown data type: {typeName}")
        };
    }

    private static void ConvertRowTypes(Object?[] row, TableSchema schema)
    {
        for (var i = 0; i < row.Length; i++)
        {
            if (row[i] == null) continue;

            var colDef = schema.Columns[i];
            row[i] = ConvertValue(row[i]!, colDef.DataType);
        }
    }

    private static Object ConvertValue(Object value, DataType targetType)
    {
        return targetType switch
        {
            DataType.Boolean => Convert.ToBoolean(value),
            DataType.Int32 => Convert.ToInt32(value),
            DataType.Int64 => Convert.ToInt64(value),
            DataType.Double => Convert.ToDouble(value),
            DataType.Decimal => Convert.ToDecimal(value),
            DataType.String => Convert.ToString(value)!,
            DataType.DateTime => Convert.ToDateTime(value),
            DataType.Binary when value is Byte[] bytes => bytes,
            DataType.GeoPoint when value is GeoPoint gp => gp,
            DataType.Vector when value is Single[] vec => vec,
            _ => value
        };
    }

    private static Int32 CompareValues(Object? left, Object? right)
    {
        if (left == null && right == null) return 0;
        if (left == null) return -1;
        if (right == null) return 1;

        // 尝试数值比较
        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left).CompareTo(Convert.ToDouble(right));

        // 字符串比较
        return String.Compare(Convert.ToString(left), Convert.ToString(right), StringComparison.OrdinalIgnoreCase);
    }

    private static Boolean IsNumeric(Object? value) =>
        value is Int32 or Int64 or Double or Decimal or Single or Byte or Int16 or UInt32 or UInt64;

    /// <summary>计算两个向量的余弦相似度</summary>
    /// <param name="a">向量 A</param>
    /// <param name="b">向量 B</param>
    /// <returns>余弦相似度（-1 到 1）</returns>
    private static Double CosineSimilarity(Single[] a, Single[] b)
    {
        if (a.Length != b.Length)
            throw new NovaException(ErrorCode.InvalidArgument, $"Vector dimensions mismatch: {a.Length} vs {b.Length}");

        Double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (Double)b[i];
            normA += a[i] * (Double)a[i];
            normB += b[i] * (Double)b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator == 0 ? 0 : dot / denominator;
    }

    /// <summary>计算两个向量的欧氏距离</summary>
    /// <param name="a">向量 A</param>
    /// <param name="b">向量 B</param>
    /// <returns>欧氏距离</returns>
    private static Double EuclideanDistance(Single[] a, Single[] b)
    {
        if (a.Length != b.Length)
            throw new NovaException(ErrorCode.InvalidArgument, $"Vector dimensions mismatch: {a.Length} vs {b.Length}");

        Double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var diff = a[i] - (Double)b[i];
            sum += diff * diff;
        }

        return Math.Sqrt(sum);
    }

    /// <summary>计算两个向量的点积</summary>
    /// <param name="a">向量 A</param>
    /// <param name="b">向量 B</param>
    /// <returns>点积</returns>
    private static Double DotProduct(Single[] a, Single[] b)
    {
        if (a.Length != b.Length)
            throw new NovaException(ErrorCode.InvalidArgument, $"Vector dimensions mismatch: {a.Length} vs {b.Length}");

        Double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * (Double)b[i];
        }

        return sum;
    }

    private static Object? ArithmeticOp(Object? left, Object? right, Func<Double, Double, Double> op)
    {
        if (left == null || right == null) return null;
        return op(Convert.ToDouble(left), Convert.ToDouble(right));
    }

    private static Object? ArithmeticNegate(Object? value)
    {
        if (value == null) return null;
        return -Convert.ToDouble(value);
    }

    private static Object? EvaluateLike(Object? left, Object? right)
    {
        if (left == null || right == null) return false;

        var str = Convert.ToString(left)!;
        var pattern = Convert.ToString(right)!;

        // 简单 LIKE 实现：% 匹配任意字符序列, _ 匹配单个字符
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("%", ".*")
            .Replace("_", ".") + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(str, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static List<Object?[]> ApplyOrderBy(List<Object?[]> rows, List<OrderByClause> orderBy, TableSchema schema)
    {
        return rows.OrderBy(r => 0, Comparer<Int32>.Default)
            .ThenBy(r => r, new OrderByComparer(orderBy, schema))
            .ToList();
    }

    private static String BuildGroupKey(Object?[] row, List<String> groupByColumns, TableSchema schema)
    {
        var parts = new List<String>();
        foreach (var colName in groupByColumns)
        {
            var idx = schema.GetColumnIndex(colName);
            parts.Add(Convert.ToString(row[idx]) ?? "NULL");
        }
        return String.Join("|", parts);
    }

    private static String[] BuildColumnNames(SelectStatement stmt, TableSchema schema)
    {
        var names = new String[stmt.Columns.Count];
        for (var i = 0; i < stmt.Columns.Count; i++)
        {
            var col = stmt.Columns[i];
            if (col.Alias != null)
            {
                names[i] = col.Alias;
            }
            else if (col.Expression is ColumnRefExpression colRef)
            {
                names[i] = colRef.ColumnName;
            }
            else if (col.Expression is FunctionExpression func)
            {
                names[i] = func.FunctionName;
            }
            else
            {
                names[i] = $"col{i}";
            }
        }
        return names;
    }

    private static Boolean HasAggregateFunction(SelectStatement stmt)
    {
        foreach (var col in stmt.Columns)
        {
            if (col.Expression is FunctionExpression func && func.IsAggregate)
                return true;
        }
        return false;
    }

    /// <summary>ORDER BY 比较器</summary>
    private class OrderByComparer(List<OrderByClause> orderBy, TableSchema schema) : IComparer<Object?[]>
    {
        public Int32 Compare(Object?[]? x, Object?[]? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            foreach (var clause in orderBy)
            {
                var idx = schema.GetColumnIndex(clause.ColumnName);
                var cmp = CompareValues(x[idx], y[idx]);

                if (cmp != 0)
                    return clause.Descending ? -cmp : cmp;
            }

            return 0;
        }
    }

    /// <summary>JOIN 结果 ORDER BY 比较器</summary>
    private class JoinOrderByComparer(List<OrderByClause> orderBy,
        List<(String Alias, String Column, Int32 TableIndex, Int32 ColIndex)> columns) : IComparer<Object?[]>
    {
        public Int32 Compare(Object?[]? x, Object?[]? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            foreach (var clause in orderBy)
            {
                var idx = -1;
                for (var i = 0; i < columns.Count; i++)
                {
                    if (String.Equals(columns[i].Column, clause.ColumnName, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }

                if (idx < 0) continue;

                var cmp = CompareValues(x[idx], y[idx]);
                if (cmp != 0)
                    return clause.Descending ? -cmp : cmp;
            }

            return 0;
        }
    }

    #endregion
}

/// <summary>SQL 执行结果</summary>
public class SqlResult
{
    /// <summary>受影响行数（DDL/DML 语句）</summary>
    public Int32 AffectedRows { get; set; }

    /// <summary>列名（SELECT 语句）</summary>
    public String[]? ColumnNames { get; set; }

    /// <summary>结果行（SELECT 语句）</summary>
    public List<Object?[]> Rows { get; set; } = [];

    /// <summary>是否为查询结果</summary>
    public Boolean IsQuery => ColumnNames != null;

    /// <summary>获取标量值（第一行第一列）</summary>
    /// <returns>标量值</returns>
    public Object? GetScalar()
    {
        if (Rows.Count > 0 && Rows[0].Length > 0)
            return Rows[0][0];
        return null;
    }
}
