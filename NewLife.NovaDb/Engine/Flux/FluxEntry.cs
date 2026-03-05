namespace NewLife.NovaDb.Engine.Flux;

/// <summary>时序数据条目</summary>
public class FluxEntry
{
    /// <summary>UTC 时间戳（Ticks）</summary>
    public Int64 Timestamp { get; set; }

    /// <summary>同毫秒内自增序号</summary>
    public Int32 SequenceId { get; set; }

    /// <summary>字段值集合</summary>
    public Dictionary<String, Object?> Fields { get; set; } = [];

    /// <summary>标签集合</summary>
    public Dictionary<String, String> Tags { get; set; } = [];

    /// <summary>获取消息 ID，格式为 "timestamp-seq"</summary>
    /// <returns>消息 ID 字符串</returns>
    public String GetMessageId() => $"{Timestamp}-{SequenceId}";

    /// <summary>解析消息 ID</summary>
    /// <param name="id">消息 ID 字符串</param>
    /// <returns>时间戳和序列号元组</returns>
    public static (Int64 timestamp, Int32 seq) ParseMessageId(String id)
    {
        if (id == null) throw new ArgumentNullException(nameof(id));

        var dashIndex = id.IndexOf('-');
        if (dashIndex < 0)
            throw new FormatException($"Invalid message ID format: '{id}'");

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        var timestamp = Int64.Parse(id.AsSpan(0, dashIndex));
        var seq = Int32.Parse(id.AsSpan(dashIndex + 1));
        return (timestamp, seq);
#else
        var timestamp = id[..dashIndex].ToLong();
        var seq = id[(dashIndex + 1)..].ToInt();
        return (timestamp, seq);
#endif
    }
}
