using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Tx;

/// <summary>
/// 事务状态
/// </summary>
public enum TransactionState
{
    /// <summary>
    /// 活跃中
    /// </summary>
    Active,

    /// <summary>
    /// 已提交
    /// </summary>
    Committed,

    /// <summary>
    /// 已回滚
    /// </summary>
    Aborted
}

/// <summary>
/// 事务实例
/// </summary>
public class Transaction : IDisposable
{
    private readonly UInt64 _txId;
    private readonly TransactionManager _manager;
    private TransactionState _state;
    private UInt64 _commitTs;
    private readonly Object _lock = new();
    private readonly List<Action> _rollbackActions = new();
    private Boolean _disposed;

    /// <summary>
    /// 事务 ID
    /// </summary>
    public UInt64 TxId => _txId;

    /// <summary>
    /// 事务状态
    /// </summary>
    public TransactionState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// 提交时间戳（仅在提交后有效）
    /// </summary>
    public UInt64 CommitTs
    {
        get
        {
            lock (_lock)
            {
                return _commitTs;
            }
        }
    }

    /// <summary>
    /// 创建事务实例
    /// </summary>
    /// <param name="txId">事务 ID</param>
    /// <param name="manager">事务管理器</param>
    internal Transaction(UInt64 txId, TransactionManager manager)
    {
        _txId = txId;
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _state = TransactionState.Active;
        _commitTs = 0;
    }

    /// <summary>
    /// 提交事务
    /// </summary>
    public void Commit()
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Transaction));

            if (_state != TransactionState.Active)
                throw new NovaException(ErrorCode.TransactionError, $"Transaction {_txId} is not active (state: {_state})");

            // 分配提交时间戳
            _commitTs = _manager.AllocateCommitTs();
            _state = TransactionState.Committed;

            // 清除回滚动作
            _rollbackActions.Clear();

            // 从活跃事务列表移除
            _manager.RemoveTransaction(_txId);
        }
    }

    /// <summary>
    /// 回滚事务
    /// </summary>
    public void Rollback()
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Transaction));

            if (_state != TransactionState.Active)
                throw new NovaException(ErrorCode.TransactionError, $"Transaction {_txId} is not active (state: {_state})");

            // 执行所有回滚动作（倒序执行）
            for (var i = _rollbackActions.Count - 1; i >= 0; i--)
            {
                try
                {
                    _rollbackActions[i]();
                }
                catch
                {
                    // 忽略回滚动作异常，继续执行其他回滚
                }
            }

            _state = TransactionState.Aborted;
            _rollbackActions.Clear();

            // 从活跃事务列表移除
            _manager.RemoveTransaction(_txId);
        }
    }

    /// <summary>
    /// 注册回滚动作
    /// </summary>
    /// <param name="action">回滚动作</param>
    public void RegisterRollbackAction(Action action)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        lock (_lock)
        {
            if (_state != TransactionState.Active)
                throw new NovaException(ErrorCode.TransactionError, $"Cannot register rollback action on non-active transaction {_txId}");

            _rollbackActions.Add(action);
        }
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            // 如果事务还活跃，自动回滚
            if (_state == TransactionState.Active)
            {
                try
                {
                    Rollback();
                }
                catch
                {
                    // 忽略回滚异常
                }
            }

            _disposed = true;
        }
    }
}
