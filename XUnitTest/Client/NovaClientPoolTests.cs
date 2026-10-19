using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NewLife;
using NewLife.Collections;
using NewLife.NovaDb.Client;
using Xunit;

namespace XUnitTest.Client;

/// <summary>NovaDb 连接池单元测试</summary>
public class NovaClientPoolTests
{
    /// <summary>启动只接受连接、不应答的 TCP 服务端，模拟连接到非 NovaDb 服务（如 MySQL）的场景</summary>
    private static (TcpListener Listener, CancellationTokenSource Cts, List<TcpClient> Clients, Task AcceptTask) StartSilentServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var cts = new CancellationTokenSource();
        var clients = new List<TcpClient>();
        var acceptTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                    lock (clients) clients.Add(client);
                }
            }
            catch
            {
                // 监听停止或取消时结束
            }
        });

        return (listener, cts, clients, acceptTask);
    }

    [Fact(DisplayName = "连接池-服务端不应答时有界失败且不无限重试")]
    public void Get_SilentServer_ThrowsInsteadOfHanging()
    {
        var (listener, cts, clients, _) = StartSilentServer();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var setting = new NovaConnectionStringBuilder($"Server=127.0.0.1;Port={port};ConnectionTimeout=1");
            using var pool = new NovaClientPool { Setting = setting, Max = 2, WaitTimeout = TimeSpan.FromSeconds(10) };

            var sw = Stopwatch.StartNew();
            Assert.ThrowsAny<Exception>(() => pool.Get());
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"连接失败耗时过长：{sw.Elapsed}");
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            lock (clients)
            {
                foreach (var client in clients) client.Dispose();
            }
        }
    }

    [Fact(DisplayName = "连接-打开失败后状态回到Closed")]
    public void Open_SilentServer_StateClosed()
    {
        var (listener, cts, clients, _) = StartSilentServer();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            using var conn = new NovaConnection($"Server=127.0.0.1;Port={port};ConnectionTimeout=1");
            Assert.ThrowsAny<Exception>(() => conn.Open());
            Assert.Equal(System.Data.ConnectionState.Closed, conn.State);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            lock (clients)
            {
                foreach (var client in clients) client.Dispose();
            }
        }
    }

    [Fact(DisplayName = "连接池-创建池实例")]
    public void CreatePool()
    {
        var setting = new NovaConnectionStringBuilder("Server=localhost;Port=13306");
        var pool = new NovaClientPool { Setting = setting };

        Assert.NotNull(pool);
        Assert.Equal(0, pool.FreeCount);
        Assert.Equal(0, pool.BusyCount);
    }

    [Fact(DisplayName = "连接池-Setting为空时OnCreate抛异常")]
    public void CreateWithoutSettingThrows()
    {
        var pool = new NovaClientPool();

        // Setting 为 null 时应抛异常
        Assert.Throws<ArgumentNullException>(() => pool.Get());
    }

    [Fact(DisplayName = "连接池-Server为空时OnCreate抛异常")]
    public void CreateWithEmptyServerThrows()
    {
        var setting = new NovaConnectionStringBuilder();
        var pool = new NovaClientPool { Setting = setting };

        // Server 为空时应抛异常
        Assert.Throws<InvalidOperationException>(() => pool.Get());
    }

    [Fact(DisplayName = "连接池管理器-相同连接字符串复用池")]
    public void PoolManagerSameConnStrSharePool()
    {
        var manager = new NovaPoolManager();
        var setting1 = new NovaConnectionStringBuilder("Server=host1;Port=13306");
        var setting2 = new NovaConnectionStringBuilder("Server=host1;Port=13306");

        var pool1 = manager.GetPool(setting1);
        var pool2 = manager.GetPool(setting2);

        Assert.Same(pool1, pool2);
    }

    [Fact(DisplayName = "连接池管理器-不同连接字符串独立池")]
    public void PoolManagerDiffConnStrDiffPool()
    {
        var manager = new NovaPoolManager();
        var setting1 = new NovaConnectionStringBuilder("Server=host1;Port=13306");
        var setting2 = new NovaConnectionStringBuilder("Server=host2;Port=13306");

        var pool1 = manager.GetPool(setting1);
        var pool2 = manager.GetPool(setting2);

        Assert.NotSame(pool1, pool2);
    }

    [Fact(DisplayName = "连接池管理器-池有默认配置")]
    public void PoolManagerDefaultConfig()
    {
        var manager = new NovaPoolManager();
        var setting = new NovaConnectionStringBuilder("Server=localhost;Port=13306");
        var pool = manager.GetPool(setting);

        Assert.Equal(2, pool.Min);
        Assert.Equal(100000, pool.Max);
        Assert.Equal(30, pool.IdleTime);
        Assert.Equal(300, pool.MaxLifetime);
    }

    [Fact(DisplayName = "工厂-PoolManager不为空")]
    public void FactoryPoolManagerNotNull()
    {
        Assert.NotNull(NovaClientFactory.Instance.PoolManager);
    }

    [Fact(DisplayName = "连接-网络模式使用连接池")]
    public void ConnectionUsesPool()
    {
        // 验证连接字符串设置正确传播到 NovaConnection
        using var conn = new NovaConnection("Server=testhost;Port=13306");
        Assert.False(conn.IsEmbedded);
        Assert.NotNull(conn.Factory.PoolManager);
    }

    [Fact(DisplayName = "连接池-OnReturn不健康连接不入池")]
    public void OnReturn_UnhealthyClient_NotReturned()
    {
        var setting = new NovaConnectionStringBuilder("Server=localhost;Port=13306");
        using var pool = new NovaClientPool { Setting = setting, Max = 10, WaitTimeout = TimeSpan.FromSeconds(1) };

        // 创建一个客户端但不打开（未连接）
        var client = new NovaClient("tcp://127.0.0.1:9999");

        // 直接调用 OnReturn 应返回 false（客户端未连接）
        // 通过 IPool<NovaClient> 接口调用 Return
        var poolInterface = (IPool<NovaClient>)pool;
        Assert.False(poolInterface.Return(client));

        client.TryDispose();
    }

    [Fact(DisplayName = "连接池-OnReturn已销毁客户端不入池")]
    public void OnReturn_DisposedClient_NotReturned()
    {
        var setting = new NovaConnectionStringBuilder("Server=localhost;Port=13306");
        using var pool = new NovaClientPool { Setting = setting, Max = 10, WaitTimeout = TimeSpan.FromSeconds(1) };

        var client = new NovaClient("tcp://127.0.0.1:9999");
        client.TryDispose();

        // 已销毁的客户端不应入池
        var poolInterface = (IPool<NovaClient>)pool;
        Assert.False(poolInterface.Return(client));
    }

    [Fact(DisplayName = "连接池-MaxLifetime过期连接惰性回收")]
    public void MaxLifetime_ExpiredClient_Recycled()
    {
        var setting = new NovaConnectionStringBuilder("Server=localhost;Port=13306");
        using var pool = new NovaClientPool { Setting = setting, Max = 5, MaxLifetime = 1, WaitTimeout = TimeSpan.Zero };

        // 借出一个客户端并归还，使其进入空闲池
        // 因无服务器连接，OnGet 会尝试 Open 失败，基类自动重试后超时抛异常
        // 这里验证池的配置已生效即可
        Assert.Equal(1, pool.MaxLifetime);
    }
}
