using System;
using System.IO;
using NewLife.NovaDb.Caching;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;
using NewLife.NovaDb.Engine.KV;
using Xunit;

#nullable enable

namespace XUnitTest.Caching;

/// <summary>NovaCacheProvider 单元测试</summary>
public class NovaCacheProviderTests : IDisposable
{
    private readonly String _testDir;

    public NovaCacheProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"NovaCacheProviderTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); }
            catch { }
        }
    }

    [Fact(DisplayName = "测试嵌入模式创建")]
    public void TestEmbeddedCreate()
    {
        var connStr = $"Data Source={_testDir}";
        var provider = new NovaCacheProvider(connStr);

        Assert.NotNull(provider.Cache);
        Assert.IsType<NovaCache>(provider.Cache);

        var novaCache = (NovaCache)provider.Cache;
        Assert.True(novaCache.IsEmbedded);
    }

    [Fact(DisplayName = "测试缓存基本操作")]
    public void TestCacheOperations()
    {
        var connStr = $"Data Source={_testDir}";
        var provider = new NovaCacheProvider(connStr);

        provider.Cache.Set("key1", "value1");
        Assert.Equal("value1", provider.Cache.Get<String>("key1"));
    }

    [Fact(DisplayName = "测试队列功能")]
    public void TestQueueOperations()
    {
        var connStr = $"Data Source={_testDir}";
        var provider = new NovaCacheProvider(connStr);

        Assert.NotNull(provider.StreamManager);

        var queue = provider.GetQueue<String>("test-topic", "test-group");
        Assert.NotNull(queue);

        queue.Add("hello");
        var msgs = queue.Take(1);
        Assert.Single(msgs);
    }

    [Fact(DisplayName = "测试分布式锁")]
    public void TestAcquireLock()
    {
        var connStr = $"Data Source={_testDir}";
        var provider = new NovaCacheProvider(connStr);

        using var lockObj = provider.AcquireLock("test-lock", 5000);
        Assert.NotNull(lockObj);
    }

    [Fact(DisplayName = "测试通过NovaCache实例创建")]
    public void TestCreateWithCache()
    {
        var kvStore = new KvStore();
        var cache = new NovaCache(kvStore);
        var provider = new NovaCacheProvider(cache);

        Assert.Same(cache, provider.Cache);
    }

    [Fact(DisplayName = "测试InnerCache默认值")]
    public void TestInnerCache()
    {
        var connStr = $"Data Source={_testDir}";
        var provider = new NovaCacheProvider(connStr);

        Assert.NotNull(provider.InnerCache);
    }
}
