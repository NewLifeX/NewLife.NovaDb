using NewLife.Messaging;
using NewLife.NovaDb.Engine.Flux;

namespace NewLife.NovaDb.Queues;

/// <summary>NovaDb 事件总线，基于 NovaQueue 实现 IEventBus 接口</summary>
/// <typeparam name="TEvent">事件类型</typeparam>
public class NovaEventBus<TEvent> : EventBus<TEvent>
{
    private readonly NovaQueue<TEvent> _queue;
    private CancellationTokenSource? _cts;

    /// <summary>创建 NovaEventBus 实例</summary>
    /// <param name="streamManager">流管理器</param>
    /// <param name="topic">事件主题</param>
    /// <param name="clientId">客户标识/消费组</param>
    public NovaEventBus(StreamManager streamManager, String topic, String clientId = "")
    {
        _queue = new NovaQueue<TEvent>(streamManager, topic);

        // 如果指定了消费组，设置并启动后台消费
        if (!String.IsNullOrEmpty(clientId))
        {
            _queue.SetGroup(clientId);
            StartConsuming();
        }
    }

    /// <summary>发布事件</summary>
    /// <param name="event">事件</param>
    /// <param name="context">事件上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>成功处理的数量</returns>
    public override async Task<Int32> PublishAsync(TEvent @event, IEventContext? context = null, CancellationToken cancellationToken = default)
    {
        // 发布到队列
        _queue.Add(@event);

        // 同时分发给本地订阅者
        return await base.PublishAsync(@event, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>启动后台消费循环</summary>
    private void StartConsuming()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            await _queue.ConsumeAsync(msg =>
            {
                // 收到队列消息后分发给本地订阅者
                _ = base.PublishAsync(msg);
            }, token).ConfigureAwait(false);
        }, token);
    }

    /// <summary>释放资源</summary>
    /// <param name="disposing">是否由 Dispose 调用</param>
    protected override void Dispose(Boolean disposing)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _queue.Dispose();

        base.Dispose(disposing);
    }
}
