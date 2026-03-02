using NewLife.Log;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.WAL;
using NewLife.Remoting;

namespace NewLife.NovaDb.Cluster;

/// <summary>主节点复制管理器，负责 Binlog 异步复制</summary>
/// <remarks>
/// 复制流程：
/// 1. 主节点写入 Binlog 事件并追加到内存缓冲区
/// 2. 后台复制线程定时检查各从节点进度
/// 3. 从缓冲区或 Binlog 文件读取待复制事件
/// 4. 通过 Remoting RPC 推送到从节点
/// 5. 从节点确认后更新复制进度
/// </remarks>
public class ReplicationManager : IDisposable
{
    private readonly String _walPath;
    private readonly Dictionary<String, NodeInfo> _slaves = [];
    private readonly Dictionary<String, UInt64> _slavePositions = [];
    private readonly Dictionary<String, ApiClient> _slaveClients = [];
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif
    private Boolean _disposed;

    private readonly List<WalRecord> _replicationBuffer = [];
    private UInt64 _masterLsn;

    private BinlogWriter? _binlogWriter;
    private Timer? _replicationTimer;
    private volatile Boolean _replicating;

    /// <summary>复制缓冲区最大记录数，默认 100 万</summary>
    public Int32 MaxBufferSize { get; set; } = 1_000_000;

    /// <summary>复制推送间隔（毫秒），默认 1000</summary>
    public Int32 ReplicationIntervalMs { get; set; } = 1000;

    /// <summary>每次推送的最大事件数，默认 100</summary>
    public Int32 MaxPushBatchSize { get; set; } = 100;

    /// <summary>主节点信息</summary>
    public NodeInfo MasterInfo { get; }

    /// <summary>Binlog 写入器（可选，设置后启用基于 Binlog 的复制）</summary>
    public BinlogWriter? BinlogWriter
    {
        get => _binlogWriter;
        set => _binlogWriter = value;
    }

    /// <summary>日志</summary>
    public ILog Log { get; set; } = Logger.Null;

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

    #region 从节点管理
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
            _slavePositions[slave.NodeId] = slave.ReplicatedLsn;
        }
    }

    /// <summary>注册从节点并建立网络连接</summary>
    /// <param name="slave">从节点信息</param>
    /// <param name="slaveUri">从节点 RPC 地址，如 tcp://192.168.1.2:3306</param>
    public void RegisterSlaveWithConnection(NodeInfo slave, String slaveUri)
    {
        if (slave == null) throw new ArgumentNullException(nameof(slave));
        if (String.IsNullOrEmpty(slaveUri)) throw new ArgumentNullException(nameof(slaveUri));

        RegisterSlave(slave);

        lock (_lock)
        {
            var client = new ApiClient(slaveUri);
            try
            {
                client.Open();
                _slaveClients[slave.NodeId] = client;
            }
            catch
            {
                // 连接失败时移除已注册的从节点
                _slaves.Remove(slave.NodeId);
                _slavePositions.Remove(slave.NodeId);
                client.TryDispose();
                throw;
            }
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

            // 关闭并移除网络连接
            if (_slaveClients.TryGetValue(nodeId, out var client))
            {
                _slaveClients.Remove(nodeId);
                client.TryDispose();
            }

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
    #endregion

    #region 记录追加
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
    #endregion

    #region 异步复制
    /// <summary>启动异步复制循环</summary>
    public void StartReplication()
    {
        if (_replicationTimer != null) return;

        _replicating = true;
        _replicationTimer = new Timer(OnReplicationTick, null, ReplicationIntervalMs, ReplicationIntervalMs);
    }

    /// <summary>停止异步复制循环</summary>
    public void StopReplication()
    {
        _replicating = false;
        _replicationTimer?.Dispose();
        _replicationTimer = null;
    }

    /// <summary>复制定时回调</summary>
    /// <param name="state">状态对象</param>
    private void OnReplicationTick(Object? state)
    {
        if (!_replicating || _disposed) return;

        List<KeyValuePair<String, ApiClient>> clients;
        lock (_lock)
        {
            if (_slaveClients.Count == 0) return;
            clients = new List<KeyValuePair<String, ApiClient>>(_slaveClients);
        }

        foreach (var kv in clients)
        {
            if (!_replicating || _disposed) break;

            try
            {
                PushToSlaveAsync(kv.Key, kv.Value).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log?.Warn("复制推送到从节点 {0} 失败: {1}", kv.Key, ex.Message);

                // 标记从节点为离线
                lock (_lock)
                {
                    if (_slaves.TryGetValue(kv.Key, out var slave))
                        slave.State = NodeState.Offline;
                }
            }
        }
    }

    /// <summary>推送待复制事件到指定从节点</summary>
    /// <param name="nodeId">从节点 ID</param>
    /// <param name="client">从节点 RPC 客户端</param>
    private async Task PushToSlaveAsync(String nodeId, ApiClient client)
    {
        // 获取待推送记录
        var pending = GetPendingRecords(nodeId, MaxPushBatchSize);
        if (pending.Count == 0) return;

        // 序列化 Binlog 事件为传输格式
        var events = new List<ReplicationEventDto>(pending.Count);
        foreach (var record in pending)
        {
            events.Add(new ReplicationEventDto
            {
                Lsn = record.Lsn,
                TxId = record.TxId,
                RecordType = (Byte)record.RecordType,
                PageId = record.PageId,
                Data = record.Data,
                Timestamp = record.Timestamp
            });
        }

        // 通过 RPC 推送到从节点
        var result = await client.InvokeAsync<ReplicationAckDto>("Nova/ApplyReplication", new { events }).ConfigureAwait(false);
        if (result != null && result.Success)
        {
            AcknowledgeReplication(nodeId, result.AckedLsn);
        }
    }

    /// <summary>获取从节点待复制的 Binlog 事件</summary>
    /// <param name="nodeId">从节点 ID</param>
    /// <param name="fromPosition">起始位置</param>
    /// <param name="maxCount">最大返回数量</param>
    /// <returns>Binlog 事件列表</returns>
    public List<BinlogEvent> GetPendingBinlogEvents(String nodeId, Int64 fromPosition, Int32 maxCount = 100)
    {
        if (nodeId == null) throw new ArgumentNullException(nameof(nodeId));
        if (_binlogWriter == null) return [];

        lock (_lock)
        {
            if (!_slaves.ContainsKey(nodeId))
                throw new NovaException(ErrorCode.NodeNotFound, $"从节点 {nodeId} 不存在");
        }

        // 从 Binlog 文件读取事件
        var allEvents = new List<BinlogEvent>();
        var files = _binlogWriter.ListFiles();

        foreach (var (fileName, _) in files)
        {
            // 解析文件索引
            var dot = fileName.LastIndexOf('.');
            if (dot < 0) continue;
            var indexStr = fileName.Substring(dot + 1);
            if (!Int32.TryParse(indexStr, out var fileIndex)) continue;

            var events = _binlogWriter.ReadEvents(fileIndex);
            foreach (var evt in events)
            {
                if (evt.Position >= fromPosition)
                {
                    allEvents.Add(evt);
                    if (allEvents.Count >= maxCount) return allEvents;
                }
            }
        }

        return allEvents;
    }
    #endregion

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            _replicating = false;
            _replicationTimer?.Dispose();
            _replicationTimer = null;

            // 关闭所有从节点连接
            foreach (var client in _slaveClients.Values)
            {
                client.TryDispose();
            }
            _slaveClients.Clear();

            _replicationBuffer.Clear();
            _slaves.Clear();
            _slavePositions.Clear();
        }
    }
}

/// <summary>复制事件传输对象</summary>
public class ReplicationEventDto
{
    /// <summary>日志序列号</summary>
    public UInt64 Lsn { get; set; }

    /// <summary>事务 ID</summary>
    public UInt64 TxId { get; set; }

    /// <summary>记录类型</summary>
    public Byte RecordType { get; set; }

    /// <summary>页 ID</summary>
    public UInt64 PageId { get; set; }

    /// <summary>数据</summary>
    public Byte[] Data { get; set; } = [];

    /// <summary>时间戳</summary>
    public Int64 Timestamp { get; set; }
}

/// <summary>复制确认传输对象</summary>
public class ReplicationAckDto
{
    /// <summary>是否成功</summary>
    public Boolean Success { get; set; }

    /// <summary>已确认的 LSN</summary>
    public UInt64 AckedLsn { get; set; }

    /// <summary>错误信息</summary>
    public String? ErrorMessage { get; set; }
}
