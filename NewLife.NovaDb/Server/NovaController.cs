using NewLife.NovaDb.Cluster;
using NewLife.NovaDb.Engine.Flux;
using NewLife.NovaDb.Engine.KV;
using NewLife.NovaDb.Sql;
using NewLife.NovaDb.Tx;
using NewLife.NovaDb.WAL;
using NewLife.Remoting;

namespace NewLife.NovaDb.Server;

/// <summary>NovaDb RPC 服务控制器，提供数据库操作接口</summary>
/// <remarks>
/// 控制器方法通过 Remoting RPC 暴露为远程接口。
/// 路由格式：Nova/{方法名}，如 Nova/Ping、Nova/Execute。
/// 控制器实例由 Remoting 框架按请求创建，通过静态字段共享 SQL 引擎与事务。
/// </remarks>
internal class NovaController : IApi
{
    /// <summary>会话</summary>
    public IApiSession Session { get; set; } = null!;

    /// <summary>共享 SQL 执行引擎，由 NovaServer 启动时设置</summary>
    internal static SqlEngine? SharedEngine { get; set; }

    /// <summary>共享复制管理器，由 NovaServer 启动时设置</summary>
    internal static ReplicationManager? SharedReplication { get; set; }

    /// <summary>共享 KV 存储引擎，由 NovaServer 启动时设置</summary>
    internal static KvStore? SharedKvStore { get; set; }

    /// <summary>共享流管理器（消息队列），由 NovaServer 启动时设置</summary>
    internal static StreamManager? SharedStreamManager { get; set; }

    /// <summary>共享事务字典，跨请求维护事务状态</summary>
    private static readonly Dictionary<String, Transaction> _transactions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Object _txLock = new();

    /// <summary>心跳</summary>
    /// <returns>服务器时间</returns>
    public String Ping() => DateTime.UtcNow.ToString("o");

    /// <summary>执行 SQL（非查询）</summary>
    /// <param name="sql">SQL 语句</param>
    /// <returns>受影响行数</returns>
    public Int32 Execute(String sql)
    {
        if (SharedEngine == null) return 0;

        var result = SharedEngine.Execute(sql);
        return result.AffectedRows;
    }

    /// <summary>查询</summary>
    /// <param name="sql">SQL 语句</param>
    /// <returns>查询结果</returns>
    public Object? Query(String sql)
    {
        if (SharedEngine == null) return null;

        var result = SharedEngine.Execute(sql);
        if (!result.IsQuery) return result.AffectedRows;

        return new
        {
            result.ColumnNames,
            Rows = result.Rows.Select(r => r.ToArray()).ToArray()
        };
    }

    /// <summary>开始事务</summary>
    /// <returns>事务 ID</returns>
    public String BeginTransaction()
    {
        var engine = SharedEngine;
        if (engine == null) return Guid.NewGuid().ToString("N");

        var tx = engine.TxManager.BeginTransaction();
        var txId = tx.TxId.ToString();

        lock (_txLock)
        {
            _transactions[txId] = tx;
        }

        return txId;
    }

    /// <summary>提交事务</summary>
    /// <param name="txId">事务 ID</param>
    /// <returns>是否成功</returns>
    public Boolean CommitTransaction(String txId)
    {
        lock (_txLock)
        {
            if (!_transactions.TryGetValue(txId, out var tx)) return true;

            tx.Commit();
            _transactions.Remove(txId);
            return true;
        }
    }

    /// <summary>回滚事务</summary>
    /// <param name="txId">事务 ID</param>
    /// <returns>是否成功</returns>
    public Boolean RollbackTransaction(String txId)
    {
        lock (_txLock)
        {
            if (!_transactions.TryGetValue(txId, out var tx)) return true;

            tx.Rollback();
            _transactions.Remove(txId);
            return true;
        }
    }

    #region KV 操作
    /// <summary>KV 设置键值对</summary>
    /// <param name="key">键</param>
    /// <param name="value">字符串值</param>
    /// <param name="ttlSeconds">过期时间（秒），0 表示永不过期</param>
    /// <returns>是否成功</returns>
    public Boolean KvSet(String key, String value, Int32 ttlSeconds = 0)
    {
        if (SharedKvStore == null) return false;

        var ttl = ttlSeconds > 0 ? TimeSpan.FromSeconds(ttlSeconds) : (TimeSpan?)null;
        SharedKvStore.SetString(key, value, ttl);
        return true;
    }

    /// <summary>KV 获取值</summary>
    /// <param name="key">键</param>
    /// <returns>字符串值，不存在返回 null</returns>
    public String? KvGet(String key)
    {
        if (SharedKvStore == null) return null;

        return SharedKvStore.GetString(key);
    }

    /// <summary>KV 删除键</summary>
    /// <param name="key">键</param>
    /// <returns>是否成功</returns>
    public Boolean KvDelete(String key)
    {
        if (SharedKvStore == null) return false;

        return SharedKvStore.Delete(key);
    }

    /// <summary>KV 检查键是否存在</summary>
    /// <param name="key">键</param>
    /// <returns>是否存在</returns>
    public Boolean KvExists(String key)
    {
        if (SharedKvStore == null) return false;

        return SharedKvStore.Exists(key);
    }
    #endregion

    #region 消息队列操作
    /// <summary>发布消息到流</summary>
    /// <param name="data">消息字段数据</param>
    /// <returns>消息 ID 字符串</returns>
    public String? MqPublish(IDictionary<String, Object?>? data)
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
    public Boolean MqCreateGroup(String groupName)
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
    public Object? MqReadGroup(String groupName, String consumer, Int32 count = 10)
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
    public Boolean MqAck(String groupName, String messageId)
    {
        if (SharedStreamManager == null) return false;

        var mid = MessageId.Parse(messageId);
        if (mid == null) return false;

        return SharedStreamManager.Acknowledge(groupName, mid);
    }
    #endregion

    #region 复制接口
    /// <summary>从节点注册到主节点</summary>
    /// <param name="nodeId">从节点 ID</param>
    /// <param name="endpoint">从节点地址</param>
    /// <param name="lastLsn">从节点最后已应用的 LSN</param>
    /// <returns>是否注册成功</returns>
    public Boolean RegisterSlave(String nodeId, String endpoint, UInt64 lastLsn)
    {
        if (SharedReplication == null) return false;

        var slave = new NodeInfo
        {
            NodeId = nodeId,
            Endpoint = endpoint,
            Role = NodeRole.Slave,
            ReplicatedLsn = lastLsn
        };

        SharedReplication.RegisterSlave(slave);
        return true;
    }

    /// <summary>从节点拉取 Binlog 事件</summary>
    /// <param name="nodeId">从节点 ID</param>
    /// <param name="fromLsn">起始 LSN</param>
    /// <param name="maxCount">最大返回数量</param>
    /// <returns>待复制的事件列表</returns>
    public PullBinlogResultDto PullBinlog(String nodeId, UInt64 fromLsn, Int32 maxCount)
    {
        if (SharedReplication == null)
            return new PullBinlogResultDto();

        var pending = SharedReplication.GetPendingRecords(nodeId, maxCount);
        var events = new ReplicationEventDto[pending.Count];
        for (var i = 0; i < pending.Count; i++)
        {
            var record = pending[i];
            events[i] = new ReplicationEventDto
            {
                Lsn = record.Lsn,
                TxId = record.TxId,
                RecordType = (Byte)record.RecordType,
                PageId = record.PageId,
                Data = record.Data,
                Timestamp = record.Timestamp
            };
        }

        return new PullBinlogResultDto
        {
            Events = events,
            MasterLsn = SharedReplication.MasterLsn
        };
    }

    /// <summary>从节点心跳</summary>
    /// <param name="nodeId">从节点 ID</param>
    /// <param name="lastLsn">从节点最后已应用的 LSN</param>
    /// <returns>主节点当前时间</returns>
    public String ReplicaHeartbeat(String nodeId, UInt64 lastLsn)
    {
        if (SharedReplication != null)
        {
            var slave = SharedReplication.GetSlave(nodeId);
            if (slave != null)
            {
                slave.LastHeartbeat = DateTime.UtcNow;
                slave.ReplicatedLsn = lastLsn;
            }
        }

        return DateTime.UtcNow.ToString("o");
    }

    /// <summary>从节点应用复制事件（主节点推送模式使用）</summary>
    /// <param name="events">复制事件列表</param>
    /// <returns>确认结果</returns>
    public ReplicationAckDto ApplyReplication(ReplicationEventDto[] events)
    {
        // 本接口运行在从节点的控制器上，接收主节点推送的事件
        // 实际的事件应用由 ReplicaClient 在本地处理
        // 这里返回确认结果
        var ack = new ReplicationAckDto { Success = true };
        if (events != null && events.Length > 0)
            ack.AckedLsn = events[events.Length - 1].Lsn;

        return ack;
    }
    #endregion
}
