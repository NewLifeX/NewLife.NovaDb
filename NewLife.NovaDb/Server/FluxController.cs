using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;
using NewLife.Remoting;

namespace NewLife.NovaDb.Server;

/// <summary>Flux 时序流 RPC 控制器，提供消息队列操作接口</summary>
/// <remarks>
/// 控制器方法通过 Remoting RPC 暴露为远程接口。
/// 路由格式：Flux/{方法名}，如 Flux/Publish、Flux/ReadGroup。
/// 控制器实例由 Remoting 框架按请求创建，通过静态字段共享 FluxEngine。
/// </remarks>
internal class FluxController : IApi
{
    /// <summary>会话</summary>
    public IApiSession Session { get; set; } = null!;

    /// <summary>共享 Flux 引擎，由 NovaServer 启动时设置</summary>
    internal static FluxEngine? SharedEngine { get; set; }

    /// <summary>共享消费组集合</summary>
    private static readonly Dictionary<String, ConsumerGroup> _consumerGroups = [];
#if NET9_0_OR_GREATER
    private static readonly System.Threading.Lock _lock = new();
#else
    private static readonly Object _lock = new();
#endif

    /// <summary>发布消息到流</summary>
    /// <param name="data">消息字段数据</param>
    /// <returns>消息 ID 字符串</returns>
    public String? Publish(IDictionary<String, Object?>? data)
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

        lock (_lock)
        {
            if (!_consumerGroups.ContainsKey(groupName))
                _consumerGroups[groupName] = new ConsumerGroup(groupName);
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

        lock (_lock)
        {
            if (!_consumerGroups.TryGetValue(groupName, out var group))
                throw new NovaException(ErrorCode.ConsumerGroupNotFound, $"Consumer group '{groupName}' not found");

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

            return result.Select(e => new
            {
                Id = new MessageId(e.Timestamp, e.SequenceId).ToString(),
                e.Fields
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

        lock (_lock)
        {
            if (!_consumerGroups.TryGetValue(groupName, out var group))
                return false;

            return group.Acknowledge(mid);
        }
    }

    /// <summary>清理静态状态，由 NovaServer 停止时调用</summary>
    internal static void Reset()
    {
        SharedEngine = null;
        lock (_lock)
        {
            _consumerGroups.Clear();
        }
    }
}
