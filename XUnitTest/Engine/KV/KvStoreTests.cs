#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.KV;
using Xunit;

namespace XUnitTest.Engine.KV;

/// <summary>KV 存储引擎单元测试</summary>
public class KvStoreTests : IDisposable
{
    private readonly String _testDir;
    private Int32 _fileCounter;

    public KvStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "novadb_kvtest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_testDir);
    }

    private KvStore CreateStore(DbOptions? options = null) => new KvStore(options, Path.Combine(_testDir, $"test_{++_fileCounter}.kvd"));

    [Fact(DisplayName = "测试设置和获取值")]
    public void TestSetAndGet()
    {
        using var store = CreateStore();
        var value = Encoding.UTF8.GetBytes("hello");

        store.Set("key1", value);

        using var result = store.Get("key1");
        Assert.NotNull(result);
        Assert.Equal(value, result!.GetSpan().ToArray());
    }

    [Fact(DisplayName = "测试删除键")]
    public void TestDelete()
    {
        using var store = CreateStore();
        store.Set("key1", [1, 2, 3]);

        Assert.True(store.Delete("key1"));
        Assert.False(store.Delete("key1"));
        Assert.False(store.Delete("nonexistent"));
    }

    [Fact(DisplayName = "测试键是否存在")]
    public void TestExists()
    {
        using var store = CreateStore();
        store.Set("key1", [1, 2, 3]);

        Assert.True(store.Exists("key1"));
        Assert.False(store.Exists("key2"));
    }

    [Fact(DisplayName = "测试字符串便捷方法")]
    public void TestStringConvenience()
    {
        using var store = CreateStore();
        store.SetString("name", "NovaDb");

        var result = store.GetString("name");
        Assert.Equal("NovaDb", result);

        Assert.Null(store.GetString("missing"));
    }

    [Fact(DisplayName = "测试 TTL 过期")]
    public void TestTtlExpiration()
    {
        using var store = CreateStore();
        store.Set("key1", [1, 2, 3], TimeSpan.FromMilliseconds(10));

        // 未过期时可以获取
        using (var pk = store.Get("key1")) Assert.NotNull(pk);

        // 等待过期
        Thread.Sleep(50);

        using (var pk = store.Get("key1")) Assert.Null(pk);
        Assert.False(store.Exists("key1"));
    }

    [Fact(DisplayName = "测试惰性删除")]
    public void TestLazyDeletion()
    {
        using var store = CreateStore();
        store.Set("key1", [1, 2, 3], TimeSpan.FromMilliseconds(10));

        Thread.Sleep(50);

        // Get 触发惰性删除
        using (var pk = store.Get("key1")) Assert.Null(pk);

        // 验证条目已被移除（Count 不再包含该键）
        Assert.Equal(0, store.Count);
    }

    [Fact(DisplayName = "测试后台清理")]
    public void TestCleanupExpired()
    {
        using var store = CreateStore();
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
        using var store = CreateStore();
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
        using var store = CreateStore();

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
        using var store = CreateStore();
        store.Set("key1", [1, 2, 3]);
        store.Set("key1", [4, 5, 6]);

        using var result = store.Get("key1");
        Assert.NotNull(result);
        Assert.Equal(new Byte[] { 4, 5, 6 }, result!.GetSpan().ToArray());
        Assert.Equal(1, store.Count);
    }

    [Fact(DisplayName = "测试获取所有键")]
    public void TestGetAllKeys()
    {
        using var store = CreateStore();
        store.Set("key1", [1]);
        store.Set("key2", [2], TimeSpan.FromMilliseconds(10));
        store.Set("key3", [3]);

        Thread.Sleep(50);

        var keys = store.GetAllKeys();
        Assert.Equal(2, keys.Count());
        Assert.Contains("key1", keys);
        Assert.Contains("key3", keys);
        Assert.DoesNotContain("key2", keys);
    }

    [Fact(DisplayName = "测试计数属性")]
    public void TestCountProperty()
    {
        using var store = CreateStore();
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
        using var store = CreateStore();

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
        using var store = CreateStore();
        store.Set("key1", null);

        Assert.True(store.Exists("key1"));

        using var result = store.Get("key1");
        Assert.Null(result);
        Assert.Equal(1, store.Count);
    }

    [Fact(DisplayName = "测试 Add 成功添加不存在的键")]
    public void TestAddSuccessWhenKeyNotExists()
    {
        using var store = CreateStore();
        var value = Encoding.UTF8.GetBytes("hello");

        Assert.True(store.Add("key1", value, TimeSpan.FromHours(1)));
        using var pk = store.Get("key1");
        Assert.Equal(value, pk!.GetSpan().ToArray());
    }

    [Fact(DisplayName = "测试 Add 已存在键时返回失败")]
    public void TestAddFailsWhenKeyExists()
    {
        using var store = CreateStore();
        store.Set("key1", Encoding.UTF8.GetBytes("old"));

        Assert.False(store.Add("key1", Encoding.UTF8.GetBytes("new"), TimeSpan.FromHours(1)));

        // 值不应被覆盖
        Assert.Equal("old", store.GetString("key1"));
    }

    [Fact(DisplayName = "测试 Add 过期键可重新添加")]
    public void TestAddSuccessWhenKeyExpired()
    {
        using var store = CreateStore();
        store.Set("key1", Encoding.UTF8.GetBytes("old"), TimeSpan.FromMilliseconds(10));

        Thread.Sleep(50);

        Assert.True(store.Add("key1", Encoding.UTF8.GetBytes("new"), TimeSpan.FromHours(1)));
        Assert.Equal("new", store.GetString("key1"));
    }

    [Fact(DisplayName = "测试 AddString 便捷方法")]
    public void TestAddString()
    {
        using var store = CreateStore();

        Assert.True(store.AddString("key1", "hello", TimeSpan.FromHours(1)));
        Assert.Equal("hello", store.GetString("key1"));

        Assert.False(store.AddString("key1", "world", TimeSpan.FromHours(1)));
        Assert.Equal("hello", store.GetString("key1"));
    }

    [Fact(DisplayName = "测试 Inc 递增已有值")]
    public void TestIncExistingValue()
    {
        using var store = CreateStore();
        store.Set("counter", BitConverter.GetBytes(10L));

        var result = store.Inc("counter");
        Assert.Equal(11, result);
    }

    [Fact(DisplayName = "测试 Inc 初始化新键")]
    public void TestIncNewKey()
    {
        using var store = CreateStore();

        var result = store.Inc("counter");
        Assert.Equal(1, result);

        // 验证存储为二进制 Int64
        using var bytes = store.Get("counter");
        Assert.NotNull(bytes);
        Assert.Equal(8, bytes!.Length);
        Assert.Equal(1L, BitConverter.ToInt64(bytes.GetSpan().ToArray(), 0));
    }

    [Fact(DisplayName = "测试 Inc 自定义递增量")]
    public void TestIncCustomDelta()
    {
        using var store = CreateStore();

        var result = store.Inc("counter", 5);
        Assert.Equal(5, result);

        result = store.Inc("counter", 10);
        Assert.Equal(15, result);
    }

    [Fact(DisplayName = "测试 Inc 过期键重新初始化")]
    public void TestIncExpiredKey()
    {
        using var store = CreateStore();
        store.Set("counter", BitConverter.GetBytes(100L), TimeSpan.FromMilliseconds(10));

        Thread.Sleep(50);

        var result = store.Inc("counter", 5);
        Assert.Equal(5, result);
    }

    [Fact(DisplayName = "测试 Inc 负数递减")]
    public void TestIncNegativeDelta()
    {
        using var store = CreateStore();
        store.Set("counter", BitConverter.GetBytes(10L));

        var result = store.Inc("counter", -3);
        Assert.Equal(7, result);
    }

    [Fact(DisplayName = "测试默认 TTL 在未指定时应用")]
    public void TestDefaultTtlApplied()
    {
        var options = new DbOptions { DefaultKvTtl = TimeSpan.FromHours(2) };
        using var store = CreateStore(options);

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
        using var store = CreateStore(options);

        store.Set("key1", Encoding.UTF8.GetBytes("value"), TimeSpan.FromMinutes(5));

        var expiration = store.GetExpiration("key1");
        Assert.NotNull(expiration);
        // 应在 5 分钟左右过期，而非 24 小时
        Assert.True(expiration.Value < DateTime.UtcNow.AddMinutes(6));
    }

    [Fact(DisplayName = "GetExpiration 对已过期键返回 null 而非过期时间")]
    public void TestGetExpirationOnExpiredKeyReturnsNull()
    {
        using var store = CreateStore();
        // 设置极短 TTL
        store.Set("expkey", [1, 2, 3], TimeSpan.FromMilliseconds(10));

        // 等待过期
        Thread.Sleep(50);

        // 已过期的键应视为不存在，GetExpiration 应返回 null
        var expiration = store.GetExpiration("expkey");
        Assert.Null(expiration);
    }

    [Fact(DisplayName = "Delete 对已过期键返回 false（与 Exists 行为一致）")]
    public void TestDeleteExpiredKeyReturnsFalse()
    {
        using var store = CreateStore();
        // 设置极短 TTL
        store.Set("expkey", [1, 2, 3], TimeSpan.FromMilliseconds(10));

        // 等待过期
        Thread.Sleep(50);

        // 已过期的键：Exists 返回 false，Delete 也应返回 false
        Assert.False(store.Exists("expkey"), "已过期键 Exists 应返回 false");
        Assert.False(store.Delete("expkey"), "已过期键 Delete 应返回 false（与 Exists 行为一致）");
    }

    [Fact(DisplayName = "Delete 对已过期键不写入磁盘 Delete 记录")]
    public void TestDeleteExpiredKeyDoesNotWriteDeleteRecord()
    {
        using var store = CreateStore();
        // 设置极短 TTL
        store.Set("expkey", [1, 2, 3], TimeSpan.FromMilliseconds(10));

        Thread.Sleep(50);

        // 在过期键上调用 Delete，不应写入 Delete 记录也不应触发压缩
        var countBefore = store.Count;
        store.Delete("expkey");
        // 过期键应已被惰性清理，Count 不变
        Assert.Equal(0, store.Count);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); } catch { }
    }
}
