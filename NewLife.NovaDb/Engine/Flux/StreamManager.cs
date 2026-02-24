using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Engine.Flux;

/// <summary>流管理器，在 FluxEngine 上层提供 Stream/MQ 语义</summary>
public class StreamManager : IDisposable
{
    private readonly FluxEngine _engine;
    private readonly Dictionary<String, ConsumerGroup> _consumerGroups = [];
    private readonly Object _lock = new();
    private Boolean _disposed;

    #region 延迟消息
    private readonly SortedList<DateTime, List<FluxEntry>> _delayedMessages = [];
    private readonly Object _delayLock = new();
    #endregion

    #region 死信队列
    private readonly Dictionary<String, List<DeadLetterEntry>> _deadLetters = [];
    private readonly Object _deadLetterLock = new();
    #endregion

    #region 阻塞读取信号
    private readonly Dictionary<String, SemaphoreSlim> _newMessageSignal = [];
    private readonly Object _signalLock = new();
    #endregion

    /// <summary>最大重试次数（默认 3）</summary>
    public Int32 MaxRetryCount { get; set; } = 3;

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
        var id = new MessageId(entry.Timestamp, entry.SequenceId);

        // 通知所有等待中的消费组
        NotifyNewMessage();

        return id;
    }

    /// <summary>发布延迟消息</summary>
    /// <param name="entry">时序条目</param>
    /// <param name="delay">延迟时间</param>
    /// <returns>消息 ID（投递后的 ID，投递前为预分配）</returns>
    public MessageId PublishDelayed(FluxEntry entry, TimeSpan delay)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));

        return PublishScheduled(entry, DateTime.UtcNow + delay);
    }

    /// <summary>发布定时投递消息</summary>
    /// <param name="entry">时序条目</param>
    /// <param name="deliverAt">投递时间（UTC）</param>
    /// <returns>消息 ID（预分配，投递后生效）</returns>
    public MessageId PublishScheduled(FluxEntry entry, DateTime deliverAt)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));

        lock (_delayLock)
        {
            if (!_delayedMessages.TryGetValue(deliverAt, out var list))
            {
                list = [];
                _delayedMessages[deliverAt] = list;
            }
            list.Add(entry);
        }

        return new MessageId(entry.Timestamp, entry.SequenceId);
    }

    /// <summary>投递到期的延迟消息</summary>
    /// <returns>投递的消息数量</returns>
    public Int32 DeliverDueMessages()
    {
        var now = DateTime.UtcNow;
        var dueEntries = new List<FluxEntry>();

        lock (_delayLock)
        {
            var keysToRemove = new List<DateTime>();
            foreach (var kvp in _delayedMessages)
            {
                if (kvp.Key > now) break;

                dueEntries.AddRange(kvp.Value);
                keysToRemove.Add(kvp.Key);
            }

            foreach (var key in keysToRemove)
            {
                _delayedMessages.Remove(key);
            }
        }

        foreach (var entry in dueEntries)
        {
            Publish(entry);
        }

        return dueEntries.Count;
    }

    /// <summary>获取延迟消息数量</summary>
    /// <returns>延迟消息总数</returns>
    public Int32 GetDelayedMessageCount()
    {
        lock (_delayLock)
        {
            var count = 0;
            foreach (var list in _delayedMessages.Values)
            {
                count += list.Count;
            }
            return count;
        }
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

        // 先投递到期的延迟消息
        DeliverDueMessages();

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

    /// <summary>阻塞读取消息，支持超时等待</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="consumer">消费者名称</param>
    /// <param name="count">最大读取数量</param>
    /// <param name="timeout">超时时间</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>消息列表（超时返回空列表）</returns>
    public async Task<List<FluxEntry>> ReadGroupAsync(
        String groupName, String consumer, Int32 count,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (groupName == null) throw new ArgumentNullException(nameof(groupName));
        if (consumer == null) throw new ArgumentNullException(nameof(consumer));

        // 先尝试同步读取
        var result = ReadGroup(groupName, consumer, count);
        if (result.Count > 0) return result;

        // 没有消息，等待信号
        var signal = GetOrCreateSignal(groupName);

        // 等待新消息或超时
        var acquired = await signal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (!acquired) return [];

        // 被唤醒后再次读取
        return ReadGroup(groupName, consumer, count);
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

    #region 死信队列
    /// <summary>将消息移入死信队列</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="id">消息 ID</param>
    /// <param name="reason">失败原因</param>
    public void MoveToDeadLetter(String groupName, MessageId id, String reason)
    {
        if (groupName == null) throw new ArgumentNullException(nameof(groupName));
        if (id == null) throw new ArgumentNullException(nameof(id));

        FluxEntry? entry = null;
        PendingEntry? pending = null;

        lock (_lock)
        {
            if (!_consumerGroups.TryGetValue(groupName, out var group))
                throw new NovaException(ErrorCode.ConsumerGroupNotFound, $"Consumer group '{groupName}' not found");

            // 查找待确认条目中的投递次数
            var pendingEntries = group.GetPendingEntries();
            pending = pendingEntries.Find(p => p.Id.Equals(id));

            // 从原始数据中查找条目
            var allEntries = _engine.GetAllEntries();
            entry = allEntries.Find(e => e.Timestamp == id.Timestamp && e.SequenceId == id.Sequence);

            // 从待确认列表中移除
            group.Acknowledge(id);
        }

        var deadLetter = new DeadLetterEntry
        {
            Id = id,
            Entry = entry ?? new FluxEntry { Timestamp = id.Timestamp, SequenceId = id.Sequence },
            Reason = reason ?? String.Empty,
            FailureCount = pending?.DeliveryCount ?? 1,
            CreatedAt = DateTime.UtcNow
        };

        lock (_deadLetterLock)
        {
            if (!_deadLetters.TryGetValue(groupName, out var list))
            {
                list = [];
                _deadLetters[groupName] = list;
            }
            list.Add(deadLetter);
        }
    }

    /// <summary>读取死信队列消息</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="count">最大读取数量</param>
    /// <returns>死信消息列表</returns>
    public List<DeadLetterEntry> ReadDeadLetters(String groupName, Int32 count = 100)
    {
        if (groupName == null) throw new ArgumentNullException(nameof(groupName));

        lock (_deadLetterLock)
        {
            if (!_deadLetters.TryGetValue(groupName, out var list))
                return [];

            return list.Count <= count ? [.. list] : [.. list.GetRange(0, count)];
        }
    }

    /// <summary>重新投递死信消息</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="id">消息 ID</param>
    /// <returns>是否成功</returns>
    public Boolean RetryDeadLetter(String groupName, MessageId id)
    {
        if (groupName == null) throw new ArgumentNullException(nameof(groupName));
        if (id == null) throw new ArgumentNullException(nameof(id));

        DeadLetterEntry? deadLetter = null;

        lock (_deadLetterLock)
        {
            if (!_deadLetters.TryGetValue(groupName, out var list))
                return false;

            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Id.Equals(id))
                {
                    deadLetter = list[i];
                    list.RemoveAt(i);
                    break;
                }
            }
        }

        if (deadLetter == null) return false;

        // 重新发布到流
        Publish(deadLetter.Entry);
        return true;
    }

    /// <summary>删除死信消息</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="id">消息 ID</param>
    /// <returns>是否成功</returns>
    public Boolean DeleteDeadLetter(String groupName, MessageId id)
    {
        if (groupName == null) throw new ArgumentNullException(nameof(groupName));
        if (id == null) throw new ArgumentNullException(nameof(id));

        lock (_deadLetterLock)
        {
            if (!_deadLetters.TryGetValue(groupName, out var list))
                return false;

            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Id.Equals(id))
                {
                    list.RemoveAt(i);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>自动检查并移动超过重试次数的消息到死信队列</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="maxAge">最大存活时间</param>
    /// <returns>移入死信队列的消息数量</returns>
    public Int32 ProcessExpiredPending(String groupName, TimeSpan maxAge)
    {
        if (groupName == null) throw new ArgumentNullException(nameof(groupName));

        List<PendingEntry> toMove;
        var now = DateTime.UtcNow;

        lock (_lock)
        {
            if (!_consumerGroups.TryGetValue(groupName, out var group))
                throw new NovaException(ErrorCode.ConsumerGroupNotFound, $"Consumer group '{groupName}' not found");

            var pending = group.GetPendingEntries();
            toMove = pending.FindAll(p => p.DeliveryCount > MaxRetryCount || (now - p.DeliveredAt) > maxAge);
        }

        var count = 0;
        foreach (var p in toMove)
        {
            var reason = p.DeliveryCount > MaxRetryCount
                ? $"Exceeded max retry count ({MaxRetryCount})"
                : $"Message age exceeded max age ({maxAge})";

            MoveToDeadLetter(groupName, p.Id, reason);
            count++;
        }

        return count;
    }
    #endregion

    /// <summary>获取消息总数</summary>
    /// <returns>消息总数</returns>
    public Int64 GetMessageCount() => _engine.GetEntryCount();

    #region 辅助
    /// <summary>获取或创建消费组的信号量</summary>
    /// <param name="groupName">消费组名称</param>
    /// <returns>信号量实例</returns>
    private SemaphoreSlim GetOrCreateSignal(String groupName)
    {
        lock (_signalLock)
        {
            if (!_newMessageSignal.TryGetValue(groupName, out var signal))
            {
                signal = new SemaphoreSlim(0);
                _newMessageSignal[groupName] = signal;
            }
            return signal;
        }
    }

    /// <summary>通知所有消费组有新消息到达</summary>
    private void NotifyNewMessage()
    {
        lock (_signalLock)
        {
            foreach (var signal in _newMessageSignal.Values)
            {
                // 仅在没有可用信号时释放，避免信号量无限增长
                if (signal.CurrentCount == 0)
                    signal.Release();
            }
        }
    }
    #endregion

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_signalLock)
        {
            foreach (var signal in _newMessageSignal.Values)
            {
                signal.Dispose();
            }
            _newMessageSignal.Clear();
        }
    }
}
