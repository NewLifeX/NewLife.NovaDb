using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NewLife.NovaDb.Caching;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;
using NewLife.NovaDb.Engine.KV;
using Xunit;

#nullable enable

namespace XUnitTest.Caching;

/// <summary>NovaCache 单元测试</summary>
public class NovaCacheTests : IDisposable
{
    private readonly String _testDir;

    public NovaCacheTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"NovaCacheTests_{Guid.NewGuid():N}");
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

    private NovaCache CreateCache()
    {
        var kvStore = new KvStore(null, Path.Combine(_testDir, "cache.kvd"));
        var mqPath = Path.Combine(_testDir, "mq");
        var fluxEngine = new FluxEngine(mqPath, new DbOptions { FluxPartitionHours = 1 });
        return new NovaCache(kvStore) { FluxEngine = fluxEngine };
    }

    [Fact(DisplayName = "测试嵌入模式标识")]
    public void TestIsEmbedded()
    {
        using var cache = CreateCache();
        Assert.True(cache.IsEmbedded);
    }

    [Fact(DisplayName = "测试设置和获取字符串")]
    public void TestSetAndGetString()
    {
        using var cache = CreateCache();

        cache.Set("name", "NovaDb");
        var result = cache.Get<String>("name");
        Assert.Equal("NovaDb", result);
    }

    [Fact(DisplayName = "测试设置和获取整数")]
    public void TestSetAndGetInt()
    {
        using var cache = CreateCache();

        cache.Set("count", 42);
        var result = cache.Get<Int32>("count");
        Assert.Equal(42, result);
    }

    [Fact(DisplayName = "测试设置和获取布尔值")]
    public void TestSetAndGetBool()
    {
        using var cache = CreateCache();

        cache.Set("flag", true);
        var result = cache.Get<Boolean>("flag");
        Assert.True(result);
    }

    [Fact(DisplayName = "测试获取不存在的键")]
    public void TestGetNonExistent()
    {
        using var cache = CreateCache();

        var result = cache.Get<String>("missing");
        Assert.Null(result);
    }

    [Fact(DisplayName = "测试包含键")]
    public void TestContainsKey()
    {
        using var cache = CreateCache();

        cache.Set("key1", "value1");
        Assert.True(cache.ContainsKey("key1"));
        Assert.False(cache.ContainsKey("key2"));
    }

    [Fact(DisplayName = "测试移除键")]
    public void TestRemove()
    {
        using var cache = CreateCache();

        cache.Set("key1", "value1");
        Assert.True(cache.ContainsKey("key1"));

        var removed = cache.Remove("key1");
        Assert.Equal(1, removed);
        Assert.False(cache.ContainsKey("key1"));
    }

    [Fact(DisplayName = "测试批量移除")]
    public void TestRemoveMultiple()
    {
        using var cache = CreateCache();

        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Set("c", "3");

        var removed = cache.Remove("a", "b");
        Assert.Equal(2, removed);
        Assert.False(cache.ContainsKey("a"));
        Assert.False(cache.ContainsKey("b"));
        Assert.True(cache.ContainsKey("c"));
    }

    [Fact(DisplayName = "测试清空缓存")]
    public void TestClear()
    {
        using var cache = CreateCache();

        cache.Set("k1", "v1");
        cache.Set("k2", "v2");
        Assert.Equal(2, cache.Count);

        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    [Fact(DisplayName = "测试过期时间设置和获取")]
    public void TestExpire()
    {
        using var cache = CreateCache();

        cache.Set("key1", "value1", 3600);
        var ttl = cache.GetExpire("key1");
        Assert.True(ttl.TotalSeconds > 3590);

        cache.SetExpire("key1", TimeSpan.FromSeconds(60));
        ttl = cache.GetExpire("key1");
        Assert.True(ttl.TotalSeconds > 50 && ttl.TotalSeconds <= 60);
    }

    [Fact(DisplayName = "测试过期不存在的键")]
    public void TestExpireNonExistent()
    {
        using var cache = CreateCache();

        var ttl = cache.GetExpire("missing");
        Assert.True(ttl.TotalSeconds < 0);
    }

    [Fact(DisplayName = "测试原子递增")]
    public void TestIncrement()
    {
        using var cache = CreateCache();

        var val = cache.Increment("counter", 1);
        Assert.Equal(1, val);

        val = cache.Increment("counter", 5);
        Assert.Equal(6, val);
    }

    [Fact(DisplayName = "测试原子递减")]
    public void TestDecrement()
    {
        using var cache = CreateCache();

        cache.Increment("counter", 10);
        var val = cache.Decrement("counter", 3);
        Assert.Equal(7, val);
    }

    [Fact(DisplayName = "测试浮点递增")]
    public void TestIncrementDouble()
    {
        using var cache = CreateCache();

        var val = cache.Increment("price", 1.5);
        Assert.Equal(1.5, val);

        val = cache.Increment("price", 2.3);
        Assert.Equal(3.8, val, 5);
    }

    [Fact(DisplayName = "测试搜索键")]
    public void TestSearch()
    {
        using var cache = CreateCache();

        cache.Set("user:1", "Alice");
        cache.Set("user:2", "Bob");
        cache.Set("order:1", "Order1");

        var userKeys = cache.Search("user:*");
        Assert.Equal(2, userKeys.Count());

        var allKeys = cache.Search("*");
        Assert.Equal(3, allKeys.Count());
    }

    [Fact(DisplayName = "测试通配符移除")]
    public void TestRemoveByPattern()
    {
        using var cache = CreateCache();

        cache.Set("temp:1", "a");
        cache.Set("temp:2", "b");
        cache.Set("keep", "c");

        var removed = cache.Remove("temp:*");
        Assert.Equal(2, removed);
        Assert.False(cache.ContainsKey("temp:1"));
        Assert.True(cache.ContainsKey("keep"));
    }

    [Fact(DisplayName = "测试Count和Keys")]
    public void TestCountAndKeys()
    {
        using var cache = CreateCache();

        cache.Set("a", "1");
        cache.Set("b", "2");

        Assert.Equal(2, cache.Count);
        Assert.Contains("a", cache.Keys);
        Assert.Contains("b", cache.Keys);
    }

    [Fact(DisplayName = "测试批量获取")]
    public void TestGetAll()
    {
        using var cache = CreateCache();

        cache.Set("x", "1");
        cache.Set("y", "2");

        var dict = cache.GetAll<String>(new[] { "x", "y", "z" });
        Assert.Equal("1", dict["x"]);
        Assert.Equal("2", dict["y"]);
        Assert.Null(dict["z"]);
    }

    [Fact(DisplayName = "测试批量设置")]
    public void TestSetAll()
    {
        using var cache = CreateCache();

        var data = new Dictionary<String, String>
        {
            ["a"] = "1",
            ["b"] = "2",
        };
        cache.SetAll(data);

        Assert.Equal("1", cache.Get<String>("a"));
        Assert.Equal("2", cache.Get<String>("b"));
    }

    [Fact(DisplayName = "测试Add不覆盖已有值")]
    public void TestAddNotOverwrite()
    {
        using var cache = CreateCache();

        Assert.True(cache.Add("key", "first"));
        Assert.False(cache.Add("key", "second"));
        Assert.Equal("first", cache.Get<String>("key"));
    }

    [Fact(DisplayName = "测试Replace返回旧值")]
    public void TestReplace()
    {
        using var cache = CreateCache();

        cache.Set("key", "old");
        var oldVal = cache.Replace("key", "new");
        Assert.Equal("old", oldVal);
        Assert.Equal("new", cache.Get<String>("key"));
    }

    [Fact(DisplayName = "测试TryGetValue")]
    public void TestTryGetValue()
    {
        using var cache = CreateCache();

        cache.Set("key", "value");
        Assert.True(cache.TryGetValue<String>("key", out var val));
        Assert.Equal("value", val);

        Assert.False(cache.TryGetValue<String>("missing", out _));
    }

    [Fact(DisplayName = "测试GetOrAdd")]
    public void TestGetOrAdd()
    {
        using var cache = CreateCache();

        var val = cache.GetOrAdd("key", k => "computed");
        Assert.Equal("computed", val);

        // 再次调用应返回已有值
        var val2 = cache.GetOrAdd("key", k => "other");
        Assert.Equal("computed", val2);
    }

    [Fact(DisplayName = "测试TTL过期后自动清理")]
    public void TestTtlAutoCleanup()
    {
        using var cache = CreateCache();

        // 设置一个很短的过期时间
        cache.Set("temp", "data", 1);
        Assert.True(cache.ContainsKey("temp"));

        // 等待过期
        Thread.Sleep(1500);
        Assert.False(cache.ContainsKey("temp"));
    }

    [Fact(DisplayName = "测试索引器")]
    public void TestIndexer()
    {
        using var cache = CreateCache();

        cache["key"] = "value";
        Assert.Equal("value", cache["key"]?.ToString());
    }

    [Fact(DisplayName = "测试Commit返回零")]
    public void TestCommit()
    {
        using var cache = CreateCache();
        Assert.Equal(0, cache.Commit());
    }

    [Fact(DisplayName = "测试浮点递减")]
    public void TestDecrementDouble()
    {
        using var cache = CreateCache();

        cache.Increment("price", 10.0);
        var val = cache.Decrement("price", 3.5);
        Assert.Equal(6.5, val, 5);
    }

    [Fact(DisplayName = "测试FluxEngine属性")]
    public void TestFluxEngineProperty()
    {
        var kvStore = new KvStore(null, Path.Combine(_testDir, "flux_prop.kvd"));
        using var cache = new NovaCache(kvStore);

        Assert.Null(cache.FluxEngine);

        var mqPath = Path.Combine(_testDir, "mq_prop");
        var fluxEngine = new FluxEngine(mqPath, new DbOptions { FluxPartitionHours = 1 });
        cache.FluxEngine = fluxEngine;

        Assert.NotNull(cache.FluxEngine);
        Assert.Same(fluxEngine, cache.FluxEngine);
    }

    [Fact(DisplayName = "测试Encoder属性")]
    public void TestEncoderProperty()
    {
        using var cache = CreateCache();

        Assert.NotNull(cache.Encoder);
        Assert.IsType<NovaJsonEncoder>(cache.Encoder);
    }

    [Fact(DisplayName = "测试GetQueue成功获取队列")]
    public void TestGetQueueWithFluxEngine()
    {
        using var cache = CreateCache();

        var queue = cache.GetQueue<String>("test-queue");
        Assert.NotNull(queue);
    }

    [Fact(DisplayName = "测试GetQueue无FluxEngine抛出异常")]
    public void TestGetQueueWithoutFluxEngineThrows()
    {
        var kvStore = new KvStore(null, Path.Combine(_testDir, "no_flux.kvd"));
        using var cache = new NovaCache(kvStore);

        Assert.Throws<NotSupportedException>(() => cache.GetQueue<String>("test-queue"));
    }

    [Fact(DisplayName = "测试CreateEventBus有FluxEngine")]
    public void TestCreateEventBusWithFluxEngine()
    {
        using var cache = CreateCache();

        var bus = cache.CreateEventBus<String>("test-topic", "client1");
        Assert.NotNull(bus);
    }

    [Fact(DisplayName = "测试CreateEventBus无FluxEngine回退基类")]
    public void TestCreateEventBusWithoutFluxEngine()
    {
        var kvStore = new KvStore(null, Path.Combine(_testDir, "no_flux2.kvd"));
        using var cache = new NovaCache(kvStore);

        var bus = cache.CreateEventBus<String>("test-topic", "client1");
        Assert.NotNull(bus);
    }

    [Fact(DisplayName = "测试Set过期时间为零永不过期")]
    public void TestSetExpireZero()
    {
        using var cache = CreateCache();

        cache.Set("key", "value", 0);
        Assert.True(cache.ContainsKey("key"));
        Assert.Equal("value", cache.Get<String>("key"));
    }

    [Fact(DisplayName = "测试Set过期时间为负使用默认")]
    public void TestSetExpireNegative()
    {
        using var cache = CreateCache();

        cache.Set("key", "value", -1);
        Assert.True(cache.ContainsKey("key"));
        Assert.Equal("value", cache.Get<String>("key"));
    }

    [Fact(DisplayName = "测试Remove空数组返回零")]
    public void TestRemoveEmptyArray()
    {
        using var cache = CreateCache();

        Assert.Equal(0, cache.Remove(Array.Empty<String>()));
    }

    [Fact(DisplayName = "测试Remove null数组返回零")]
    public void TestRemoveNullArray()
    {
        using var cache = CreateCache();

        Assert.Equal(0, cache.Remove((String[])null!));
    }

    [Fact(DisplayName = "测试Get获取Object类型")]
    public void TestGetObjectType()
    {
        using var cache = CreateCache();

        cache.Set("key", "hello");
        var val = cache.Get<Object>("key");
        Assert.NotNull(val);
        Assert.Equal("hello", val.ToString());
    }

    [Fact(DisplayName = "测试Remove不存在的键返回零")]
    public void TestRemoveNonExistent()
    {
        using var cache = CreateCache();

        var removed = cache.Remove("nonexistent");
        Assert.Equal(0, removed);
    }

    [Fact(DisplayName = "测试Search分页")]
    public void TestSearchPagination()
    {
        using var cache = CreateCache();

        for (var i = 1; i <= 5; i++)
            cache.Set($"item:{i}", $"val{i}");

        var page = cache.Search("item:*", 2, 2);
        Assert.Equal(2, page.Count());
    }

    [Fact(DisplayName = "测试嵌入模式构造函数null抛出异常")]
    public void TestConstructorNullKvStoreThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new NovaCache((KvStore)null!));
    }

    [Fact(DisplayName = "测试网络模式构造函数null抛出异常")]
    public void TestConstructorNullClientThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new NovaCache((NewLife.NovaDb.Client.NovaClient)null!));
    }
}
