namespace NewLife.NovaDb.Engine.Flux;

/// <summary>时序列式存储压缩统计（F19）</summary>
/// <remarks>
/// 汇总引擎自启动以来的封存情况：行式 AOF 体积与列式块体积的比值即压缩率，
/// 可用于监控列式存储与 LZ4 压缩带来的空间收益。
/// </remarks>
public sealed class FluxCompressionStats
{
    /// <summary>已封存块数量</summary>
    public Int32 BlockCount { get; internal set; }

    /// <summary>已封存条目数量</summary>
    public Int64 EntryCount { get; internal set; }

    /// <summary>行式 AOF 等效字节数</summary>
    public Int64 RowBytes { get; internal set; }

    /// <summary>列式块存储字节数（含块头与校验）</summary>
    public Int64 BlockBytes { get; internal set; }

    /// <summary>列总数</summary>
    public Int32 ColumnCount { get; internal set; }

    /// <summary>启用 LZ4 压缩的列数</summary>
    public Int32 CompressedColumnCount { get; internal set; }

    /// <summary>压缩率，行式体积与列式体积的比值，无数据时为 0</summary>
    public Double CompressionRatio => BlockBytes > 0 ? (Double)RowBytes / BlockBytes : 0;

    /// <summary>存储节省率，0~1，无数据时为 0</summary>
    public Double SavedRatio => RowBytes > 0 ? 1.0 - (Double)BlockBytes / RowBytes : 0;
}
