using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Engine.Flux;

/// <summary>流管理器，在 FluxEngine 上层提供 Stream/MQ 语义</summary>
public class StreamManager : IDisposable
{
    private readonly FluxEngine _engine;
    private readonly Dictionary<String, ConsumerGroup> _consumerGroups = [];
    private readonly Object _lock = new();
    private Boolean _disposed;

    /// <summary>创建流管理器</summary>
    /// <param name="engine">底层 FluxEngine 实例</param>
    public StreamManager(FluxEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>发布消息到流</summary>
    /// <param name="entry">时序条目</param>
    /// <returns>消息 ID</returns>
    public MessageId Publish(FluxEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));

        _engine.Append(entry);
        return new MessageId(entry.Timestamp, entry.SequenceId);
    }

    /// <summary>创建消费组</summary>
    /// <param name="groupName">消费组名称</param>
    public void CreateConsumerGroup(String groupName)
    {
        if (groupName == null) throw new ArgumentNullException(nameof(groupName));

        lock (_lock)
        {
            if (!_consumerGroups.ContainsKey(groupName))
                _consumerGroups[groupName] = new ConsumerGroup(groupName);
        }
    }

    /// <summary>消费组读取新消息</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="consumer">消费者名称</param>
    /// <param name="count">最大读取数量</param>
    /// <returns>新消息列表</returns>
    public List<FluxEntry> ReadGroup(String groupName, String consumer, Int32 count)
    {
        if (groupName == null) throw new ArgumentNullException(nameof(groupName));
        if (consumer == null) throw new ArgumentNullException(nameof(consumer));

        lock (_lock)
        {
            if (!_consumerGroups.TryGetValue(groupName, out var group))
                throw new NovaException(ErrorCode.ConsumerGroupNotFound, $"Consumer group '{groupName}' not found");

            var allEntries = _engine.GetAllEntries();
            var result = new List<FluxEntry>();

            foreach (var entry in allEntries)
            {
                if (result.Count >= count) break;

                var mid = new MessageId(entry.Timestamp, entry.SequenceId);

                // 跳过已投递的消息
                if (group.LastDeliveredId != null && mid.CompareTo(group.LastDeliveredId) <= 0)
                    continue;

                result.Add(entry);
                group.AddPending(mid, consumer);
                group.LastDeliveredId = mid;
            }

            return result;
        }
    }

    /// <summary>确认消息</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="id">消息 ID</param>
    /// <returns>是否确认成功</returns>
    public Boolean Acknowledge(String groupName, MessageId id)
    {
        if (groupName == null) throw new ArgumentNullException(nameof(groupName));
        if (id == null) throw new ArgumentNullException(nameof(id));

        lock (_lock)
        {
            if (!_consumerGroups.TryGetValue(groupName, out var group))
                throw new NovaException(ErrorCode.ConsumerGroupNotFound, $"Consumer group '{groupName}' not found");

            return group.Acknowledge(id);
        }
    }

    /// <summary>获取消费组的待确认条目</summary>
    /// <param name="groupName">消费组名称</param>
    /// <returns>待确认条目列表</returns>
    public List<PendingEntry> GetPendingEntries(String groupName)
    {
        if (groupName == null) throw new ArgumentNullException(nameof(groupName));

        lock (_lock)
        {
            if (!_consumerGroups.TryGetValue(groupName, out var group))
                throw new NovaException(ErrorCode.ConsumerGroupNotFound, $"Consumer group '{groupName}' not found");

            return group.GetPendingEntries();
        }
    }

    /// <summary>获取所有消费组名称</summary>
    /// <returns>消费组名称列表</returns>
    public List<String> GetConsumerGroupNames()
    {
        lock (_lock)
        {
            return [.. _consumerGroups.Keys];
        }
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
