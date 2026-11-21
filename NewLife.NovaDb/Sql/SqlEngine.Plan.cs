using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Tx;

namespace NewLife.NovaDb.Sql;

/// <summary>单表访问路径类型</summary>
internal enum TableAccessKind
{
    /// <summary>全表扫描</summary>
    FullScan,

    /// <summary>主键等值查找</summary>
    PrimaryKeyLookup,

    /// <summary>主键范围扫描</summary>
    PrimaryKeyRange,

    /// <summary>二级索引等值查找</summary>
    IndexLookup,

    /// <summary>多路二级索引等值合并（顶层 OR 各分支取主键并集）</summary>
    IndexMerge,

    /// <summary>二级索引顺序扫描，按索引键序产出以满足 ORDER BY</summary>
    IndexOrder,
}

/// <summary>单表访问计划。由 WHERE 条件推导，SELECT/UPDATE/DELETE 与 EXPLAIN 共用</summary>
internal sealed class TableAccessPlan
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

    /// <summary>联合索引前缀探测值：每个元素是一组前缀列值，长度小于索引列数</summary>
    public List<Object?[]> IndexPrefixes { get; } = [];

    /// <summary>索引合并的各路等值子计划（仅 <see cref="TableAccessKind.IndexMerge"/>）</summary>
    public List<TableAccessPlan> MergeBranches { get; } = [];

    /// <summary>候选集是否已完整表达 WHERE，没有剩余条件</summary>
    public Boolean CoversWhere { get; set; }

    /// <summary>候选集是否已按 ORDER BY 要求的顺序产出（免排序）</summary>
    public Boolean ProvidesOrder { get; set; }

    /// <summary>顺序扫描方向是否为降序（仅 <see cref="ProvidesOrder"/> 时有意义）</summary>
    public Boolean OrderDescending { get; set; }

    /// <summary>联合索引顺序扫描的前缀等值条件（仅 <see cref="TableAccessKind.IndexOrder"/>）</summary>
    public Object?[]? OrderPrefix { get; set; }

    /// <summary>估算行数</summary>
    public Int64? EstimatedRows { get; set; }

    /// <summary>计划总代价，单位与 <see cref="SqlCostModel"/> 的常量一致</summary>
    public Double Cost { get; set; }

    /// <summary>计划选择依据：stats 表示按统计代价比较，fallback 表示统计缺失时沿用规则优先级</summary>
    public String Reason { get; set; } = "fallback";

    /// <summary>计划类型文本</summary>
    public String PlanType => Kind switch
    {
        TableAccessKind.PrimaryKeyLookup => "PK LOOKUP",
        TableAccessKind.PrimaryKeyRange => "PK RANGE",
        TableAccessKind.IndexLookup => "INDEX LOOKUP",
        TableAccessKind.IndexMerge => "INDEX MERGE",
        TableAccessKind.IndexOrder => "INDEX ORDER",
        _ => "FULL SCAN",
    };

    /// <summary>使用的索引描述</summary>
    public String KeyText => Kind switch
    {
        TableAccessKind.PrimaryKeyLookup or TableAccessKind.PrimaryKeyRange => PrimaryKeyText,
        TableAccessKind.IndexLookup or TableAccessKind.IndexOrder => $"{IndexName}({IndexColumn})",
        TableAccessKind.IndexMerge => String.Join(", ", MergeBranches.Select(e => e.KeyText)),
        _ => String.Empty,
    };

    /// <summary>主键索引描述</summary>
    public String PrimaryKeyText { get; set; } = String.Empty;

    /// <summary>是否有下界</summary>
    public Boolean HasLower { get; set; }

    /// <summary>是否有上界</summary>
    public Boolean HasUpper { get; set; }
}

partial class SqlEngine
{
    #region 访问计划

    /// <summary>二级索引命中行数达到该阈值且超过全表比例时才回退全表扫描，避免小表误判</summary>
    private const Int32 FullScanFallbackMinRows = 32;

    /// <summary>索引合并时主键并集去重一行的成本</summary>
    private const Double MergeDedupeRowCost = 0.6;

    /// <summary>单个索引最多展开的探测组合数，超过时放弃该索引候选</summary>
    private const Int32 MaxIndexProbeCombinations = 100;

    /// <summary>根据 WHERE 条件推导单表访问计划：先枚举全部候选路径，再按代价选择</summary>
    /// <param name="table">表实例，为空时只做语法层推导（如 EXPLAIN 未知表）</param>
    /// <param name="tableName">表名</param>
    /// <param name="tableAlias">表别名</param>
    /// <param name="schema">表架构</param>
    /// <param name="where">WHERE 条件</param>
    /// <param name="parameters">参数字典</param>
    /// <param name="allowSecondaryIndex">是否允许使用二级索引（UPDATE/DELETE 仅复用主键路径）</param>
    /// <param name="needsSort">查询是否带 ORDER BY（需要排序时计入排序成本）</param>
    /// <param name="orderBy">ORDER BY 子句，用于尝试免排序的索引顺序扫描</param>
    /// <param name="fetchLimit">LIMIT 提示（OFFSET+LIMIT），可边扫边停的计划只按该行数计成本</param>
    /// <returns>访问计划</returns>
    private TableAccessPlan BuildTableAccessPlan(NovaTable? table, String tableName, String? tableAlias, TableSchema schema, SqlExpression? where, Dictionary<String, Object?>? parameters, Boolean allowSecondaryIndex = true, Boolean needsSort = false, List<OrderByClause>? orderBy = null, Int64? fetchLimit = null)
    {
        // 统计信息缺失或过期时为 null，此时沿用原有规则优先级，行为与引入代价模型前一致
        var stats = table == null ? null : GetTableStatistics(tableName, table);

        Int64? rowCount = null;
        if (stats != null) rowCount = stats.RowCount;
        else if (table != null) rowCount = table.RowCount;

        var pkCol = schema.GetPrimaryKeyColumn();

        // ORDER BY 能否直接由索引顺序满足：主键唯一，排序列以主键打头即可；二级索引要求排序列是其最左前缀
        var order = ResolveOrderColumns(orderBy, schema, tableName, tableAlias);
        var primaryOrder = order != null && pkCol != null && CanUseOrderIndex(pkCol, isPrimaryKey: true) && IsPrimaryKeyOrder(order, pkCol);

        // 候选一：全表扫描，始终可用；代价相同时优先选它，保证计划稳定
        var fullScan = new TableAccessPlan
        {
            Kind = TableAccessKind.FullScan,
            TableName = tableName,
            CoversWhere = where == null,
            EstimatedRows = rowCount,
            ProvidesOrder = primaryOrder,
            OrderDescending = primaryOrder && order![0].Descending,
        };

        TableAccessPlan? lookupPlan = null;
        TableAccessPlan? rangePlan = null;
        TableAccessPlan? mergePlan = null;
        var indexPlans = new List<TableAccessPlan>();
        var orderPlans = new List<TableAccessPlan>();
        var candidates = new List<TableAccessPlan> { fullScan };

        if (where != null)
        {
            var conjuncts = new List<SqlExpression>();
            CollectConjuncts(where, conjuncts);

            if (pkCol != null)
            {
                var primaryKeyText = $"PRIMARY({pkCol.Name})";

                // 候选二：主键等值/IN，主键索引直接定位单行或少量行
                if (CanUseEqualityIndex(pkCol.DataType))
                {
                    var used = new List<SqlExpression>();
                    var keys = CollectEqualityKeys(conjuncts, pkCol, tableName, tableAlias, parameters, used);
                    if (keys != null)
                    {
                        lookupPlan = new TableAccessPlan
                        {
                            Kind = TableAccessKind.PrimaryKeyLookup,
                            TableName = tableName,
                            PrimaryKeyText = primaryKeyText,
                            CoversWhere = used.Count == conjuncts.Count,
                            EstimatedRows = keys.Count,
                            // 主键列表按升序排列，多键只在升序时可免排序；单键任意方向都成立
                            ProvidesOrder = primaryOrder && (keys.Count <= 1 || !order![0].Descending),
                        };
                        lookupPlan.Keys.AddRange(keys);
                        candidates.Add(lookupPlan);
                    }
                }

                // 候选三：主键范围（字符串等类型缺少与 SQL 一致的大小写/区域性排序，不做范围下推）
                if (CanUseRangeIndex(pkCol.DataType))
                {
                    var rangeCandidate = new TableAccessPlan
                    {
                        Kind = TableAccessKind.PrimaryKeyRange,
                        TableName = tableName,
                        PrimaryKeyText = primaryKeyText,
                        ProvidesOrder = primaryOrder,
                        OrderDescending = primaryOrder && order![0].Descending,
                    };

                    var used = new List<SqlExpression>();
                    CollectRangeBounds(conjuncts, pkCol, tableName, tableAlias, parameters, rangeCandidate, used);
                    if (rangeCandidate.HasLower || rangeCandidate.HasUpper)
                    {
                        rangeCandidate.CoversWhere = used.Count == conjuncts.Count;
                        rangeCandidate.EstimatedRows = EstimateRangeRows(stats, pkCol, rangeCandidate, rowCount);
                        rangePlan = rangeCandidate;
                        candidates.Add(rangeCandidate);
                    }
                }
            }

            // 候选四：二级索引等值（单列索引）与联合索引最左前缀
            if (allowSecondaryIndex)
            {
                CollectIndexPlans(table, tableName, tableAlias, schema, conjuncts, parameters, pkCol, stats, order, indexPlans);

                // 候选五：二级索引顺序扫描，按索引键序满足 ORDER BY
                CollectIndexOrderPlans(tableName, tableAlias, schema, conjuncts, parameters, pkCol, stats, rowCount, order, orderPlans);

                candidates.AddRange(indexPlans);
                candidates.AddRange(orderPlans);

                // 候选六：顶层 OR 的多路索引等值合并，先分别取主键再求并集
                if (table != null)
                {
                    mergePlan = BuildIndexMergePlan(table, tableName, tableAlias, schema, where, parameters, pkCol, stats);
                    if (mergePlan != null) candidates.Add(mergePlan);
                }
            }
        }
        else if (allowSecondaryIndex)
        {
            // 无 WHERE 时同样可以按二级索引顺序扫描，省掉排序
            CollectIndexOrderPlans(tableName, tableAlias, schema, [], parameters, pkCol, stats, rowCount, order, orderPlans);
            candidates.AddRange(orderPlans);
        }

        TableAccessPlan chosen;
        if (stats == null)
        {
            // 无统计：保持原有规则优先级，避免因缺少统计而选错；顺序扫描候选排在限制更强的路径之后
            chosen = lookupPlan
                ?? (indexPlans.Count > 0 ? indexPlans[0] : null)
                ?? mergePlan
                ?? rangePlan
                ?? (orderPlans.Count > 0 ? orderPlans[0] : null)
                ?? fullScan;
            chosen.Reason = "fallback";
        }
        else
        {
            chosen = fullScan;
            var bestCost = EstimatePlanCost(fullScan, stats, needsSort, fetchLimit);
            foreach (var candidate in candidates)
            {
                var cost = EstimatePlanCost(candidate, stats, needsSort, fetchLimit);
                if (cost < bestCost)
                {
                    chosen = candidate;
                    bestCost = cost;
                }
            }

            chosen.Reason = "stats";
        }

        chosen.Cost = EstimatePlanCost(chosen, stats, needsSort, fetchLimit);
        return chosen;
    }

    /// <summary>估算计划总代价，需要排序且计划本身不保序时叠加排序成本</summary>
    private static Double EstimatePlanCost(TableAccessPlan plan, TableStatistics? stats, Boolean needsSort, Int64? fetchLimit = null)
    {
        var cost = plan.Kind == TableAccessKind.IndexMerge ? EstimateMergeCost(plan, stats) : SqlCostModel.EstimateCost(plan, stats, fetchLimit);
        if (needsSort && !plan.ProvidesOrder) cost += SqlCostModel.EstimateSortCost(plan.EstimatedRows ?? stats?.RowCount ?? 0L);

        return cost;
    }

    /// <summary>估算索引合并代价：各路成本之和，再加上主键并集去重</summary>
    private static Double EstimateMergeCost(TableAccessPlan plan, TableStatistics? stats)
    {
        var cost = 0.0;
        foreach (var branch in plan.MergeBranches)
            cost += SqlCostModel.EstimateCost(branch, stats);

        return cost + (plan.EstimatedRows ?? 0L) * MergeDedupeRowCost;
    }

    /// <summary>收集可用的二级索引等值/前缀候选</summary>
    private void CollectIndexPlans(NovaTable? table, String tableName, String? tableAlias, TableSchema schema, List<SqlExpression> conjuncts, Dictionary<String, Object?>? parameters, ColumnDefinition? pkCol, TableStatistics? stats, List<(String Column, Boolean Descending)>? order, List<TableAccessPlan> plans)
    {
        foreach (var index in schema.Indexes)
        {
            var columns = ResolveIndexColumns(schema, index);
            if (columns == null) continue;

            if (columns.Count == 1)
            {
                var count = plans.Count;
                CollectSingleColumnIndexPlan(table, tableName, tableAlias, index, columns[0], conjuncts, parameters, pkCol, stats, plans);
                if (plans.Count > count) MarkConstantColumnsOrder(plans[count], columns, order);
                continue;
            }

            var candidate = BuildCompositeIndexPlan(table, tableName, tableAlias, columns, index, conjuncts, parameters, pkCol, stats);
            if (candidate != null)
            {
                MarkConstantColumnsOrder(candidate, columns, order);
                plans.Add(candidate);
            }
        }
    }

    /// <summary>等值命中时判断 ORDER BY 是否已被常量列满足，满足则免排序</summary>
    /// <param name="plan">索引等值候选</param>
    /// <param name="columns">索引列</param>
    /// <param name="order">排序列</param>
    /// <remarks>等值命中的所有行在这些列上取值相同，任意排列都符合 ORDER BY，方向也不受影响</remarks>
    private static void MarkConstantColumnsOrder(TableAccessPlan plan, List<ColumnDefinition> columns, List<(String Column, Boolean Descending)>? order)
    {
        if (order == null || order.Count == 0) return;

        // 只有唯一探测键才代表列值恒定：IN 列表会命中多组不同的键
        Int32 constantColumns;
        if (plan.Keys.Count == 1 && plan.IndexPrefixes.Count == 0)
            constantColumns = columns.Count;
        else if (plan.IndexPrefixes.Count == 1)
            constantColumns = plan.IndexPrefixes[0].Length;
        else
            return;

        for (var i = 0; i < order.Count; i++)
        {
            var found = false;
            for (var j = 0; j < constantColumns; j++)
            {
                if (String.Equals(columns[j].Name, order[i].Column, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found) return;
        }

        plan.ProvidesOrder = true;
    }

    /// <summary>收集能按索引键序满足 ORDER BY 的二级索引顺序扫描候选</summary>
    /// <param name="tableName">表名</param>
    /// <param name="tableAlias">表别名</param>
    /// <param name="schema">表架构</param>
    /// <param name="conjuncts">WHERE 的顶层 AND 子条件</param>
    /// <param name="parameters">参数字典</param>
    /// <param name="pkCol">主键列</param>
    /// <param name="stats">表统计信息</param>
    /// <param name="rowCount">表行数粗估</param>
    /// <param name="order">排序列</param>
    /// <param name="plans">输出候选列表</param>
    /// <remarks>
    /// 匹配规则：排序列必须是索引列的最左前缀且方向一致，各列不可空、类型排序与 SQL 一致；
    /// 单列索引复用同列范围条件限定扫描区间，联合索引复用前缀等值条件限定扫描分组。
    /// </remarks>
    private void CollectIndexOrderPlans(String tableName, String? tableAlias, TableSchema schema, List<SqlExpression> conjuncts, Dictionary<String, Object?>? parameters, ColumnDefinition? pkCol, TableStatistics? stats, Int64? rowCount, List<(String Column, Boolean Descending)>? order, List<TableAccessPlan> plans)
    {
        if (order == null || order.Count == 0) return;

        // 多列方向不一致时，单项索引扫描无法同时满足升序与降序，只能退回排序
        var descending = order[0].Descending;
        for (var i = 1; i < order.Count; i++)
        {
            if (order[i].Descending != descending) return;
        }

        foreach (var index in schema.Indexes)
        {
            if (index.Columns.Count == 0 || order.Count > index.Columns.Count) continue;

            // 排序列允许在索引中后移：前面的索引列只要被单值等值条件完整覆盖，顺序扫描仍按索引键序产出
            var start = -1;
            var leadingKeys = new List<Object>();
            var leadingUsed = new List<SqlExpression>();
            for (var s = 0; s + order.Count <= index.Columns.Count && start < 0; s++)
            {
                if (!String.Equals(index.Columns[s], order[0].Column, StringComparison.OrdinalIgnoreCase)) continue;

                var keys = new List<Object>();
                var usedKeys = new List<SqlExpression>();
                var ok = true;
                for (var k = 0; k < s && ok; k++)
                {
                    if (!schema.HasColumn(index.Columns[k]))
                    {
                        ok = false;
                        break;
                    }

                    var prefixColumn = schema.GetColumn(index.Columns[k]);
                    if (!CanUseEqualityIndex(prefixColumn.DataType))
                    {
                        ok = false;
                        break;
                    }

                    var prefixUsed = new List<SqlExpression>();
                    var prefixValues = CollectEqualityKeys(conjuncts, prefixColumn, tableName, tableAlias, parameters, prefixUsed);
                    if (prefixValues == null || prefixValues.Count != 1)
                    {
                        ok = false;
                        break;
                    }

                    keys.Add(prefixValues[0]);
                    usedKeys.AddRange(prefixUsed);
                }

                if (!ok) continue;

                // 排序列必须紧跟等值前缀，且各列不可空、类型排序与 SQL 一致
                var matched = true;
                for (var i = 0; i < order.Count && matched; i++)
                {
                    var columnName = index.Columns[s + i];
                    if (!String.Equals(columnName, order[i].Column, StringComparison.OrdinalIgnoreCase)) matched = false;
                    else if (!schema.HasColumn(columnName) || !CanUseOrderIndex(schema.GetColumn(columnName))) matched = false;
                }
                if (!matched) continue;

                start = s;
                leadingKeys = keys;
                leadingUsed = usedKeys;
            }

            if (start < 0) continue;

            // 索引首列就是主键时，主键索引本身就是等价且更廉价的顺序来源（主键唯一，后续索引列不影响顺序）
            if (pkCol != null && String.Equals(index.Columns[0], pkCol.Name, StringComparison.OrdinalIgnoreCase)) continue;

            var candidate = new TableAccessPlan
            {
                Kind = TableAccessKind.IndexOrder,
                TableName = tableName,
                IndexName = index.IndexName,
                IndexColumn = String.Join(",", index.Columns),
                PrimaryKeyText = pkCol == null ? String.Empty : $"PRIMARY({pkCol.Name})",
                ProvidesOrder = true,
                OrderDescending = descending,
            };

            var used = new List<SqlExpression>(leadingUsed);
            if (index.Columns.Count == 1)
            {
                // 单列索引：同列范围条件可直接限定跳表扫描区间
                CollectRangeBounds(conjuncts, schema.GetColumn(index.Columns[0]), tableName, tableAlias, parameters, candidate, used);
                candidate.EstimatedRows = candidate.HasLower || candidate.HasUpper
                    ? EstimateRangeRows(stats, schema.GetColumn(index.Columns[0]), candidate, rowCount)
                    : rowCount;
            }
            else
            {
                // 联合索引：等值前缀 + 排序列上的等值条件都可限定扫描分组，后续列仍按索引键序产出
                var prefix = new List<Object>(leadingKeys);
                for (var i = 0; i < order.Count; i++)
                {
                    var column = schema.GetColumn(index.Columns[start + i]);
                    if (!CanUseEqualityIndex(column.DataType)) break;

                    var columnUsed = new List<SqlExpression>();
                    var keys = CollectEqualityKeys(conjuncts, column, tableName, tableAlias, parameters, columnUsed);
                    if (keys == null || keys.Count != 1) break;

                    prefix.Add(keys[0]);
                    used.AddRange(columnUsed);
                }

                if (prefix.Count > 0) candidate.OrderPrefix = [.. prefix];
                candidate.EstimatedRows = rowCount;
            }

            candidate.CoversWhere = used.Count == conjuncts.Count;
            plans.Add(candidate);
        }
    }

    /// <summary>取出索引列定义；存在未知列或类型不支持等值匹配时返回 null</summary>
    private static List<ColumnDefinition>? ResolveIndexColumns(TableSchema schema, IndexDefinition index)
    {
        if (index.Columns.Count == 0) return null;

        var columns = new List<ColumnDefinition>(index.Columns.Count);
        foreach (var name in index.Columns)
        {
            if (!schema.HasColumn(name)) return null;

            var column = schema.GetColumn(name);
            if (!CanUseEqualityIndex(column.DataType)) return null;

            columns.Add(column);
        }

        return columns;
    }

    /// <summary>收集单列二级索引的等值候选</summary>
    private void CollectSingleColumnIndexPlan(NovaTable? table, String tableName, String? tableAlias, IndexDefinition index, ColumnDefinition column, List<SqlExpression> conjuncts, Dictionary<String, Object?>? parameters, ColumnDefinition? pkCol, TableStatistics? stats, List<TableAccessPlan> plans)
    {
        var used = new List<SqlExpression>();
        var keys = CollectEqualityKeys(conjuncts, column, tableName, tableAlias, parameters, used);
        if (keys == null) return;

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
            // 命中行数优先用统计估算（行数/NDV），统计缺失或该列无统计时退回索引实际命中的候选行数
            var estimated = EstimateIndexMatches(stats, column.Name, keys);
            if (estimated.HasValue)
                candidate.EstimatedRows = estimated.Value;
            else
            {
                candidate.IndexMatches = LookupIndexMatches(table, candidate);
                candidate.EstimatedRows = candidate.IndexMatches.Count;
            }

            // 命中行占比过高时，随机回表比顺序全表扫描更慢，保留为候选过滤的保底阈值
            if (IsIndexUnselective(table, candidate.EstimatedRows.Value, stats)) return;
        }

        plans.Add(candidate);
    }

    /// <summary>收集联合索引候选：最左前缀等值（可叠加下一列范围），或全部列等值</summary>
    private TableAccessPlan? BuildCompositeIndexPlan(NovaTable? table, String tableName, String? tableAlias, List<ColumnDefinition> columns, IndexDefinition index, List<SqlExpression> conjuncts, Dictionary<String, Object?>? parameters, ColumnDefinition? pkCol, TableStatistics? stats)
    {
        var used = new List<SqlExpression>();
        var prefixKeys = new List<List<Object>>();
        foreach (var column in columns)
        {
            var columnUsed = new List<SqlExpression>();
            var keys = CollectEqualityKeys(conjuncts, column, tableName, tableAlias, parameters, columnUsed);
            if (keys == null) break;

            prefixKeys.Add(keys);
            used.AddRange(columnUsed);
        }

        if (prefixKeys.Count == 0) return null;

        // 组合数过多时放弃该索引，避免 IN 列表把计划展开成笛卡尔积
        if (CountCombinations(prefixKeys) > MaxIndexProbeCombinations) return null;

        var candidate = new TableAccessPlan
        {
            Kind = TableAccessKind.IndexLookup,
            TableName = tableName,
            IndexName = index.IndexName,
            IndexColumn = String.Join(",", index.Columns),
            PrimaryKeyText = pkCol == null ? String.Empty : $"PRIMARY({pkCol.Name})",
        };

        if (prefixKeys.Count == columns.Count)
        {
            // 全部索引列都是等值条件：拼成完整联合键直接点查
            foreach (var combo in BuildCombinations(prefixKeys))
                candidate.Keys.Add(combo);
        }
        else
        {
            // 前缀等值：联合键按元素有序，前缀相同时下一列连续排列，可叠加下一列范围
            foreach (var combo in BuildCombinations(prefixKeys))
                candidate.IndexPrefixes.Add(combo);

            var next = columns[prefixKeys.Count];
            if (CanUseRangeIndex(next.DataType))
            {
                var rangeUsed = new List<SqlExpression>();
                CollectRangeBounds(conjuncts, next, tableName, tableAlias, parameters, candidate, rangeUsed);
                if (candidate.HasLower || candidate.HasUpper) used.AddRange(rangeUsed);
            }
        }

        candidate.CoversWhere = used.Count == conjuncts.Count;

        if (table != null)
        {
            var estimated = EstimateCompositeRows(stats, columns, prefixKeys, candidate);
            if (estimated.HasValue)
                candidate.EstimatedRows = estimated.Value;
            else
            {
                candidate.IndexMatches = LookupIndexMatches(table, candidate);
                candidate.EstimatedRows = candidate.IndexMatches.Count;
            }

            if (IsIndexUnselective(table, candidate.EstimatedRows.Value, stats)) return null;
        }

        return candidate;
    }

    /// <summary>用统计估算联合索引候选的命中行数，统计缺失时返回 null</summary>
    /// <param name="stats">表统计信息</param>
    /// <param name="columns">索引列</param>
    /// <param name="prefixKeys">已收集等值条件的各列候选键</param>
    /// <param name="plan">候选计划，提供下一列的范围边界</param>
    private static Int64? EstimateCompositeRows(TableStatistics? stats, List<ColumnDefinition> columns, List<List<Object>> prefixKeys, TableAccessPlan plan)
    {
        if (stats == null || stats.RowCount <= 0) return null;

        // 前缀列按 NDV 连乘估算（独立性假设），再乘下一列范围的选择率
        var rows = (Double)stats.RowCount;
        for (var i = 0; i < prefixKeys.Count; i++)
        {
            if (!stats.Columns.TryGetValue(columns[i].Name, out var col) || col.DistinctCount <= 0) return null;

            rows = rows / col.DistinctCount * prefixKeys[i].Count;
            if (rows < 1) rows = 1;
        }

        if (plan.HasLower || plan.HasUpper)
        {
            var next = columns[prefixKeys.Count];
            var rangeRows = stats.EstimateRange(next.Name, plan.Lower, plan.Upper, plan.HasLower, plan.HasUpper, -1);
            if (rangeRows < 0) return null;

            rows *= (Double)rangeRows / stats.RowCount;
        }

        var estimate = (Int64)Math.Round(rows);
        return estimate < 1 ? 1 : estimate > stats.RowCount ? stats.RowCount : estimate;
    }

    /// <summary>计算各列候选键展开后的组合数</summary>
    private static Int64 CountCombinations(List<List<Object>> keyLists)
    {
        var total = 1L;
        foreach (var keys in keyLists)
        {
            total *= keys.Count;
            if (total > MaxIndexProbeCombinations) return total;
        }

        return total;
    }

    /// <summary>把各列候选键展开成联合键组合</summary>
    private static List<Object[]> BuildCombinations(List<List<Object>> keyLists)
    {
        var combos = new List<Object[]> { new Object[0] };
        foreach (var keys in keyLists)
        {
            var next = new List<Object[]>(combos.Count * keys.Count);
            foreach (var combo in combos)
            {
                foreach (var key in keys)
                {
                    var values = new Object[combo.Length + 1];
                    Array.Copy(combo, values, combo.Length);
                    values[combo.Length] = key;
                    next.Add(values);
                }
            }
            combos = next;
        }

        return combos;
    }

    /// <summary>把顶层 OR 拆成多路等值索引分支，生成索引合并候选</summary>
    private TableAccessPlan? BuildIndexMergePlan(NovaTable table, String tableName, String? tableAlias, TableSchema schema, SqlExpression? where, Dictionary<String, Object?>? parameters, ColumnDefinition? pkCol, TableStatistics? stats)
    {
        if (where == null) return null;

        var disjuncts = new List<SqlExpression>();
        CollectDisjuncts(where, disjuncts);
        if (disjuncts.Count < 2) return null;

        var branches = new List<TableAccessPlan>();
        foreach (var disjunct in disjuncts)
        {
            var conjuncts = new List<SqlExpression>();
            CollectConjuncts(disjunct, conjuncts);

            var branch = BuildEqualityBranch(table, tableName, tableAlias, schema, conjuncts, parameters, pkCol);
            if (branch == null) return null;

            branches.Add(branch);
        }

        var plan = new TableAccessPlan
        {
            Kind = TableAccessKind.IndexMerge,
            TableName = tableName,
            PrimaryKeyText = pkCol == null ? String.Empty : $"PRIMARY({pkCol.Name})",
            CoversWhere = false,
        };
        plan.MergeBranches.AddRange(branches);

        // 各路命中行数之和是并集的上界，去重后实际回表行数不会更多
        var total = 0L;
        foreach (var branch in branches)
            total += branch.EstimatedRows ?? 0L;

        var rowCount = stats != null ? stats.RowCount : table.RowCount;
        var estimate = Math.Min(rowCount, total);
        plan.EstimatedRows = estimate < 1 ? 1 : estimate;

        if (IsIndexUnselective(table, plan.EstimatedRows.Value, stats)) return null;

        return plan;
    }

    /// <summary>为一个 OR 分支生成等值索引子计划，分支条件无法被单个索引完整覆盖时返回 null</summary>
    private TableAccessPlan? BuildEqualityBranch(NovaTable table, String tableName, String? tableAlias, TableSchema schema, List<SqlExpression> conjuncts, Dictionary<String, Object?>? parameters, ColumnDefinition? pkCol)
    {
        // 优先主键等值
        if (pkCol != null && CanUseEqualityIndex(pkCol.DataType))
        {
            var used = new List<SqlExpression>();
            var keys = CollectEqualityKeys(conjuncts, pkCol, tableName, tableAlias, parameters, used);
            if (keys != null && used.Count == conjuncts.Count)
            {
                var plan = new TableAccessPlan
                {
                    Kind = TableAccessKind.PrimaryKeyLookup,
                    TableName = tableName,
                    PrimaryKeyText = $"PRIMARY({pkCol.Name})",
                    CoversWhere = true,
                    EstimatedRows = keys.Count,
                };
                plan.Keys.AddRange(keys);

                return plan;
            }
        }

        // 再找能完整覆盖分支条件的二级索引等值
        foreach (var index in schema.Indexes)
        {
            var columns = ResolveIndexColumns(schema, index);
            if (columns == null) continue;

            var used = new List<SqlExpression>();
            var prefixKeys = new List<List<Object>>();
            foreach (var column in columns)
            {
                var columnUsed = new List<SqlExpression>();
                var keys = CollectEqualityKeys(conjuncts, column, tableName, tableAlias, parameters, columnUsed);
                if (keys == null) break;

                prefixKeys.Add(keys);
                used.AddRange(columnUsed);
            }

            // 只有全部索引列都有等值条件时才作为合并分支，前缀扫描留给单表计划
            if (prefixKeys.Count != columns.Count || used.Count != conjuncts.Count) continue;
            if (CountCombinations(prefixKeys) > MaxIndexProbeCombinations) continue;

            var plan = new TableAccessPlan
            {
                Kind = TableAccessKind.IndexLookup,
                TableName = tableName,
                IndexName = index.IndexName,
                IndexColumn = String.Join(",", index.Columns),
                PrimaryKeyText = pkCol == null ? String.Empty : $"PRIMARY({pkCol.Name})",
                CoversWhere = true,
            };

            if (columns.Count == 1)
                plan.Keys.AddRange(prefixKeys[0]);
            else
            {
                foreach (var combo in BuildCombinations(prefixKeys))
                    plan.Keys.Add(combo);
            }

            plan.IndexMatches = LookupIndexMatches(table, plan);
            plan.EstimatedRows = plan.IndexMatches.Count;

            return plan;
        }

        return null;
    }

    /// <summary>把 WHERE 拆成顶层 OR 的分支条件</summary>
    private static void CollectDisjuncts(SqlExpression expr, List<SqlExpression> disjuncts)
    {
        if (expr is BinaryExpression bin && bin.Operator == BinaryOperator.Or)
        {
            CollectDisjuncts(bin.Left, disjuncts);
            CollectDisjuncts(bin.Right, disjuncts);
        }
        else
        {
            disjuncts.Add(expr);
        }
    }

    /// <summary>估算主键范围命中行数，统计缺失或无法估算时退回表行数</summary>
    private static Int64? EstimateRangeRows(TableStatistics? stats, ColumnDefinition pkCol, TableAccessPlan candidate, Int64? rowCount)
    {
        if (stats != null)
        {
            var estimated = stats.EstimateRange(pkCol.Name, candidate.Lower, candidate.Upper, candidate.HasLower, candidate.HasUpper, -1);
            if (estimated >= 0) return estimated;
        }

        return rowCount;
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
                    maxRows, cancellationToken, plan.ProvidesOrder && plan.OrderDescending);

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

            case TableAccessKind.IndexOrder:
                return table.ScanIndexOrder(tx, plan.IndexName!, plan.OrderPrefix,
                    plan.HasLower ? plan.Lower : null, plan.HasLower, plan.LowerInclusive,
                    plan.HasUpper ? plan.Upper : null, plan.HasUpper, plan.UpperInclusive,
                    plan.OrderDescending, maxRows, cancellationToken);

            case TableAccessKind.IndexMerge:
                {
                    // 各路主键并集先去重，再统一回表；结果顺序按主键升序，与全表扫描一致
                    var pks = plan.IndexMatches ?? LookupMergeMatches(table, plan);
                    plan.IndexMatches = pks;

                    var rows = new List<Object?[]>(pks.Count);
                    var count = 0;
                    foreach (var pk in pks)
                    {
                        if ((++count & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();

                        var row = table.Get(tx, pk);
                        if (row != null) rows.Add(row);
                    }
                    return rows;
                }

            default:
                // 全表扫描：LIMIT 下推时同样只反序列化需要的行；ORDER BY 主键降序时反向扫描
                if (plan.ProvidesOrder && plan.OrderDescending)
                    return table.GetRange(tx, null, true, null, true, maxRows, cancellationToken, descending: true);

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
        if (plan.IndexPrefixes.Count > 0)
        {
            foreach (var prefix in plan.IndexPrefixes)
            {
                var matches = table.LookupByIndexPrefix(plan.IndexName, prefix,
                    plan.HasLower ? plan.Lower : null, plan.HasLower, plan.LowerInclusive,
                    plan.HasUpper ? plan.Upper : null, plan.HasUpper, plan.UpperInclusive, 0);
                if (matches == null) continue;

                foreach (var pk in matches)
                {
                    if (seen.Add(pk)) result.Add(pk);
                }
            }
        }
        else
        {
            foreach (var key in plan.Keys)
            {
                var matches = table.LookupByIndex(plan.IndexName, [key]);
                if (matches == null) continue;

                foreach (var pk in matches)
                {
                    if (seen.Add(pk)) result.Add(pk);
                }
            }
        }

        if (result.Count > 1)
            result.Sort((x, y) => CompareValues(x, y));

        return result;
    }

    /// <summary>取索引合并各路命中主键的并集并去重，按主键升序保持与全表扫描一致的输出顺序</summary>
    /// <param name="table">表实例</param>
    /// <param name="plan">索引合并计划</param>
    /// <returns>去重后的主键列表</returns>
    private static List<Object> LookupMergeMatches(NovaTable table, TableAccessPlan plan)
    {
        var result = new List<Object>();
        var seen = new HashSet<Object>();
        foreach (var branch in plan.MergeBranches)
        {
            var pks = branch.Kind == TableAccessKind.PrimaryKeyLookup
                ? branch.Keys
                : branch.IndexMatches ?? LookupIndexMatches(table, branch);

            foreach (var pk in pks)
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

    /// <summary>判断列能否用索引顺序满足 ORDER BY</summary>
    /// <param name="column">列定义</param>
    /// <param name="isPrimaryKey">是否主键列。主键非空，所有行都在主键索引里</param>
    /// <remarks>
    /// 可空列的 NULL 不进二级索引，字符串与日期的索引排序口径又和 SQL 排序不一致，
    /// 直接按索引顺序输出会与「扫描后排序」的结果不同，故只对数值列免排序。
    /// </remarks>
    private static Boolean CanUseOrderIndex(ColumnDefinition column, Boolean isPrimaryKey = false)
        => column.DataType is DataType.Int32 or DataType.Int64 or DataType.Double or DataType.Decimal or DataType.DateTime
            && (isPrimaryKey || !column.Nullable);

    /// <summary>把 ORDER BY 解析成列清单，含表别名限定列名；存在无法识别的列时返回 null</summary>
    /// <param name="orderBy">ORDER BY 子句</param>
    /// <param name="schema">表架构</param>
    /// <param name="tableName">表名</param>
    /// <param name="tableAlias">表别名</param>
    /// <returns>列名与方向列表；无 ORDER BY 或无法解析时返回 null</returns>
    private static List<(String Column, Boolean Descending)>? ResolveOrderColumns(List<OrderByClause>? orderBy, TableSchema schema, String tableName, String? tableAlias)
    {
        if (orderBy == null || orderBy.Count == 0) return null;

        var result = new List<(String, Boolean)>(orderBy.Count);
        foreach (var clause in orderBy)
        {
            var name = clause.ColumnName;
            if (String.IsNullOrEmpty(name)) return null;

            // 兼容别名限定列名（如 e.name），取点后部分作为列名
            var dot = name.LastIndexOf('.');
            if (dot >= 0)
            {
                var prefix = name.Substring(0, dot);
                if (!String.Equals(prefix, tableName, StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(prefix, tableAlias, StringComparison.OrdinalIgnoreCase)) return null;

                name = name.Substring(dot + 1);
            }

            if (!schema.HasColumn(name)) return null;

            result.Add((name, clause.Descending));
        }

        return result;
    }

    /// <summary>判断排序列是否以主键打头，主键唯一时后续排序列不再影响顺序</summary>
    private static Boolean IsPrimaryKeyOrder(List<(String Column, Boolean Descending)> order, ColumnDefinition pkCol)
        => order.Count > 0 && String.Equals(order[0].Column, pkCol.Name, StringComparison.OrdinalIgnoreCase);

    #endregion
}
