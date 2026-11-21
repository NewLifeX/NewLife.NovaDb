namespace NewLife.NovaDb.Tx;

/// <summary>事务管理器，负责分配事务 ID 和提交时间戳</summary>
public class TransactionManager
{
    private UInt64 _nextTxId;
    private UInt64 _nextCommitTs;
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif
    private readonly Dictionary<UInt64, Transaction> _activeTxs = [];
    // 已回滚（中止）的事务 ID 集合，用于区分"已回滚"与"已提交"
    private readonly HashSet<UInt64> _abortedTxs = [];

    /// <summary>获取下一个事务 ID</summary>
    public UInt64 NextTxId
    {
        get
        {
            lock (_lock)
            {
                return _nextTxId;
            }
        }
    }

    /// <summary>获取下一个提交时间戳</summary>
    public UInt64 NextCommitTs
    {
        get
        {
            lock (_lock)
            {
                return _nextCommitTs;
            }
        }
    }

    /// <summary>创建事务管理器实例</summary>
    public TransactionManager()
    {
        _nextTxId = 1;
        _nextCommitTs = 1;
    }

    /// <summary>开始新事务</summary>
    /// <returns>新事务实例</returns>
    public Transaction BeginTransaction()
    {
        lock (_lock)
        {
            var txId = _nextTxId++;
            var tx = new Transaction(txId, this);
            _activeTxs[txId] = tx;
            return tx;
        }
    }

    /// <summary>分配提交时间戳</summary>
    /// <returns>提交时间戳</returns>
    internal UInt64 AllocateCommitTs()
    {
        lock (_lock)
        {
            return _nextCommitTs++;
        }
    }

    /// <summary>移除事务</summary>
    /// <param name="txId">事务 ID</param>
    /// <param name="aborted">是否为回滚（中止），true 表示回滚，false 表示提交</param>
    internal void RemoveTransaction(UInt64 txId, Boolean aborted = false)
    {
        lock (_lock)
        {
            _activeTxs.Remove(txId);
            if (aborted)
                _abortedTxs.Add(txId);
        }
    }

    /// <summary>检查事务是否活跃</summary>
    /// <param name="txId">事务 ID</param>
    /// <returns>是否活跃</returns>
    public Boolean IsTransactionActive(UInt64 txId)
    {
        lock (_lock)
        {
            return _activeTxs.ContainsKey(txId);
        }
    }

    /// <summary>获取所有活跃事务 ID</summary>
    /// <returns>活跃事务 ID 列表</returns>
    public UInt64[] GetActiveTransactions()
    {
        lock (_lock)
        {
            return _activeTxs.Keys.ToArray();
        }
    }

    /// <summary>捕获可见性快照：批量判断大量行版本时复用，避免逐行加锁</summary>
    /// <param name="readTxId">读取事务 ID</param>
    /// <returns>可见性快照</returns>
    public VisibilitySnapshot CaptureSnapshot(UInt64 readTxId)
    {
        lock (_lock)
        {
            return new VisibilitySnapshot(readTxId, new HashSet<UInt64>(_activeTxs.Keys), [.. _abortedTxs]);
        }
    }

    /// <summary>是否存在除指定事务之外的活跃事务</summary>
    /// <param name="txId">当前事务 ID</param>
    /// <returns>存在其他活跃事务时返回 true</returns>
    /// <remarks>没有并发写事务时二级索引条目与可见行一一对应，可用于免回表的索引计数</remarks>
    public Boolean HasOtherActiveTransactions(UInt64 txId)
    {
        lock (_lock)
        {
            var count = _activeTxs.Count;
            if (txId > 0 && _activeTxs.ContainsKey(txId)) count--;

            return count > 0;
        }
    }

    /// <summary>检查事务对于读取事务是否可见</summary>
    /// <param name="createdByTx">创建行的事务 ID</param>
    /// <param name="deletedByTx">删除行的事务 ID（0 表示未删除）</param>
    /// <param name="readTxId">读取事务 ID</param>
    /// <returns>是否可见</returns>
    public Boolean IsVisible(UInt64 createdByTx, UInt64 deletedByTx, UInt64 readTxId)
    {
        // Read Committed 语义：只读取已提交的数据
        lock (_lock)
        {
            // 如果创建事务是当前读取事务，且未被删除或删除者不是自己，则可见（读己之写）
            if (createdByTx == readTxId)
            {
                return deletedByTx == 0 || deletedByTx != readTxId;
            }

            // 如果创建事务还活跃（未提交），则不可见（不读脏数据）
            if (_activeTxs.ContainsKey(createdByTx))
                return false;

            // 如果创建事务已回滚，则不可见（回滚的写入不应可见）
            if (_abortedTxs.Contains(createdByTx))
                return false;

            // 如果已被删除
            if (deletedByTx > 0)
            {
                // 如果删除事务还活跃，则可见（删除未提交）
                if (_activeTxs.ContainsKey(deletedByTx))
                    return true;

                // 如果删除事务已回滚，则可见（删除被撤销）
                if (_abortedTxs.Contains(deletedByTx))
                    return true;

                // 删除事务已提交，不可见
                return false;
            }

            // 创建事务已提交，且未被删除，可见
            return true;
        }
    }
}

/// <summary>可见性快照：一次性捕获活跃/回滚事务集合，批量判断行版本时无需逐行加锁</summary>
public sealed class VisibilitySnapshot
{
    private readonly HashSet<UInt64> _active;
    private readonly HashSet<UInt64> _aborted;

    /// <summary>读取事务 ID</summary>
    public UInt64 ReadTxId { get; }

    internal VisibilitySnapshot(UInt64 readTxId, HashSet<UInt64> active, HashSet<UInt64> aborted)
    {
        ReadTxId = readTxId;
        _active = active;
        _aborted = aborted;
    }

    /// <summary>判断行版本是否可见</summary>
    /// <param name="createdByTx">创建行的事务 ID</param>
    /// <param name="deletedByTx">删除行的事务 ID（0 表示未删除）</param>
    /// <returns>是否可见</returns>
    public Boolean IsVisible(UInt64 createdByTx, UInt64 deletedByTx)
    {
        // 读己之写
        if (createdByTx == ReadTxId) return deletedByTx == 0 || deletedByTx != ReadTxId;

        // 创建事务未提交或已回滚，不可见
        if (_active.Contains(createdByTx) || _aborted.Contains(createdByTx)) return false;

        if (deletedByTx > 0)
        {
            // 删除未提交或已回滚，仍可见
            if (_active.Contains(deletedByTx) || _aborted.Contains(deletedByTx)) return true;

            return false;
        }

        return true;
    }
}
