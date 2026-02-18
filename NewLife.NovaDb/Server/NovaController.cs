using NewLife.NovaDb.Sql;
using NewLife.NovaDb.Tx;
using NewLife.Remoting;

namespace NewLife.NovaDb.Server;

/// <summary>NovaDb RPC 服务控制器，提供数据库操作接口</summary>
/// <remarks>
/// 控制器方法通过 Remoting RPC 暴露为远程接口。
/// 路由格式：Nova/{方法名}，如 Nova/Ping、Nova/Execute。
/// 控制器实例由 Remoting 框架按请求创建，通过静态字段共享 SQL 引擎与事务。
/// </remarks>
internal class NovaController : IApi
{
    /// <summary>会话</summary>
    public IApiSession Session { get; set; } = null!;

    /// <summary>共享 SQL 执行引擎，由 NovaServer 启动时设置</summary>
    internal static SqlEngine? SharedEngine { get; set; }

    /// <summary>共享事务字典，跨请求维护事务状态</summary>
    private static readonly Dictionary<String, Transaction> _transactions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Object _txLock = new();

    /// <summary>心跳</summary>
    /// <returns>服务器时间</returns>
    public String Ping() => DateTime.UtcNow.ToString("o");

    /// <summary>执行 SQL（非查询）</summary>
    /// <param name="sql">SQL 语句</param>
    /// <returns>受影响行数</returns>
    public Int32 Execute(String sql)
    {
        if (SharedEngine == null) return 0;

        var result = SharedEngine.Execute(sql);
        return result.AffectedRows;
    }

    /// <summary>查询</summary>
    /// <param name="sql">SQL 语句</param>
    /// <returns>查询结果</returns>
    public Object? Query(String sql)
    {
        if (SharedEngine == null) return null;

        var result = SharedEngine.Execute(sql);
        if (!result.IsQuery) return result.AffectedRows;

        return new
        {
            result.ColumnNames,
            Rows = result.Rows.Select(r => r.ToArray()).ToArray()
        };
    }

    /// <summary>开始事务</summary>
    /// <returns>事务 ID</returns>
    public String BeginTransaction()
    {
        var engine = SharedEngine;
        if (engine == null) return Guid.NewGuid().ToString("N");

        var tx = engine.TxManager.BeginTransaction();
        var txId = tx.TxId.ToString();

        lock (_txLock)
        {
            _transactions[txId] = tx;
        }

        return txId;
    }

    /// <summary>提交事务</summary>
    /// <param name="txId">事务 ID</param>
    /// <returns>是否成功</returns>
    public Boolean CommitTransaction(String txId)
    {
        lock (_txLock)
        {
            if (!_transactions.TryGetValue(txId, out var tx)) return true;

            tx.Commit();
            _transactions.Remove(txId);
            return true;
        }
    }

    /// <summary>回滚事务</summary>
    /// <param name="txId">事务 ID</param>
    /// <returns>是否成功</returns>
    public Boolean RollbackTransaction(String txId)
    {
        lock (_txLock)
        {
            if (!_transactions.TryGetValue(txId, out var tx)) return true;

            tx.Rollback();
            _transactions.Remove(txId);
            return true;
        }
    }
}
