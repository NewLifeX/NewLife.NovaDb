using System;
using System.IO;
using System.Linq;
using System.Threading;
using NewLife.Caching;
using NewLife.NovaDb.Caching;
using NewLife.NovaDb.Client;
using Xunit;

#nullable enable

namespace XUnitTest.Caching;

/// <summary>嵌入模式 NovaCache 集成测试，通过 NovaClientFactory 创建缓存实例</summary>
public class NovaCacheEmbeddedIntegrationTests : IDisposable
{
    private readonly String _testDir;
    private readonly NovaClientFactory _factory;

    public NovaCacheEmbeddedIntegrationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"NovaCacheEmbedded_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _factory = new NovaClientFactory();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); }
            catch { }
        }
    }

    private String ConnectionString => $"Data Source={_testDir}";

    private ICache CreateCache(String tableName = "kv") => _factory.GetCache(ConnectionString, tableName);

    [Fact(DisplayName = "嵌入缓存-创建缓存实例")]
    public void TestCreateCache()
    {
        var cache = CreateCache();

        Assert.NotNull(cache);
        Assert.IsType<NovaCache>(cache);
        Assert.True(((NovaCache)cache).IsEmbedded);
    }

    [Fact(DisplayName = "嵌入缓存-设置和获取字符串")]
    public void TestSetAndGetString()
    {
        var cache = CreateCache();

        cache.Set("name", "NovaDb");
        Assert.Equal("NovaDb", cache.Get<String>("name"));
    }

    [Fact(DisplayName = "嵌入缓存-设置和获取整数")]
    public void TestSetAndGetInt()
    {
        var cache = CreateCache();

        cache.Set("count", 42);
        Assert.Equal(42, cache.Get<Int32>("count"));
    }

    [Fact(DisplayName = "嵌入缓存-设置和获取布尔值")]
    public void TestSetAndGetBool()
    {
        var cache = CreateCache();

        cache.Set("flag", true);
        Assert.True(cache.Get<Boolean>("flag"));
    }

    [Fact(DisplayName = "嵌入缓存-获取不存在的键")]
    public void TestGetNonExistent()
    {
        var cache = CreateCache();

        Assert.Null(cache.Get<String>("missing"));
    }

    [Fact(DisplayName = "嵌入缓存-包含键")]
    public void TestContainsKey()
    {
        var cache = CreateCache();

        cache.Set("k1", "v1");
        Assert.True(cache.ContainsKey("k1"));
        Assert.False(cache.ContainsKey("k2"));
    }

    [Fact(DisplayName = "嵌入缓存-移除键")]
    public void TestRemove()
    {
        var cache = CreateCache();

        cache.Set("k1", "v1");
        Assert.Equal(1, cache.Remove("k1"));
        Assert.False(cache.ContainsKey("k1"));
    }

    [Fact(DisplayName = "嵌入缓存-批量移除")]
    public void TestRemoveMultiple()
    {
        var cache = CreateCache();

        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Set("c", "3");

        Assert.Equal(2, cache.Remove("a", "b"));
        Assert.True(cache.ContainsKey("c"));
    }

    [Fact(DisplayName = "嵌入缓存-清空")]
    public void TestClear()
    {
        var cache = CreateCache();

        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Clear();

        Assert.Equal(0, cache.Count);
    }

    [Fact(DisplayName = "嵌入缓存-过期时间")]
    public void TestExpire()
    {
        var cache = CreateCache();

        cache.Set("key", "val", 3600);
        var ttl = cache.GetExpire("key");
        Assert.True(ttl.TotalSeconds > 3590);

        cache.SetExpire("key", TimeSpan.FromSeconds(60));
        ttl = cache.GetExpire("key");
        Assert.True(ttl.TotalSeconds > 50 && ttl.TotalSeconds <= 60);
    }

    [Fact(DisplayName = "嵌入缓存-原子递增")]
    public void TestIncrement()
    {
        var cache = CreateCache();

        Assert.Equal(1, cache.Increment("counter", 1));
        Assert.Equal(6, cache.Increment("counter", 5));
    }

    [Fact(DisplayName = "嵌入缓存-原子递减")]
    public void TestDecrement()
    {
        var cache = CreateCache();

        cache.Increment("counter", 10);
        Assert.Equal(7, cache.Decrement("counter", 3));
    }

    [Fact(DisplayName = "嵌入缓存-浮点递增")]
    public void TestIncrementDouble()
    {
        var cache = CreateCache();

        Assert.Equal(1.5, cache.Increment("price", 1.5));
        Assert.Equal(3.8, cache.Increment("price", 2.3), 5);
    }

    [Fact(DisplayName = "嵌入缓存-浮点递减")]
    public void TestDecrementDouble()
    {
        var cache = CreateCache();

        cache.Increment("price", 10.0);
        Assert.Equal(6.5, cache.Decrement("price", 3.5), 5);
    }

    [Fact(DisplayName = "嵌入缓存-搜索键")]
    public void TestSearch()
    {
        var cache = CreateCache();

        cache.Set("user:1", "Alice");
        cache.Set("user:2", "Bob");
        cache.Set("order:1", "Order1");

        Assert.Equal(2, cache.Search("user:*").Count());
        Assert.Equal(3, cache.Search("*").Count());
    }

    [Fact(DisplayName = "嵌入缓存-通配符移除")]
    public void TestRemoveByPattern()
    {
        var cache = CreateCache();

        cache.Set("temp:1", "a");
        cache.Set("temp:2", "b");
        cache.Set("keep", "c");

        Assert.Equal(2, cache.Remove("temp:*"));
        Assert.True(cache.ContainsKey("keep"));
    }

    [Fact(DisplayName = "嵌入缓存-Count和Keys")]
    public void TestCountAndKeys()
    {
        var cache = CreateCache();

        cache.Set("x", "1");
        cache.Set("y", "2");

        Assert.Equal(2, cache.Count);
        Assert.Contains("x", cache.Keys);
        Assert.Contains("y", cache.Keys);
    }

    [Fact(DisplayName = "嵌入缓存-Add不覆盖")]
    public void TestAddNotOverwrite()
    {
        var cache = CreateCache();

        Assert.True(cache.Add("key", "first"));
        Assert.False(cache.Add("key", "second"));
        Assert.Equal("first", cache.Get<String>("key"));
    }

    [Fact(DisplayName = "嵌入缓存-Replace")]
    public void TestReplace()
    {
        var cache = CreateCache();

        cache.Set("key", "old");
        Assert.Equal("old", cache.Replace("key", "new"));
        Assert.Equal("new", cache.Get<String>("key"));
    }

    [Fact(DisplayName = "嵌入缓存-批量操作")]
    public void TestBatchOperations()
    {
        var cache = CreateCache();

        var data = new System.Collections.Generic.Dictionary<String, String>
        {
            ["a"] = "1",
            ["b"] = "2",
        };
        cache.SetAll(data);

        var result = cache.GetAll<String>(new[] { "a", "b", "c" });
        Assert.Equal("1", result["a"]);
        Assert.Equal("2", result["b"]);
        Assert.Null(result["c"]);
    }

    [Fact(DisplayName = "嵌入缓存-TTL过期自动清理")]
    public void TestTtlAutoCleanup()
    {
        var cache = CreateCache();

        cache.Set("temp", "data", 1);
        Assert.True(cache.ContainsKey("temp"));

        Thread.Sleep(1500);
        Assert.False(cache.ContainsKey("temp"));
    }

    [Fact(DisplayName = "嵌入缓存-多表隔离")]
    public void TestMultiTableIsolation()
    {
        var cache1 = CreateCache("table1");
        var cache2 = CreateCache("table2");

        cache1.Set("key", "value1");
        cache2.Set("key", "value2");

        Assert.Equal("value1", cache1.Get<String>("key"));
        Assert.Equal("value2", cache2.Get<String>("key"));
    }

    [Fact(DisplayName = "嵌入缓存-CacheProvider完整流程")]
    public void TestCacheProviderFullFlow()
    {
        var provider = _factory.GetCacheProvider(ConnectionString);

        Assert.NotNull(provider);

        var cache = provider.Cache;
        Assert.NotNull(cache);

        cache.Set("provider_key", "provider_value");
        Assert.Equal("provider_value", cache.Get<String>("provider_key"));

        cache.Remove("provider_key");
        Assert.False(cache.ContainsKey("provider_key"));
    }

    [Fact(DisplayName = "嵌入缓存-分布式锁")]
    public void TestDistributedLock()
    {
        var cache = CreateCache();

        using var lockObj = cache.AcquireLock("lock-key", 5000);
        Assert.NotNull(lockObj);
    }

    [Fact(DisplayName = "嵌入缓存-GetOrAdd")]
    public void TestGetOrAdd()
    {
        var cache = CreateCache();

        var val = cache.GetOrAdd("key", k => "computed");
        Assert.Equal("computed", val);

        var val2 = cache.GetOrAdd("key", k => "other");
        Assert.Equal("computed", val2);
    }

    [Fact(DisplayName = "嵌入缓存-TryGetValue")]
    public void TestTryGetValue()
    {
        var cache = CreateCache();

        cache.Set("key", "value");
        Assert.True(cache.TryGetValue<String>("key", out var val));
        Assert.Equal("value", val);

        Assert.False(cache.TryGetValue<String>("missing", out _));
    }
}
