using System;
using System.Linq;
using System.Text;
using NewLife.NovaDb.Engine.KV;
using Xunit;

namespace XUnitTest.Engine.KV;

/// <summary>KvStore 扩展功能单元测试</summary>
public class KvStoreExtensionTests
{
    [Fact(DisplayName = "测试Clear清空所有数据")]
    public void TestClear()
    {
        var store = new KvStore();
        store.SetString("k1", "v1");
        store.SetString("k2", "v2");

        Assert.Equal(2, store.Count);
        store.Clear();
        Assert.Equal(0, store.Count);
    }

    [Fact(DisplayName = "测试Search搜索键")]
    public void TestSearch()
    {
        var store = new KvStore();
        store.SetString("user:1", "Alice");
        store.SetString("user:2", "Bob");
        store.SetString("order:1", "Order1");

        var results = store.Search("user:*").ToList();
        Assert.Equal(2, results.Count);

        results = store.Search("*").ToList();
        Assert.Equal(3, results.Count);

        results = store.Search("order:*").ToList();
        Assert.Single(results);
    }

    [Fact(DisplayName = "测试Search分页")]
    public void TestSearchPagination()
    {
        var store = new KvStore();
        for (var i = 0; i < 10; i++)
            store.SetString($"key:{i}", $"val:{i}");

        var results = store.Search("key:*", 2, 3).ToList();
        Assert.Equal(3, results.Count);
    }

    [Fact(DisplayName = "测试GetTtl获取剩余时间")]
    public void TestGetTtl()
    {
        var store = new KvStore();
        store.SetString("key1", "value1", TimeSpan.FromSeconds(3600));

        var ttl = store.GetTtl("key1");
        Assert.True(ttl.TotalSeconds > 3590);
    }

    [Fact(DisplayName = "测试GetTtl永不过期返回Zero")]
    public void TestGetTtlNoExpire()
    {
        var store = new KvStore();
        store.SetString("key1", "value1");

        var ttl = store.GetTtl("key1");
        Assert.Equal(TimeSpan.Zero, ttl);
    }

    [Fact(DisplayName = "测试GetTtl不存在键返回负值")]
    public void TestGetTtlNonExistent()
    {
        var store = new KvStore();

        var ttl = store.GetTtl("missing");
        Assert.True(ttl.TotalSeconds < 0);
    }

    [Fact(DisplayName = "测试批量删除")]
    public void TestBatchDelete()
    {
        var store = new KvStore();
        store.SetString("a", "1");
        store.SetString("b", "2");
        store.SetString("c", "3");

        var count = store.Delete(new[] { "a", "b" });
        Assert.Equal(2, count);
        Assert.False(store.Exists("a"));
        Assert.False(store.Exists("b"));
        Assert.True(store.Exists("c"));
    }

    [Fact(DisplayName = "测试按模式删除")]
    public void TestDeleteByPattern()
    {
        var store = new KvStore();
        store.SetString("temp:1", "a");
        store.SetString("temp:2", "b");
        store.SetString("keep", "c");

        var count = store.DeleteByPattern("temp:*");
        Assert.Equal(2, count);
        Assert.False(store.Exists("temp:1"));
        Assert.True(store.Exists("keep"));
    }

    [Fact(DisplayName = "测试通配符问号匹配")]
    public void TestQuestionMarkPattern()
    {
        var store = new KvStore();
        store.SetString("a1", "v1");
        store.SetString("a2", "v2");
        store.SetString("ab", "v3");

        var results = store.Search("a?").ToList();
        Assert.Equal(3, results.Count);
    }
}
