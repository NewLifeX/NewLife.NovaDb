using System.Data;
using System.Data.Common;

namespace NewLife.NovaDb.Client;

/// <summary>NovaDb ADO.NET 事务</summary>
public class NovaTransaction : DbTransaction
{
    private readonly NovaConnection _connection;
    private Boolean _completed;

    /// <summary>远程事务 ID（服务器模式）</summary>
    public String? TxId { get; set; }

    /// <summary>创建事务实例</summary>
    /// <param name="connection">关联的连接</param>
    public NovaTransaction(NovaConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));

        // 服务器模式：通过 RPC 开始远程事务
        if (_connection.Client != null)
            TxId = _connection.Client.BeginTransactionAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>隔离级别</summary>
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    /// <summary>关联的连接</summary>
    protected override DbConnection DbConnection => _connection;

    /// <summary>是否已完成</summary>
    public Boolean IsCompleted => _completed;

    /// <summary>提交事务</summary>
    public override void Commit()
    {
        if (_connection.Client != null && TxId != null)
            _connection.Client.CommitTransactionAsync(TxId).ConfigureAwait(false).GetAwaiter().GetResult();

        _completed = true;
    }

    /// <summary>回滚事务</summary>
    public override void Rollback()
    {
        if (_connection.Client != null && TxId != null)
            _connection.Client.RollbackTransactionAsync(TxId).ConfigureAwait(false).GetAwaiter().GetResult();

        _completed = true;
    }
}
