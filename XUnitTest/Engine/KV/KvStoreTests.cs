using System;
using System.Text;
using System.Threading;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.KV;
using Xunit;

namespace XUnitTest.Engine.KV;

/// <summary>KV 存储引擎单元测试</summary>
public class KvStoreTests
{
    [Fact(DisplayName = "测试设置和获取值")]
    public void TestSetAndGet()
    {
        var store = new KvStore();
        var value = Encoding.UTF8.GetBytes("hello");

        store.Set("key1", value);

        var result = store.Get("key1");
        Assert.NotNull(result);
        Assert.Equal(value, result);
    }

    [Fact(DisplayName = "测试删除键")]
    public void TestDelete()
    {
        var store = new KvStore();
        store.Set("key1", [1, 2, 3]);

        Assert.True(store.Delete("key1"));
        Assert.False(store.Delete("key1"));
        Assert.False(store.Delete("nonexistent"));
    }

    [Fact(DisplayName = "测试键是否存在")]
    public void TestExists()
    {
        var store = new KvStore();
        store.Set("key1", [1, 2, 3]);

        Assert.True(store.Exists("key1"));
        Assert.False(store.Exists("key2"));
    }

    [Fact(DisplayName = "测试字符串便捷方法")]
    public void TestStringConvenience()
    {
        var store = new KvStore();
        store.SetString("name", "NovaDb");

        var result = store.GetString("name");
        Assert.Equal("NovaDb", result);

        Assert.Null(store.GetString("missing"));
    }

    [Fact(DisplayName = "测试 TTL 过期")]
    public void TestTtlExpiration()
    {
        var store = new KvStore();
        store.Set("key1", [1, 2, 3], TimeSpan.FromMilliseconds(10));

        // 未过期时可以获取
        Assert.NotNull(store.Get("key1"));

        // 等待过期
        Thread.Sleep(50);

        Assert.Null(store.Get("key1"));
        Assert.False(store.Exists("key1"));
    }

    [Fact(DisplayName = "测试惰性删除")]
    public void TestLazyDeletion()
    {
        var store = new KvStore();
        store.Set("key1", [1, 2, 3], TimeSpan.FromMilliseconds(10));

        Thread.Sleep(50);

        // Get 触发惰性删除
        Assert.Null(store.Get("key1"));

        // 验证条目已被移除（Count 不再包含该键）
        Assert.Equal(0, store.Count);
    }

    [Fact(DisplayName = "测试后台清理")]
    public void TestCleanupExpired()
    {
        var store = new KvStore();
        store.Set("key1", [1], TimeSpan.FromMilliseconds(10));
        store.Set("key2", [2], TimeSpan.FromMilliseconds(10));
        store.Set("key3", [3]); // 永不过期

        Thread.Sleep(50);

        var removed = store.CleanupExpired();
        Assert.Equal(2, removed);
        Assert.Equal(1, store.Count);
        Assert.True(store.Exists("key3"));
    }

    [Fact(DisplayName = "测试更新 TTL")]
    public void TestSetExpiration()
    {
        var store = new KvStore();
        store.Set("key1", [1, 2, 3]);

        // 设置 TTL
        Assert.True(store.SetExpiration("key1", TimeSpan.FromHours(1)));

        var expiration = store.GetExpiration("key1");
        Assert.NotNull(expiration);

        // 不存在的键返回 false
        Assert.False(store.SetExpiration("missing", TimeSpan.FromHours(1)));
    }

    [Fact(DisplayName = "测试获取过期时间")]
    public void TestGetExpiration()
    {
        var store = new KvStore();

        // 无 TTL
        store.Set("key1", [1]);
        Assert.Null(store.GetExpiration("key1"));

        // 有 TTL
        store.Set("key2", [2], TimeSpan.FromHours(1));
        var expiration = store.GetExpiration("key2");
        Assert.NotNull(expiration);
        Assert.True(expiration.Value > DateTime.UtcNow);

        // 不存在的键
        Assert.Null(store.GetExpiration("missing"));
    }

    [Fact(DisplayName = "测试覆盖已有值")]
    public void TestOverwriteValue()
    {
        var store = new KvStore();
        store.Set("key1", [1, 2, 3]);
        store.Set("key1", [4, 5, 6]);

        var result = store.Get("key1");
        Assert.NotNull(result);
        Assert.Equal(new Byte[] { 4, 5, 6 }, result);
        Assert.Equal(1, store.Count);
    }

    [Fact(DisplayName = "测试获取所有键")]
    public void TestGetAllKeys()
    {
        var store = new KvStore();
        store.Set("key1", [1]);
        store.Set("key2", [2], TimeSpan.FromMilliseconds(10));
        store.Set("key3", [3]);

        Thread.Sleep(50);

        var keys = store.GetAllKeys();
        Assert.Equal(2, keys.Count);
        Assert.Contains("key1", keys);
        Assert.Contains("key3", keys);
        Assert.DoesNotContain("key2", keys);
    }

    [Fact(DisplayName = "测试计数属性")]
    public void TestCountProperty()
    {
        var store = new KvStore();
        Assert.Equal(0, store.Count);

        store.Set("key1", [1]);
        store.Set("key2", [2]);
        Assert.Equal(2, store.Count);

        store.Set("key3", [3], TimeSpan.FromMilliseconds(10));
        Thread.Sleep(50);

        // 过期的不计入
        Assert.Equal(2, store.Count);
    }

    [Fact(DisplayName = "测试空键抛出异常")]
    public void TestEmptyKeyThrows()
    {
        var store = new KvStore();

        Assert.Throws<ArgumentException>(() => store.Set(null!, [1]));
        Assert.Throws<ArgumentException>(() => store.Set("", [1]));
        Assert.Throws<ArgumentException>(() => store.Get(null!));
        Assert.Throws<ArgumentException>(() => store.Get(""));
        Assert.Throws<ArgumentException>(() => store.Delete((String)null!));
        Assert.Throws<ArgumentException>(() => store.Delete(""));
        Assert.Throws<ArgumentException>(() => store.Exists(null!));
        Assert.Throws<ArgumentException>(() => store.Exists(""));
    }

    [Fact(DisplayName = "测试空值可存储")]
    public void TestNullValueCanBeStored()
    {
        var store = new KvStore();
        store.Set("key1", null);

        Assert.True(store.Exists("key1"));

        var result = store.Get("key1");
        Assert.Null(result);
        Assert.Equal(1, store.Count);
    }

    [Fact(DisplayName = "测试 Add 成功添加不存在的键")]
    public void TestAddSuccessWhenKeyNotExists()
    {
        var store = new KvStore();
        var value = Encoding.UTF8.GetBytes("hello");

        Assert.True(store.Add("key1", value, TimeSpan.FromHours(1)));
        Assert.Equal(value, store.Get("key1"));
    }

    [Fact(DisplayName = "测试 Add 已存在键时返回失败")]
    public void TestAddFailsWhenKeyExists()
    {
        var store = new KvStore();
        store.Set("key1", Encoding.UTF8.GetBytes("old"));

        Assert.False(store.Add("key1", Encoding.UTF8.GetBytes("new"), TimeSpan.FromHours(1)));

        // 值不应被覆盖
        Assert.Equal("old", store.GetString("key1"));
    }

    [Fact(DisplayName = "测试 Add 过期键可重新添加")]
    public void TestAddSuccessWhenKeyExpired()
    {
        var store = new KvStore();
        store.Set("key1", Encoding.UTF8.GetBytes("old"), TimeSpan.FromMilliseconds(10));

        Thread.Sleep(50);

        Assert.True(store.Add("key1", Encoding.UTF8.GetBytes("new"), TimeSpan.FromHours(1)));
        Assert.Equal("new", store.GetString("key1"));
    }

    [Fact(DisplayName = "测试 AddString 便捷方法")]
    public void TestAddString()
    {
        var store = new KvStore();

        Assert.True(store.AddString("key1", "hello", TimeSpan.FromHours(1)));
        Assert.Equal("hello", store.GetString("key1"));

        Assert.False(store.AddString("key1", "world", TimeSpan.FromHours(1)));
        Assert.Equal("hello", store.GetString("key1"));
    }

    [Fact(DisplayName = "测试 Inc 递增已有值")]
    public void TestIncExistingValue()
    {
        var store = new KvStore();
        store.SetString("counter", "10");

        var result = store.Inc("counter");
        Assert.Equal(11, result);
        Assert.Equal("11", store.GetString("counter"));
    }

    [Fact(DisplayName = "测试 Inc 初始化新键")]
    public void TestIncNewKey()
    {
        var store = new KvStore();

        var result = store.Inc("counter");
        Assert.Equal(1, result);
        Assert.Equal("1", store.GetString("counter"));
    }

    [Fact(DisplayName = "测试 Inc 自定义递增量")]
    public void TestIncCustomDelta()
    {
        var store = new KvStore();

        var result = store.Inc("counter", 5);
        Assert.Equal(5, result);

        result = store.Inc("counter", 10);
        Assert.Equal(15, result);
        Assert.Equal("15", store.GetString("counter"));
    }

    [Fact(DisplayName = "测试 Inc 过期键重新初始化")]
    public void TestIncExpiredKey()
    {
        var store = new KvStore();
        store.SetString("counter", "100", TimeSpan.FromMilliseconds(10));

        Thread.Sleep(50);

        var result = store.Inc("counter", 5);
        Assert.Equal(5, result);
    }

    [Fact(DisplayName = "测试 Inc 负数递减")]
    public void TestIncNegativeDelta()
    {
        var store = new KvStore();
        store.SetString("counter", "10");

        var result = store.Inc("counter", -3);
        Assert.Equal(7, result);
        Assert.Equal("7", store.GetString("counter"));
    }

    [Fact(DisplayName = "测试默认 TTL 在未指定时应用")]
    public void TestDefaultTtlApplied()
    {
        var options = new DbOptions { DefaultKvTtl = TimeSpan.FromHours(2) };
        var store = new KvStore(options);

        store.Set("key1", Encoding.UTF8.GetBytes("value"));

        var expiration = store.GetExpiration("key1");
        Assert.NotNull(expiration);
        Assert.True(expiration.Value > DateTime.UtcNow);
        Assert.True(expiration.Value < DateTime.UtcNow.AddHours(3));
    }

    [Fact(DisplayName = "测试显式 TTL 优先于默认 TTL")]
    public void TestExplicitTtlOverridesDefault()
    {
        var options = new DbOptions { DefaultKvTtl = TimeSpan.FromHours(24) };
        var store = new KvStore(options);

        store.Set("key1", Encoding.UTF8.GetBytes("value"), TimeSpan.FromMinutes(5));

        var expiration = store.GetExpiration("key1");
        Assert.NotNull(expiration);
        // 应在 5 分钟左右过期，而非 24 小时
        Assert.True(expiration.Value < DateTime.UtcNow.AddMinutes(6));
    }
}
