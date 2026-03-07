#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.KV;
using Xunit;

namespace XUnitTest.Engine.KV;

/// <summary>KvStore 持久化与高级功能单元测试</summary>
public class KvStorePersistTests : IDisposable
{
    private readonly String _testDir;

    public KvStorePersistTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "novadb_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }

    private Int32 _memCounter;

    private String GetFilePath(String name = "test.kvd") => Path.Combine(_testDir, name);

    private KvStore CreateStore(DbOptions? options = null) => new KvStore(options, GetFilePath($"mem_{++_memCounter}.kvd"));

    #region 持久化基础
    [Fact(DisplayName = "测试持久化Set并恢复数据")]
    public void TestPersistAndRecover()
    {
        var filePath = GetFilePath();

        // 写入数据
        using (var store = new KvStore(null, filePath))
        {
            store.SetString("key1", "value1");
            store.SetString("key2", "value2");
            store.Set("key3", [1, 2, 3]);
        }

        // 重新打开，验证恢复
        using (var store = new KvStore(null, filePath))
        {
            Assert.Equal(3, store.Count);
            Assert.Equal("value1", store.GetString("key1"));
            Assert.Equal("value2", store.GetString("key2"));
            using var pk = store.Get("key3");
            Assert.Equal(new Byte[] { 1, 2, 3 }, pk!.GetSpan().ToArray());
        }
    }

    [Fact(DisplayName = "测试持久化Delete操作")]
    public void TestPersistDelete()
    {
        var filePath = GetFilePath();

        using (var store = new KvStore(null, filePath))
        {
            store.SetString("key1", "value1");
            store.SetString("key2", "value2");
            store.Delete("key1");
        }

        using (var store = new KvStore(null, filePath))
        {
            Assert.Equal(1, store.Count);
            using (var pk = store.Get("key1")) Assert.Null(pk);
            Assert.Equal("value2", store.GetString("key2"));
        }
    }

    [Fact(DisplayName = "测试持久化Clear操作")]
    public void TestPersistClear()
    {
        var filePath = GetFilePath();

        using (var store = new KvStore(null, filePath))
        {
            store.SetString("key1", "value1");
            store.SetString("key2", "value2");
            store.Clear();
            store.SetString("key3", "value3");
        }

        using (var store = new KvStore(null, filePath))
        {
            Assert.Equal(1, store.Count);
            using (var pk = store.Get("key1")) Assert.Null(pk);
            using (var pk = store.Get("key2")) Assert.Null(pk);
            Assert.Equal("value3", store.GetString("key3"));
        }
    }

    [Fact(DisplayName = "测试持久化TTL恢复时跳过过期键")]
    public void TestPersistTtlSkipExpired()
    {
        var filePath = GetFilePath();

        using (var store = new KvStore(null, filePath))
        {
            store.SetString("alive", "ok");
            store.Set("expired", Encoding.UTF8.GetBytes("gone"), TimeSpan.FromMilliseconds(10));
        }

        Thread.Sleep(50);

        using (var store = new KvStore(null, filePath))
        {
            Assert.Equal(1, store.Count);
            Assert.Equal("ok", store.GetString("alive"));
            using (var pk = store.Get("expired")) Assert.Null(pk);
        }
    }

    [Fact(DisplayName = "测试持久化覆盖写入恢复最新值")]
    public void TestPersistOverwriteRecovery()
    {
        var filePath = GetFilePath();

        using (var store = new KvStore(null, filePath))
        {
            store.SetString("key1", "v1");
            store.SetString("key1", "v2");
            store.SetString("key1", "v3");
        }

        using (var store = new KvStore(null, filePath))
        {
            Assert.Equal(1, store.Count);
            Assert.Equal("v3", store.GetString("key1"));
        }
    }

    [Fact(DisplayName = "测试数据文件创建新目录")]
    public void TestCreateDirectoryForFile()
    {
        var subDir = Path.Combine(_testDir, "sub", "dir");
        var filePath = Path.Combine(subDir, "test.kvd");

        using var store = new KvStore(null, filePath);
        store.SetString("key1", "value1");

        Assert.True(File.Exists(filePath));
    }

    #endregion

    #region Compact
    [Fact(DisplayName = "测试Compact压缩数据文件")]
    public void TestCompact()
    {
        var filePath = GetFilePath("compact.kvd");

        using (var store = new KvStore(null, filePath))
        {
            // 写入大量数据然后删除部分
            for (var i = 0; i < 100; i++)
                store.SetString($"key:{i}", $"value:{i}");

            for (var i = 0; i < 50; i++)
                store.Delete($"key:{i}");

            var sizeBeforeCompact = new FileInfo(filePath).Length;

            store.Compact();

            var sizeAfterCompact = new FileInfo(filePath).Length;

            // 压缩后文件应该更小
            Assert.True(sizeAfterCompact < sizeBeforeCompact);
            Assert.Equal(50, store.Count);
        }

        // 压缩后数据文件可正常恢复
        using (var store = new KvStore(null, filePath))
        {
            Assert.Equal(50, store.Count);
            for (var i = 50; i < 100; i++)
                Assert.Equal($"value:{i}", store.GetString($"key:{i}"));

            for (var i = 0; i < 50; i++)
                Assert.Null(store.Get($"key:{i}"));
        }
    }

    [Fact(DisplayName = "测试Compact跳过过期条目")]
    public void TestCompactSkipsExpired()
    {
        var filePath = GetFilePath("compact_ttl.kvd");

        using (var store = new KvStore(null, filePath))
        {
            store.SetString("alive", "ok");
            store.Set("expired", Encoding.UTF8.GetBytes("gone"), TimeSpan.FromMilliseconds(10));

            Thread.Sleep(50);

            store.Compact();

            Assert.Equal(1, store.Count);
        }

        using (var store = new KvStore(null, filePath))
        {
            Assert.Equal(1, store.Count);
            Assert.Equal("ok", store.GetString("alive"));
        }
    }
    #endregion

    #region WAL 模式
    [Fact(DisplayName = "测试Full模式持久化")]
    public void TestWalModeFull()
    {
        var filePath = GetFilePath("wal_full.kvd");
        var options = new DbOptions { WalMode = WalMode.Full };

        using (var store = new KvStore(options, filePath))
        {
            store.SetString("key1", "value1");
        }

        using (var store = new KvStore(options, filePath))
        {
            Assert.Equal("value1", store.GetString("key1"));
        }
    }

    [Fact(DisplayName = "测试Normal模式持久化")]
    public void TestWalModeNormal()
    {
        var filePath = GetFilePath("wal_normal.kvd");
        var options = new DbOptions { WalMode = WalMode.Normal };

        using (var store = new KvStore(options, filePath))
        {
            store.SetString("key1", "value1");
            Thread.Sleep(1500); // 等待定时刷盘
        }

        using (var store = new KvStore(options, filePath))
        {
            Assert.Equal("value1", store.GetString("key1"));
        }
    }
    #endregion

    #region TryGet
    [Fact(DisplayName = "测试TryGet成功获取")]
    public void TestTryGetSuccess()
    {
        using var store = CreateStore();
        store.Set("key1", [1, 2, 3]);

        Assert.True(store.TryGet("key1", out var value));
        Assert.NotNull(value);
        Assert.Equal(new Byte[] { 1, 2, 3 }, value!.GetSpan().ToArray());
        value.Dispose();
    }

    [Fact(DisplayName = "测试TryGet不存在的键")]
    public void TestTryGetMissing()
    {
        using var store = CreateStore();

        Assert.False(store.TryGet("missing", out var value));
        Assert.Null(value);
    }

    [Fact(DisplayName = "测试TryGet过期键")]
    public void TestTryGetExpired()
    {
        using var store = CreateStore();
        store.Set("key1", [1], TimeSpan.FromMilliseconds(10));

        Thread.Sleep(50);

        Assert.False(store.TryGet("key1", out var value));
        Assert.Null(value);
    }
    #endregion

    #region Replace
    [Fact(DisplayName = "测试Replace已有键返回旧值")]
    public void TestReplaceExistingKey()
    {
        using var store = CreateStore();
        store.SetString("key1", "old");

        using var old = store.Replace("key1", Encoding.UTF8.GetBytes("new"));
        Assert.Equal("old", Encoding.UTF8.GetString(old!.GetSpan().ToArray()));
        Assert.Equal("new", store.GetString("key1"));
    }

    [Fact(DisplayName = "测试Replace不存在的键")]
    public void TestReplaceNewKey()
    {
        using var store = CreateStore();

        using var old = store.Replace("key1", Encoding.UTF8.GetBytes("value1"));
        Assert.Null(old);
        Assert.Equal("value1", store.GetString("key1"));
    }

    [Fact(DisplayName = "测试Replace保持原TTL")]
    public void TestReplaceKeepsTtl()
    {
        using var store = CreateStore();
        store.Set("key1", [1], TimeSpan.FromHours(2));
        var originalTtl = store.GetExpiration("key1");

        store.Replace("key1", [2])?.Dispose();

        var afterTtl = store.GetExpiration("key1");
        Assert.NotNull(originalTtl);
        Assert.NotNull(afterTtl);
        Assert.Equal(originalTtl!.Value.Ticks, afterTtl!.Value.Ticks);
    }

    [Fact(DisplayName = "测试Replace覆盖TTL")]
    public void TestReplaceOverridesTtl()
    {
        using var store = CreateStore();
        store.Set("key1", [1], TimeSpan.FromHours(2));

        store.Replace("key1", [2], TimeSpan.FromMinutes(5))?.Dispose();

        var expiration = store.GetExpiration("key1");
        Assert.NotNull(expiration);
        Assert.True(expiration!.Value < DateTime.UtcNow.AddMinutes(6));
    }
    #endregion

    #region IncDouble
    [Fact(DisplayName = "测试IncDouble新键初始化")]
    public void TestIncDoubleNewKey()
    {
        using var store = CreateStore();

        var result = store.IncDouble("counter", 3.14);
        Assert.Equal(3.14, result, 6);
    }

    [Fact(DisplayName = "测试IncDouble递增已有值")]
    public void TestIncDoubleExistingValue()
    {
        using var store = CreateStore();
        store.IncDouble("counter", 1.5);

        var result = store.IncDouble("counter", 2.5);
        Assert.Equal(4.0, result, 6);
    }

    [Fact(DisplayName = "测试IncDouble负数递减")]
    public void TestIncDoubleNegative()
    {
        using var store = CreateStore();
        store.IncDouble("counter", 10.0);

        var result = store.IncDouble("counter", -3.5);
        Assert.Equal(6.5, result, 6);
    }

    [Fact(DisplayName = "测试IncDouble过期键重新初始化")]
    public void TestIncDoubleExpiredKey()
    {
        using var store = CreateStore();
        store.IncDouble("counter", 100.0, TimeSpan.FromMilliseconds(10));

        Thread.Sleep(50);

        var result = store.IncDouble("counter", 5.0);
        Assert.Equal(5.0, result, 6);
    }
    #endregion

    #region 批量操作
    [Fact(DisplayName = "测试SetAll批量设置")]
    public void TestSetAll()
    {
        using var store = CreateStore();
        var values = new Dictionary<String, Byte[]?>
        {
            ["k1"] = Encoding.UTF8.GetBytes("v1"),
            ["k2"] = Encoding.UTF8.GetBytes("v2"),
            ["k3"] = null,
        };

        store.SetAll(values);

        Assert.Equal(3, store.Count);
        Assert.Equal("v1", store.GetString("k1"));
        Assert.Equal("v2", store.GetString("k2"));
        Assert.True(store.Exists("k3"));
        using (var pk = store.Get("k3")) Assert.Null(pk);
    }

    [Fact(DisplayName = "测试GetAll批量获取")]
    public void TestGetAll()
    {
        using var store = CreateStore();
        store.SetString("k1", "v1");
        store.SetString("k2", "v2");
        store.SetString("k3", "v3");

        var result = store.GetAll(new[] { "k1", "k2", "missing" });

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("k1"));
        Assert.True(result.ContainsKey("k2"));
        Assert.False(result.ContainsKey("missing"));
        foreach (var v in result.Values) v?.Dispose();
    }

    [Fact(DisplayName = "测试GetAll跳过过期键")]
    public void TestGetAllSkipsExpired()
    {
        using var store = CreateStore();
        store.SetString("alive", "ok");
        store.Set("expired", Encoding.UTF8.GetBytes("gone"), TimeSpan.FromMilliseconds(10));

        Thread.Sleep(50);

        var result = store.GetAll(new[] { "alive", "expired" });
        Assert.Single(result);
        Assert.True(result.ContainsKey("alive"));
        foreach (var v in result.Values) v?.Dispose();
    }

    [Fact(DisplayName = "测试SetAll带TTL")]
    public void TestSetAllWithTtl()
    {
        using var store = CreateStore();
        var values = new Dictionary<String, Byte[]?>
        {
            ["k1"] = Encoding.UTF8.GetBytes("v1"),
            ["k2"] = Encoding.UTF8.GetBytes("v2"),
        };

        store.SetAll(values, TimeSpan.FromMilliseconds(10));

        Thread.Sleep(50);

        Assert.Equal(0, store.Count);
    }

    [Fact(DisplayName = "测试SetAll持久化并恢复")]
    public void TestSetAllPersistAndRecover()
    {
        var filePath = GetFilePath("batch.kvd");

        using (var store = new KvStore(null, filePath))
        {
            var values = new Dictionary<String, Byte[]?>
            {
                ["k1"] = Encoding.UTF8.GetBytes("v1"),
                ["k2"] = Encoding.UTF8.GetBytes("v2"),
                ["k3"] = Encoding.UTF8.GetBytes("v3"),
            };
            store.SetAll(values);
        }

        using (var store = new KvStore(null, filePath))
        {
            Assert.Equal(3, store.Count);
            Assert.Equal("v1", store.GetString("k1"));
            Assert.Equal("v2", store.GetString("k2"));
            Assert.Equal("v3", store.GetString("k3"));
        }
    }
    #endregion

    #region Dispose
    [Fact(DisplayName = "测试Dispose后操作抛出异常")]
    public void TestOperationsAfterDispose()
    {
        var store = CreateStore();
        store.SetString("key1", "value1");
        store.Dispose();

        Assert.Throws<ObjectDisposedException>(() => store.Get("key1"));
        Assert.Throws<ObjectDisposedException>(() => store.Set("key2", [1]));
        Assert.Throws<ObjectDisposedException>(() => store.Delete("key1"));
        Assert.Throws<ObjectDisposedException>(() => store.Exists("key1"));
        Assert.Throws<ObjectDisposedException>(() => store.Clear());
    }

    [Fact(DisplayName = "测试重复Dispose不抛异常")]
    public void TestDoubleDispose()
    {
        var store = CreateStore();
        store.Dispose();
        store.Dispose(); // 不应抛异常
    }

    [Fact(DisplayName = "测试Dispose刷盘后可恢复")]
    public void TestDisposeFlushesData()
    {
        var filePath = GetFilePath("dispose.kvd");

        var store = new KvStore(null, filePath);
        store.SetString("key1", "value1");
        store.Dispose();

        // 数据文件应可正常恢复
        using var store2 = new KvStore(null, filePath);
        Assert.Equal("value1", store2.GetString("key1"));
    }
    #endregion

    #region Inc持久化
    [Fact(DisplayName = "测试Inc持久化并恢复")]
    public void TestIncPersistAndRecover()
    {
        var filePath = GetFilePath("inc.kvd");

        using (var store = new KvStore(null, filePath))
        {
            store.Inc("counter", 5);
            store.Inc("counter", 3);
        }

        using (var store = new KvStore(null, filePath))
        {
            // 恢复后继续递增，验证二进制 Int64 持久化正确
            var result = store.Inc("counter", 2);
            Assert.Equal(10, result);
        }
    }

    [Fact(DisplayName = "测试IncDouble持久化并恢复")]
    public void TestIncDoublePersistAndRecover()
    {
        var filePath = GetFilePath("incd.kvd");

        using (var store = new KvStore(null, filePath))
        {
            store.IncDouble("score", 1.5);
            store.IncDouble("score", 2.5);
        }

        using (var store = new KvStore(null, filePath))
        {
            // 恢复后继续递增，验证二进制 Double 持久化正确
            var result = store.IncDouble("score", 0.5);
            Assert.Equal(4.5, result, 6);
        }
    }
    #endregion

    #region 大文件 MMF 恢复
    [Fact(DisplayName = "测试大量数据MMF恢复")]
    public void TestLargeFileMmfRecovery()
    {
        var filePath = GetFilePath("large.kvd");

        // 写入足够多的数据以触发 MMF 路径（>64KB）
        using (var store = new KvStore(null, filePath))
        {
            for (var i = 0; i < 500; i++)
            {
                var value = new Byte[128];
                Array.Fill(value, (Byte)(i % 256));
                store.Set($"key:{i:D4}", value);
            }
        }

        // 验证文件大于 64KB
        Assert.True(new FileInfo(filePath).Length > 65536);

        // 恢复并验证
        using (var store = new KvStore(null, filePath))
        {
            Assert.Equal(500, store.Count);
            for (var i = 0; i < 500; i++)
            {
                using var result = store.Get($"key:{i:D4}");
                Assert.NotNull(result);
                Assert.Equal(128, result!.Length);
                var span = result.GetSpan();
                Assert.Equal((Byte)(i % 256), span[0]);
            }
        }
    }
    #endregion
}
