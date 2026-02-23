namespace NewLife.NovaDb.Engine.Flux;

/// <summary>降采样聚合结果</summary>
public class DownsampleResult
{
    /// <summary>桶起始时间 Ticks</summary>
    public Int64 BucketStartTicks { get; set; }

    /// <summary>聚合值</summary>
    public Double Value { get; set; }

    /// <summary>桶起始时间</summary>
    public DateTime BucketStart => new(BucketStartTicks);

    /// <summary>创建降采样结果</summary>
    /// <param name="bucketStartTicks">桶起始时间 Ticks</param>
    /// <param name="value">聚合值</param>
    public DownsampleResult(Int64 bucketStartTicks, Double value)
    {
        BucketStartTicks = bucketStartTicks;
        Value = value;
    }
}
