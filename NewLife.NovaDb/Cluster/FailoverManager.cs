using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Cluster;

/// <summary>手动故障切换管理器，负责将从节点提升为主节点</summary>
/// <remarks>
/// 对应模块 R04（手动故障切换），提供手动 Failover 能力：
/// 1. 选择 LSN 最大的从节点提升为新主节点
/// 2. 其余从节点重新指向新主
/// 3. 切换前校验同步延迟
/// </remarks>
public class FailoverManager
{
    private readonly ReplicationManager _replication;
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif

    /// <summary>允许切换的最大复制延迟（LSN 差值）。超过此值拒绝切换，避免数据丢失。默认 100</summary>
    public UInt64 MaxAllowedLag { get; set; } = 100;

    /// <summary>故障切换历史记录</summary>
    public IReadOnlyList<FailoverRecord> History
    {
        get
        {
            lock (_lock)
            {
                return _history.AsReadOnly();
            }
        }
    }

    private readonly List<FailoverRecord> _history = [];

    /// <summary>创建故障切换管理器</summary>
    /// <param name="replication">复制管理器</param>
    public FailoverManager(ReplicationManager replication)
    {
        _replication = replication ?? throw new ArgumentNullException(nameof(replication));
    }

    /// <summary>手动提升指定从节点为新主节点</summary>
    /// <param name="nodeId">要提升的从节点 ID</param>
    /// <returns>故障切换结果</returns>
    public FailoverResult Promote(String nodeId)
    {
        if (nodeId == null) throw new ArgumentNullException(nameof(nodeId));

        lock (_lock)
        {
            var slave = _replication.GetSlave(nodeId);
            if (slave == null)
                throw new NovaException(ErrorCode.NodeNotFound, $"Slave node '{nodeId}' not found");

            if (slave.State == NodeState.Offline)
                throw new NovaException(ErrorCode.ReplicationError, $"Cannot promote offline node '{nodeId}'");

            // 检查复制延迟
            var lag = _replication.GetReplicationLag(nodeId);
            if (lag > MaxAllowedLag)
                throw new NovaException(ErrorCode.ReplicationLag,
                    $"Replication lag ({lag}) exceeds max allowed ({MaxAllowedLag}). Force promote or wait for sync.");

            // 提升从节点为新主节点
            var oldMaster = _replication.MasterInfo;
            var oldMasterLsn = _replication.MasterLsn;

            // 将旧主标记为离线
            oldMaster.State = NodeState.Offline;

            // 提升目标节点
            slave.Role = NodeRole.Master;
            slave.State = NodeState.Online;

            // 从复制列表中移除已提升的节点
            _replication.RemoveSlave(nodeId);

            // 其余从节点状态标记为需要重新同步
            var remainingSlaves = _replication.GetAllSlaves();
            foreach (var s in remainingSlaves)
            {
                s.State = NodeState.Syncing;
            }

            var record = new FailoverRecord
            {
                OldMasterId = oldMaster.NodeId,
                NewMasterId = nodeId,
                OldMasterLsn = oldMasterLsn,
                NewMasterLsn = slave.ReplicatedLsn,
                FailoverTime = DateTime.UtcNow,
                DataLossLsn = oldMasterLsn > slave.ReplicatedLsn ? oldMasterLsn - slave.ReplicatedLsn : 0
            };
            _history.Add(record);

            return new FailoverResult
            {
                Success = true,
                NewMaster = slave,
                OldMaster = oldMaster,
                DataLossLsn = record.DataLossLsn,
                RemainingSlaves = remainingSlaves.Count
            };
        }
    }

    /// <summary>强制提升（忽略延迟检查），用于主节点不可用时</summary>
    /// <param name="nodeId">要提升的从节点 ID</param>
    /// <returns>故障切换结果</returns>
    public FailoverResult ForcePromote(String nodeId)
    {
        var saved = MaxAllowedLag;
        MaxAllowedLag = UInt64.MaxValue;
        try
        {
            return Promote(nodeId);
        }
        finally
        {
            MaxAllowedLag = saved;
        }
    }

    /// <summary>自动选择最佳从节点（LSN 最大）并提升</summary>
    /// <returns>故障切换结果</returns>
    public FailoverResult PromoteBest()
    {
        lock (_lock)
        {
            var slaves = _replication.GetAllSlaves();
            if (slaves.Count == 0)
                throw new NovaException(ErrorCode.ReplicationError, "No slave nodes available for failover");

            // 选择 LSN 最大的从节点
            NodeInfo? best = null;
            foreach (var s in slaves)
            {
                if (s.State == NodeState.Offline) continue;
                if (best == null || s.ReplicatedLsn > best.ReplicatedLsn)
                    best = s;
            }

            if (best == null)
                throw new NovaException(ErrorCode.ReplicationError, "No online slave nodes available for failover");

            return Promote(best.NodeId);
        }
    }
}

/// <summary>故障切换结果</summary>
public class FailoverResult
{
    /// <summary>是否成功</summary>
    public Boolean Success { get; set; }

    /// <summary>新主节点</summary>
    public NodeInfo NewMaster { get; set; } = null!;

    /// <summary>旧主节点</summary>
    public NodeInfo OldMaster { get; set; } = null!;

    /// <summary>可能丢失的 LSN 数量</summary>
    public UInt64 DataLossLsn { get; set; }

    /// <summary>剩余从节点数量</summary>
    public Int32 RemainingSlaves { get; set; }
}

/// <summary>故障切换历史记录</summary>
public class FailoverRecord
{
    /// <summary>旧主节点 ID</summary>
    public String OldMasterId { get; set; } = String.Empty;

    /// <summary>新主节点 ID</summary>
    public String NewMasterId { get; set; } = String.Empty;

    /// <summary>旧主 LSN</summary>
    public UInt64 OldMasterLsn { get; set; }

    /// <summary>新主 LSN</summary>
    public UInt64 NewMasterLsn { get; set; }

    /// <summary>可能丢失的 LSN</summary>
    public UInt64 DataLossLsn { get; set; }

    /// <summary>切换时间</summary>
    public DateTime FailoverTime { get; set; }
}
