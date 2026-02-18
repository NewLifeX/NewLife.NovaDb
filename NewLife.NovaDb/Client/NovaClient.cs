using NewLife.Remoting;

namespace NewLife.NovaDb.Client;

/// <summary>NovaDb 远程客户端，基于 Remoting 的 ApiClient 实现 RPC 通信</summary>
public class NovaClient : DisposeBase
{
    private ApiClient? _client;

    /// <summary>服务器地址，格式如 tcp://127.0.0.1:3306</summary>
    public String ServerUri { get; set; } = String.Empty;

    /// <summary>是否已连接</summary>
    public Boolean IsConnected => _client?.Active ?? false;

    /// <summary>内部 ApiClient 实例</summary>
    public ApiClient? Client => _client;

    /// <summary>创建客户端实例</summary>
    /// <param name="serverUri">服务器地址，如 tcp://127.0.0.1:3306</param>
    public NovaClient(String serverUri)
    {
        if (serverUri == null) throw new ArgumentNullException(nameof(serverUri));
        ServerUri = serverUri;
    }

    /// <summary>打开连接</summary>
    public void Open()
    {
        if (_client != null && _client.Active) return;

        var client = new ApiClient(ServerUri);
        client.Open();
        _client = client;
    }

    /// <summary>关闭连接</summary>
    /// <param name="reason">关闭原因</param>
    public void Close(String? reason = null)
    {
        _client?.Close(reason ?? "NovaClient.Close");
        _client = null;
    }

    /// <summary>心跳</summary>
    /// <returns>服务器时间</returns>
    public async Task<String?> PingAsync()
    {
        EnsureOpen();
        return await _client!.InvokeAsync<String>("Nova/Ping").ConfigureAwait(false);
    }

    /// <summary>执行 SQL（非查询）</summary>
    /// <param name="sql">SQL 语句</param>
    /// <returns>受影响行数</returns>
    public async Task<Int32> ExecuteAsync(String sql)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Int32>("Nova/Execute", new { sql }).ConfigureAwait(false);
    }

    /// <summary>查询</summary>
    /// <param name="sql">SQL 语句</param>
    /// <returns>查询结果</returns>
    public async Task<TResult?> QueryAsync<TResult>(String sql)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<TResult>("Nova/Query", new { sql }).ConfigureAwait(false);
    }

    /// <summary>开始事务</summary>
    /// <returns>事务 ID</returns>
    public async Task<String?> BeginTransactionAsync()
    {
        EnsureOpen();
        return await _client!.InvokeAsync<String>("Nova/BeginTransaction").ConfigureAwait(false);
    }

    /// <summary>提交事务</summary>
    /// <param name="txId">事务 ID</param>
    /// <returns>是否成功</returns>
    public async Task<Boolean> CommitTransactionAsync(String txId)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Boolean>("Nova/CommitTransaction", new { txId }).ConfigureAwait(false);
    }

    /// <summary>回滚事务</summary>
    /// <param name="txId">事务 ID</param>
    /// <returns>是否成功</returns>
    public async Task<Boolean> RollbackTransactionAsync(String txId)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Boolean>("Nova/RollbackTransaction", new { txId }).ConfigureAwait(false);
    }

    /// <summary>泛型 RPC 调用</summary>
    /// <typeparam name="TResult">返回类型</typeparam>
    /// <param name="action">远程操作名称</param>
    /// <param name="args">参数</param>
    /// <returns>调用结果</returns>
    public async Task<TResult?> InvokeAsync<TResult>(String action, Object? args = null)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<TResult>(action, args).ConfigureAwait(false);
    }

    private void EnsureOpen()
    {
        if (_client == null || !_client.Active)
            throw new InvalidOperationException("Client is not connected. Call Open() first.");
    }

    /// <summary>释放资源</summary>
    /// <param name="disposing">是否由 Dispose 调用</param>
    protected override void Dispose(Boolean disposing)
    {
        base.Dispose(disposing);

        _client?.Close(disposing ? "Dispose" : "GC");
        _client.TryDispose();
        _client = null;
    }
}
