namespace NewLife.NovaDb.Engine.Flux;

/// <summary>列式块编码统计，记录封存一个分区时的编码与压缩效果</summary>
internal sealed class FluxBlockStats
{
    /// <summary>条目数量</summary>
    public Int32 EntryCount { get; set; }

    /// <summary>列数量</summary>
    public Int32 ColumnCount { get; set; }

    /// <summary>启用 LZ4 压缩的列数量</summary>
    public Int32 CompressedColumnCount { get; set; }

    /// <summary>列负载原始字节数（压缩前）</summary>
    public Int64 RawPayloadBytes { get; set; }

    /// <summary>列负载存储字节数（压缩后）</summary>
    public Int64 StoredPayloadBytes { get; set; }
}
