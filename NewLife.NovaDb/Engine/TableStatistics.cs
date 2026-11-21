using System.Globalization;
using System.Text;

namespace NewLife.NovaDb.Engine;

/// <summary>等深直方图的一个桶，记录桶上界与该桶的估算行数</summary>
public class HistogramBucket
{
    /// <summary>桶上界（含），桶内值不大于该值且大于前一桶上界</summary>
    public Object? UpperBound { get; set; }

    /// <summary>桶内估算行数</summary>
    public Int64 Count { get; set; }

    /// <summary>创建直方图桶</summary>
    /// <param name="upperBound">桶上界</param>
    /// <param name="count">桶内行数</param>
    public HistogramBucket(Object? upperBound, Int64 count)
    {
        UpperBound = upperBound;
        Count = count;
    }
}

/// <summary>列级统计信息：NDV、可空性、等深直方图</summary>
public class ColumnStatistics
{
    /// <summary>列名</summary>
    public String ColumnName { get; set; } = String.Empty;

    /// <summary>是否允许为空</summary>
    public Boolean Nullable { get; set; } = true;

    /// <summary>去重后的值个数（NDV）。采样列按采样比例放大后取整</summary>
    public Int64 DistinctCount { get; set; }

    /// <summary>最小值，无法确定时为 null</summary>
    public Object? MinValue { get; set; }

    /// <summary>最大值，无法确定时为 null</summary>
    public Object? MaxValue { get; set; }

    /// <summary>等深直方图，桶数默认 16，按值升序排列</summary>
    public List<HistogramBucket> Buckets { get; set; } = [];

    /// <summary>统计值是否来自采样（false 表示按索引全量精确统计）</summary>
    public Boolean IsSampled { get; set; }
}

/// <summary>索引级统计信息：索引名 → 键数量</summary>
public class IndexStatistics
{
    /// <summary>索引名，主键索引固定为 PRIMARY</summary>
    public String IndexName { get; set; } = String.Empty;

    /// <summary>是否唯一索引</summary>
    public Boolean IsUnique { get; set; }

    /// <summary>键数量（NDV 精确值）</summary>
    public Int64 KeyCount { get; set; }
}

/// <summary>表统计目录，供查询优化器做基数估算</summary>
/// <remarks>
/// 由 ANALYZE 生成，只读使用。估算函数都是纯函数，无法估算时返回调用方给出的默认值，不抛异常。
/// </remarks>
public class TableStatistics
{
    /// <summary>默认直方图桶数</summary>
    public const Int32 DefaultBucketCount = 16;

    /// <summary>表名</summary>
    public String TableName { get; set; } = String.Empty;

    /// <summary>可见行数</summary>
    public Int64 RowCount { get; set; }

    /// <summary>分析时主键索引条目数（含历史版本），用于判断统计是否过期</summary>
    public Int64 EntryCount { get; set; }

    /// <summary>采样行数，0 表示本次分析全部来自索引精确统计</summary>
    public Int64 SampledRows { get; set; }

    /// <summary>统计版本号，同一张表每次分析自增</summary>
    public Int64 StatVersion { get; set; }

    /// <summary>分析时间</summary>
    public DateTime AnalyzedAt { get; set; }

    /// <summary>分析时的数据版本号</summary>
    public Int64 DataVersion { get; set; }

    /// <summary>分析时的表结构签名，结构变化后统计自动失效</summary>
    public String SchemaSignature { get; set; } = String.Empty;

    /// <summary>分析时所属的表实例，表被重建后统计自动失效</summary>
    internal Object? Owner { get; set; }

    /// <summary>列统计：列名 → 统计</summary>
    public Dictionary<String, ColumnStatistics> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>索引统计：索引名 → 统计</summary>
    public Dictionary<String, IndexStatistics> Indexes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>判断统计相对当前表是否已过期</summary>
    /// <remarks>数据版本或行数变化超过 20%（下限 64 次），或表结构发生变化时判定过期</remarks>
    /// <param name="table">表实例</param>
    /// <returns>是否过期</returns>
    public Boolean IsStale(NovaTable table)
    {
        if (table == null) return true;
        if (Owner != null && !ReferenceEquals(Owner, table)) return true;

        if (!String.Equals(BuildSchemaSignature(table.Schema), SchemaSignature, StringComparison.Ordinal)) return true;

        var entryDelta = Math.Abs((Int64)table.RowCount - EntryCount);
        if (entryDelta > Math.Max(EntryCount / 5, 64)) return true;

        var versionDelta = table.DataVersion - DataVersion;
        return versionDelta > Math.Max(Math.Max(EntryCount, DataVersion) / 5, 64);
    }

    /// <summary>构造表结构签名，列与索引定义变化时签名随之变化</summary>
    /// <param name="schema">表结构</param>
    /// <returns>结构签名</returns>
    public static String BuildSchemaSignature(TableSchema? schema)
    {
        if (schema == null) return String.Empty;

        var sb = new StringBuilder();
        sb.Append(schema.TableName).Append('|');
        foreach (var col in schema.Columns)
        {
            sb.Append(col.Name).Append(':').Append((Int32)col.DataType).Append(':').Append(col.Nullable ? '1' : '0').Append(';');
        }

        sb.Append('|');
        foreach (var idx in schema.Indexes)
        {
            sb.Append(idx.IndexName).Append('(').Append(String.Join(",", idx.Columns)).Append(')').Append(idx.IsUnique ? 'U' : 'N').Append(';');
        }

        return sb.ToString();
    }

    /// <summary>估算等值条件命中的行数（按 行数 / NDV 计算）</summary>
    /// <param name="column">列名</param>
    /// <param name="value">等值条件常量</param>
    /// <param name="defaultValue">无统计或无法估算时的默认值</param>
    /// <returns>估算行数</returns>
    public Int64 EstimateEquals(String column, Object? value, Int64 defaultValue)
    {
        if (column == null || value == null || RowCount <= 0) return defaultValue;
        if (!Columns.TryGetValue(column, out var col) || col.DistinctCount <= 0) return defaultValue;

        // 超出采样/索引统计范围的值不可能命中
        if (col.MinValue != null && TryCompare(value, col.MinValue, out var minCmp) && minCmp < 0) return 0;
        if (col.MaxValue != null && TryCompare(value, col.MaxValue, out var maxCmp) && maxCmp > 0) return 0;

        var estimate = RowCount / col.DistinctCount;
        if (estimate < 1) return 1;
        return estimate > RowCount ? RowCount : estimate;
    }

    /// <summary>估算范围条件命中的行数（等深直方图累加，边界桶按线性插值取中值）</summary>
    /// <param name="column">列名</param>
    /// <param name="lower">下界（含）</param>
    /// <param name="upper">上界（含）</param>
    /// <param name="hasLower">是否有下界</param>
    /// <param name="hasUpper">是否有上界</param>
    /// <param name="defaultValue">无统计或无法估算时的默认值</param>
    /// <returns>估算行数</returns>
    public Int64 EstimateRange(String column, Object? lower, Object? upper, Boolean hasLower, Boolean hasUpper, Int64 defaultValue)
    {
        if (column == null || RowCount <= 0) return defaultValue;
        if (!Columns.TryGetValue(column, out var col) || col.Buckets.Count == 0) return defaultValue;
        if (!hasLower && !hasUpper) return RowCount;

        try
        {
            var end = hasUpper ? EstimateRank(col.Buckets, upper, true) : RowCount;
            if (end < 0) return defaultValue;

            var start = hasLower ? EstimateRank(col.Buckets, lower, false) : 0;
            if (start < 0) return defaultValue;

            var estimate = end - start;
            if (estimate < 0) return 0;
            return estimate > RowCount ? RowCount : estimate;
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>估算小于（或等于）指定值的行数，无法比较时返回 -1</summary>
    private static Int64 EstimateRank(List<HistogramBucket> buckets, Object? bound, Boolean inclusive)
    {
        if (bound == null || buckets.Count == 0) return -1;

        Int64 sum = 0;
        Object? previousUpper = null;
        foreach (var bucket in buckets)
        {
            if (!TryCompare(bucket.UpperBound, bound, out var cmp)) return -1;

            if (cmp < 0 || (cmp == 0 && inclusive))
            {
                sum += bucket.Count;
                previousUpper = bucket.UpperBound;
                continue;
            }

            if (cmp == 0) return sum;

            // 边界落在当前桶内部，按与前一桶上界的线性位置取比例
            var fraction = EstimateFraction(previousUpper, bucket.UpperBound, bound);
            sum += (Int64)Math.Round(bucket.Count * fraction);
            return sum;
        }

        return sum;
    }

    /// <summary>估算边界值在当前桶内的相对位置，无法插值时取中值</summary>
    private static Double EstimateFraction(Object? lower, Object? upper, Object? bound)
    {
        if (lower == null || upper == null || bound == null) return 0.5;

        try
        {
            var lo = ToDouble(lower);
            var hi = ToDouble(upper);
            var value = ToDouble(bound);
            if (Double.IsNaN(lo) || Double.IsNaN(hi) || Double.IsNaN(value) || hi <= lo) return 0.5;

            var fraction = (value - lo) / (hi - lo);
            return fraction < 0 ? 0 : fraction > 1 ? 1 : fraction;
        }
        catch
        {
            return 0.5;
        }
    }

    private static Double ToDouble(Object value) => value switch
    {
        DateTime dt => (Double)dt.Ticks,
        DateTimeOffset dto => (Double)dto.UtcDateTime.Ticks,
        Byte or SByte or Int16 or UInt16 or Int32 or UInt32 or Int64 or UInt64 or Single or Double or Decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => Double.NaN,
    };

    /// <summary>按与索引/排序一致的规则比较两个值，类型不兼容时返回 false 而不是抛异常</summary>
    private static Boolean TryCompare(Object? left, Object? right, out Int32 result)
    {
        result = 0;
        if (left == null || right == null) return false;

        try
        {
            result = new ComparableObject(left).CompareTo(new ComparableObject(right));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
