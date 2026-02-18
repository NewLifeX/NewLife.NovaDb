namespace NewLife.NovaDb.Engine.Flux;

/// <summary>死信队列条目</summary>
public class DeadLetterEntry
{
    /// <summary>消息 ID</summary>
    public MessageId Id { get; set; } = null!;

    /// <summary>原始消息数据</summary>
    public FluxEntry Entry { get; set; } = null!;

    /// <summary>失败原因</summary>
    public String Reason { get; set; } = String.Empty;

    /// <summary>失败次数</summary>
    public Int32 FailureCount { get; set; }

    /// <summary>进入死信队列的时间</summary>
    public DateTime CreatedAt { get; set; }
}
