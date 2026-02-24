using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NewLife.Caching;
using NewLife.NovaDb.Caching;
using NewLife.NovaDb.Client;
using Xunit;

#nullable enable

namespace XUnitTest.Caching;

/// <summary>网络模式 NovaCache 集成测试，通过 NovaServer + NovaClient 远程操作缓存</summary>
[Collection("IntegrationTests")]
public class NovaCacheNetworkIntegrationTests : IClassFixture<IntegrationServerFixture>
{
    private readonly IntegrationServerFixture _fixture;
    private Int32 _port => _fixture.Port;

    public NovaCacheNetworkIntegrationTests(IntegrationServerFixture fixture)
    {
        _fixture = fixture;
    }

    private NovaCache CreateNetworkCache()
    {
        var client = new NovaClient($"tcp://127.0.0.1:{_port}");
        client.Open();
        return new NovaCache(client) { Name = "default" };
    }

    [Fact(DisplayName = "网络缓存-创建实例")]
    public void TestCreateNetworkCache()
    {
        using var cache = CreateNetworkCache();

        Assert.NotNull(cache);
        Assert.False(cache.IsEmbedded);
    }

    [Fact(DisplayName = "网络缓存-设置和获取字符串")]
    public void TestSetAndGetString()
    {
        using var cache = CreateNetworkCache();
        var key = "net_str_" + Guid.NewGuid().ToString("N")[..8];

        cache.Set(key, "hello");
        Assert.Equal("hello", cache.Get<String>(key));
    }

    [Fact(DisplayName = "网络缓存-设置和获取整数")]
    public void TestSetAndGetInt()
    {
        using var cache = CreateNetworkCache();
        var key = "net_int_" + Guid.NewGuid().ToString("N")[..8];

        cache.Set(key, 123);
        Assert.Equal(123, cache.Get<Int32>(key));
    }

    [Fact(DisplayName = "网络缓存-获取不存在的键")]
    public void TestGetNonExistent()
    {
        using var cache = CreateNetworkCache();

        Assert.Null(cache.Get<String>("net_miss_" + Guid.NewGuid().ToString("N")));
    }

    [Fact(DisplayName = "网络缓存-包含键")]
    public void TestContainsKey()
    {
        using var cache = CreateNetworkCache();
        var key = "net_contains_" + Guid.NewGuid().ToString("N")[..8];

        cache.Set(key, "v");
        Assert.True(cache.ContainsKey(key));
        Assert.False(cache.ContainsKey(key + "_miss"));
    }

    [Fact(DisplayName = "网络缓存-移除键")]
    public void TestRemove()
    {
        using var cache = CreateNetworkCache();
        var key = "net_rm_" + Guid.NewGuid().ToString("N")[..8];

        cache.Set(key, "v");
        Assert.Equal(1, cache.Remove(key));
        Assert.False(cache.ContainsKey(key));
    }

    [Fact(DisplayName = "网络缓存-批量移除")]
    public void TestRemoveMultiple()
    {
        using var cache = CreateNetworkCache();
        var prefix = "net_mrm_" + Guid.NewGuid().ToString("N")[..8];

        cache.Set($"{prefix}_a", "1");
        cache.Set($"{prefix}_b", "2");
        cache.Set($"{prefix}_c", "3");

        Assert.Equal(2, cache.Remove($"{prefix}_a", $"{prefix}_b"));
        Assert.True(cache.ContainsKey($"{prefix}_c"));
    }

    [Fact(DisplayName = "网络缓存-清空")]
    public void TestClear()
    {
        using var cache = CreateNetworkCache();

        cache.Set("net_clr_1", "a");
        cache.Set("net_clr_2", "b");
        cache.Clear();

        Assert.Equal(0, cache.Count);
    }

    [Fact(DisplayName = "网络缓存-Count和Keys")]
    public void TestCountAndKeys()
    {
        using var cache = CreateNetworkCache();

        // 先清空以确保计数准确
        cache.Clear();
        var key1 = "net_ck_a";
        var key2 = "net_ck_b";

        cache.Set(key1, "1");
        cache.Set(key2, "2");

        Assert.True(cache.Count >= 2);
        Assert.Contains(key1, cache.Keys);
        Assert.Contains(key2, cache.Keys);
    }

    [Fact(DisplayName = "网络缓存-原子递增")]
    public void TestIncrement()
    {
        using var cache = CreateNetworkCache();
        var key = "net_inc_" + Guid.NewGuid().ToString("N")[..8];

        Assert.Equal(1, cache.Increment(key, 1));
        Assert.Equal(6, cache.Increment(key, 5));
    }

    [Fact(DisplayName = "网络缓存-原子递减")]
    public void TestDecrement()
    {
        using var cache = CreateNetworkCache();
        var key = "net_dec_" + Guid.NewGuid().ToString("N")[..8];

        cache.Increment(key, 10);
        Assert.Equal(7, cache.Decrement(key, 3));
    }

    [Fact(DisplayName = "网络缓存-浮点递增")]
    public void TestIncrementDouble()
    {
        using var cache = CreateNetworkCache();
        var key = "net_incd_" + Guid.NewGuid().ToString("N")[..8];

        Assert.Equal(1.5, cache.Increment(key, 1.5));
        Assert.Equal(3.8, cache.Increment(key, 2.3), 5);
    }

    [Fact(DisplayName = "网络缓存-搜索键")]
    public void TestSearch()
    {
        using var cache = CreateNetworkCache();
        var prefix = "net_srch_" + Guid.NewGuid().ToString("N")[..8];

        cache.Set($"{prefix}:1", "a");
        cache.Set($"{prefix}:2", "b");

        var found = cache.Search($"{prefix}:*");
        Assert.Equal(2, found.Count());
    }

    [Fact(DisplayName = "网络缓存-通配符移除")]
    public void TestRemoveByPattern()
    {
        using var cache = CreateNetworkCache();
        var prefix = "net_prm_" + Guid.NewGuid().ToString("N")[..8];

        cache.Set($"{prefix}:1", "a");
        cache.Set($"{prefix}:2", "b");

        var removed = cache.Remove($"{prefix}:*");
        Assert.Equal(2, removed);
    }

    [Fact(DisplayName = "网络缓存-过期时间")]
    public void TestExpire()
    {
        using var cache = CreateNetworkCache();
        var key = "net_exp_" + Guid.NewGuid().ToString("N")[..8];

        cache.Set(key, "val", 3600);
        Assert.True(cache.GetExpire(key).TotalSeconds > 3590);

        cache.SetExpire(key, TimeSpan.FromSeconds(60));
        var ttl = cache.GetExpire(key);
        Assert.True(ttl.TotalSeconds > 50 && ttl.TotalSeconds <= 60);
    }

    [Fact(DisplayName = "网络缓存-完整CRUD流程")]
    public void TestFullCrudFlow()
    {
        using var cache = CreateNetworkCache();
        var key = "net_crud_" + Guid.NewGuid().ToString("N")[..8];

        // 写入
        cache.Set(key, "initial");
        Assert.True(cache.ContainsKey(key));
        Assert.Equal("initial", cache.Get<String>(key));

        // 更新
        cache.Set(key, "updated");
        Assert.Equal("updated", cache.Get<String>(key));

        // 删除
        cache.Remove(key);
        Assert.False(cache.ContainsKey(key));
        Assert.Null(cache.Get<String>(key));
    }

    [Fact(DisplayName = "网络缓存-GetQueue无FluxEngine抛出异常")]
    public void TestGetQueueThrowsWithoutFluxEngine()
    {
        using var cache = CreateNetworkCache();

        Assert.Throws<NotSupportedException>(() => cache.GetQueue<String>("test-queue"));
    }

    [Fact(DisplayName = "网络缓存-批量设置和获取")]
    public void TestSetAllAndGetAll()
    {
        using var cache = CreateNetworkCache();

        var prefix = "net_batch_" + Guid.NewGuid().ToString("N")[..8];
        var data = new Dictionary<String, String>
        {
            [$"{prefix}_a"] = "val1",
            [$"{prefix}_b"] = "val2",
            [$"{prefix}_c"] = "val3",
        };

        cache.SetAll(data);

        var keys = data.Keys.Concat(new[] { $"{prefix}_miss" }).ToArray();
        var result = cache.GetAll<String>(keys);

        Assert.Equal("val1", result[$"{prefix}_a"]);
        Assert.Equal("val2", result[$"{prefix}_b"]);
        Assert.Equal("val3", result[$"{prefix}_c"]);
        Assert.Null(result[$"{prefix}_miss"]);
    }

    [Fact(DisplayName = "网络缓存-批量操作吞吐量超过100000ops")]
    public void TestBatchThroughputExceeds100K()
    {
        using var cache = CreateNetworkCache();

        // 使用唯一前缀避免与其他测试冲突
        var prefix = "tp_" + Guid.NewGuid().ToString("N")[..8];

        // 准备批量数据：每批 1000 条，每条 64 字节
        var batchSize = 1000;
        var totalOps = 0;
        var data = new Dictionary<String, String>();
        for (var i = 0; i < batchSize; i++)
            data[$"{prefix}:{i}"] = new String('X', 64);

        // 预热
        cache.SetAll(data);
        cache.GetAll<String>(data.Keys);

        // 计时：批量写入（每次 SetAll 将 1000 条数据打包为一次 RPC 调用）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var iterations = 200;
        for (var n = 0; n < iterations; n++)
        {
            cache.SetAll(data);
            totalOps += batchSize;
        }
        sw.Stop();

        var writeOpsPerSec = totalOps / sw.Elapsed.TotalSeconds;

        // 计时：批量读取（每次 GetAll 将 1000 个键打包为一次 RPC 调用）
        totalOps = 0;
        var keys = data.Keys.ToArray();
        sw.Restart();
        for (var n = 0; n < iterations; n++)
        {
            cache.GetAll<String>(keys);
            totalOps += batchSize;
        }
        sw.Stop();

        var readOpsPerSec = totalOps / sw.Elapsed.TotalSeconds;

        // 清理测试数据，避免影响其他测试
        cache.Remove($"{prefix}:*");

        // 批量操作通过将多个操作合并到单次 RPC 调用来摊薄网络开销，
        // 写入或读取方向的吞吐量应超过 100,000 ops/s
        Assert.True(writeOpsPerSec > 100_000 || readOpsPerSec > 100_000,
            $"批量吞吐量未达标: Write={writeOpsPerSec:N0} ops/s, Read={readOpsPerSec:N0} ops/s");
    }
}
