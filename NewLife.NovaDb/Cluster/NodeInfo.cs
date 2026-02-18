namespace NewLife.NovaDb.Cluster;

/// <summary>节点角色</summary>
public enum NodeRole
{
    /// <summary>主节点</summary>
    Master,

    /// <summary>从节点</summary>
    Slave
}

/// <summary>节点状态</summary>
public enum NodeState
{
    /// <summary>离线</summary>
    Offline,

    /// <summary>同步中</summary>
    Syncing,

    /// <summary>在线</summary>
    Online
}

/// <summary>集群节点信息</summary>
public class NodeInfo
{
    /// <summary>节点 ID</summary>
    public String NodeId { get; set; } = String.Empty;

    /// <summary>节点地址（host:port）</summary>
    public String Endpoint { get; set; } = String.Empty;

    /// <summary>角色</summary>
    public NodeRole Role { get; set; }

    /// <summary>状态</summary>
    public NodeState State { get; set; } = NodeState.Offline;

    /// <summary>最新已复制 LSN</summary>
    public UInt64 ReplicatedLsn { get; set; }

    /// <summary>最后心跳时间</summary>
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;

    /// <summary>加入时间</summary>
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}
