using System.Collections.Concurrent;
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

        return new NovaClient($"tcp://{server}:{port}");
    }

    /// <summary>获取连接。剔除无效连接</summary>
    /// <returns>可用的 NovaClient 实例</returns>
    public override NovaClient Get()
    {
        var retryCount = 0;
        while (true)
        {
            var client = base.Get();

            // 新创建的连接尚未打开，直接返回由调用方打开
            if (!client.IsConnected)
            {
                client.Open();
                return client;
            }

            // 已打开的连接检查是否仍然可用
            if (!client.IsConnected)
            {
                // 连接已失效，丢弃后重试
                client.TryDispose();
                if (retryCount++ > 10) throw new InvalidOperationException("无法从连接池获取可用连接");
                continue;
            }

            return client;
        }
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
            AllIdleTime = 300,
        };

        return pool;
    }
}
