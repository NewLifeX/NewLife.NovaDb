using System.Collections.Concurrent;
using System.Threading;
using NewLife.Collections;
using NewLife.Log;

namespace NewLife.NovaDb.Client;

/// <summary>NovaDb 连接池。每个连接字符串一个连接池，管理多个可重用的 NovaClient 连接</summary>
public class NovaClientPool : ObjectPool<NovaClient>
{
    /// <summary>连接字符串设置</summary>
    public NovaConnectionStringBuilder? Setting { get; set; }

    /// <summary>创建连接</summary>
    /// <returns>新的 NovaClient 实例</returns>
    protected override NovaClient OnCreate()
    {
        var set = Setting ?? throw new ArgumentNullException(nameof(Setting));
        var server = set.Server;
        var port = set.Port;
        if (String.IsNullOrEmpty(server)) throw new InvalidOperationException("连接字符串中未指定 Server");

        // 传递默认数据库名，服务器据此进行多库路由
        return new NovaClient($"tcp://{server}:{port}") { Database = set.Database };
    }

    /// <summary>借出时健康检查。新连接或空闲连接被取出时调用，确保连接可用</summary>
    /// <param name="value">NovaClient 实例</param>
    /// <returns>可用返回 true，不可用则基类自动销毁并重试</returns>
    protected override Boolean OnGet(NovaClient value)
    {
        if (value.IsConnected) return true;

        // 未连接（新创建或空闲断开），尝试打开
        try
        {
            value.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>归还时校验。连接归还到池时调用，仅健康的连接才允许入池</summary>
    /// <param name="value">NovaClient 实例</param>
    /// <returns>健康返回 true，不健康则基类销毁不入池</returns>
    protected override Boolean OnReturn(NovaClient value)
    {
        if (value == null) return false;

        // 已销毁或被 Dispose 的连接不入池
        if (value is DisposeBase db && db.Disposed) return false;

        // 已断开的连接不入池（可能网络异常断开）
        if (!value.IsConnected) return false;

        return true;
    }

    /// <summary>异步健康检查。默认委托给同步 <see cref="OnGet"/></summary>
    /// <param name="value">NovaClient 实例</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>可用返回 true，不可用则基类自动销毁并重试</returns>
    protected override Task<Boolean> OnGetAsync(NovaClient value, CancellationToken cancellationToken = default)
    {
        // 当前 Open() 是同步操作，委托给同步 OnGet
        // 子类可在 OpenAsync 实现后重写此方法
        return Task.FromResult(OnGet(value));
    }
}

/// <summary>连接池管理器。根据连接字符串，换取对应连接池</summary>
public class NovaPoolManager
{
    private readonly ConcurrentDictionary<String, NovaClientPool> _pools = new();

    /// <summary>获取连接池。连接字符串相同时共用连接池</summary>
    /// <param name="setting">连接字符串设置</param>
    /// <returns>对应的连接池实例</returns>
    public NovaClientPool GetPool(NovaConnectionStringBuilder setting) => _pools.GetOrAdd(setting.ConnectionString, k => CreatePool(setting));

    /// <summary>创建连接池</summary>
    /// <param name="setting">连接字符串设置</param>
    /// <returns>新的连接池实例</returns>
    protected virtual NovaClientPool CreatePool(NovaConnectionStringBuilder setting)
    {
        using var span = DefaultTracer.Instance?.NewSpan("db:nova:CreatePool", setting.ConnectionString);

        var pool = new NovaClientPool
        {
            Setting = setting,
            Min = 2,
            Max = 100000,
            IdleTime = 30,
            MaxLifetime = 300,
        };

        return pool;
    }
}
