using NewLife.Log;
using NewLife.NovaDb.Cluster;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using NewLife.NovaDb.Tx;
using NewLife.Remoting;

namespace NewLife.NovaDb.Server;

/// <summary>NovaDb RPC 服务控制器，提供关系型引擎操作接口</summary>
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

    /// <summary>引擎解析器。根据数据库名获取对应引擎，由 NovaServer 启动时设置（多库路由）</summary>
    internal static Func<String?, SqlEngine?>? EngineResolver { get; set; }

    /// <summary>数据库列表提供器。SHOW DATABASES 由服务器返回库列表</summary>
    internal static Func<List<String>>? DatabaseListProvider { get; set; }

    /// <summary>共享复制管理器，由 NovaServer 启动时设置</summary>
    internal static ReplicationManager? SharedReplication { get; set; }

    /// <summary>共享事务字典，跨请求维护事务状态</summary>
    private static readonly Dictionary<String, (SqlEngine Engine, Transaction Tx)> _transactions = new(StringComparer.OrdinalIgnoreCase);
#if NET9_0_OR_GREATER
    private static readonly System.Threading.Lock _txLock = new();
#else
    private static readonly Object _txLock = new();
#endif

    /// <summary>心跳</summary>
    /// <returns>服务器时间</returns>
    public String Ping() => DateTime.UtcNow.ToString("o");

    /// <summary>获取服务器协议版本</summary>
    /// <returns>协议版本号（主版本）</returns>
    public Int32 GetVersion() => NovaProtocol.ProtocolVersion;

    /// <summary>解析数据库名对应的引擎</summary>
    /// <param name="db">数据库名，为空时回退到默认引擎</param>
    /// <returns>引擎实例，不存在返回 null</returns>
    private static SqlEngine? ResolveEngine(String? db) => EngineResolver?.Invoke(db) ?? SharedEngine;

    /// <summary>执行 SQL（非查询）</summary>
    /// <param name="sql">SQL 语句</param>
    /// <param name="txId">事务 ID（可选，绑定到已开启事务）</param>
    /// <param name="db">数据库名（可选，多库路由）</param>
    /// <returns>受影响行数</returns>
    public Int32 Execute(String sql, String? txId = null, String? db = null)
    {
        XTrace.WriteLine($"Nova.Execute[{db}]: {sql}");

        try
        {
            var engine = ResolveEngine(db);
            if (engine == null) return 0;

            var result = ExecuteWithTransaction(engine, sql, txId);
            return result.AffectedRows;
        }
        catch (Exception ex)
        {
            XTrace.WriteLine($"Nova.Execute[{db}] error: {ex.Message}");
            throw;
        }
    }

    /// <summary>查询</summary>
    /// <param name="sql">SQL 语句</param>
    /// <param name="txId">事务 ID（可选，绑定到已开启事务）</param>
    /// <param name="db">数据库名（可选，多库路由）</param>
    /// <returns>查询结果</returns>
    public Object? Query(String sql, String? txId = null, String? db = null)
    {
        XTrace.WriteLine($"Nova.Query[{db}]: {sql}");

        try
        {
            // SHOW DATABASES 由服务器数据库管理器生成库列表（多库支持）
            if (sql.TrimStart().StartsWithIgnoreCase("SHOW DATABASES"))
            {
                var dbs = DatabaseListProvider?.Invoke();
                var rows = dbs?.Select(e => new[] { (Object)e }).ToList();
                return new
                {
                    ColumnNames = new[] { "Database" },
                    Rows = rows
                };
            }

            var engine = ResolveEngine(db);
            if (engine == null) return null;

            var result = ExecuteWithTransaction(engine, sql, txId);
            if (!result.IsQuery) return result.AffectedRows;

            return new
            {
                result.ColumnNames,
                Rows = result.Rows.Select(r => r.ToArray()).ToArray()
            };
        }
        catch (Exception ex)
        {
            XTrace.WriteLine($"Nova.Query[{db}] error: {ex.Message}");
            throw;
        }
    }

    /// <summary>在指定事务（若有）中执行 SQL</summary>
    /// <param name="engine">目标引擎</param>
    /// <param name="sql">SQL 语句</param>
    /// <param name="txId">事务 ID（可选）</param>
    /// <returns>执行结果</returns>
    private static SqlResult ExecuteWithTransaction(SqlEngine engine, String sql, String? txId)
    {
        // 有事务 ID 时，绑定到已开启的事务
        if (!txId.IsNullOrEmpty())
        {
            lock (_txLock)
            {
                if (_transactions.TryGetValue(txId, out var txState))
                    return txState.Engine.Execute(sql, null, txState.Tx);
            }

            // 事务不存在，返回明确错误
            throw new NovaException(ErrorCode.TransactionError, $"Transaction '{txId}' not found");
        }

        return engine.Execute(sql);
    }

    /// <summary>开始事务</summary>
    /// <param name="db">数据库名（可选，多库路由）</param>
    /// <returns>事务 ID</returns>
    public String BeginTransaction(String? db = null)
    {
        var engine = ResolveEngine(db);
        if (engine == null) return Guid.NewGuid().ToString("N");

        var tx = engine.TxManager.BeginTransaction();
        var txId = tx.TxId.ToString();

        lock (_txLock)
        {
            _transactions[txId] = (engine, tx);
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
            if (!_transactions.TryGetValue(txId, out var txState)) return true;

            txState.Tx.Commit();
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
            if (!_transactions.TryGetValue(txId, out var txState)) return true;

            txState.Tx.Rollback();
            _transactions.Remove(txId);
            return true;
        }
    }

    #region 复制接口
    /// <summary>从节点注册到主节点</summary>
    /// <param name="nodeId">从节点 ID</param>
    /// <param name="endpoint">从节点地址</param>
    /// <param name="lastLsn">从节点最后已应用的 LSN</param>
    /// <returns>是否注册成功</returns>
    public Boolean RegisterSlave(String nodeId, String endpoint, UInt64 lastLsn)
    {
        if (SharedReplication == null) return false;

        var slave = new NodeInfo
        {
            NodeId = nodeId,
            Endpoint = endpoint,
            Role = NodeRole.Slave,
            ReplicatedLsn = lastLsn
        };

        SharedReplication.RegisterSlave(slave);
        return true;
    }

    /// <summary>从节点拉取 Binlog 事件</summary>
    /// <param name="nodeId">从节点 ID</param>
    /// <param name="fromLsn">起始 LSN</param>
    /// <param name="maxCount">最大返回数量</param>
    /// <returns>待复制的事件列表</returns>
    public PullBinlogResultDto PullBinlog(String nodeId, UInt64 fromLsn, Int32 maxCount)
    {
        if (SharedReplication == null)
            return new PullBinlogResultDto();

        var pending = SharedReplication.GetPendingRecords(nodeId, maxCount);
        var events = new ReplicationEventDto[pending.Count];
        for (var i = 0; i < pending.Count; i++)
        {
            var (header, data) = pending[i];
            events[i] = new ReplicationEventDto
            {
                Lsn = header.Lsn,
                TxId = header.TxId,
                RecordType = (Byte)header.RecordType,
                PageId = header.PageId,
                Data = data ?? [],
                Timestamp = header.Timestamp
            };
        }

        return new PullBinlogResultDto
        {
            Events = events,
            MasterLsn = SharedReplication.MasterLsn
        };
    }

    /// <summary>从节点心跳</summary>
    /// <param name="nodeId">从节点 ID</param>
    /// <param name="lastLsn">从节点最后已应用的 LSN</param>
    /// <returns>主节点当前时间</returns>
    public String ReplicaHeartbeat(String nodeId, UInt64 lastLsn)
    {
        if (SharedReplication != null)
        {
            var slave = SharedReplication.GetSlave(nodeId);
            if (slave != null)
            {
                slave.LastHeartbeat = DateTime.UtcNow;
                slave.ReplicatedLsn = lastLsn;
            }
        }

        return DateTime.UtcNow.ToString("o");
    }

    /// <summary>从节点应用复制事件（主节点推送模式使用）</summary>
    /// <param name="events">复制事件列表</param>
    /// <returns>确认结果</returns>
    public ReplicationAckDto ApplyReplication(ReplicationEventDto[] events)
    {
        // 本接口运行在从节点的控制器上，接收主节点推送的事件
        // 实际的事件应用由 ReplicaClient 在本地处理
        // 这里返回确认结果
        var ack = new ReplicationAckDto { Success = true };
        if (events != null && events.Length > 0)
            ack.AckedLsn = events[events.Length - 1].Lsn;

        return ack;
    }
    #endregion
}
