using NewLife.NovaDb.Engine.Flux;
using NewLife.Remoting;

namespace NewLife.NovaDb.Server;

/// <summary>Flux 时序流 RPC 控制器，提供消息队列操作接口</summary>
/// <remarks>
/// 控制器方法通过 Remoting RPC 暴露为远程接口。
/// 路由格式：Flux/{方法名}，如 Flux/Publish、Flux/ReadGroup。
/// 控制器实例由 Remoting 框架按请求创建，通过静态字段共享流管理器。
/// </remarks>
internal class FluxController : IApi
{
    /// <summary>会话</summary>
    public IApiSession Session { get; set; } = null!;

    /// <summary>共享流管理器（消息队列），由 NovaServer 启动时设置</summary>
    internal static StreamManager? SharedStreamManager { get; set; }

    /// <summary>发布消息到流</summary>
    /// <param name="data">消息字段数据</param>
    /// <returns>消息 ID 字符串</returns>
    public String? Publish(IDictionary<String, Object?>? data)
    {
        if (SharedStreamManager == null) return null;

        var entry = new FluxEntry
        {
            Timestamp = DateTime.UtcNow.Ticks,
        };

        if (data != null)
        {
            foreach (var kvp in data)
            {
                entry.Fields[kvp.Key] = kvp.Value;
            }
        }

        var mid = SharedStreamManager.Publish(entry);
        return mid.ToString();
    }

    /// <summary>创建消费组</summary>
    /// <param name="groupName">消费组名称</param>
    /// <returns>是否成功</returns>
    public Boolean CreateGroup(String groupName)
    {
        if (SharedStreamManager == null) return false;

        SharedStreamManager.CreateConsumerGroup(groupName);
        return true;
    }

    /// <summary>消费组读取消息</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="consumer">消费者名称</param>
    /// <param name="count">最大读取数量</param>
    /// <returns>消息列表</returns>
    public Object? ReadGroup(String groupName, String consumer, Int32 count = 10)
    {
        if (SharedStreamManager == null) return null;

        var entries = SharedStreamManager.ReadGroup(groupName, consumer, count);
        return entries.Select(e => new
        {
            Id = new MessageId(e.Timestamp, e.SequenceId).ToString(),
            e.Fields
        }).ToArray();
    }

    /// <summary>确认消息</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="messageId">消息 ID 字符串</param>
    /// <returns>是否成功</returns>
    public Boolean Ack(String groupName, String messageId)
    {
        if (SharedStreamManager == null) return false;

        var mid = MessageId.Parse(messageId);
        if (mid == null) return false;

        return SharedStreamManager.Acknowledge(groupName, mid);
    }
}
