using System.Globalization;

namespace NewLife.NovaDb.Sql;

/// <summary>EXPLAIN 查询计划执行器</summary>
public partial class SqlEngine
{
    /// <summary>执行 EXPLAIN 语句，返回查询计划</summary>
    /// <param name="explain">EXPLAIN 语句</param>
    /// <param name="parameters">参数字典</param>
    /// <returns>查询计划结果</returns>
    private SqlResult ExecuteExplain(ExplainStatement explain, Dictionary<String, Object?>? parameters)
    {
        var inner = explain.InnerStatement;
        var plan = new List<Object?[]>();

        switch (inner)
        {
            case SelectStatement select:
                ExplainSelect(select, plan, parameters);
                break;
            case InsertStatement insert:
                ExplainInsert(insert, plan);
                break;
            case UpdateStatement update:
                ExplainUpdate(update, plan, parameters);
                break;
            case DeleteStatement delete:
                ExplainDelete(delete, plan, parameters);
                break;
            default:
                plan.Add(["1", "DDL", inner.StatementType.ToString(), "", "", "DDL statement, no query plan"]);
                break;
        }

        return new SqlResult
        {
            ColumnNames = ["id", "type", "table", "key", "rows", "extra"],
            Rows = plan
        };
    }

    /// <summary>生成 SELECT 查询计划</summary>
    private void ExplainSelect(SelectStatement select, List<Object?[]> plan, Dictionary<String, Object?>? parameters)
    {
        var stepId = 1;

        // 主表扫描：与真实执行共用同一套访问计划推导，保证 EXPLAIN 结果可信
        var tableName = select.TableName ?? "";
        var scanType = "FULL SCAN";
        var key = "";
        var estimatedRows = "?";
        var extra = "";
        var providesOrder = false;

        if (!String.IsNullOrEmpty(tableName) && tableName != "DUAL")
        {
            using var rl = _metaLock.AcquireRead();
            if (_schemas.TryGetValue(tableName, out var schema))
            {
                _tables.TryGetValue(tableName, out var table);

                // 与执行阶段一致：分组/聚合后排序的是结果组，与表行顺序无关
                var needsSort = select.OrderBy != null && select.OrderBy.Count > 0 && !HasAggregateFunction(select)
                    && (select.GroupBy == null || select.GroupBy.Count == 0);
                var access = BuildTableAccessPlan(table, tableName, select.TableAlias, schema, select.Where, parameters,
                    needsSort: needsSort, orderBy: needsSort ? select.OrderBy : null,
                    fetchLimit: select.Limit.HasValue ? (Int64)(select.OffsetValue ?? 0) + select.Limit.Value : null);
                scanType = access.PlanType;
                key = access.KeyText;
                providesOrder = access.ProvidesOrder;

                if (access.EstimatedRows.HasValue)
                    estimatedRows = access.EstimatedRows.Value.ToString();
                else if (table != null)
                    estimatedRows = table.RowCount.ToString();

                if (!access.CoversWhere)
                    extra = "Residual WHERE filter";

                // 访问路径本身按索引顺序产出，执行阶段不再排序
                if (access.ProvidesOrder)
                    extra = AppendExtra(extra, access.OrderDescending ? "using index order, descending" : "using index order");

                // 与执行阶段一致：COUNT(*) 可完全由索引回答时标注不回表
                if (IsIndexOnlyCount(select) && access.Kind == TableAccessKind.IndexLookup && access.CoversWhere)
                    extra = AppendExtra(extra, "Index-only COUNT");

                extra = AppendCost(extra, access);
            }
        }
        else if (String.IsNullOrEmpty(tableName))
        {
            scanType = "NO TABLE";
            estimatedRows = "1";
        }

        plan.Add([stepId.ToString(), scanType, tableName, key, estimatedRows, extra]);
        stepId++;

        // JOIN 计划
        if (select.Joins != null)
        {
            foreach (var join in select.Joins)
            {
                var joinType = join.Type.ToString().ToUpperInvariant() + " JOIN";
                var joinTable = join.TableName;
                var joinExtra = $"Nested Loop, ON: {join.Condition}";
                plan.Add([stepId.ToString(), joinType, joinTable, "", "?", joinExtra]);
                stepId++;
            }
        }

        // WHERE 过滤
        if (select.Where != null)
        {
            var filterExtra = new List<String>();
            if (select.Where != null)
                filterExtra.Add("WHERE filter applied");

            if (filterExtra.Count > 0)
                extra = String.Join("; ", filterExtra);
        }

        // GROUP BY
        if (select.GroupBy != null && select.GroupBy.Count > 0)
        {
            var groupCols = String.Join(", ", select.GroupBy);
            plan.Add([stepId.ToString(), "GROUP BY", "", "", "?", $"Columns: {groupCols}"]);
            stepId++;
        }

        // HAVING
        if (select.Having != null)
        {
            plan.Add([stepId.ToString(), "HAVING", "", "", "?", "Post-aggregation filter"]);
            stepId++;
        }

        // ORDER BY：访问路径已按索引顺序产出时不再需要内存排序
        if (select.OrderBy != null && select.OrderBy.Count > 0 && !providesOrder)
        {
            plan.Add([stepId.ToString(), "SORT", "", "", "?", $"In-memory sort, {select.OrderBy.Count} key(s)"]);
            stepId++;
        }

        // LIMIT/OFFSET
        if (select.Limit.HasValue)
        {
            var limitExtra = $"LIMIT {select.Limit.Value}";
            if (select.OffsetValue.HasValue)
                limitExtra += $" OFFSET {select.OffsetValue.Value}";
            plan.Add([stepId.ToString(), "LIMIT", "", "", select.Limit.Value.ToString(), limitExtra]);
        }
    }

    /// <summary>生成 INSERT 计划</summary>
    private void ExplainInsert(InsertStatement insert, List<Object?[]> plan)
    {
        var rowCount = insert.ValuesList?.Count ?? 0;
        plan.Add(["1", "INSERT", insert.TableName, "PRIMARY", rowCount.ToString(), $"Insert {rowCount} row(s) with PK check"]);
    }

    /// <summary>生成 UPDATE 计划</summary>
    private void ExplainUpdate(UpdateStatement update, List<Object?[]> plan, Dictionary<String, Object?>? parameters)
    {
        var scanType = update.Where != null ? "FILTERED SCAN" : "FULL SCAN";
        var key = "";
        var estimatedRows = "?";
        var extra = $"Update {update.SetClauses?.Count ?? 0} column(s)";

        // UPDATE 只复用主键访问路径，与执行阶段保持一致
        if (update.Where != null)
        {
            using var rl = _metaLock.AcquireRead();
            if (_schemas.TryGetValue(update.TableName, out var schema))
            {
                _tables.TryGetValue(update.TableName, out var table);

                var access = BuildTableAccessPlan(table, update.TableName, null, schema, update.Where, parameters, allowSecondaryIndex: false);
                if (access.EstimatedRows.HasValue)
                    estimatedRows = access.EstimatedRows.Value.ToString();

                if (access.Kind is TableAccessKind.PrimaryKeyLookup or TableAccessKind.PrimaryKeyRange)
                {
                    scanType = access.PlanType;
                    key = access.KeyText;
                }

                extra = AppendCost(extra, access);
            }
        }

        plan.Add(["1", scanType, update.TableName, key, estimatedRows, extra]);
    }

    /// <summary>生成 DELETE 计划</summary>
    private void ExplainDelete(DeleteStatement delete, List<Object?[]> plan, Dictionary<String, Object?>? parameters)
    {
        var scanType = delete.Where != null ? "FILTERED SCAN" : "FULL SCAN";
        var key = "";
        var estimatedRows = "?";
        var extra = "Soft delete (mark version)";

        // DELETE 只复用主键访问路径，与执行阶段保持一致
        if (delete.Where != null)
        {
            using var rl = _metaLock.AcquireRead();
            if (_schemas.TryGetValue(delete.TableName, out var schema))
            {
                _tables.TryGetValue(delete.TableName, out var table);

                var access = BuildTableAccessPlan(table, delete.TableName, null, schema, delete.Where, parameters, allowSecondaryIndex: false);
                if (access.EstimatedRows.HasValue)
                    estimatedRows = access.EstimatedRows.Value.ToString();

                if (access.Kind is TableAccessKind.PrimaryKeyLookup or TableAccessKind.PrimaryKeyRange)
                {
                    scanType = access.PlanType;
                    key = access.KeyText;
                }

                extra = AppendCost(extra, access);
            }
        }

        plan.Add(["1", scanType, delete.TableName, key, estimatedRows, extra]);
    }

    /// <summary>在 extra 列追加说明文字，保留已有内容</summary>
    private static String AppendExtra(String extra, String text)
        => String.IsNullOrEmpty(extra) ? text : $"{extra}; {text}";

    /// <summary>在 extra 列追加计划代价，保留已有说明文字</summary>
    private static String AppendCost(String extra, TableAccessPlan plan)
    {
        var cost = plan.Cost.ToString("0.0", CultureInfo.InvariantCulture);
        return AppendExtra(extra, $"cost={cost}");
    }
}
