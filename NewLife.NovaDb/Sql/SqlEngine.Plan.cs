using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Tx;

namespace NewLife.NovaDb.Sql;

partial class SqlEngine
{
    #region 访问计划

    /// <summary>单表访问路径类型</summary>
    private enum TableAccessKind
    {
        /// <summary>全表扫描</summary>
        FullScan,

        /// <summary>主键等值查找</summary>
        PrimaryKeyLookup,

        /// <summary>主键范围扫描</summary>
        PrimaryKeyRange,

        /// <summary>二级索引等值查找</summary>
        IndexLookup,
    }

    /// <summary>单表访问计划。由 WHERE 条件推导，SELECT/UPDATE/DELETE 与 EXPLAIN 共用</summary>
    private sealed class TableAccessPlan
    {
        /// <summary>访问路径</summary>
        public TableAccessKind Kind { get; set; }

        /// <summary>表名</summary>
        public String TableName { get; set; } = String.Empty;

        /// <summary>二级索引名（仅 IndexLookup）</summary>
        public String? IndexName { get; set; }

        /// <summary>二级索引列名（仅 IndexLookup）</summary>
        public String? IndexColumn { get; set; }

        /// <summary>等值查询键（已转换为列类型）</summary>
        public List<Object> Keys { get; } = [];

        /// <summary>范围下界</summary>
        public Object? Lower { get; set; }

        /// <summary>是否包含下界</summary>
        public Boolean LowerInclusive { get; set; }

        /// <summary>范围上界</summary>
        public Object? Upper { get; set; }

        /// <summary>是否包含上界</summary>
        public Boolean UpperInclusive { get; set; }

        /// <summary>二级索引命中的主键列表，供执行阶段复用</summary>
        public List<Object>? IndexMatches { get; set; }

        /// <summary>候选集是否已完整表达 WHERE，没有剩余条件</summary>
        public Boolean CoversWhere { get; set; }

        /// <summary>估算行数</summary>
        public Int64? EstimatedRows { get; set; }

        /// <summary>计划类型文本</summary>
        public String PlanType => Kind switch
        {
            TableAccessKind.PrimaryKeyLookup => "PK LOOKUP",
            TableAccessKind.PrimaryKeyRange => "PK RANGE",
            TableAccessKind.IndexLookup => "INDEX LOOKUP",
            _ => "FULL SCAN",
        };

        /// <summary>使用的索引描述</summary>
        public String KeyText => Kind switch
        {
            TableAccessKind.PrimaryKeyLookup or TableAccessKind.PrimaryKeyRange => PrimaryKeyText,
            TableAccessKind.IndexLookup => $"{IndexName}({IndexColumn})",
            _ => String.Empty,
        };

        /// <summary>主键索引描述</summary>
        public String PrimaryKeyText { get; set; } = String.Empty;

        /// <summary>是否有下界</summary>
        public Boolean HasLower { get; set; }

        /// <summary>是否有上界</summary>
        public Boolean HasUpper { get; set; }
    }

    /// <summary>二级索引命中行数达到该阈值且超过全表比例时才回退全表扫描，避免小表误判</summary>
    private const Int32 FullScanFallbackMinRows = 32;

    /// <summary>根据 WHERE 条件推导单表访问计划</summary>
    /// <param name="table">表实例，为空时只做语法层推导（如 EXPLAIN 未知表）</param>
    /// <param name="tableName">表名</param>
    /// <param name="tableAlias">表别名</param>
    /// <param name="schema">表架构</param>
    /// <param name="where">WHERE 条件</param>
    /// <param name="parameters">参数字典</param>
    /// <param name="allowSecondaryIndex">是否允许使用二级索引（UPDATE/DELETE 仅复用主键路径）</param>
    /// <returns>访问计划</returns>
    private TableAccessPlan BuildTableAccessPlan(NovaTable? table, String tableName, String? tableAlias, TableSchema schema, SqlExpression? where, Dictionary<String, Object?>? parameters, Boolean allowSecondaryIndex = true)
    {
        var plan = new TableAccessPlan { Kind = TableAccessKind.FullScan, TableName = tableName };

        // 统计信息缺失或过期时为 null，后续全部回退到 RowCount 粗估
        var stats = table == null ? null : GetTableStatistics(tableName, table);
        if (stats != null) plan.EstimatedRows = stats.RowCount;

        if (where == null)
        {
            plan.CoversWhere = true;
            return plan;
        }

        var conjuncts = new List<SqlExpression>();
        CollectConjuncts(where, conjuncts);

        // 1. 主键等值/IN：主键索引直接定位单行或少量行
        var pkCol = schema.GetPrimaryKeyColumn();
        if (pkCol != null && CanUseEqualityIndex(pkCol.DataType))
        {
            plan.PrimaryKeyText = $"PRIMARY({pkCol.Name})";

            var used = new List<SqlExpression>();
            var keys = CollectEqualityKeys(conjuncts, pkCol, tableName, tableAlias, parameters, used);
            if (keys != null)
            {
                plan.Kind = TableAccessKind.PrimaryKeyLookup;
                plan.Keys.AddRange(keys);
                plan.CoversWhere = used.Count == conjuncts.Count;
                plan.EstimatedRows = keys.Count;
                return plan;
            }
        }

        // 2. 二级索引等值：只支持单列索引（联合索引键为数组，暂不参与等值匹配）
        if (allowSecondaryIndex)
        {
            var indexPlan = BuildIndexAccessPlan(table, tableName, tableAlias, schema, conjuncts, parameters, pkCol, stats);
            if (indexPlan != null) return indexPlan;
        }

        // 3. 主键范围扫描（字符串等类型缺少与 SQL 一致的大小写/区域性排序，不做范围下推）
        if (pkCol != null && CanUseRangeIndex(pkCol.DataType))
        {
            plan.PrimaryKeyText = $"PRIMARY({pkCol.Name})";

            var used = new List<SqlExpression>();
            CollectRangeBounds(conjuncts, pkCol, tableName, tableAlias, parameters, plan, used);
            if (plan.HasLower || plan.HasUpper)
            {
                plan.Kind = TableAccessKind.PrimaryKeyRange;
                plan.CoversWhere = used.Count == conjuncts.Count;

                // 有统计时用等深直方图估算范围行数，缺失时由 EXPLAIN 回退到 RowCount
                if (stats != null)
                {
                    var estimated = stats.EstimateRange(pkCol.Name, plan.Lower, plan.Upper, plan.HasLower, plan.HasUpper, -1);
                    if (estimated >= 0) plan.EstimatedRows = estimated;
                }

                return plan;
            }

            plan.Lower = null;
            plan.Upper = null;
            plan.HasLower = false;
            plan.HasUpper = false;
        }

        return plan;
    }

    /// <summary>尝试为单列二级索引构造等值访问计划</summary>
    private TableAccessPlan? BuildIndexAccessPlan(NovaTable? table, String tableName, String? tableAlias, TableSchema schema, List<SqlExpression> conjuncts, Dictionary<String, Object?>? parameters, ColumnDefinition? pkCol, TableStatistics? stats)
    {
        foreach (var index in schema.Indexes)
        {
            if (index.Columns.Count != 1 || !schema.HasColumn(index.Columns[0])) continue;

            var column = schema.GetColumn(index.Columns[0]);
            if (!CanUseEqualityIndex(column.DataType)) continue;

            var used = new List<SqlExpression>();
            var keys = CollectEqualityKeys(conjuncts, column, tableName, tableAlias, parameters, used);
            if (keys == null) continue;

            var candidate = new TableAccessPlan
            {
                Kind = TableAccessKind.IndexLookup,
                TableName = tableName,
                IndexName = index.IndexName,
                IndexColumn = column.Name,
                PrimaryKeyText = pkCol == null ? String.Empty : $"PRIMARY({pkCol.Name})",
                CoversWhere = used.Count == conjuncts.Count,
            };
            candidate.Keys.AddRange(keys);

            if (table != null)
            {
                // 命中行数优先用统计估算（行数/NDV），无统计时退回索引实际命中的候选行数
                var estimated = EstimateIndexMatches(stats, column.Name, keys);
                candidate.IndexMatches = LookupIndexMatches(table, candidate);
                candidate.EstimatedRows = estimated ?? candidate.IndexMatches.Count;

                // 命中行占比过高时，随机回表比顺序全表扫描更慢，放弃索引
                if (IsIndexUnselective(table, candidate.EstimatedRows.Value, stats)) continue;
            }

            return candidate;
        }

        return null;
    }

    /// <summary>按计划取候选行，索引只负责缩小范围，WHERE 仍需在结果上复核</summary>
    /// <param name="plan">访问计划</param>
    /// <param name="table">表实例</param>
    /// <param name="tx">事务</param>
    /// <param name="maxRows">最大行数，0 表示不限制</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>候选行列表</returns>
    private List<Object?[]> FetchPlanRows(TableAccessPlan plan, NovaTable table, Transaction tx, Int32 maxRows, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (plan.Kind)
        {
            case TableAccessKind.PrimaryKeyLookup:
                {
                    var rows = new List<Object?[]>(plan.Keys.Count);
                    var count = 0;
                    foreach (var key in plan.Keys)
                    {
                        if ((++count & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();

                        var row = table.Get(tx, key);
                        if (row != null) rows.Add(row);
                        if (maxRows > 0 && rows.Count >= maxRows) break;
                    }
                    return rows;
                }

            case TableAccessKind.PrimaryKeyRange:
                return table.GetRange(tx,
                    plan.HasLower ? plan.Lower : null, !plan.HasLower || plan.LowerInclusive,
                    plan.HasUpper ? plan.Upper : null, !plan.HasUpper || plan.UpperInclusive,
                    maxRows, cancellationToken);

            case TableAccessKind.IndexLookup:
                {
                    var pks = plan.IndexMatches ?? LookupIndexMatches(table, plan);
                    var rows = new List<Object?[]>(pks.Count);
                    var count = 0;
                    foreach (var pk in pks)
                    {
                        if ((++count & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();

                        var row = table.Get(tx, pk);
                        if (row != null) rows.Add(row);
                        if (maxRows > 0 && rows.Count >= maxRows) break;
                    }
                    return rows;
                }

            default:
                // 全表扫描：LIMIT 下推时同样只反序列化需要的行
                return maxRows > 0 ? table.GetRange(tx, null, true, null, true, maxRows, cancellationToken) : table.GetAll(tx, cancellationToken);
        }
    }

    /// <summary>通过二级索引取命中的主键，并按主键升序排列以保持与全表扫描一致的输出顺序</summary>
    /// <param name="table">表实例</param>
    /// <param name="plan">访问计划</param>
    /// <returns>命中的主键列表</returns>
    private static List<Object> LookupIndexMatches(NovaTable table, TableAccessPlan plan)
    {
        var result = new List<Object>();
        if (plan.IndexName == null) return result;

        var seen = new HashSet<Object>();
        foreach (var key in plan.Keys)
        {
            var matches = table.LookupByIndex(plan.IndexName, [key]);
            if (matches == null) continue;

            foreach (var pk in matches)
            {
                if (seen.Add(pk)) result.Add(pk);
            }
        }

        if (result.Count > 1)
            result.Sort((x, y) => CompareValues(x, y));

        return result;
    }

    /// <summary>统计估算二级索引等值命中的行数，统计缺失或列无统计时返回 null</summary>
    private static Int64? EstimateIndexMatches(TableStatistics? stats, String columnName, List<Object> keys)
    {
        if (stats == null) return null;

        var total = 0L;
        foreach (var key in keys)
        {
            var estimated = stats.EstimateEquals(columnName, key, -1);
            if (estimated < 0) return null;

            total += estimated;
        }

        return total;
    }

    /// <summary>判断二级索引是否因命中过多而不值得走索引</summary>
    /// <param name="table">表实例</param>
    /// <param name="matchCount">估算命中行数</param>
    /// <param name="stats">统计信息，为空时用索引条目数作为全表行数</param>
    private static Boolean IsIndexUnselective(NovaTable table, Int64 matchCount, TableStatistics? stats)
    {
        if (matchCount <= FullScanFallbackMinRows) return false;

        var total = stats != null ? stats.RowCount : table.RowCount;
        return total > 0 && matchCount * 4 > total;
    }

    /// <summary>把 WHERE 拆成顶层 AND 的子条件</summary>
    private static void CollectConjuncts(SqlExpression expr, List<SqlExpression> conjuncts)
    {
        if (expr is BinaryExpression bin && bin.Operator == BinaryOperator.And)
        {
            CollectConjuncts(bin.Left, conjuncts);
            CollectConjuncts(bin.Right, conjuncts);
        }
        else
        {
            conjuncts.Add(expr);
        }
    }

    /// <summary>收集指定列的等值/IN 候选键。同一列存在多个等值条件时取候选最少的一个，其余交由残余 WHERE 复核</summary>
    private List<Object>? CollectEqualityKeys(List<SqlExpression> conjuncts, ColumnDefinition column, String tableName, String? tableAlias, Dictionary<String, Object?>? parameters, List<SqlExpression> used)
    {
        List<Object>? best = null;
        SqlExpression? bestConjunct = null;

        foreach (var conjunct in conjuncts)
        {
            var keys = TryGetEqualityKeys(conjunct, column, tableName, tableAlias, parameters);
            if (keys == null) continue;

            if (best == null || keys.Count < best.Count)
            {
                best = keys;
                bestConjunct = conjunct;
            }
        }

        if (best == null) return null;

        if (best.Count > 1)
            best.Sort((x, y) => CompareValues(x, y));

        used.Add(bestConjunct!);
        return best;
    }

    /// <summary>识别 列=常量 与 列 IN (常量...) 形式的等值条件</summary>
    private List<Object>? TryGetEqualityKeys(SqlExpression expr, ColumnDefinition column, String tableName, String? tableAlias, Dictionary<String, Object?>? parameters)
    {
        switch (expr)
        {
            case BinaryExpression bin when bin.Operator == BinaryOperator.Equal:
                {
                    var leftIsColumn = IsColumnRef(bin.Left, column.Name, tableName, tableAlias);
                    var rightIsColumn = IsColumnRef(bin.Right, column.Name, tableName, tableAlias);

                    SqlExpression? valueExpr = null;
                    if (leftIsColumn && !rightIsColumn)
                        valueExpr = bin.Right;
                    else if (rightIsColumn && !leftIsColumn)
                        valueExpr = bin.Left;

                    if (valueExpr == null) return null;
                    if (!TryGetConstant(valueExpr, parameters, out var raw)) return null;
                    if (!TryConvertIndexValue(raw, column, out var value)) return null;

                    return [value!];
                }

            case InExpression inExpr when !inExpr.IsNot && inExpr.Subquery == null && IsColumnRef(inExpr.Operand, column.Name, tableName, tableAlias):
                {
                    var keys = new List<Object>();
                    foreach (var item in inExpr.Values)
                    {
                        if (!TryGetConstant(item, parameters, out var raw)) return null;

                        // NULL 不参与等值匹配
                        if (raw == null) continue;
                        if (!TryConvertIndexValue(raw, column, out var value)) return null;

                        if (!ContainsKey(keys, value!)) keys.Add(value!);
                    }

                    return keys.Count > 0 ? keys : null;
                }

            default:
                return null;
        }
    }

    /// <summary>收集指定列的主键范围边界，多个边界时取更紧的一侧</summary>
    private void CollectRangeBounds(List<SqlExpression> conjuncts, ColumnDefinition column, String tableName, String? tableAlias, Dictionary<String, Object?>? parameters, TableAccessPlan plan, List<SqlExpression> used)
    {
        foreach (var conjunct in conjuncts)
        {
            if (conjunct is not BinaryExpression bin) continue;
            if (bin.Operator is not (BinaryOperator.LessThan or BinaryOperator.LessOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterOrEqual)) continue;

            Boolean columnOnLeft;
            if (IsColumnRef(bin.Left, column.Name, tableName, tableAlias))
                columnOnLeft = true;
            else if (IsColumnRef(bin.Right, column.Name, tableName, tableAlias))
                columnOnLeft = false;
            else
                continue;

            var valueExpr = columnOnLeft ? bin.Right : bin.Left;
            if (!TryGetConstant(valueExpr, parameters, out var raw)) continue;
            if (!TryConvertIndexValue(raw, column, out var value)) continue;

            // 列在右侧时运算符方向翻转，统一成“列 运算符 常量”
            var op = columnOnLeft ? bin.Operator : FlipOperator(bin.Operator);

            // 整数列遇到小数边界时会被取整，边界放宽为包含（多取一行由 WHERE 复核丢弃），避免漏行
            var inexact = column.DataType is DataType.Int32 or DataType.Int64 && IsFractional(raw!);

            if (op is BinaryOperator.GreaterThan or BinaryOperator.GreaterOrEqual)
            {
                var inclusive = op == BinaryOperator.GreaterOrEqual || inexact;
                var tighter = !plan.HasLower;
                if (!tighter)
                {
                    var cmp = CompareValues(value, plan.Lower);
                    tighter = cmp > 0 || (cmp == 0 && !inclusive);
                }

                if (tighter)
                {
                    plan.Lower = value;
                    plan.LowerInclusive = inclusive;
                    plan.HasLower = true;
                }
            }
            else
            {
                var inclusive = op == BinaryOperator.LessOrEqual || inexact;
                var tighter = !plan.HasUpper;
                if (!tighter)
                {
                    var cmp = CompareValues(value, plan.Upper);
                    tighter = cmp < 0 || (cmp == 0 && !inclusive);
                }

                if (tighter)
                {
                    plan.Upper = value;
                    plan.UpperInclusive = inclusive;
                    plan.HasUpper = true;
                }
            }

            used.Add(conjunct);
        }
    }

    /// <summary>翻转比较运算符方向，用于常量在左侧的条件</summary>
    private static BinaryOperator FlipOperator(BinaryOperator op) => op switch
    {
        BinaryOperator.LessThan => BinaryOperator.GreaterThan,
        BinaryOperator.LessOrEqual => BinaryOperator.GreaterOrEqual,
        BinaryOperator.GreaterThan => BinaryOperator.LessThan,
        BinaryOperator.GreaterOrEqual => BinaryOperator.LessOrEqual,
        _ => op,
    };

    /// <summary>判断原始常量是否为带小数的数值</summary>
    private static Boolean IsFractional(Object raw) => raw switch
    {
        Double d => Math.Truncate(d) != d,
        Single f => Math.Truncate(f) != f,
        Decimal m => Decimal.Truncate(m) != m,
        _ => false,
    };

    /// <summary>判断表达式是否为指定列的引用（忽略表别名前缀）</summary>
    private static Boolean IsColumnRef(SqlExpression expr, String columnName, String tableName, String? tableAlias)
    {
        if (expr is not ColumnRefExpression col) return false;
        if (!String.Equals(col.ColumnName, columnName, StringComparison.OrdinalIgnoreCase)) return false;
        if (String.IsNullOrEmpty(col.TablePrefix)) return true;

        return String.Equals(col.TablePrefix, tableName, StringComparison.OrdinalIgnoreCase)
            || String.Equals(col.TablePrefix, tableAlias, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取常量表达式的值（字面量、参数、负数字面量）</summary>
    private static Boolean TryGetConstant(SqlExpression expr, Dictionary<String, Object?>? parameters, out Object? value)
    {
        switch (expr)
        {
            case LiteralExpression lit:
                value = lit.Value;
                return true;

            case ParameterExpression param:
                if (parameters != null && parameters.TryGetValue(param.ParameterName, out var paramValue))
                {
                    value = paramValue;
                    return true;
                }
                break;

            case UnaryExpression unary when unary.Operator == "-":
                if (TryGetConstant(unary.Operand, parameters, out var operand))
                {
                    value = operand switch
                    {
                        Int32 i => -i,
                        Int64 l => -l,
                        Double d => -d,
                        Decimal m => -m,
                        Single f => -f,
                        _ => null,
                    };
                    if (value != null) return true;
                }
                break;
        }

        value = null;
        return false;
    }

    /// <summary>把常量转换为列类型，失败时放弃走索引</summary>
    private static Boolean TryConvertIndexValue(Object? raw, ColumnDefinition column, out Object? value)
    {
        value = null;
        if (raw == null) return false;

        try
        {
            value = ConvertValue(raw, column.DataType);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return false;
        }

        return value != null;
    }

    /// <summary>判断候选键列表是否已包含指定值</summary>
    private static Boolean ContainsKey(List<Object> keys, Object value)
    {
        foreach (var key in keys)
        {
            if (CompareValues(key, value) == 0) return true;
        }

        return false;
    }

    /// <summary>判断列类型是否可参与索引等值匹配</summary>
    private static Boolean CanUseEqualityIndex(DataType dataType) => dataType is
        DataType.Boolean or DataType.Int32 or DataType.Int64 or DataType.Double or DataType.Decimal or DataType.String or DataType.DateTime;

    /// <summary>判断列类型是否可参与主键范围扫描</summary>
    /// <remarks>字符串的索引排序是区域性敏感比较，与 SQL 的大小写不敏感比较不一致，范围下推可能漏行，故排除</remarks>
    private static Boolean CanUseRangeIndex(DataType dataType) => dataType is
        DataType.Int32 or DataType.Int64 or DataType.Double or DataType.Decimal or DataType.DateTime;

    #endregion
}
