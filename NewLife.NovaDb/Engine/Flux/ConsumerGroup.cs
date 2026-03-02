namespace NewLife.NovaDb.Engine.Flux;

/// <summary>待确认消息条目</summary>
public class PendingEntry
{
    /// <summary>消息 ID</summary>
    public MessageId Id { get; set; } = null!;

    /// <summary>消费者名称</summary>
    public String Consumer { get; set; } = String.Empty;

    /// <summary>投递次数</summary>
    public Int32 DeliveryCount { get; set; }

    /// <summary>投递时间</summary>
    public DateTime DeliveredAt { get; set; }
}

/// <summary>消费组</summary>
public class ConsumerGroup
{
    /// <summary>消费组名称</summary>
    public String Name { get; }

    /// <summary>组游标，最后投递的消息 ID</summary>
    public MessageId? LastDeliveredId { get; set; }

    private readonly Dictionary<String, PendingEntry> _pendingEntries = [];
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif

    /// <summary>创建消费组</summary>
    /// <param name="name">消费组名称</param>
    public ConsumerGroup(String name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>确认消息，从待确认列表中移除</summary>
    /// <param name="id">消息 ID</param>
    /// <returns>是否移除成功</returns>
    public Boolean Acknowledge(MessageId id)
    {
        if (id == null) throw new ArgumentNullException(nameof(id));

        lock (_lock)
        {
            return _pendingEntries.Remove(id.ToString());
        }
    }

    /// <summary>添加到待确认列表</summary>
    /// <param name="id">消息 ID</param>
    /// <param name="consumer">消费者名称</param>
    public void AddPending(MessageId id, String consumer)
    {
        if (id == null) throw new ArgumentNullException(nameof(id));
        if (consumer == null) throw new ArgumentNullException(nameof(consumer));

        lock (_lock)
        {
            var key = id.ToString();
            if (_pendingEntries.TryGetValue(key, out var existing))
            {
                existing.DeliveryCount++;
                existing.DeliveredAt = DateTime.UtcNow;
            }
            else
            {
                _pendingEntries[key] = new PendingEntry
                {
                    Id = id,
                    Consumer = consumer,
                    DeliveryCount = 1,
                    DeliveredAt = DateTime.UtcNow
                };
            }
        }
    }

    /// <summary>获取所有待确认条目</summary>
    /// <returns>待确认条目列表</returns>
    public List<PendingEntry> GetPendingEntries()
    {
        lock (_lock)
        {
            return [.. _pendingEntries.Values];
        }
    }

    /// <summary>获取待确认消息数量</summary>
    /// <returns>待确认数量</returns>
    public Int32 GetPendingCount()
    {
        lock (_lock)
        {
            return _pendingEntries.Count;
        }
    }
}
