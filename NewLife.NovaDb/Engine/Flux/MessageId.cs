namespace NewLife.NovaDb.Engine.Flux;

/// <summary>消息 ID，格式为 "timestamp-sequence"</summary>
public class MessageId : IComparable<MessageId>, IEquatable<MessageId>
{
    /// <summary>UTC 时间戳（Ticks）</summary>
    public Int64 Timestamp { get; }

    /// <summary>同毫秒内自增序号</summary>
    public Int32 Sequence { get; }

    /// <summary>创建消息 ID</summary>
    /// <param name="timestamp">UTC 时间戳（Ticks）</param>
    /// <param name="sequence">序列号</param>
    public MessageId(Int64 timestamp, Int32 sequence)
    {
        Timestamp = timestamp;
        Sequence = sequence;
    }

    /// <summary>根据已有条目自动生成消息 ID，同毫秒内自增序列号</summary>
    /// <param name="entries">已有条目列表</param>
    /// <param name="timestamp">目标时间戳</param>
    /// <returns>自动递增的消息 ID</returns>
    public static MessageId Auto(IEnumerable<FluxEntry> entries, Int64 timestamp)
    {
        if (entries == null) throw new ArgumentNullException(nameof(entries));

        var maxSeq = -1;
        foreach (var entry in entries)
        {
            if (entry.Timestamp == timestamp && entry.SequenceId > maxSeq)
                maxSeq = entry.SequenceId;
        }

        return new MessageId(timestamp, maxSeq + 1);
    }

    /// <summary>转换为字符串，格式为 "timestamp-sequence"</summary>
    /// <returns>消息 ID 字符串</returns>
    public override String ToString() => $"{Timestamp}-{Sequence}";

    /// <summary>解析消息 ID 字符串</summary>
    /// <param name="value">消息 ID 字符串</param>
    /// <returns>消息 ID 实例</returns>
    public static MessageId Parse(String value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));

        var dashIndex = value.IndexOf('-');
        if (dashIndex < 0)
            throw new FormatException($"Invalid MessageId format: '{value}'");

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        var timestamp = Int64.Parse(value.AsSpan(0, dashIndex));
        var sequence = Int32.Parse(value.AsSpan(dashIndex + 1));
        return new MessageId(timestamp, sequence);
#else
        var timestamp = value[..dashIndex].ToLong();
        var sequence = value[(dashIndex + 1)..].ToInt();
        return new MessageId(timestamp, sequence);
#endif
    }

    /// <summary>比较两个消息 ID</summary>
    /// <param name="other">另一个消息 ID</param>
    /// <returns>比较结果</returns>
    public Int32 CompareTo(MessageId? other)
    {
        if (other == null) return 1;

        var cmp = Timestamp.CompareTo(other.Timestamp);
        if (cmp != 0) return cmp;

        return Sequence.CompareTo(other.Sequence);
    }

    /// <summary>判断是否相等</summary>
    /// <param name="other">另一个消息 ID</param>
    /// <returns>是否相等</returns>
    public Boolean Equals(MessageId? other)
    {
        if (other == null) return false;
        return Timestamp == other.Timestamp && Sequence == other.Sequence;
    }

    /// <summary>判断是否相等</summary>
    /// <param name="obj">另一个对象</param>
    /// <returns>是否相等</returns>
    public override Boolean Equals(Object? obj) => obj is MessageId other && Equals(other);

    /// <summary>获取哈希码</summary>
    /// <returns>哈希码</returns>
    public override Int32 GetHashCode()
    {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        return HashCode.Combine(Timestamp, Sequence);
#else
        unchecked
        {
            var hash = 17;
            hash = hash * 23 + Timestamp.GetHashCode();
            hash = hash * 23 + Sequence.GetHashCode();
            return hash;
        }
#endif
    }
}
