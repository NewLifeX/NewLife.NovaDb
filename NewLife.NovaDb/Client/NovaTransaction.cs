using System.Data;
using System.Data.Common;
using NewLife.NovaDb.Tx;

namespace NewLife.NovaDb.Client;

/// <summary>NovaDb ADO.NET 事务</summary>
public class NovaTransaction : DbTransaction
{
    private readonly NovaConnection _connection;
    private Boolean _completed;
    private Transaction? _engineTx;

    /// <summary>远程事务 ID（网络模式）</summary>
    public String? TxId { get; set; }

    /// <summary>创建事务实例</summary>
    /// <param name="connection">关联的连接</param>
    public NovaTransaction(NovaConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));

        // 嵌入模式：从引擎事务管理器创建事务
        if (_connection.SqlEngine != null)
        {
            _engineTx = _connection.SqlEngine.TxManager.BeginTransaction();
        }
        // 网络模式：通过 RPC 开始远程事务
        else if (_connection.Client != null)
        {
            TxId = _connection.Client.BeginTransactionAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }
    }

    /// <summary>隔离级别</summary>
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    /// <summary>关联的连接</summary>
    protected override DbConnection DbConnection => _connection;

    /// <summary>是否已完成</summary>
    public Boolean IsCompleted => _completed;

    /// <summary>引擎层事务（嵌入模式有效）</summary>
    internal Transaction? EngineTx => _engineTx;

    /// <summary>提交事务</summary>
    public override void Commit()
    {
        if (_connection.Client != null && TxId != null)
            _connection.Client.CommitTransactionAsync(TxId).ConfigureAwait(false).GetAwaiter().GetResult();
        else if (_engineTx != null)
            _engineTx.Commit();

        _completed = true;
    }

    /// <summary>回滚事务</summary>
    public override void Rollback()
    {
        if (_connection.Client != null && TxId != null)
            _connection.Client.RollbackTransactionAsync(TxId).ConfigureAwait(false).GetAwaiter().GetResult();
        else if (_engineTx != null)
            _engineTx.Rollback();

        _completed = true;
    }

    /// <summary>释放资源。未提交/回滚时自动回滚</summary>
    protected override void Dispose(Boolean disposing)
    {
        if (!_completed && _engineTx != null)
        {
            try
            {
                _engineTx.Rollback();
            }
            catch { }
        }

        base.Dispose(disposing);
    }
}
