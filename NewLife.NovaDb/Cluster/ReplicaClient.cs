using NewLife.Log;
using NewLife.NovaDb.WAL;
using NewLife.Remoting;

namespace NewLife.NovaDb.Cluster;

/// <summary>从节点复制客户端，负责从主节点拉取 Binlog 并重放</summary>
/// <remarks>
/// 复制流程：
/// 1. 通过 Remoting ApiClient 连接主节点
/// 2. 后台定时拉取 Binlog 事件（PullBinlog RPC）
/// 3. 按序重放事件到本地存储
/// 4. 定时发送心跳维持连接
/// 5. 断线后自动重连并从断点续传
/// </remarks>
public class ReplicaClient : IDisposable
{
    private readonly NodeInfo _localNode;
    private readonly String _masterEndpoint;
    private UInt64 _lastAppliedLsn;
    private readonly List<WalRecord> _appliedRecords = [];
    private readonly Action<UInt64, Byte[]>? _applyCallback;
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif
    private Boolean _isConnected;
    private Boolean _disposed;

    private ApiClient? _remoteClient;
    private Timer? _pullTimer;
    private Timer? _heartbeatTimer;
    private volatile Boolean _pulling;

    /// <summary>本地节点信息</summary>
    public NodeInfo LocalNode => _localNode;

    /// <summary>主节点地址</summary>
    public String MasterEndpoint => _masterEndpoint;

    /// <summary>拉取间隔（毫秒），默认 1000</summary>
    public Int32 PullIntervalMs { get; set; } = 1000;

    /// <summary>心跳间隔（毫秒），默认 5000</summary>
    public Int32 HeartbeatIntervalMs { get; set; } = 5000;

    /// <summary>每次拉取的最大事件数，默认 100</summary>
    public Int32 MaxPullBatchSize { get; set; } = 100;

    /// <summary>重连间隔（毫秒），默认 3000</summary>
    public Int32 ReconnectIntervalMs { get; set; } = 3000;

    /// <summary>日志</summary>
    public ILog Log { get; set; } = Logger.Null;

    /// <summary>最后应用的 LSN</summary>
    public UInt64 LastAppliedLsn
    {
        get
        {
            lock (_lock)
            {
                return _lastAppliedLsn;
            }
        }
    }

    /// <summary>是否已连接</summary>
    public Boolean IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _isConnected;
            }
        }
    }

    /// <summary>已应用的记录数</summary>
    public Int32 AppliedRecordCount
    {
        get
        {
            lock (_lock)
            {
                return _appliedRecords.Count;
            }
        }
    }

    /// <summary>远程 RPC 客户端</summary>
    public ApiClient? RemoteClient => _remoteClient;

    /// <summary>创建从节点客户端</summary>
    /// <param name="localNode">本地节点信息</param>
    /// <param name="masterEndpoint">主节点地址</param>
    /// <param name="applyCallback">页数据应用回调，参数为 (PageId, Data)</param>
    public ReplicaClient(NodeInfo localNode, String masterEndpoint, Action<UInt64, Byte[]>? applyCallback = null)
    {
        _localNode = localNode ?? throw new ArgumentNullException(nameof(localNode));
        _masterEndpoint = masterEndpoint ?? throw new ArgumentNullException(nameof(masterEndpoint));
        _applyCallback = applyCallback;
    }

    /// <summary>连接到主节点（本地模式，不建立网络连接）</summary>
    public void Connect()
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ReplicaClient));

            _isConnected = true;
            _localNode.State = NodeState.Syncing;
        }
    }

    /// <summary>通过网络连接到主节点并启动复制</summary>
    /// <param name="masterUri">主节点 RPC 地址，如 tcp://192.168.1.1:3306</param>
    public void ConnectRemote(String masterUri)
    {
        if (String.IsNullOrEmpty(masterUri)) throw new ArgumentNullException(nameof(masterUri));

        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ReplicaClient));

            var client = new ApiClient(masterUri);
            client.Open();
            _remoteClient = client;
            _isConnected = true;
            _localNode.State = NodeState.Syncing;
        }

        // 向主节点注册自己
        RegisterWithMasterAsync().ConfigureAwait(false).GetAwaiter().GetResult();

        // 启动拉取和心跳定时器
        StartPulling();
    }

    /// <summary>向主节点注册从节点</summary>
    private async Task RegisterWithMasterAsync()
    {
        if (_remoteClient == null) return;

        await _remoteClient.InvokeAsync<Boolean>("Nova/RegisterSlave", new
        {
            nodeId = _localNode.NodeId,
            endpoint = _localNode.Endpoint,
            lastLsn = _lastAppliedLsn
        }).ConfigureAwait(false);
    }

    /// <summary>启动后台拉取定时器</summary>
    private void StartPulling()
    {
        _pulling = true;
        _pullTimer = new Timer(OnPullTick, null, PullIntervalMs, PullIntervalMs);
        _heartbeatTimer = new Timer(OnHeartbeatTick, null, HeartbeatIntervalMs, HeartbeatIntervalMs);
    }

    /// <summary>停止后台拉取</summary>
    public void StopPulling()
    {
        _pulling = false;
        _pullTimer?.Dispose();
        _pullTimer = null;
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    /// <summary>拉取定时回调</summary>
    /// <param name="state">状态对象</param>
    private void OnPullTick(Object? state)
    {
        if (!_pulling || _disposed || _remoteClient == null) return;

        try
        {
            PullAndApplyAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log?.Warn("从主节点拉取复制事件失败: {0}", ex.Message);

            // 标记为断线，等待重连
            lock (_lock)
            {
                _isConnected = false;
                _localNode.State = NodeState.Offline;
            }
        }
    }

    /// <summary>心跳定时回调</summary>
    /// <param name="state">状态对象</param>
    private void OnHeartbeatTick(Object? state)
    {
        if (!_pulling || _disposed || _remoteClient == null) return;

        try
        {
            _remoteClient.InvokeAsync<String>("Nova/ReplicaHeartbeat", new
            {
                nodeId = _localNode.NodeId,
                lastLsn = _lastAppliedLsn
            }).ConfigureAwait(false).GetAwaiter().GetResult();

            _localNode.LastHeartbeat = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Log?.Warn("复制心跳失败: {0}", ex.Message);
        }
    }

    /// <summary>从主节点拉取事件并应用</summary>
    private async Task PullAndApplyAsync()
    {
        if (_remoteClient == null) return;

        var result = await _remoteClient.InvokeAsync<PullBinlogResultDto>("Nova/PullBinlog", new
        {
            nodeId = _localNode.NodeId,
            fromLsn = _lastAppliedLsn,
            maxCount = MaxPullBatchSize
        }).ConfigureAwait(false);

        if (result?.Events == null || result.Events.Length == 0) return;

        // 转换并应用
        var records = new List<WalRecord>(result.Events.Length);
        foreach (var evt in result.Events)
        {
            records.Add(new WalRecord
            {
                Lsn = evt.Lsn,
                TxId = evt.TxId,
                RecordType = (WalRecordType)evt.RecordType,
                PageId = evt.PageId,
                Data = evt.Data ?? [],
                Timestamp = evt.Timestamp
            });
        }

        ApplyRecords(records);
    }

    /// <summary>断开连接</summary>
    public void Disconnect()
    {
        lock (_lock)
        {
            StopPulling();
            _isConnected = false;
            _localNode.State = NodeState.Offline;

            _remoteClient?.Close("Disconnect");
            _remoteClient.TryDispose();
            _remoteClient = null;
        }
    }

    /// <summary>应用 WAL 记录（从主节点接收后重放）</summary>
    /// <param name="records">WAL 记录集合</param>
    public void ApplyRecords(IEnumerable<WalRecord> records)
    {
        if (records == null) throw new ArgumentNullException(nameof(records));

        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ReplicaClient));

            foreach (var record in records)
            {
                // 幂等：跳过已应用的记录
                if (record.Lsn <= _lastAppliedLsn) continue;

                // 对 UpdatePage 类型调用回调
                if (record.RecordType == WalRecordType.UpdatePage && _applyCallback != null)
                {
                    _applyCallback(record.PageId, record.Data);
                }

                _lastAppliedLsn = record.Lsn;
                _appliedRecords.Add(record);
                _localNode.ReplicatedLsn = record.Lsn;
            }
        }
    }

    /// <summary>获取断点续传位置</summary>
    /// <returns>最后应用的 LSN</returns>
    public UInt64 GetResumePosition() => LastAppliedLsn;

    /// <summary>重置到指定 LSN（用于故障恢复）</summary>
    /// <param name="lsn">目标 LSN</param>
    public void ResetToLsn(UInt64 lsn)
    {
        lock (_lock)
        {
            _lastAppliedLsn = lsn;
            _localNode.ReplicatedLsn = lsn;
        }
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            _pulling = false;
            _pullTimer?.Dispose();
            _pullTimer = null;
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;

            _isConnected = false;
            _localNode.State = NodeState.Offline;
            _appliedRecords.Clear();

            _remoteClient?.Close("Dispose");
            _remoteClient.TryDispose();
            _remoteClient = null;
        }
    }
}

/// <summary>拉取 Binlog 结果传输对象</summary>
public class PullBinlogResultDto
{
    /// <summary>复制事件列表</summary>
    public ReplicationEventDto[] Events { get; set; } = [];

    /// <summary>主节点当前 LSN</summary>
    public UInt64 MasterLsn { get; set; }
}
