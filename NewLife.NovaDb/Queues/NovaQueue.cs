using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using NewLife.Caching;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;
using NewLife.Serialization;
#if NET9_0_OR_GREATER
using LockType = System.Threading.Lock;
#else
using LockType = System.Object;
#endif

namespace NewLife.NovaDb.Queues;

/// <summary>NovaDb 消息队列，基于 FluxEngine 实现 IProducerConsumer 接口</summary>
/// <remarks>
/// 参考 RedisStream 设计，提供生产消费能力，支持消费组、ConsumeAsync 大循环、延迟消息和死信队列。
/// 内置消费组管理、Pending/Ack、延迟投递、死信队列及阻塞读取等完整 Stream/MQ 语义。
/// </remarks>
/// <typeparam name="T">消息类型</typeparam>
public class NovaQueue<T> : IProducerConsumer<T>, IDisposable
{
    #region 属性
    private readonly FluxEngine _engine;
    private readonly String _topic;
    private readonly Dictionary<String, ConsumerGroup> _consumerGroups = [];
    private readonly LockType _lock = new();
    private Boolean _disposed;

    #region 延迟消息
    private readonly SortedList<DateTime, List<FluxEntry>> _delayedMessages = [];
    private readonly LockType _delayLock = new();
    #endregion

    #region 死信队列
    private readonly Dictionary<String, List<DeadLetterEntry>> _deadLetters = [];
    private readonly LockType _deadLetterLock = new();
    #endregion

    #region 阻塞读取信号
    private readonly Dictionary<String, SemaphoreSlim> _newMessageSignal = [];
    private readonly LockType _signalLock = new();
    #endregion

    /// <summary>消费者组名称。指定消费组后使用消费组模式</summary>
    public String? Group { get; set; }

    /// <summary>消费者名称</summary>
    public String Consumer { get; set; }

    /// <summary>异步消费时的阻塞等待时间（秒）。默认 15 秒</summary>
    public Int32 BlockTime { get; set; } = 15;

    /// <summary>最大重试次数。默认 10 次</summary>
    public Int32 MaxRetry { get; set; } = 10;

    /// <summary>最大重试次数，超过后移入死信队列（默认 3）</summary>
    public Int32 MaxRetryCount { get; set; } = 3;

    /// <summary>主题名称</summary>
    public String Topic => _topic;

    /// <summary>队列消息总数</summary>
    public Int32 Count => (Int32)_engine.GetEntryCount();

    /// <summary>队列是否为空</summary>
    public Boolean IsEmpty => Count == 0;
    #endregion

    #region 构造
    /// <summary>创建 NovaQueue 实例</summary>
    /// <param name="engine">Flux 引擎实例</param>
    /// <param name="topic">主题名称</param>
    public NovaQueue(FluxEngine engine, String topic)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _topic = topic ?? throw new ArgumentNullException(nameof(topic));
        Consumer = $"{Environment.MachineName}@{Guid.NewGuid().ToString("N")[..8]}";
    }

    /// <summary>销毁</summary>
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
    #endregion

    #region 生产消费
    /// <summary>设置消费组。如果消费组不存在则创建</summary>
    /// <param name="group">消费组名称</param>
    /// <returns>是否成功</returns>
    public Boolean SetGroup(String group)
    {
        if (String.IsNullOrEmpty(group)) throw new ArgumentNullException(nameof(group));

        Group = group;
        CreateConsumerGroup(group);
        return true;
    }

    /// <summary>生产添加消息</summary>
    /// <param name="values">消息列表</param>
    /// <returns>添加的消息数量</returns>
    public Int32 Add(params T[] values)
    {
        if (values == null || values.Length == 0) return 0;

        var count = 0;
        foreach (var value in values)
        {
            var entry = new FluxEntry
            {
                Timestamp = DateTime.UtcNow.Ticks,
            };

            // 将消息数据放入 Fields
            entry.Fields["__topic"] = _topic;
            entry.Fields["__data"] = ConvertToString(value);
            entry.Fields["__type"] = typeof(T).Name;

            Publish(entry);
            count++;
        }
        return count;
    }

    /// <summary>消费获取一批消息</summary>
    /// <param name="count">获取数量</param>
    /// <returns>消息列表</returns>
    public IEnumerable<T> Take(Int32 count = 1)
    {
        if (String.IsNullOrEmpty(Group))
            throw new InvalidOperationException("需要先通过 SetGroup 设置消费组");

        var entries = ReadGroup(Group!, Consumer, count);
        return entries.Where(e => IsTopicMatch(e)).Select(e => ParseMessage(e)!);
    }

    /// <summary>消费获取一个消息</summary>
    /// <param name="timeout">超时（秒）。0 表示永久等待</param>
    /// <returns>消息</returns>
    public T? TakeOne(Int32 timeout = 0)
    {
        var items = Take(1).ToList();
        if (items.Count > 0) return items[0];

        if (timeout <= 0) return default;

        // 等待消息到达
        var ts = TimeSpan.FromSeconds(timeout);
        var entries = ReadGroupAsync(Group!, Consumer, 1, ts).ConfigureAwait(false).GetAwaiter().GetResult();
        var matched = entries.Where(e => IsTopicMatch(e)).Select(e => ParseMessage(e)!).ToList();
        return matched.Count > 0 ? matched[0] : default;
    }

    /// <summary>异步消费获取一个消息</summary>
    /// <param name="timeout">超时（秒）</param>
    /// <returns>消息</returns>
    public Task<T?> TakeOneAsync(Int32 timeout = 0) => TakeOneAsync(timeout, default);

    /// <summary>异步消费获取一个消息</summary>
    /// <param name="timeout">超时（秒）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>消息</returns>
    public async Task<T?> TakeOneAsync(Int32 timeout, CancellationToken cancellationToken)
    {
        if (String.IsNullOrEmpty(Group))
            throw new InvalidOperationException("需要先通过 SetGroup 设置消费组");

        var ts = timeout > 0 ? TimeSpan.FromSeconds(timeout) : TimeSpan.FromSeconds(BlockTime);
        var entries = await ReadGroupAsync(Group!, Consumer, 1, ts, cancellationToken).ConfigureAwait(false);
        var matched = entries.Where(e => IsTopicMatch(e)).Select(e => ParseMessage(e)!).ToList();
        return matched.Count > 0 ? matched[0] : default;
    }

    /// <summary>确认消费</summary>
    /// <param name="keys">消息 ID</param>
    /// <returns>确认数量</returns>
    public Int32 Acknowledge(params String[] keys)
    {
        if (String.IsNullOrEmpty(Group)) return 0;

        var count = 0;
        foreach (var key in keys)
        {
            var mid = MessageId.Parse(key);
            if (mid != null && Acknowledge(Group!, mid))
                count++;
        }
        return count;
    }
    #endregion

    #region ConsumeAsync
    /// <summary>队列消费大循环，处理消息后自动确认</summary>
    /// <param name="onMessage">消息处理回调。如果处理时抛出异常，消息将保留在 Pending 中</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns></returns>
    public async Task ConsumeAsync(Func<T, String, CancellationToken, Task> onMessage, CancellationToken cancellationToken = default)
    {
        // 打断状态机，后续逻辑在其它线程执行
        await Task.Yield();

        if (String.IsNullOrEmpty(Group))
            throw new InvalidOperationException("需要先通过 SetGroup 设置消费组");

        var timeout = BlockTime;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var ts = TimeSpan.FromSeconds(timeout);
                var entries = await ReadGroupAsync(Group!, Consumer, 1, ts, cancellationToken).ConfigureAwait(false);
                if (entries.Count > 0)
                {
                    foreach (var entry in entries.Where(e => IsTopicMatch(e)))
                    {
                        var msg = ParseMessage(entry);
                        if (msg == null) continue;

                        var msgId = entry.GetMessageId();

                        // 处理消息
                        await onMessage(msg, msgId, cancellationToken).ConfigureAwait(false);

                        // 自动确认
                        var mid = MessageId.Parse(msgId);
                        if (mid != null) Acknowledge(Group!, mid);
                    }
                }
                else
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // 消费异常时短暂等待后继续
                if (!cancellationToken.IsCancellationRequested)
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>队列消费大循环（简化版），处理消息后自动确认</summary>
    /// <param name="onMessage">消息处理回调</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns></returns>
    public Task ConsumeAsync(Action<T> onMessage, CancellationToken cancellationToken = default) => ConsumeAsync((m, k, t) =>
    {
        onMessage(m);
        return Task.FromResult(0);
    }, cancellationToken);
    #endregion

    #region 消费组管理
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

    /// <summary>获取消息总数</summary>
    /// <returns>消息总数</returns>
    public Int64 GetMessageCount() => _engine.GetEntryCount();
    #endregion

    #region 延迟消息
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
    #endregion

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

    #region 辅助
    /// <summary>检查条目是否属于当前主题</summary>
    private Boolean IsTopicMatch(FluxEntry entry)
    {
        if (!entry.Fields.TryGetValue("__topic", out var topic)) return true;
        return String.Equals(topic?.ToString(), _topic, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从条目中解析消息</summary>
    [return: MaybeNull]
    private static T ParseMessage(FluxEntry entry)
    {
        if (!entry.Fields.TryGetValue("__data", out var data) || data == null)
            return default;

        var str = data.ToString()!;
        return ConvertFromString(str);
    }

    /// <summary>将值转换为字符串</summary>
    private static String ConvertToString(T value)
    {
        if (value == null) return String.Empty;
        if (value is String str) return str;

        if (value is IFormattable fmt)
            return fmt.ToString(null, CultureInfo.InvariantCulture);

        if (typeof(T) == typeof(Boolean))
            return value.ToString()!;

        return value.ToJson();
    }

    /// <summary>从字符串转换为目标类型</summary>
    [return: MaybeNull]
    private static T ConvertFromString(String str)
    {
        if (str == null) return default;

        var type = typeof(T);
        if (type == typeof(String)) return (T)(Object)str;
        if (type == typeof(Int32)) return (T)(Object)Int32.Parse(str, CultureInfo.InvariantCulture);
        if (type == typeof(Int64)) return (T)(Object)Int64.Parse(str, CultureInfo.InvariantCulture);
        if (type == typeof(Double)) return (T)(Object)Double.Parse(str, CultureInfo.InvariantCulture);
        if (type == typeof(Single)) return (T)(Object)Single.Parse(str, CultureInfo.InvariantCulture);
        if (type == typeof(Decimal)) return (T)(Object)Decimal.Parse(str, CultureInfo.InvariantCulture);
        if (type == typeof(Boolean)) return (T)(Object)Boolean.Parse(str);
        if (type == typeof(Object)) return (T)(Object)str;

        return str.ToJsonEntity<T>();
    }

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
}
