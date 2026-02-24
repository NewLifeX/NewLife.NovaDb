using System.Diagnostics.CodeAnalysis;
using NewLife.Caching;
using NewLife.NovaDb.Engine.Flux;
using NewLife.Serialization;

namespace NewLife.NovaDb.Queues;

/// <summary>NovaDb 消息队列，基于 StreamManager 实现 IProducerConsumer 接口</summary>
/// <remarks>
/// 参考 RedisStream 设计，提供生产消费能力，支持消费组和 ConsumeAsync 大循环。
/// Pending 中死信处理、过期消费组消费者清理由 Flux 队列引擎自身实现，NovaQueue 作为客户端不实现。
/// </remarks>
/// <typeparam name="T">消息类型</typeparam>
public class NovaQueue<T> : IProducerConsumer<T>, IDisposable
{
    #region 属性
    private readonly StreamManager _streamManager;
    private readonly String _topic;
    private Boolean _disposed;

    /// <summary>消费者组名称。指定消费组后使用消费组模式</summary>
    public String? Group { get; set; }

    /// <summary>消费者名称</summary>
    public String Consumer { get; set; }

    /// <summary>异步消费时的阻塞等待时间（秒）。默认 15 秒</summary>
    public Int32 BlockTime { get; set; } = 15;

    /// <summary>最大重试次数。默认 10 次</summary>
    public Int32 MaxRetry { get; set; } = 10;

    /// <summary>主题名称</summary>
    public String Topic => _topic;

    /// <summary>队列消息总数</summary>
    public Int32 Count => (Int32)_streamManager.GetMessageCount();

    /// <summary>队列是否为空</summary>
    public Boolean IsEmpty => Count == 0;
    #endregion

    #region 构造
    /// <summary>创建 NovaQueue 实例</summary>
    /// <param name="streamManager">流管理器</param>
    /// <param name="topic">主题名称</param>
    public NovaQueue(StreamManager streamManager, String topic)
    {
        _streamManager = streamManager ?? throw new ArgumentNullException(nameof(streamManager));
        _topic = topic ?? throw new ArgumentNullException(nameof(topic));
        Consumer = $"{Environment.MachineName}@{Guid.NewGuid().ToString("N")[..8]}";
    }

    /// <summary>销毁</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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
        _streamManager.CreateConsumerGroup(group);
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

            _streamManager.Publish(entry);
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

        var entries = _streamManager.ReadGroup(Group!, Consumer, count);
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
        var entries = _streamManager.ReadGroupAsync(Group!, Consumer, 1, ts).ConfigureAwait(false).GetAwaiter().GetResult();
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
        var entries = await _streamManager.ReadGroupAsync(Group!, Consumer, 1, ts, cancellationToken).ConfigureAwait(false);
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
            if (mid != null && _streamManager.Acknowledge(Group!, mid))
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
                var entries = await _streamManager.ReadGroupAsync(Group!, Consumer, 1, ts, cancellationToken).ConfigureAwait(false);
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
                        if (mid != null) _streamManager.Acknowledge(Group!, mid);
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

        var type = typeof(T);
        if (type == typeof(Int32) || type == typeof(Int64) || type == typeof(Double) ||
            type == typeof(Single) || type == typeof(Decimal) || type == typeof(Boolean))
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
        if (type == typeof(Int32)) return (T)(Object)Int32.Parse(str);
        if (type == typeof(Int64)) return (T)(Object)Int64.Parse(str);
        if (type == typeof(Double)) return (T)(Object)Double.Parse(str);
        if (type == typeof(Single)) return (T)(Object)Single.Parse(str);
        if (type == typeof(Decimal)) return (T)(Object)Decimal.Parse(str);
        if (type == typeof(Boolean)) return (T)(Object)Boolean.Parse(str);
        if (type == typeof(Object)) return (T)(Object)str;

        return str.ToJsonEntity<T>();
    }
    #endregion
}
