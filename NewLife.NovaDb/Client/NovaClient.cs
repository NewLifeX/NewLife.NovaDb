using NewLife.Remoting;

namespace NewLife.NovaDb.Client;

/// <summary>NovaDb 远程客户端，基于 Remoting 的 ApiClient 实现 RPC 通信</summary>
public class NovaClient : DisposeBase
{
    #region 属性
    private ApiClient? _client;

    /// <summary>服务器地址，格式如 tcp://127.0.0.1:3306</summary>
    public String ServerUri { get; set; } = String.Empty;

    /// <summary>是否已连接</summary>
    public Boolean IsConnected => _client?.Active ?? false;

    /// <summary>内部 ApiClient 实例</summary>
    public ApiClient? Client => _client;
    #endregion

    #region 构造
    /// <summary>创建客户端实例</summary>
    /// <param name="serverUri">服务器地址，如 tcp://127.0.0.1:3306</param>
    public NovaClient(String serverUri)
    {
        if (serverUri == null) throw new ArgumentNullException(nameof(serverUri));
        ServerUri = serverUri;
    }
    #endregion

    #region 方法
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
    #endregion

    #region KV 操作
    /// <summary>KV 设置键值对</summary>
    /// <param name="key">键</param>
    /// <param name="value">值</param>
    /// <param name="ttlSeconds">过期时间（秒），0 表示永不过期</param>
    /// <returns>是否成功</returns>
    public async Task<Boolean> KvSetAsync(String key, String value, Int32 ttlSeconds = 0)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Boolean>("Nova/KvSet", new { key, value, ttlSeconds }).ConfigureAwait(false);
    }

    /// <summary>KV 获取值</summary>
    /// <param name="key">键</param>
    /// <returns>值，不存在返回 null</returns>
    public async Task<String?> KvGetAsync(String key)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<String>("Nova/KvGet", new { key }).ConfigureAwait(false);
    }

    /// <summary>KV 删除键</summary>
    /// <param name="key">键</param>
    /// <returns>是否成功</returns>
    public async Task<Boolean> KvDeleteAsync(String key)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Boolean>("Nova/KvDelete", new { key }).ConfigureAwait(false);
    }

    /// <summary>KV 检查键是否存在</summary>
    /// <param name="key">键</param>
    /// <returns>是否存在</returns>
    public async Task<Boolean> KvExistsAsync(String key)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Boolean>("Nova/KvExists", new { key }).ConfigureAwait(false);
    }
    #endregion

    #region 消息队列操作
    /// <summary>发布消息到流</summary>
    /// <param name="data">消息数据（JSON 格式的字段字典）</param>
    /// <returns>消息 ID</returns>
    public async Task<String?> MqPublishAsync(IDictionary<String, Object?> data)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<String>("Nova/MqPublish", new { data }).ConfigureAwait(false);
    }

    /// <summary>创建消费组</summary>
    /// <param name="groupName">消费组名称</param>
    /// <returns>是否成功</returns>
    public async Task<Boolean> MqCreateGroupAsync(String groupName)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Boolean>("Nova/MqCreateGroup", new { groupName }).ConfigureAwait(false);
    }

    /// <summary>消费组读取消息</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="consumer">消费者名称</param>
    /// <param name="count">最大读取数量</param>
    /// <returns>消息列表</returns>
    public async Task<Object?> MqReadGroupAsync(String groupName, String consumer, Int32 count = 10)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Object>("Nova/MqReadGroup", new { groupName, consumer, count }).ConfigureAwait(false);
    }

    /// <summary>确认消息</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="messageId">消息 ID</param>
    /// <returns>是否成功</returns>
    public async Task<Boolean> MqAckAsync(String groupName, String messageId)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Boolean>("Nova/MqAck", new { groupName, messageId }).ConfigureAwait(false);
    }
    #endregion

    #region 释放
    /// <summary>释放资源</summary>
    /// <param name="disposing">是否由 Dispose 调用</param>
    protected override void Dispose(Boolean disposing)
    {
        base.Dispose(disposing);

        _client?.Close(disposing ? "Dispose" : "GC");
        _client.TryDispose();
        _client = null;
    }
    #endregion
}
