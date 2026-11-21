using NewLife.NovaDb.Engine;

namespace NewLife.NovaDb.Sql;

/// <summary>访问计划代价模型</summary>
/// <remarks>
/// 成本单位是一次行反序列化，数值只用于比较候选计划的相对大小，不代表真实耗时。
/// 所有方法都是纯函数，只依赖计划本身与统计信息，不访问表实例，便于单元测试。
/// </remarks>
internal static class SqlCostModel
{
    /// <summary>顺序扫描并反序列化一行的成本</summary>
    public const Double ScanRowCost = 1.0;

    /// <summary>一次索引探测（定位一个键）的成本</summary>
    public const Double IndexProbeCost = 1.0;

    /// <summary>索引命中后随机回表一行的成本，高于顺序扫描</summary>
    public const Double LookupRowCost = 4.0;

    /// <summary>候选行上再执行一次残余 WHERE 过滤的成本</summary>
    public const Double FilterRowCost = 0.2;

    /// <summary>按索引顺序扫描一行的成本，用于主键范围与后续免排序路径</summary>
    public const Double IndexScanRowCost = 0.5;

    /// <summary>排序成本系数，乘以 n*log2(n) 得到排序代价</summary>
    public const Double SortCostFactor = 0.1;

    /// <summary>估算访问计划的代价</summary>
    /// <param name="plan">候选计划</param>
    /// <param name="stats">表统计信息，可为空</param>
    /// <param name="fetchLimit">LIMIT 提示：可边扫边停的计划只需取这么多行，用于修正代价</param>
    /// <returns>代价，单位与常量一致</returns>
    public static Double EstimateCost(TableAccessPlan plan, TableStatistics? stats, Int64? fetchLimit = null)
    {
        if (plan == null) return 0;

        var rows = GetRows(plan, stats);

        // 可边扫边停的计划（保序输出 / 主键直达 / 无条件全扫）在 LIMIT 下不必付全量代价
        if (fetchLimit.HasValue && fetchLimit.Value > 0 && plan.CoversWhere && CanEarlyStop(plan) && fetchLimit.Value < rows)
            rows = fetchLimit.Value;

        var filter = plan.CoversWhere ? 0 : rows * FilterRowCost;

        // 索引探测次数取候选键数量，至少按一次计算
        var probes = plan.Keys.Count > 0 ? plan.Keys.Count : 1;

        return plan.Kind switch
        {
            TableAccessKind.FullScan => rows * ScanRowCost + filter,
            TableAccessKind.PrimaryKeyLookup => probes * IndexProbeCost + rows * LookupRowCost + filter,
            TableAccessKind.PrimaryKeyRange => IndexProbeCost + rows * (IndexScanRowCost + ScanRowCost) + filter,
            TableAccessKind.IndexLookup => probes * IndexProbeCost + rows * LookupRowCost + filter,
            // 索引顺序扫描：沿索引键顺序遍历并按主键回表，比随机回表便宜，且省掉排序
            TableAccessKind.IndexOrder => IndexProbeCost + rows * (IndexScanRowCost + ScanRowCost) + filter,
            _ => rows * ScanRowCost + filter,
        };
    }

    /// <summary>判断计划能否在凑够 LIMIT 行后提前停止</summary>
    /// <param name="plan">访问计划</param>
    /// <returns>是否可提前停止</returns>
    private static Boolean CanEarlyStop(TableAccessPlan plan)
        => plan.ProvidesOrder || plan.Kind is TableAccessKind.FullScan or TableAccessKind.PrimaryKeyLookup or TableAccessKind.PrimaryKeyRange;

    /// <summary>估算排序代价 0.1*n*log2(n)</summary>
    /// <param name="rows">参与排序的行数</param>
    /// <returns>排序代价</returns>
    public static Double EstimateSortCost(Int64 rows)
    {
        if (rows <= 1) return 0;

        return SortCostFactor * rows * (Math.Log(rows) / Math.Log(2));
    }

    /// <summary>取计划估算行数，缺失时退回统计行数或候选键数</summary>
    private static Double GetRows(TableAccessPlan plan, TableStatistics? stats)
    {
        var rows = plan.EstimatedRows ?? stats?.RowCount ?? plan.Keys.Count;
        return rows > 0 ? (Double)rows : 0;
    }
}
