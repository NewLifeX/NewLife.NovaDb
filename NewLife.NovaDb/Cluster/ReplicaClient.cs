using NewLife.NovaDb.WAL;

namespace NewLife.NovaDb.Cluster;

/// <summary>从节点复制客户端，负责拉取和重放 WAL</summary>
public class ReplicaClient : IDisposable
{
    private readonly NodeInfo _localNode;
    private readonly String _masterEndpoint;
    private UInt64 _lastAppliedLsn;
    private readonly List<WalRecord> _appliedRecords = new();
    private readonly Action<UInt64, Byte[]>? _applyCallback;
    private readonly Object _lock = new();
    private Boolean _isConnected;
    private Boolean _disposed;

    /// <summary>本地节点信息</summary>
    public NodeInfo LocalNode => _localNode;

    /// <summary>主节点地址</summary>
    public String MasterEndpoint => _masterEndpoint;

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

    /// <summary>连接到主节点</summary>
    public void Connect()
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ReplicaClient));

            _isConnected = true;
            _localNode.State = NodeState.Syncing;
        }
    }

    /// <summary>断开连接</summary>
    public void Disconnect()
    {
        lock (_lock)
        {
            _isConnected = false;
            _localNode.State = NodeState.Offline;
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

            _isConnected = false;
            _localNode.State = NodeState.Offline;
            _appliedRecords.Clear();
        }
    }
}
