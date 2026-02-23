using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Tx;

namespace NewLife.NovaDb.Sql;

partial class SqlEngine
{
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
            "binlog" => BuildSysBinlogData(),
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
        using var rl2 = _metaLock.AcquireRead();
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
                    rows.Add(new Object?[]
                    {
                        tableName, $"pk_{tableName}", true, pkCol.Name
                    });
                }

                // 二级索引
                foreach (var idx in tableSchema.Indexes)
                {
                    rows.Add(new Object?[]
                    {
                        tableName, idx.IndexName, idx.IsUnique, String.Join(",", idx.Columns)
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
                rows.Add(new Object?[] { fileName, size, isCurrent });
            }
        }

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
}
