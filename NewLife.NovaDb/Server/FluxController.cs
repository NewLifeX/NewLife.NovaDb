using NewLife;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;
using NewLife.Remoting;

namespace NewLife.NovaDb.Server;

/// <summary>Flux 时序流 RPC 控制器，提供消息队列操作接口</summary>
/// <remarks>
/// 控制器方法通过 Remoting RPC 暴露为远程接口。
/// 路由格式：Flux/{方法名}，如 Flux/Publish、Flux/ReadGroup。
/// 控制器实例由 Remoting 框架按请求创建，共享状态（FluxEngine 与消费组）由 NovaServer 按实例注入。
/// </remarks>
internal class FluxController : IApi
{
    /// <summary>会话</summary>
    public IApiSession Session { get; set; } = null!;

    /// <summary>共享 Flux 引擎，由 NovaServer 启动时注入</summary>
    internal FluxEngine? SharedEngine { get; set; }

    /// <summary>共享消费组集合（由服务器持有，按实例注入）</summary>
    internal Dictionary<String, ConsumerGroup> ConsumerGroups { get; set; } = [];

    /// <summary>消费组集合锁（由服务器持有，按实例注入）</summary>
#if NET9_0_OR_GREATER
    internal System.Threading.Lock SyncRoot { get; set; } = new();
#else
    internal Object SyncRoot { get; set; } = new();
#endif

    /// <summary>发布消息到流</summary>
    /// <param name="data">消息字段数据</param>
    /// <returns>消息 ID 字符串</returns>
    // 值类型标注为 Object 以匹配 NewLife.Remoting 源生成器生成的调用代码（其将 Object? 归一化为 Object），保持构建零可空告警
    public String? Publish(IDictionary<String, Object>? data)
    {
        if (SharedEngine == null) return null;

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

        SharedEngine.Append(entry);
        var mid = new MessageId(entry.Timestamp, entry.SequenceId);
        return mid.ToString();
    }

    /// <summary>创建消费组</summary>
    /// <param name="groupName">消费组名称</param>
    /// <returns>是否成功</returns>
    public Boolean CreateGroup(String groupName)
    {
        if (SharedEngine == null) return false;

        lock (SyncRoot)
        {
            if (!ConsumerGroups.ContainsKey(groupName))
                ConsumerGroups[groupName] = new ConsumerGroup(groupName);
        }
        return true;
    }

    /// <summary>消费组读取消息</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="consumer">消费者名称</param>
    /// <param name="count">最大读取数量</param>
    /// <returns>消息列表</returns>
    public Object? ReadGroup(String groupName, String consumer, Int32 count = 10)
    {
        if (SharedEngine == null) return null;

        lock (SyncRoot)
        {
            if (!ConsumerGroups.TryGetValue(groupName, out var group))
                throw new NovaException(ErrorCode.ConsumerGroupNotFound, "Consumer group '{0}' not found", groupName);

            var allEntries = SharedEngine.GetAllEntries();
            var result = new List<FluxEntry>();

            foreach (var entry in allEntries)
            {
                if (result.Count >= count) break;

                var mid = new MessageId(entry.Timestamp, entry.SequenceId);
                if (group.LastDeliveredId != null && mid.CompareTo(group.LastDeliveredId) <= 0)
                    continue;

                result.Add(entry);
                group.AddPending(mid, consumer);
                group.LastDeliveredId = mid;
            }

            // 元素同样使用字典：NativeAOT 下匿名类型成员会被裁剪
            return result.Select(e => new Dictionary<String, Object?>
            {
                ["Id"] = new MessageId(e.Timestamp, e.SequenceId).ToString(),
                ["Fields"] = e.Fields
            }).ToArray();
        }
    }

    /// <summary>确认消息</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="messageId">消息 ID 字符串</param>
    /// <returns>是否成功</returns>
    public Boolean Ack(String groupName, String messageId)
    {
        if (SharedEngine == null) return false;

        var mid = MessageId.Parse(messageId);
        if (mid == null) return false;

        lock (SyncRoot)
        {
            if (!ConsumerGroups.TryGetValue(groupName, out var group))
                return false;

            return group.Acknowledge(mid);
        }
    }
}
