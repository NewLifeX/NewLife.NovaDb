using NewLife.NovaDb.Core;

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
                ExplainSelect(select, plan);
                break;
            case InsertStatement insert:
                ExplainInsert(insert, plan);
                break;
            case UpdateStatement update:
                ExplainUpdate(update, plan);
                break;
            case DeleteStatement delete:
                ExplainDelete(delete, plan);
                break;
            default:
                plan.Add(new Object?[] { "1", "DDL", inner.StatementType.ToString(), "", "", "DDL statement, no query plan" });
                break;
        }

        return new SqlResult
        {
            ColumnNames = ["id", "type", "table", "key", "rows", "extra"],
            Rows = plan
        };
    }

    /// <summary>生成 SELECT 查询计划</summary>
    private void ExplainSelect(SelectStatement select, List<Object?[]> plan)
    {
        var stepId = 1;

        // 主表扫描
        var tableName = select.TableName ?? "";
        var scanType = "FULL SCAN";
        var key = "";
        var estimatedRows = "?";
        var extra = "";

        if (!String.IsNullOrEmpty(tableName) && tableName != "DUAL")
        {
            using var rl = _metaLock.AcquireRead();
            if (_schemas.TryGetValue(tableName, out var schema))
            {
                // 检查 WHERE 条件是否可以使用主键
                var pkCol = schema.GetPrimaryKeyColumn();
                if (pkCol != null && select.Where != null && IsPrimaryKeyLookup(select.Where, pkCol.Name))
                {
                    scanType = "PK LOOKUP";
                    key = $"PRIMARY({pkCol.Name})";
                    estimatedRows = "1";
                }
                else
                {
                    scanType = "FULL SCAN";
                    key = "";
                    // 估算行数
                    if (_tables.TryGetValue(tableName, out _))
                        estimatedRows = "?";
                }
            }
        }
        else if (String.IsNullOrEmpty(tableName))
        {
            scanType = "NO TABLE";
            estimatedRows = "1";
        }

        plan.Add(new Object?[] { stepId.ToString(), scanType, tableName, key, estimatedRows, extra });
        stepId++;

        // JOIN 计划
        if (select.Joins != null)
        {
            foreach (var join in select.Joins)
            {
                var joinType = join.Type.ToString().ToUpper() + " JOIN";
                var joinTable = join.TableName;
                var joinExtra = $"Nested Loop, ON: {join.Condition}";
                plan.Add(new Object?[] { stepId.ToString(), joinType, joinTable, "", "?", joinExtra });
                stepId++;
            }
        }

        // WHERE 过滤
        if (select.Where != null)
        {
            var filterExtra = new System.Collections.Generic.List<String>();
            if (select.Where != null)
                filterExtra.Add("WHERE filter applied");

            if (filterExtra.Count > 0)
                extra = String.Join("; ", filterExtra);
        }

        // GROUP BY
        if (select.GroupBy != null && select.GroupBy.Count > 0)
        {
            var groupCols = String.Join(", ", select.GroupBy);
            plan.Add(new Object?[] { stepId.ToString(), "GROUP BY", "", "", "?", $"Columns: {groupCols}" });
            stepId++;
        }

        // HAVING
        if (select.Having != null)
        {
            plan.Add(new Object?[] { stepId.ToString(), "HAVING", "", "", "?", "Post-aggregation filter" });
            stepId++;
        }

        // ORDER BY
        if (select.OrderBy != null && select.OrderBy.Count > 0)
        {
            plan.Add(new Object?[] { stepId.ToString(), "SORT", "", "", "?", $"In-memory sort, {select.OrderBy.Count} key(s)" });
            stepId++;
        }

        // LIMIT/OFFSET
        if (select.Limit.HasValue)
        {
            var limitExtra = $"LIMIT {select.Limit.Value}";
            if (select.OffsetValue.HasValue)
                limitExtra += $" OFFSET {select.OffsetValue.Value}";
            plan.Add(new Object?[] { stepId.ToString(), "LIMIT", "", "", select.Limit.Value.ToString(), limitExtra });
        }
    }

    /// <summary>检查 WHERE 条件是否为主键等值查找</summary>
    private static Boolean IsPrimaryKeyLookup(SqlExpression where, String pkName)
    {
        if (where is BinaryExpression bin && bin.Operator == BinaryOperator.Equal)
        {
            if (bin.Left is ColumnRefExpression col && String.Equals(col.ColumnName, pkName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (bin.Right is ColumnRefExpression col2 && String.Equals(col2.ColumnName, pkName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>生成 INSERT 计划</summary>
    private void ExplainInsert(InsertStatement insert, List<Object?[]> plan)
    {
        var rowCount = insert.ValuesList?.Count ?? 0;
        plan.Add(new Object?[] { "1", "INSERT", insert.TableName, "PRIMARY", rowCount.ToString(), $"Insert {rowCount} row(s) with PK check" });
    }

    /// <summary>生成 UPDATE 计划</summary>
    private void ExplainUpdate(UpdateStatement update, List<Object?[]> plan)
    {
        var scanType = update.Where != null ? "FILTERED SCAN" : "FULL SCAN";

        // 检查是否为主键更新
        if (update.Where != null)
        {
            using var rl = _metaLock.AcquireRead();
            if (_schemas.TryGetValue(update.TableName, out var schema))
            {
                var pkCol = schema.GetPrimaryKeyColumn();
                if (pkCol != null && IsPrimaryKeyLookup(update.Where, pkCol.Name))
                {
                    scanType = "PK LOOKUP";
                }
            }
        }

        plan.Add(new Object?[] { "1", scanType, update.TableName, "", "?", $"Update {update.SetClauses?.Count ?? 0} column(s)" });
    }

    /// <summary>生成 DELETE 计划</summary>
    private void ExplainDelete(DeleteStatement delete, List<Object?[]> plan)
    {
        var scanType = delete.Where != null ? "FILTERED SCAN" : "FULL SCAN";

        if (delete.Where != null)
        {
            using var rl = _metaLock.AcquireRead();
            if (_schemas.TryGetValue(delete.TableName, out var schema))
            {
                var pkCol = schema.GetPrimaryKeyColumn();
                if (pkCol != null && IsPrimaryKeyLookup(delete.Where, pkCol.Name))
                {
                    scanType = "PK LOOKUP";
                }
            }
        }

        plan.Add(new Object?[] { "1", scanType, delete.TableName, "", "?", "Soft delete (mark version)" });
    }
}
