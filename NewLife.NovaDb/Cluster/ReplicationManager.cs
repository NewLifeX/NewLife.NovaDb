using NewLife.NovaDb.Core;
using NewLife.NovaDb.WAL;

namespace NewLife.NovaDb.Cluster;

/// <summary>主节点复制管理器，负责 WAL 流复制</summary>
public class ReplicationManager : IDisposable
{
    private readonly String _walPath;
    private readonly Dictionary<String, NodeInfo> _slaves = new();
    private readonly Dictionary<String, UInt64> _slavePositions = new();
    private readonly Object _lock = new();
    private Boolean _disposed;

    private readonly List<WalRecord> _replicationBuffer = new();
    private UInt64 _masterLsn;

    /// <summary>复制缓冲区最大记录数，默认 100 万</summary>
    public Int32 MaxBufferSize { get; set; } = 1_000_000;

    /// <summary>主节点信息</summary>
    public NodeInfo MasterInfo { get; }

    /// <summary>从节点数量</summary>
    public Int32 SlaveCount
    {
        get
        {
            lock (_lock)
            {
                return _slaves.Count;
            }
        }
    }

    /// <summary>当前主 LSN</summary>
    public UInt64 MasterLsn
    {
        get
        {
            lock (_lock)
            {
                return _masterLsn;
            }
        }
    }

    /// <summary>创建复制管理器</summary>
    /// <param name="walPath">WAL 文件路径</param>
    /// <param name="masterInfo">主节点信息</param>
    public ReplicationManager(String walPath, NodeInfo masterInfo)
    {
        _walPath = walPath ?? throw new ArgumentNullException(nameof(walPath));
        if (masterInfo == null) throw new ArgumentNullException(nameof(masterInfo));
        if (masterInfo.Role != NodeRole.Master)
            throw new NovaException(ErrorCode.NotMaster, "节点角色必须为 Master");

        MasterInfo = masterInfo;
        MasterInfo.State = NodeState.Online;
    }

    /// <summary>注册从节点</summary>
    /// <param name="slave">从节点信息</param>
    public void RegisterSlave(NodeInfo slave)
    {
        if (slave == null) throw new ArgumentNullException(nameof(slave));
        if (slave.Role != NodeRole.Slave)
            throw new NovaException(ErrorCode.ReplicationError, "只能注册从节点角色");

        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ReplicationManager));

            if (_slaves.ContainsKey(slave.NodeId))
                throw new NovaException(ErrorCode.ReplicationError, $"从节点 {slave.NodeId} 已注册");

            slave.State = NodeState.Syncing;
            _slaves[slave.NodeId] = slave;
            _slavePositions[slave.NodeId] = 0;
        }
    }

    /// <summary>移除从节点</summary>
    /// <param name="nodeId">节点 ID</param>
    /// <returns>是否移除成功</returns>
    public Boolean RemoveSlave(String nodeId)
    {
        if (nodeId == null) throw new ArgumentNullException(nameof(nodeId));

        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ReplicationManager));

            var removed = _slaves.Remove(nodeId);
            _slavePositions.Remove(nodeId);
            return removed;
        }
    }

    /// <summary>获取从节点信息</summary>
    /// <param name="nodeId">节点 ID</param>
    /// <returns>节点信息，不存在则返回 null</returns>
    public NodeInfo? GetSlave(String nodeId)
    {
        if (nodeId == null) throw new ArgumentNullException(nameof(nodeId));

        lock (_lock)
        {
            return _slaves.TryGetValue(nodeId, out var node) ? node : null;
        }
    }

    /// <summary>获取所有从节点</summary>
    /// <returns>从节点列表</returns>
    public List<NodeInfo> GetAllSlaves()
    {
        lock (_lock)
        {
            return new List<NodeInfo>(_slaves.Values);
        }
    }

    /// <summary>追加 WAL 记录到复制缓冲区</summary>
    /// <param name="record">WAL 记录</param>
    public void AppendRecord(WalRecord record)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));

        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ReplicationManager));

            _masterLsn++;
            record.Lsn = _masterLsn;
            _replicationBuffer.Add(record);

            // 超过缓冲区限制时移除最旧的记录
            while (_replicationBuffer.Count > MaxBufferSize)
            {
                _replicationBuffer.RemoveAt(0);
            }
        }
    }

    /// <summary>获取从节点待复制的记录</summary>
    /// <param name="nodeId">节点 ID</param>
    /// <param name="maxCount">最大返回数量</param>
    /// <returns>待复制的 WAL 记录列表</returns>
    public List<WalRecord> GetPendingRecords(String nodeId, Int32 maxCount = 100)
    {
        if (nodeId == null) throw new ArgumentNullException(nameof(nodeId));

        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ReplicationManager));

            if (!_slaves.ContainsKey(nodeId))
                throw new NovaException(ErrorCode.NodeNotFound, $"从节点 {nodeId} 不存在");

            var ackedLsn = _slavePositions[nodeId];
            var pending = new List<WalRecord>();

            foreach (var record in _replicationBuffer)
            {
                if (record.Lsn > ackedLsn)
                {
                    pending.Add(record);
                    if (pending.Count >= maxCount) break;
                }
            }

            return pending;
        }
    }

    /// <summary>确认从节点已复制到指定 LSN</summary>
    /// <param name="nodeId">节点 ID</param>
    /// <param name="lsn">已复制的 LSN</param>
    public void AcknowledgeReplication(String nodeId, UInt64 lsn)
    {
        if (nodeId == null) throw new ArgumentNullException(nameof(nodeId));

        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ReplicationManager));

            if (!_slaves.ContainsKey(nodeId))
                throw new NovaException(ErrorCode.NodeNotFound, $"从节点 {nodeId} 不存在");

            _slavePositions[nodeId] = lsn;

            var slave = _slaves[nodeId];
            slave.ReplicatedLsn = lsn;
            slave.LastHeartbeat = DateTime.UtcNow;

            // 已追上主节点则标记为在线
            if (lsn >= _masterLsn)
                slave.State = NodeState.Online;
            else
                slave.State = NodeState.Syncing;
        }
    }

    /// <summary>获取从节点复制延迟（LSN 差值）</summary>
    /// <param name="nodeId">节点 ID</param>
    /// <returns>延迟的 LSN 数量</returns>
    public UInt64 GetReplicationLag(String nodeId)
    {
        if (nodeId == null) throw new ArgumentNullException(nameof(nodeId));

        lock (_lock)
        {
            if (!_slaves.ContainsKey(nodeId))
                throw new NovaException(ErrorCode.NodeNotFound, $"从节点 {nodeId} 不存在");

            var ackedLsn = _slavePositions[nodeId];
            return _masterLsn - ackedLsn;
        }
    }

    /// <summary>检查是否所有从节点已同步到指定 LSN</summary>
    /// <param name="lsn">目标 LSN</param>
    /// <returns>是否全部同步</returns>
    public Boolean IsFullySynced(UInt64 lsn)
    {
        lock (_lock)
        {
            if (_slaves.Count == 0) return true;

            foreach (var pos in _slavePositions.Values)
            {
                if (pos < lsn) return false;
            }

            return true;
        }
    }

    /// <summary>清理已同步的缓冲区记录</summary>
    /// <returns>清理的记录数</returns>
    public Int32 CleanupBuffer()
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ReplicationManager));

            if (_slaves.Count == 0 || _replicationBuffer.Count == 0) return 0;

            // 找到所有从节点中最小的已确认 LSN
            var minAckedLsn = UInt64.MaxValue;
            foreach (var pos in _slavePositions.Values)
            {
                if (pos < minAckedLsn) minAckedLsn = pos;
            }

            if (minAckedLsn == 0) return 0;

            // 移除 LSN <= minAckedLsn 的记录
            var removed = _replicationBuffer.RemoveAll(r => r.Lsn <= minAckedLsn);
            return removed;
        }
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            _replicationBuffer.Clear();
            _slaves.Clear();
            _slavePositions.Clear();
        }
    }
}
