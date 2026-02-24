using NewLife.Data;
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
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <param name="value">二进制值</param>
    /// <param name="ttlSeconds">过期时间（秒），0 表示永不过期</param>
    /// <returns>是否成功</returns>
    public async Task<Boolean> KvSetAsync(String tableName, String key, Byte[]? value, Int32 ttlSeconds = 0)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Boolean>("Kv/Set", new { tableName, key, value, ttlSeconds }).ConfigureAwait(false);
    }

    /// <summary>KV 获取值</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <returns>二进制值，不存在返回 null</returns>
    public async Task<Byte[]?> KvGetAsync(String tableName, String key)
    {
        EnsureOpen();
        var str = await _client!.InvokeAsync<String>("Kv/Get", new { tableName, key }).ConfigureAwait(false);
        return str == null ? null : Convert.FromBase64String(str);
    }

    /// <summary>KV 获取值（Packet 模式，避免 Base64 编码开销）</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <returns>二进制数据包，不存在返回 null</returns>
    public async Task<Packet?> KvGetPacketAsync(String tableName, String key)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Packet>("Kv/GetPacket", new { tableName, key }).ConfigureAwait(false);
    }

    /// <summary>KV 删除键</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <returns>是否成功</returns>
    public async Task<Boolean> KvDeleteAsync(String tableName, String key)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Boolean>("Kv/Delete", new { tableName, key }).ConfigureAwait(false);
    }

    /// <summary>KV 检查键是否存在</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <returns>是否存在</returns>
    public async Task<Boolean> KvExistsAsync(String tableName, String key)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Boolean>("Kv/Exists", new { tableName, key }).ConfigureAwait(false);
    }

    /// <summary>KV 按通配符模式删除键</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="pattern">通配符模式（支持 * 和 ?）</param>
    /// <returns>删除的键个数</returns>
    public async Task<Int32> KvDeleteByPatternAsync(String tableName, String pattern)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Int32>("Kv/DeleteByPattern", new { tableName, pattern }).ConfigureAwait(false);
    }

    /// <summary>KV 获取缓存项总数</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <returns>总数</returns>
    public async Task<Int32> KvGetCountAsync(String tableName)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Int32>("Kv/GetCount", new { tableName }).ConfigureAwait(false);
    }

    /// <summary>KV 获取所有缓存键</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <returns>键数组</returns>
    public async Task<String[]> KvGetAllKeysAsync(String tableName)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<String[]>("Kv/GetAllKeys", new { tableName }).ConfigureAwait(false) ?? [];
    }

    /// <summary>KV 清空所有缓存项</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    public async Task KvClearAsync(String tableName)
    {
        EnsureOpen();
        await _client!.InvokeAsync<Object>("Kv/Clear", new { tableName }).ConfigureAwait(false);
    }

    /// <summary>KV 设置缓存项有效期</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <param name="ttlSeconds">过期时间（秒）</param>
    /// <returns>是否成功</returns>
    public async Task<Boolean> KvSetExpireAsync(String tableName, String key, Int32 ttlSeconds)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Boolean>("Kv/SetExpire", new { tableName, key, ttlSeconds }).ConfigureAwait(false);
    }

    /// <summary>KV 获取缓存项剩余有效期</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <returns>剩余 TTL（秒），-1 表示无过期或不存在</returns>
    public async Task<Double> KvGetExpireAsync(String tableName, String key)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Double>("Kv/GetExpire", new { tableName, key }).ConfigureAwait(false);
    }

    /// <summary>KV 原子递增（整数）</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <param name="value">变化量</param>
    /// <returns>更新后的值</returns>
    public async Task<Int64> KvIncrementAsync(String tableName, String key, Int64 value)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Int64>("Kv/Increment", new { tableName, key, value }).ConfigureAwait(false);
    }

    /// <summary>KV 原子递增（浮点）</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <param name="value">变化量</param>
    /// <returns>更新后的值</returns>
    public async Task<Double> KvIncrementDoubleAsync(String tableName, String key, Double value)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Double>("Kv/IncrementDouble", new { tableName, key, value }).ConfigureAwait(false);
    }

    /// <summary>KV 搜索匹配的键</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="pattern">搜索模式</param>
    /// <param name="offset">偏移量</param>
    /// <param name="count">数量，-1 表示不限</param>
    /// <returns>匹配的键数组</returns>
    public async Task<String[]> KvSearchAsync(String tableName, String pattern, Int32 offset = 0, Int32 count = -1)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<String[]>("Kv/Search", new { tableName, pattern, offset, count }).ConfigureAwait(false) ?? [];
    }

    /// <summary>KV 批量获取值</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="keys">键数组</param>
    /// <returns>键到二进制值的字典</returns>
    public async Task<IDictionary<String, Byte[]?>> KvGetAllAsync(String tableName, String[] keys)
    {
        EnsureOpen();
        var dict = await _client!.InvokeAsync<IDictionary<String, String?>>("Kv/GetAll", new { tableName, keys }).ConfigureAwait(false);
        var result = new Dictionary<String, Byte[]?>();
        if (dict != null)
        {
            foreach (var kvp in dict)
                result[kvp.Key] = kvp.Value == null ? null : Convert.FromBase64String(kvp.Value);
        }
        return result;
    }

    /// <summary>KV 批量设置键值对</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="values">键到二进制值的字典</param>
    /// <param name="ttlSeconds">过期时间（秒），0 表示永不过期</param>
    /// <returns>设置的键个数</returns>
    public async Task<Int32> KvSetAllAsync(String tableName, IDictionary<String, Byte[]?> values, Int32 ttlSeconds = 0)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Int32>("Kv/SetAll", new { tableName, values, ttlSeconds }).ConfigureAwait(false);
    }
    #endregion

    #region 消息队列操作
    /// <summary>发布消息到流</summary>
    /// <param name="data">消息数据（JSON 格式的字段字典）</param>
    /// <returns>消息 ID</returns>
    public async Task<String?> MqPublishAsync(IDictionary<String, Object?> data)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<String>("Flux/Publish", new { data }).ConfigureAwait(false);
    }

    /// <summary>创建消费组</summary>
    /// <param name="groupName">消费组名称</param>
    /// <returns>是否成功</returns>
    public async Task<Boolean> MqCreateGroupAsync(String groupName)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Boolean>("Flux/CreateGroup", new { groupName }).ConfigureAwait(false);
    }

    /// <summary>消费组读取消息</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="consumer">消费者名称</param>
    /// <param name="count">最大读取数量</param>
    /// <returns>消息列表</returns>
    public async Task<Object?> MqReadGroupAsync(String groupName, String consumer, Int32 count = 10)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Object>("Flux/ReadGroup", new { groupName, consumer, count }).ConfigureAwait(false);
    }

    /// <summary>确认消息</summary>
    /// <param name="groupName">消费组名称</param>
    /// <param name="messageId">消息 ID</param>
    /// <returns>是否成功</returns>
    public async Task<Boolean> MqAckAsync(String groupName, String messageId)
    {
        EnsureOpen();
        return await _client!.InvokeAsync<Boolean>("Flux/Ack", new { groupName, messageId }).ConfigureAwait(false);
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
