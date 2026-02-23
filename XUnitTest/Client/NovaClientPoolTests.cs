using System;
using NewLife.NovaDb.Client;
using Xunit;

namespace XUnitTest.Client;

/// <summary>NovaDb 连接池单元测试</summary>
public class NovaClientPoolTests
{
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
        Assert.Equal(300, pool.AllIdleTime);
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
}
