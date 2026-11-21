using System.Globalization;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Tx;

namespace NewLife.NovaDb.Sql;

partial class SqlEngine
{
    #region SELECT 执行

    private SqlResult ExecuteSelect(SelectStatement stmt, Dictionary<String, Object?>? parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

        // MySQL 兼容的 information_schema 视图（XCode 元数据访问入口）
        if (stmt.TableName.StartsWith("information_schema.", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteInformationSchemaQuery(stmt, parameters);
        }

        // JOIN 查询
        if (stmt.HasJoin)
        {
            return ExecuteSelectWithJoin(stmt, parameters, cancellationToken);
        }

        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);

        using var tx = _txManager.BeginTransaction();

        // ORDER BY 只在对原始行排序时才可能被索引顺序满足：分组/聚合后排序的是结果组，与表行顺序无关
        var orderRows = stmt.OrderBy != null && stmt.OrderBy.Count > 0 && !HasAggregateFunction(stmt)
            && (stmt.GroupBy == null || stmt.GroupBy.Count == 0);

        // 1. 访问计划：能走主键/二级索引就不全表扫描
        var plan = BuildTableAccessPlan(table, stmt.TableName, stmt.TableAlias, schema, stmt.Where, parameters,
            needsSort: orderRows, orderBy: orderRows ? stmt.OrderBy : null,
            fetchLimit: stmt.Limit.HasValue ? (Int64)(stmt.OffsetValue ?? 0) + stmt.Limit.Value : null);

        // 1.1 索引覆盖的 COUNT(*)：只按索引命中的主键统计可见行，不反序列化整行
        var indexOnlyCount = TryExecuteIndexOnlyCount(stmt, plan, table, tx, schema);
        if (indexOnlyCount != null) return indexOnlyCount;

        // 1.2 无条件的 COUNT(*)：直接数主键索引的存活条目，不反序列化整行
        if (stmt.Where == null && IsIndexOnlyCount(stmt) && table.TryCountRows(tx, out var totalRows))
        {
            var fastCount = new SqlResult
            {
                ColumnNames = BuildColumnNames(stmt, schema),
            };
            // 与聚合路径保持一致：COUNT(*) 以 Int32 返回
            fastCount.Rows.Add([(Int32)totalRows]);
            return fastCount;
        }

        // LIMIT 下推：无排序/分组/聚合且条件被访问路径完整覆盖时，只需取 offset+limit 行
        // 顺序扫描（主键/索引顺序）还能保证“边扫边停”的结果与排序后切片一致
        var maxRows = 0;
        if (stmt.Limit.HasValue
            && !HasAggregateFunction(stmt)
            && (stmt.GroupBy == null || stmt.GroupBy.Count == 0)
            && plan.CoversWhere)
        {
            // LIMIT 提前停止只允许「顺序确定」的路径：强制停止会改变无 ORDER BY 查询返回的行集合
            var canStopEarly = plan.ProvidesOrder
                || (stmt.OrderBy == null && plan.Kind is TableAccessKind.PrimaryKeyLookup or TableAccessKind.PrimaryKeyRange or TableAccessKind.FullScan);
            if (canStopEarly) maxRows = (stmt.OffsetValue ?? 0) + stmt.Limit.Value;
        }

        // 2. 取候选行（只反序列化语句引用到的列）
        var neededColumns = BuildNeededColumnMask(stmt, schema);
        var rows = FetchPlanRows(plan, table, tx, maxRows, cancellationToken, neededColumns);

        // 3. WHERE 过滤（索引只缩小候选集，语义仍以条件复核为准）
        if (stmt.Where != null)
        {
            rows = FilterRows(rows, stmt.Where, schema, parameters, cancellationToken);
        }

        // 4. GROUP BY
        if (stmt.GroupBy != null && stmt.GroupBy.Count > 0)
        {
            return ExecuteGroupBy(stmt, rows, schema, parameters, cancellationToken);
        }

        // 5. 检查是否有聚合函数（无 GROUP BY）
        if (HasAggregateFunction(stmt))
        {
            return ExecuteAggregate(stmt, rows, schema, parameters, cancellationToken);
        }

        // 6. ORDER BY：访问计划已按索引顺序产出时跳过内存排序
        if (stmt.OrderBy != null && stmt.OrderBy.Count > 0 && !plan.ProvidesOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows = ApplyOrderBy(rows, stmt.OrderBy, schema);
        }

        // 7. OFFSET / LIMIT
        if (stmt.OffsetValue.HasValue)
        {
            rows = rows.Skip(stmt.OffsetValue.Value).ToList();
        }
        if (stmt.Limit.HasValue)
        {
            rows = rows.Take(stmt.Limit.Value).ToList();
        }

        // 8. 投影
        return BuildSelectResult(stmt, rows, schema, parameters, cancellationToken);
    }

    /// <summary>逐行过滤候选行，每 1024 行检查一次取消令牌</summary>
    /// <param name="rows">候选行</param>
    /// <param name="condition">过滤条件</param>
    /// <param name="schema">表架构</param>
    /// <param name="parameters">参数字典</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>满足条件的行列表（保持原有顺序）</returns>
    private List<Object?[]> FilterRows(List<Object?[]> rows, SqlExpression condition, TableSchema schema, Dictionary<String, Object?>? parameters, CancellationToken cancellationToken)
    {
        var filtered = new List<Object?[]>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            if ((i & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();

            var row = rows[i];
            if (EvaluateCondition(condition, row, schema, parameters)) filtered.Add(row);
        }

        return filtered;
    }

    /// <summary>判断是否为可直接由索引回答的 COUNT(*)：单个 COUNT(*)，无分组/筛选/排序/分页/连接</summary>
    /// <param name="stmt">SELECT 语句</param>
    /// <returns>是否可走索引计数</returns>
    private static Boolean IsIndexOnlyCount(SelectStatement stmt)
    {
        if (stmt.Columns.Count != 1) return false;
        if (stmt.HasJoin) return false;
        if (stmt.GroupBy != null && stmt.GroupBy.Count > 0) return false;
        if (stmt.Having != null) return false;
        if (stmt.OrderBy != null && stmt.OrderBy.Count > 0) return false;
        if (stmt.Limit.HasValue || stmt.OffsetValue.HasValue) return false;

        var expr = stmt.Columns[0].Expression;
        if (expr is not FunctionExpression func || !func.IsAggregate) return false;
        if (!String.Equals(func.FunctionName, "COUNT", StringComparison.OrdinalIgnoreCase)) return false;
        if (func.Arguments.Count == 0) return true;

        return func.Arguments.Count == 1 && func.Arguments[0] is ColumnRefExpression col && col.ColumnName == "*";
    }

    /// <summary>用索引直接回答 COUNT(*)：索引命中的主键按 MVCC 可见性累计，不反序列化行数据</summary>
    /// <param name="stmt">SELECT 语句</param>
    /// <param name="plan">访问计划</param>
    /// <param name="table">表实例</param>
    /// <param name="tx">事务</param>
    /// <param name="schema">表架构</param>
    /// <returns>计数结果；条件不满足（如需要回表复核）时返回 null 走原路径</returns>
    private SqlResult? TryExecuteIndexOnlyCount(SelectStatement stmt, TableAccessPlan plan, NovaTable table, Transaction tx, TableSchema schema)
    {
        if (!IsIndexOnlyCount(stmt)) return null;
        if (plan.Kind != TableAccessKind.IndexLookup || plan.IndexName == null || !plan.CoversWhere) return null;

        // 索引文件被运维删除后已注销，必须回退逐行计数
        if (!table.HasSecondaryIndex(plan.IndexName)) return null;

        // 无并发写事务时二级索引条目与可见行一一对应，直接数条目，不必回表逐行判断可见性
        if (!_txManager.HasOtherActiveTransactions(tx.TxId))
        {
            var total = 0L;
            if (plan.IndexPrefixes.Count > 0)
            {
                foreach (var prefix in plan.IndexPrefixes)
                {
                    total += table.CountIndexPrefix(plan.IndexName, prefix,
                        plan.HasLower ? plan.Lower : null, plan.HasLower, plan.LowerInclusive,
                        plan.HasUpper ? plan.Upper : null, plan.HasUpper, plan.UpperInclusive);
                }
            }
            else
            {
                foreach (var key in plan.Keys)
                {
                    total += table.CountIndexKey(plan.IndexName, key);
                }
            }

            var fast = new SqlResult
            {
                ColumnNames = BuildColumnNames(stmt, schema),
            };
            fast.Rows.Add([total]);
            return fast;
        }

        var pks = plan.IndexMatches ?? LookupIndexMatches(table, plan);
        plan.IndexMatches = pks;

        var result = new SqlResult
        {
            ColumnNames = BuildColumnNames(stmt, schema),
        };
        result.Rows.Add([table.CountVisibleRows(tx, pks)]);

        return result;
    }

    #region 系统表查询

    /// <summary>系统表前缀</summary>
    private const String SystemTablePrefix = "_sys.";

    private SqlResult ExecuteSystemTableQuery(SelectStatement stmt, Dictionary<String, Object?>? parameters)
    {
        var sysTableName = stmt.TableName!.Substring(SystemTablePrefix.Length).ToLowerInvariant();

        // 构建虚拟 Schema 和行数据
        var (schema, rows) = sysTableName switch
        {
            "tables" => BuildSysTablesData(),
            "columns" => BuildSysColumnsData(),
            "indexes" => BuildSysIndexesData(),
            "metrics" => BuildSysMetricsData(),
            "stats" => BuildSysStatsData(),
            "version" => BuildSysVersionData(),
            "binlog" => BuildSysBinlogData(),
            "flux_compression" => BuildSysFluxCompressionData(),
            "table_files" => BuildSysTableFilesData(),
            _ => throw new NovaException(ErrorCode.TableNotFound, "System table '{0}' not found", stmt.TableName)
        };

        // WHERE 过滤
        if (stmt.Where != null)
        {
            rows = rows.Where(row => EvaluateCondition(stmt.Where, row, schema, parameters)).ToList();
        }

        // 聚合查询（COUNT/SUM/AVG 等），系统视图同样支持，便于 ORM 做存在性检查
        if (stmt.GroupBy is { Count: > 0 })
            return ExecuteGroupBy(stmt, rows, schema, parameters);
        if (HasAggregateFunction(stmt))
            return ExecuteAggregate(stmt, rows, schema, parameters);

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
        // NovaDb 原生字段
        schema.AddColumn(new ColumnDefinition("name", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("column_count", DataType.Int32, false));
        schema.AddColumn(new ColumnDefinition("primary_key", DataType.String, true));
        schema.AddColumn(new ColumnDefinition("row_count", DataType.Int32, false));
        // MySQL information_schema.tables 兼容字段（XCode 等会使用）
        schema.AddColumn(new ColumnDefinition("table_schema", DataType.String, true));
        schema.AddColumn(new ColumnDefinition("table_name", DataType.String, true));
        schema.AddColumn(new ColumnDefinition("table_rows", DataType.Int64, true));
        schema.AddColumn(new ColumnDefinition("table_comment", DataType.String, true));

        var dbName = GetCurrentDatabaseName();
        var rows = new List<Object?[]>();
        using var rl1 = _metaLock.AcquireRead();
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

                rows.Add(
                [
                    tableName,
                    tableSchema.Columns.Count,
                    pkCol?.Name,
                    rowCount,
                    dbName,
                    tableName,
                    (Int64)rowCount,
                    tableSchema.Comment
                ]);
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
        using var rl2 = _metaLock.AcquireRead();
        {
            foreach (var kvp in _schemas)
            {
                var tableName = kvp.Key;
                var tableSchema = kvp.Value;

                foreach (var col in tableSchema.Columns)
                {
                    rows.Add(
                    [
                        tableName, col.Name, DataTypeNames.Format(col.DataType, col.Length), col.Nullable, col.IsPrimaryKey, col.Ordinal
                    ]);
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
        using var rl3 = _metaLock.AcquireRead();
        {
            foreach (var kvp in _schemas)
            {
                var tableName = kvp.Key;
                var tableSchema = kvp.Value;
                var pkCol = tableSchema.GetPrimaryKeyColumn();

                // 主键索引始终存在
                if (pkCol != null)
                {
                    rows.Add(
                    [
                        tableName, $"pk_{tableName}", true, pkCol.Name
                    ]);
                }

                // 二级索引
                foreach (var idx in tableSchema.Indexes)
                {
                    rows.Add(
                    [
                        tableName, idx.IndexName, idx.IsUnique, String.Join(",", idx.Columns)
                    ]);
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
        using (var rl4 = _metaLock.AcquireRead())
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
            new Object?[] { "uptime_seconds", Metrics.Uptime.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) }
        };

        return (schema, rows);
    }

    private (TableSchema Schema, List<Object?[]> Rows) BuildSysStatsData()
    {
        var schema = new TableSchema("_sys.stats");
        schema.AddColumn(new ColumnDefinition("table_name", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("column_name", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("row_count", DataType.Int64, false));
        schema.AddColumn(new ColumnDefinition("ndv", DataType.Int64, false));
        schema.AddColumn(new ColumnDefinition("buckets", DataType.String, true));
        schema.AddColumn(new ColumnDefinition("stat_version", DataType.Int64, false));
        schema.AddColumn(new ColumnDefinition("analyzed_at", DataType.DateTime, false));

        var rows = new List<Object?[]>();
        var statistics = new List<TableStatistics>();
        using (var rl5 = _metaLock.AcquireRead())
        {
            // 表已删除时同步清理缓存，避免统计目录持有已释放的表实例
            lock (_statsLock)
            {
                var dropped = _statistics.Keys.Where(k => !_schemas.ContainsKey(k)).ToList();
                foreach (var name in dropped)
                    _statistics.Remove(name);
            }

            foreach (var tableName in _schemas.Keys)
            {
                var stats = GetCachedTableStatistics(tableName);
                if (stats != null) statistics.Add(stats);
            }
        }

        foreach (var stats in statistics)
        {
            foreach (var column in stats.Columns.Values)
            {
                rows.Add(
                [
                    stats.TableName,
                    column.ColumnName,
                    stats.RowCount,
                    column.DistinctCount,
                    FormatHistogram(column.Buckets),
                    stats.StatVersion,
                    stats.AnalyzedAt
                ]);
            }
        }

        return (schema, rows);
    }

    /// <summary>把等深直方图格式化为简表，例如 [(1:10),(2:10)]</summary>
    private static String FormatHistogram(List<HistogramBucket> buckets)
        => "[" + String.Join(",", buckets.Select(b => $"({FormatStatValue(b.UpperBound)}:{b.Count})")) + "]";

    /// <summary>统计值格式化，数值使用不变区域性避免小数点随语言变化</summary>
    private static String FormatStatValue(Object? value) => value switch
    {
        null => "NULL",
        DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? String.Empty,
    };

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

    private (TableSchema Schema, List<Object?[]> Rows) BuildSysBinlogData()
    {
        var schema = new TableSchema("_sys.binlog");
        schema.AddColumn(new ColumnDefinition("file_name", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("file_size", DataType.Int64, false));
        schema.AddColumn(new ColumnDefinition("current", DataType.Boolean, false));

        var rows = new List<Object?[]>();

        if (Binlog != null)
        {
            foreach (var (fileName, size) in Binlog.ListFiles())
            {
                var isCurrent = fileName.EndsWith($".{Binlog.FileIndex:D6}");
                rows.Add([fileName, size, isCurrent]);
            }
        }

        return (schema, rows);
    }

    private (TableSchema Schema, List<Object?[]> Rows) BuildSysFluxCompressionData()
    {
        var schema = new TableSchema("_sys.flux_compression");
        schema.AddColumn(new ColumnDefinition("metric", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("value", DataType.String, false));

        var rows = new List<Object?[]>();

        // 未使用消息队列时不存在 Flux 引擎，返回空结果
        var flux = FluxProvider?.Invoke();
        if (flux == null) return (schema, rows);

        var stats = flux.GetCompressionStats();
        rows.Add(["block_count", stats.BlockCount.ToString()]);
        rows.Add(["entry_count", stats.EntryCount.ToString()]);
        rows.Add(["column_count", stats.ColumnCount.ToString()]);
        rows.Add(["compressed_column_count", stats.CompressedColumnCount.ToString()]);
        rows.Add(["row_bytes", stats.RowBytes.ToString()]);
        rows.Add(["block_bytes", stats.BlockBytes.ToString()]);
        rows.Add(["compression_ratio", stats.CompressionRatio.ToString("F2", CultureInfo.InvariantCulture)]);
        rows.Add(["saved_ratio", stats.SavedRatio.ToString("F4", CultureInfo.InvariantCulture)]);

        return (schema, rows);
    }

    private (TableSchema Schema, List<Object?[]> Rows) BuildSysTableFilesData()
    {
        var schema = new TableSchema("_sys.table_files");
        schema.AddColumn(new ColumnDefinition("table_name", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("file_name", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("role", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("exists", DataType.Boolean, false));
        schema.AddColumn(new ColumnDefinition("size", DataType.Int64, false));
        schema.AddColumn(new ColumnDefinition("degraded", DataType.Boolean, false));

        var rows = new List<Object?[]>();

        using var _ = _metaLock.AcquireRead();
        foreach (var table in _tables.Values)
        {
            foreach (var file in table.GetFileStates())
            {
                rows.Add([file.TableName, file.FileName, file.RoleName, file.Exists, file.Size, file.Degraded]);
            }
        }

        return (schema, rows);
    }

    #endregion

    #region information_schema 视图

    /// <summary>information_schema 前缀</summary>
    private const String InformationSchemaPrefix = "information_schema.";

    /// <summary>执行 MySQL 兼容的 information_schema 视图查询</summary>
    /// <remarks>XCode 等元数据访问会先 SHOW TABLE STATUS，再查 information_schema.columns / statistics</remarks>
    private SqlResult ExecuteInformationSchemaQuery(SelectStatement stmt, Dictionary<String, Object?>? parameters)
    {
        var name = stmt.TableName!.Substring(InformationSchemaPrefix.Length).ToLowerInvariant();

        var (schema, rows) = name switch
        {
            "columns" => BuildInformationSchemaColumns(),
            "statistics" => BuildInformationSchemaStatistics(),
            "tables" => BuildInformationSchemaTables(),
            _ => throw new NovaException(ErrorCode.TableNotFound, "System view '{0}' not found", stmt.TableName)
        };

        if (stmt.Where != null)
            rows = rows.Where(row => EvaluateCondition(stmt.Where, row, schema, parameters)).ToList();

        // 聚合查询（COUNT/SUM/AVG 等），information_schema 同样支持，便于 ORM 做存在性检查
        if (stmt.GroupBy is { Count: > 0 })
            return ExecuteGroupBy(stmt, rows, schema, parameters);
        if (HasAggregateFunction(stmt))
            return ExecuteAggregate(stmt, rows, schema, parameters);

        if (stmt.OrderBy != null)
            rows = ApplyOrderBy(rows, stmt.OrderBy, schema);

        if (stmt.OffsetValue.HasValue)
            rows = rows.Skip(stmt.OffsetValue.Value).ToList();
        if (stmt.Limit.HasValue)
            rows = rows.Take(stmt.Limit.Value).ToList();

        return BuildSelectResult(stmt, rows, schema, parameters);
    }

    /// <summary>当前数据库名（取自数据目录名称）</summary>
    private String GetCurrentDatabaseName()
        => Path.GetFileName(_dbPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private (TableSchema Schema, List<Object?[]> Rows) BuildInformationSchemaTables()
    {
        var schema = new TableSchema("information_schema.tables");
        schema.AddColumn(new ColumnDefinition("TABLE_SCHEMA", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("TABLE_NAME", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("TABLE_TYPE", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("ENGINE", DataType.String, true));
        schema.AddColumn(new ColumnDefinition("TABLE_ROWS", DataType.Int64, true));
        schema.AddColumn(new ColumnDefinition("TABLE_COMMENT", DataType.String, true));

        var dbName = GetCurrentDatabaseName();
        var rows = new List<Object?[]>();
        using var _ = _metaLock.AcquireRead();
        foreach (var ts in _schemas.Values)
        {
            var stats = GetTableStatsNoLock(ts.TableName);
            rows.Add([dbName, ts.TableName, "BASE TABLE", ts.EngineName, (Object?)(stats?.TableRows ?? 0L), ts.Comment ?? String.Empty]);
        }
        return (schema, rows);
    }

    private (TableSchema Schema, List<Object?[]> Rows) BuildInformationSchemaColumns()
    {
        var schema = new TableSchema("information_schema.columns");
        schema.AddColumn(new ColumnDefinition("TABLE_SCHEMA", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("TABLE_NAME", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("COLUMN_NAME", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("ORDINAL_POSITION", DataType.Int32, false));
        schema.AddColumn(new ColumnDefinition("COLUMN_DEFAULT", DataType.String, true));
        schema.AddColumn(new ColumnDefinition("IS_NULLABLE", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("DATA_TYPE", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("CHARACTER_MAXIMUM_LENGTH", DataType.Int64, true));
        schema.AddColumn(new ColumnDefinition("NUMERIC_PRECISION", DataType.Int32, true));
        schema.AddColumn(new ColumnDefinition("NUMERIC_SCALE", DataType.Int32, true));
        schema.AddColumn(new ColumnDefinition("COLUMN_TYPE", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("COLUMN_KEY", DataType.String, true));
        schema.AddColumn(new ColumnDefinition("EXTRA", DataType.String, true));
        schema.AddColumn(new ColumnDefinition("COLUMN_COMMENT", DataType.String, true));

        var dbName = GetCurrentDatabaseName();
        var rows = new List<Object?[]>();
        using var _ = _metaLock.AcquireRead();
        foreach (var ts in _schemas.Values)
        {
            for (var i = 0; i < ts.Columns.Count; i++)
            {
                var col = ts.Columns[i];
                var (dataType, columnType, precision, scale, maxLength) = MapToSchemaType(col);
                rows.Add(
                [
                    dbName,                                                 // TABLE_SCHEMA
                    ts.TableName,                                           // TABLE_NAME
                    col.Name,                                               // COLUMN_NAME
                    i + 1,                                                  // ORDINAL_POSITION
                    (Object?)DBNull.Value,                                  // COLUMN_DEFAULT
                    col.Nullable ? "YES" : "NO",                            // IS_NULLABLE
                    dataType,                                               // DATA_TYPE
                    (Object?)(maxLength.HasValue ? maxLength.Value : DBNull.Value), // CHARACTER_MAXIMUM_LENGTH
                    (Object?)(precision.HasValue ? precision.Value : DBNull.Value), // NUMERIC_PRECISION
                    (Object?)(scale.HasValue ? scale.Value : DBNull.Value),         // NUMERIC_SCALE
                    columnType,                                             // COLUMN_TYPE
                    col.IsPrimaryKey ? "PRI" : String.Empty,                // COLUMN_KEY
                    col.IsAutoIncrement ? "auto_increment" : String.Empty,  // EXTRA
                    col.Comment ?? String.Empty                             // COLUMN_COMMENT
                ]);
            }
        }
        return (schema, rows);
    }

    private (TableSchema Schema, List<Object?[]> Rows) BuildInformationSchemaStatistics()
    {
        var schema = new TableSchema("information_schema.statistics");
        schema.AddColumn(new ColumnDefinition("TABLE_SCHEMA", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("TABLE_NAME", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("NON_UNIQUE", DataType.Int32, false));
        schema.AddColumn(new ColumnDefinition("INDEX_NAME", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("SEQ_IN_INDEX", DataType.Int32, false));
        schema.AddColumn(new ColumnDefinition("COLUMN_NAME", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("COLLATION", DataType.String, true));
        schema.AddColumn(new ColumnDefinition("CARDINALITY", DataType.Int64, true));
        schema.AddColumn(new ColumnDefinition("INDEX_TYPE", DataType.String, true));

        var dbName = GetCurrentDatabaseName();
        var rows = new List<Object?[]>();
        using var _ = _metaLock.AcquireRead();
        foreach (var ts in _schemas.Values)
        {
            // 已执行 ANALYZE 的表用真实 NDV 填充基数，未分析时为 NULL
            var stats = GetCachedTableStatistics(ts.TableName);

            // 主键
            var pk = ts.GetPrimaryKeyColumn();
            if (pk != null)
            {
                Object? cardinality = stats != null && stats.Indexes.TryGetValue("PRIMARY", out var pkStats) ? pkStats.KeyCount : null;
                rows.Add([dbName, ts.TableName, 0, "PRIMARY", 1, pk.Name, "A", cardinality, "BTREE"]);
            }

            // 二级索引
            foreach (var idx in ts.Indexes)
            {
                Object? cardinality = stats != null && stats.Indexes.TryGetValue(idx.IndexName, out var idxStats) ? idxStats.KeyCount : null;
                for (var i = 0; i < idx.Columns.Count; i++)
                {
                    rows.Add([dbName, ts.TableName, idx.IsUnique ? 0 : 1, idx.IndexName, i + 1, idx.Columns[i], "A", cardinality, "BTREE"]);
                }
            }
        }
        return (schema, rows);
    }

    /// <summary>把列类型映射为 information_schema 展示用的元数据。类型名使用规范名，长度为真实声明值</summary>
    /// <param name="col">列定义</param>
    /// <returns>规范类型名、带长度的类型名、数值精度、数值标度、字符/字节最大长度</returns>
    private static (String DataType, String ColumnType, Int32? Precision, Int32? Scale, Int64? MaxLength) MapToSchemaType(ColumnDefinition col)
    {
        var (precision, scale) = col.DataType switch
        {
            DataType.Boolean => ((Int32?)3, (Int32?)0),
            DataType.Int32 => (10, 0),
            DataType.Int64 => (19, 0),
            DataType.Double => (22, null),
            // DECIMAL 固定 128 位，(18,4) 只为与 ORM 默认值对齐，避免每次同步产生 ALTER
            DataType.Decimal => (18, 4),
            _ => (null, null)
        };

        var maxLength = col.Length > 0 && col.DataType is DataType.String or DataType.Binary ? (Int64?)col.Length : null;

        return (DataTypeNames.GetName(col.DataType).ToLowerInvariant(), DataTypeNames.Format(col.DataType, col.Length), precision, scale, maxLength);
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

    private SqlResult ExecuteSelectWithJoin(SelectStatement stmt, Dictionary<String, Object?>? parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var tx = _txManager.BeginTransaction();

        // 构建合并 Schema：左表 + 所有 JOIN 右表
        var leftSchema = GetSchema(stmt.TableName!);
        var leftTable = GetTable(stmt.TableName!);
        var leftAlias = stmt.TableAlias ?? stmt.TableName!;

        var leftRows = leftTable.GetAll(tx, cancellationToken);

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

        // 嵌套循环累计步数，用于按间隔检查取消
        var joinSteps = 0;

        // 依次处理每个 JOIN
        for (var joinIdx = 0; joinIdx < stmt.Joins!.Count; joinIdx++)
        {
            var join = stmt.Joins[joinIdx];
            var rightSchema = GetSchema(join.TableName);
            var rightTable = GetTable(join.TableName);
            var rightAlias = join.Alias ?? join.TableName;
            var rightRows = rightTable.GetAll(tx, cancellationToken);

            aliasSchemas[rightAlias] = rightSchema;
            aliasSchemas[join.TableName] = rightSchema;

            var rightColStart = mergedColumns.Count;
            for (var i = 0; i < rightSchema.Columns.Count; i++)
                mergedColumns.Add((rightAlias, rightSchema.Columns[i].Name, joinIdx + 1, i));

            // Nested Loop Join
            var joinedRows = new List<Object?[]>();

            var leftCount = 0;
            foreach (var leftRow in currentRows)
            {
                var matched = false;

                foreach (var rightRow in rightRows)
                {
                    // 每 1024 次内层循环检查一次取消，避免超大连接长时间不可中断
                    if ((++joinSteps & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();

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

                if ((++leftCount & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();

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
                // rightColStart 即为左侧列总宽度（右列起始位置 = 左侧列数）
                var leftWidth = rightColStart;
                foreach (var rightRow in rightRows)
                {
                    var hasMatch = false;
                    foreach (var leftRow in currentRows)
                    {
                        if ((++joinSteps & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();

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
            var filtered = new List<Object?[]>(currentRows.Count);
            for (var i = 0; i < currentRows.Count; i++)
            {
                if ((i & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();

                var row = currentRows[i];
                if (EvaluateJoinCondition(stmt.Where, row, mergedColumns, parameters)) filtered.Add(row);
            }
            currentRows = filtered;
        }

        // ORDER BY
        if (stmt.OrderBy != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentRows = ApplyJoinOrderBy(currentRows, stmt.OrderBy, mergedColumns);
        }

        // OFFSET / LIMIT
        if (stmt.OffsetValue.HasValue)
            currentRows = currentRows.Skip(stmt.OffsetValue.Value).ToList();
        if (stmt.Limit.HasValue)
            currentRows = currentRows.Take(stmt.Limit.Value).ToList();

        // 投影
        return BuildJoinSelectResult(stmt, currentRows, mergedColumns, parameters, cancellationToken);
    }

    private SqlResult ExecuteGroupBy(SelectStatement stmt, List<Object?[]> rows, TableSchema schema, Dictionary<String, Object?>? parameters, CancellationToken cancellationToken = default)
    {
        // 窗口分组状态（F17）：SESSION_WINDOW / STATE_WINDOW 需要按行序推进
        var states = CreateWindowStates(stmt);

        // 按 GROUP BY 列与窗口函数分组
        var groups = new Dictionary<String, List<Object?[]>>();
        var windowKeys = new Dictionary<String, Object?[]>();
        for (var i = 0; i < rows.Count; i++)
        {
            if ((i & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();

            var row = rows[i];
            var key = BuildGroupByKey(stmt, row, schema, parameters, states, out var keys);
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
                windowKeys[key] = keys;
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

        cancellationToken.ThrowIfCancellationRequested();

        // ORDER BY 作用于分组结果：按投影里的普通列（或同名原列）比较，聚合列别名排序不支持时保持分组顺序
        var groupList = filteredGroups.Select(g => new KeyValuePair<String, List<Object?[]>>(g.Key, g.Value)).ToList();
        if (stmt.OrderBy != null && stmt.OrderBy.Count > 0 && groupList.Count > 1)
            SortGroups(stmt, groupList, schema);

        // 构建结果
        var result = new SqlResult();
        var colNames = BuildColumnNames(stmt, schema);
        result.ColumnNames = colNames;

        var groupIndex = 0;
        foreach (var group in groupList)
        {
            if ((groupIndex++ & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();

            var representative = group.Value[0];
            var outputRow = new Object?[stmt.Columns.Count];
            var keys = windowKeys.TryGetValue(group.Key, out var k) ? k : null;

            for (var i = 0; i < stmt.Columns.Count; i++)
            {
                var col = stmt.Columns[i];

                // 窗口函数列直接取分组时确定的窗口键
                if (keys != null && col.Expression is FunctionExpression func)
                {
                    var wi = FindWindowIndex(stmt, func);
                    if (wi >= 0)
                    {
                        outputRow[i] = keys[wi];
                        continue;
                    }
                }

                outputRow[i] = EvaluateSelectExpression(col.Expression, group.Value, representative, schema, parameters);
            }

            result.Rows.Add(outputRow);
        }

        return result;
    }

    /// <summary>按 ORDER BY 对分组结果排序</summary>
    /// <param name="stmt">查询语句</param>
    /// <param name="groups">分组列表，原地排序</param>
    /// <param name="schema">表结构</param>
    /// <remarks>
    /// 只支持按分组键或投影中的普通列排序（如 <c>GROUP BY b ORDER BY b</c>）：
    /// 取分组内第一行（代表行）的值比较。聚合列的别名排序需要先算聚合值，
    /// 这里不重算，保持原分组顺序，避免出现"看起来排了但值不对"的结果。
    /// </remarks>
    private static void SortGroups(SelectStatement stmt, List<KeyValuePair<String, List<Object?[]>>> groups, TableSchema schema)
    {
        // ORDER BY 列 → 代表行中的列序号；解析不到时放弃排序
        var ordinals = new Int32[stmt.OrderBy!.Count];
        for (var i = 0; i < stmt.OrderBy.Count; i++)
        {
            var name = stmt.OrderBy[i].ColumnName;
            var ordinal = FindColumnOrdinal(schema, name);
            if (ordinal < 0)
            {
                // 投影里是普通列引用时，也能按该列排序（别名场景）
                var selectColumn = stmt.Columns.FirstOrDefault(c => String.Equals(c.Alias, name, StringComparison.OrdinalIgnoreCase)
                    || String.Equals((c.Expression as ColumnRefExpression)?.ColumnName, name, StringComparison.OrdinalIgnoreCase));
                if (selectColumn?.Expression is ColumnRefExpression columnRef)
                    ordinal = FindColumnOrdinal(schema, columnRef.ColumnName);

                if (ordinal < 0) return;
            }

            ordinals[i] = ordinal;
        }

        groups.Sort((left, right) =>
        {
            var leftRow = left.Value[0];
            var rightRow = right.Value[0];

            for (var i = 0; i < ordinals.Length; i++)
            {
                var ordinal = ordinals[i];
                var leftValue = ordinal < leftRow.Length ? leftRow[ordinal] : null;
                var rightValue = ordinal < rightRow.Length ? rightRow[ordinal] : null;
                var cmp = CompareValues(leftValue, rightValue);
                if (cmp != 0) return stmt.OrderBy[i].Descending ? -cmp : cmp;
            }

            return 0;
        });
    }

    #region 窗口分组（F17）
    /// <summary>创建窗口分组状态列表，与 GROUP BY 项按位置对齐</summary>
    /// <param name="stmt">查询语句</param>
    /// <returns>状态列表，无窗口函数时返回 null</returns>
    private static List<GroupByWindowStates?>? CreateWindowStates(SelectStatement stmt)
    {
        var windows = stmt.GroupByWindows;
        if (windows == null) return null;

        var states = new List<GroupByWindowStates?>(windows.Count);
        for (var i = 0; i < windows.Count; i++)
        {
            states.Add(windows[i] == null ? null : new GroupByWindowStates());
        }

        return states;
    }

    /// <summary>构建分组键，普通列与窗口函数混合时按位置拼接</summary>
    /// <param name="stmt">查询语句</param>
    /// <param name="row">数据行</param>
    /// <param name="schema">表结构</param>
    /// <param name="parameters">查询参数</param>
    /// <param name="states">窗口分组状态</param>
    /// <param name="windowKeys">输出各分组项的窗口键值</param>
    /// <returns>分组键字符串</returns>
    private String BuildGroupByKey(SelectStatement stmt, Object?[] row, TableSchema schema, Dictionary<String, Object?>? parameters, List<GroupByWindowStates?>? states, out Object?[] windowKeys)
    {
        var windows = stmt.GroupByWindows;
        var keys = windows == null ? [] : new Object?[windows.Count];
        windowKeys = keys;

        return states == null ? BuildGroupKey(row, stmt.GroupBy!, schema) : BuildWindowGroupKey(stmt, row, schema, parameters, states, keys);
    }

    /// <summary>构建含窗口函数的分组键，窗口状态按其余分组列隔离</summary>
    /// <param name="stmt">查询语句</param>
    /// <param name="row">数据行</param>
    /// <param name="schema">表结构</param>
    /// <param name="parameters">查询参数</param>
    /// <param name="states">窗口分组状态</param>
    /// <param name="keys">输出各分组项的窗口键值</param>
    /// <returns>分组键字符串</returns>
    private String BuildWindowGroupKey(SelectStatement stmt, Object?[] row, TableSchema schema, Dictionary<String, Object?>? parameters, List<GroupByWindowStates?> states, Object?[] keys)
    {
        var windows = stmt.GroupByWindows!;

        // 先计算普通分组列，作为窗口状态的分区键（同一设备/维度独立推进窗口）
        var parts = new List<String>(stmt.GroupBy!.Count);
        for (var i = 0; i < stmt.GroupBy.Count; i++)
        {
            if (windows[i] != null) continue;

            var idx = schema.GetColumnIndex(stmt.GroupBy[i]);
            parts.Add(FormatGroupValue(idx >= 0 && idx < row.Length ? row[idx] : null));
        }

        var partitionKey = String.Join("|", parts);

        for (var i = 0; i < stmt.GroupBy.Count; i++)
        {
            var window = windows[i];
            if (window == null) continue;

            var holder = states[i]!;
            if (!holder.States.TryGetValue(partitionKey, out var state))
            {
                state = new GroupByWindowState();
                holder.States[partitionKey] = state;
            }

            var value = EvaluateWindowKey(state, window, row, schema, parameters);
            keys[i] = value;
            parts.Add(FormatGroupValue(value));
        }

        return String.Join("|", parts);
    }

    /// <summary>推进窗口函数状态并返回当前窗口键</summary>
    /// <param name="state">窗口状态</param>
    /// <param name="expr">窗口函数表达式</param>
    /// <param name="row">数据行</param>
    /// <param name="schema">表结构</param>
    /// <param name="parameters">查询参数</param>
    /// <returns>当前窗口键：SESSION_WINDOW 返回窗口起始时间，STATE_WINDOW 返回窗口起始行时间或序号</returns>
    private Object? EvaluateWindowKey(GroupByWindowState state, SqlExpression expr, Object?[] row, TableSchema schema, Dictionary<String, Object?>? parameters)
    {
        if (expr is not FunctionExpression func) return null;

        if (func.FunctionName.EqualIgnoreCase("SESSION_WINDOW"))
        {
            if (func.Arguments.Count < 2) throw new NovaException(ErrorCode.InvalidArgument, "SESSION_WINDOW requires (time, gapSeconds)");

            var ts = EvaluateExpression(func.Arguments[0], row, schema, parameters);
            var gap = Convert.ToDouble(EvaluateExpression(func.Arguments[1], row, schema, parameters), CultureInfo.InvariantCulture);
            if (ts == null) return state.CurrentKey;

            if (!state.Initialized)
            {
                state.Initialized = true;
                state.LastValue = ts;
                state.CurrentKey = ts;
                return state.CurrentKey;
            }

            // 与上一行时间差超过间隔即开启新会话
            if (IsGapExceeded(ts, state.LastValue!, gap)) state.CurrentKey = ts;
            state.LastValue = ts;
            return state.CurrentKey;
        }

        // STATE_WINDOW：值变化即开启新窗口
        var value = func.Arguments.Count > 0 ? EvaluateExpression(func.Arguments[0], row, schema, parameters) : null;
        if (!state.Initialized || !Equals(value, state.LastValue))
        {
            state.Initialized = true;
            state.LastValue = value;
            state.Ordinal++;
            state.CurrentKey = func.Arguments.Count > 1 ? EvaluateExpression(func.Arguments[1], row, schema, parameters) ?? state.Ordinal : state.Ordinal;
        }

        return state.CurrentKey;
    }

    /// <summary>判断两行时间差是否超过窗口间隔</summary>
    /// <param name="current">当前行时间</param>
    /// <param name="last">上一行时间</param>
    /// <param name="gap">间隔：时间为 DateTime 时为秒，数值时间戳时与数值同单位</param>
    /// <returns>是否超过间隔</returns>
    private static Boolean IsGapExceeded(Object current, Object last, Double gap)
    {
        if (current is DateTime dt)
        {
            var lt = Convert.ToDateTime(last, CultureInfo.InvariantCulture);
            return Math.Abs((dt - lt).TotalSeconds) > gap;
        }

        return Math.Abs(Convert.ToDouble(current, CultureInfo.InvariantCulture) - Convert.ToDouble(last, CultureInfo.InvariantCulture)) > gap;
    }

    /// <summary>格式化分组键分量</summary>
    /// <param name="value">分量值</param>
    /// <returns>字符串</returns>
    private static String FormatGroupValue(Object? value) => value switch
    {
        null => "NULL",
        DateTime dt => dt.ToString("o"),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
    };

    /// <summary>查找 SELECT 列中的窗口函数在 GROUP BY 中的位置</summary>
    /// <param name="stmt">查询语句</param>
    /// <param name="func">函数表达式</param>
    /// <returns>位置索引，未匹配时返回 -1</returns>
    private static Int32 FindWindowIndex(SelectStatement stmt, FunctionExpression func)
    {
        var windows = stmt.GroupByWindows;
        if (windows == null) return -1;

        for (var i = 0; i < windows.Count; i++)
        {
            if (windows[i] is FunctionExpression gf && gf.FunctionName.EqualIgnoreCase(func.FunctionName)) return i;
        }

        return -1;
    }

    #endregion

    private SqlResult ExecuteAggregate(SelectStatement stmt, List<Object?[]> rows, TableSchema schema, Dictionary<String, Object?>? parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

    /// <summary>GROUP BY 窗口函数的分组状态容器，按其余分组列隔离（F17）</summary>
    private sealed class GroupByWindowStates
    {
        /// <summary>各分组维度的窗口状态，键为普通分组列拼接的字符串</summary>
        public Dictionary<String, GroupByWindowState> States { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>GROUP BY 窗口函数的分组状态（F17）</summary>
    private sealed class GroupByWindowState
    {
        /// <summary>是否已处理首行</summary>
        public Boolean Initialized { get; set; }

        /// <summary>上一行的分组依据值（时间或状态值）</summary>
        public Object? LastValue { get; set; }

        /// <summary>当前窗口键</summary>
        public Object? CurrentKey { get; set; }

        /// <summary>窗口序号，STATE_WINDOW 未提供时间参数时作为窗口键</summary>
        public Int64 Ordinal { get; set; }
    }

    private SqlResult BuildSelectResult(SelectStatement stmt, List<Object?[]> rows, TableSchema schema, Dictionary<String, Object?>? parameters, CancellationToken cancellationToken = default)
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

            for (var i = 0; i < rows.Count; i++)
            {
                if ((i & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();

                var row = rows[i];
                var outputRow = new Object?[stmt.Columns.Count];
                for (var j = 0; j < stmt.Columns.Count; j++)
                {
                    var col = stmt.Columns[j];
                    outputRow[j] = EvaluateExpression(col.Expression, row, schema, parameters);
                }
                result.Rows.Add(outputRow);
            }
        }

        return result;
    }

    #endregion
}
