using NewLife.NovaDb.Cluster;
using NewLife.NovaDb.Sql;
using NewLife.NovaDb.Tx;
using NewLife.Remoting;

namespace NewLife.NovaDb.Server;

/// <summary>NovaDb RPC 服务控制器，提供关系型引擎操作接口</summary>
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

    /// <summary>共享事务字典，跨请求维护事务状态</summary>
    private static readonly Dictionary<String, Transaction> _transactions = new(StringComparer.OrdinalIgnoreCase);
#if NET9_0_OR_GREATER
    private static readonly System.Threading.Lock _txLock = new();
#else
    private static readonly Object _txLock = new();
#endif

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
            var (header, data) = pending[i];
            events[i] = new ReplicationEventDto
            {
                Lsn = header.Lsn,
                TxId = header.TxId,
                RecordType = (Byte)header.RecordType,
                PageId = header.PageId,
                Data = data ?? [],
                Timestamp = header.Timestamp
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
