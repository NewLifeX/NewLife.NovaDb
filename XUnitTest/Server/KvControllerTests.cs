#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NewLife.NovaDb.Engine.KV;
using NewLife.NovaDb.Server;
using Xunit;

namespace XUnitTest.Server;

public class KvControllerTests : IDisposable
{
    private readonly String _testDir;
    private readonly KvStore _kvStore;
    private readonly KvController _controller;

    public KvControllerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "KvControllerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _kvStore = CreateStore();
        KvController.SharedKvStore = _kvStore;
        KvController.SharedServer = null;

        _controller = new KvController();
    }

    public void Dispose()
    {
        KvController.SharedKvStore = null;
        KvController.SharedServer = null;

        _kvStore.Dispose();

        try { Directory.Delete(_testDir, true); } catch { }
    }

    private KvStore CreateStore() => new KvStore(null, Path.Combine(_testDir, "controller.kvd"));

    #region Set
    [Fact(DisplayName = "Set_默认表设置键值对成功")]
    public void Set_DefaultTable_ReturnsTrue()
    {
        var data = Encoding.UTF8.GetBytes("hello");
        var result = _controller.Set("default", "key1", data);
        Assert.True(result);

        // 验证值已写入
        var base64 = _controller.Get("default", "key1");
        Assert.NotNull(base64);
        Assert.Equal("hello", Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
    }

    [Fact(DisplayName = "Set_带TTL设置键值对成功")]
    public void Set_WithTtl_ReturnsTrue()
    {
        var data = Encoding.UTF8.GetBytes("ttl_value");
        var result = _controller.Set("default", "ttlKey", data, 60);
        Assert.True(result);

        var base64 = _controller.Get("default", "ttlKey");
        Assert.NotNull(base64);
        Assert.Equal("ttl_value", Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
    }

    [Fact(DisplayName = "Set_空值设置成功")]
    public void Set_NullValue_ReturnsTrue()
    {
        var result = _controller.Set("default", "nullKey", null);
        Assert.True(result);
    }

    [Fact(DisplayName = "Set_存储未初始化返回false")]
    public void Set_StoreNotInitialized_ReturnsFalse()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            var result = _controller.Set("default", "key1", Encoding.UTF8.GetBytes("v"));
            Assert.False(result);
        }
        finally
        {
            KvController.SharedKvStore = original;
        }
    }
    #endregion

    #region Get
    [Fact(DisplayName = "Get_返回Base64编码的值")]
    public void Get_ExistingKey_ReturnsBase64()
    {
        var data = Encoding.UTF8.GetBytes("world");
        _controller.Set("default", "getKey", data);

        var base64 = _controller.Get("default", "getKey");
        Assert.NotNull(base64);

        var bytes = Convert.FromBase64String(base64);
        Assert.Equal("world", Encoding.UTF8.GetString(bytes));
    }

    [Fact(DisplayName = "Get_不存在的键返回null")]
    public void Get_MissingKey_ReturnsNull()
    {
        var result = _controller.Get("default", "nonExistentKey");
        Assert.Null(result);
    }

    [Fact(DisplayName = "Get_存储未初始化返回null")]
    public void Get_StoreNotInitialized_ReturnsNull()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            var result = _controller.Get("default", "key1");
            Assert.Null(result);
        }
        finally
        {
            KvController.SharedKvStore = original;
        }
    }
    #endregion

    #region Delete
    [Fact(DisplayName = "Delete_删除已存在的键成功")]
    public void Delete_ExistingKey_ReturnsTrue()
    {
        _controller.Set("default", "delKey", Encoding.UTF8.GetBytes("v"));
        var result = _controller.Delete("default", "delKey");
        Assert.True(result);

        Assert.Null(_controller.Get("default", "delKey"));
    }

    [Fact(DisplayName = "Delete_不存在的键返回false")]
    public void Delete_MissingKey_ReturnsFalse()
    {
        var result = _controller.Delete("default", "neverSetKey");
        Assert.False(result);
    }

    [Fact(DisplayName = "Delete_存储未初始化返回false")]
    public void Delete_StoreNotInitialized_ReturnsFalse()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            var result = _controller.Delete("default", "key1");
            Assert.False(result);
        }
        finally
        {
            KvController.SharedKvStore = original;
        }
    }
    #endregion

    #region Exists
    [Fact(DisplayName = "Exists_已存在的键返回true")]
    public void Exists_ExistingKey_ReturnsTrue()
    {
        _controller.Set("default", "existKey", Encoding.UTF8.GetBytes("v"));
        Assert.True(_controller.Exists("default", "existKey"));
    }

    [Fact(DisplayName = "Exists_不存在的键返回false")]
    public void Exists_MissingKey_ReturnsFalse()
    {
        Assert.False(_controller.Exists("default", "missingKey"));
    }

    [Fact(DisplayName = "Exists_存储未初始化返回false")]
    public void Exists_StoreNotInitialized_ReturnsFalse()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            Assert.False(_controller.Exists("default", "key1"));
        }
        finally
        {
            KvController.SharedKvStore = original;
        }
    }
    #endregion

    #region DeleteByPattern
    [Fact(DisplayName = "DeleteByPattern_匹配模式删除成功")]
    public void DeleteByPattern_MatchingKeys_ReturnsCount()
    {
        _controller.Set("default", "pat:a", Encoding.UTF8.GetBytes("1"));
        _controller.Set("default", "pat:b", Encoding.UTF8.GetBytes("2"));
        _controller.Set("default", "other", Encoding.UTF8.GetBytes("3"));

        var deleted = _controller.DeleteByPattern("default", "pat:*");
        Assert.Equal(2, deleted);
        Assert.False(_controller.Exists("default", "pat:a"));
        Assert.True(_controller.Exists("default", "other"));
    }

    [Fact(DisplayName = "DeleteByPattern_无匹配返回0")]
    public void DeleteByPattern_NoMatch_ReturnsZero()
    {
        var deleted = _controller.DeleteByPattern("default", "zzz:*");
        Assert.Equal(0, deleted);
    }

    [Fact(DisplayName = "DeleteByPattern_存储未初始化返回0")]
    public void DeleteByPattern_StoreNotInitialized_ReturnsZero()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            Assert.Equal(0, _controller.DeleteByPattern("default", "*"));
        }
        finally
        {
            KvController.SharedKvStore = original;
        }
    }
    #endregion

    #region GetCount
    [Fact(DisplayName = "GetCount_返回正确数量")]
    public void GetCount_WithData_ReturnsCorrectCount()
    {
        _controller.Set("default", "c1", Encoding.UTF8.GetBytes("v1"));
        _controller.Set("default", "c2", Encoding.UTF8.GetBytes("v2"));
        _controller.Set("default", "c3", Encoding.UTF8.GetBytes("v3"));

        Assert.Equal(3, _controller.GetCount("default"));
    }

    [Fact(DisplayName = "GetCount_空存储返回0")]
    public void GetCount_EmptyStore_ReturnsZero()
    {
        Assert.Equal(0, _controller.GetCount("default"));
    }

    [Fact(DisplayName = "GetCount_存储未初始化返回0")]
    public void GetCount_StoreNotInitialized_ReturnsZero()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            Assert.Equal(0, _controller.GetCount("default"));
        }
        finally
        {
            KvController.SharedKvStore = original;
        }
    }
    #endregion

    #region GetAllKeys
    [Fact(DisplayName = "GetAllKeys_返回所有键")]
    public void GetAllKeys_WithData_ReturnsAllKeys()
    {
        _controller.Set("default", "ak1", Encoding.UTF8.GetBytes("v1"));
        _controller.Set("default", "ak2", Encoding.UTF8.GetBytes("v2"));

        var keys = _controller.GetAllKeys("default");
        Assert.Equal(2, keys.Length);
        Assert.Contains("ak1", keys);
        Assert.Contains("ak2", keys);
    }

    [Fact(DisplayName = "GetAllKeys_空存储返回空数组")]
    public void GetAllKeys_EmptyStore_ReturnsEmptyArray()
    {
        var keys = _controller.GetAllKeys("default");
        Assert.NotNull(keys);
        Assert.Empty(keys);
    }

    [Fact(DisplayName = "GetAllKeys_存储未初始化返回空数组")]
    public void GetAllKeys_StoreNotInitialized_ReturnsEmptyArray()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            var keys = _controller.GetAllKeys("default");
            Assert.NotNull(keys);
            Assert.Empty(keys);
        }
        finally
        {
            KvController.SharedKvStore = original;
        }
    }
    #endregion

    #region Clear
    [Fact(DisplayName = "Clear_清空所有数据")]
    public void Clear_WithData_RemovesAll()
    {
        _controller.Set("default", "clr1", Encoding.UTF8.GetBytes("v1"));
        _controller.Set("default", "clr2", Encoding.UTF8.GetBytes("v2"));
        Assert.True(_controller.GetCount("default") > 0);

        _controller.Clear("default");
        Assert.Equal(0, _controller.GetCount("default"));
    }

    [Fact(DisplayName = "Clear_清空空存储无异常")]
    public void Clear_EmptyStore_NoException()
    {
        var ex = Record.Exception(() => _controller.Clear("default"));
        Assert.Null(ex);
    }
    #endregion

    #region SetExpire
    [Fact(DisplayName = "SetExpire_设置过期时间成功")]
    public void SetExpire_ExistingKey_ReturnsTrue()
    {
        _controller.Set("default", "expKey", Encoding.UTF8.GetBytes("v"));
        var result = _controller.SetExpire("default", "expKey", 120);
        Assert.True(result);
    }

    [Fact(DisplayName = "SetExpire_不存在的键返回false")]
    public void SetExpire_MissingKey_ReturnsFalse()
    {
        var result = _controller.SetExpire("default", "noKey", 60);
        Assert.False(result);
    }

    [Fact(DisplayName = "SetExpire_存储未初始化返回false")]
    public void SetExpire_StoreNotInitialized_ReturnsFalse()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            Assert.False(_controller.SetExpire("default", "key1", 60));
        }
        finally
        {
            KvController.SharedKvStore = original;
        }
    }
    #endregion

    #region GetExpire
    [Fact(DisplayName = "GetExpire_返回剩余TTL")]
    public void GetExpire_WithTtl_ReturnsPositive()
    {
        _controller.Set("default", "ttlCheck", Encoding.UTF8.GetBytes("v"), 300);
        var ttl = _controller.GetExpire("default", "ttlCheck");
        Assert.True(ttl > 0, $"Expected positive TTL, got {ttl}");
    }

    [Fact(DisplayName = "GetExpire_不存在的键返回负值")]
    public void GetExpire_MissingKey_ReturnsNegative()
    {
        var ttl = _controller.GetExpire("default", "missingTtlKey");
        Assert.True(ttl < 0, $"Expected negative TTL for missing key, got {ttl}");
    }

    [Fact(DisplayName = "GetExpire_存储未初始化返回-1")]
    public void GetExpire_StoreNotInitialized_ReturnsMinusOne()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            Assert.Equal(-1, _controller.GetExpire("default", "key1"));
        }
        finally
        {
            KvController.SharedKvStore = original;
        }
    }
    #endregion

    #region Increment
    [Fact(DisplayName = "Increment_递增成功返回新值")]
    public void Increment_ExistingKey_ReturnsNewValue()
    {
        _controller.Set("default", "incKey", BitConverter.GetBytes(10L));
        var result = _controller.Increment("default", "incKey", 5);
        Assert.Equal(15, result);
    }

    [Fact(DisplayName = "Increment_新键从0开始递增")]
    public void Increment_NewKey_StartsFromZero()
    {
        var result = _controller.Increment("default", "newIncKey", 3);
        Assert.Equal(3, result);
    }

    [Fact(DisplayName = "Increment_存储未初始化返回0")]
    public void Increment_StoreNotInitialized_ReturnsZero()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            Assert.Equal(0, _controller.Increment("default", "key1", 1));
        }
        finally
        {
            KvController.SharedKvStore = original;
        }
    }
    #endregion

    #region IncrementDouble
    [Fact(DisplayName = "IncrementDouble_浮点递增成功")]
    public void IncrementDouble_Success_ReturnsNewValue()
    {
        var result = _controller.IncrementDouble("default", "dblKey", 1.5);
        Assert.Equal(1.5, result, 5);

        result = _controller.IncrementDouble("default", "dblKey", 2.3);
        Assert.Equal(3.8, result, 5);
    }

    [Fact(DisplayName = "IncrementDouble_负数递减")]
    public void IncrementDouble_NegativeDelta_Decrements()
    {
        _controller.IncrementDouble("default", "dblDec", 10.0);
        var result = _controller.IncrementDouble("default", "dblDec", -3.5);
        Assert.Equal(6.5, result, 5);
    }

    [Fact(DisplayName = "IncrementDouble_存储未初始化返回0")]
    public void IncrementDouble_StoreNotInitialized_ReturnsZero()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            Assert.Equal(0, _controller.IncrementDouble("default", "key1", 1.0));
        }
        finally
        {
            KvController.SharedKvStore = original;
        }
    }
    #endregion

    #region Search
    [Fact(DisplayName = "Search_按模式搜索匹配的键")]
    public void Search_MatchingPattern_ReturnsKeys()
    {
        _controller.Set("default", "user:1", Encoding.UTF8.GetBytes("a"));
        _controller.Set("default", "user:2", Encoding.UTF8.GetBytes("b"));
        _controller.Set("default", "order:1", Encoding.UTF8.GetBytes("c"));

        var result = _controller.Search("default", "user:*");
        Assert.Equal(2, result.Length);
        Assert.Contains("user:1", result);
        Assert.Contains("user:2", result);
    }

    [Fact(DisplayName = "Search_无匹配返回空数组")]
    public void Search_NoMatch_ReturnsEmptyArray()
    {
        var result = _controller.Search("default", "zzz:*");
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact(DisplayName = "Search_存储未初始化返回空数组")]
    public void Search_StoreNotInitialized_ReturnsEmptyArray()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            var result = _controller.Search("default", "*");
            Assert.NotNull(result);
            Assert.Empty(result);
        }
        finally
        {
            KvController.SharedKvStore = original;
        }
    }
    #endregion

    #region GetStore路由
    [Fact(DisplayName = "GetStore_空表名使用默认存储")]
    public void GetStore_EmptyTableName_UsesDefault()
    {
        _controller.Set("", "routeKey1", Encoding.UTF8.GetBytes("v"));
        Assert.True(_controller.Exists("default", "routeKey1"));
    }

    [Fact(DisplayName = "GetStore_default表名使用默认存储")]
    public void GetStore_DefaultTableName_UsesDefault()
    {
        _controller.Set("default", "routeKey2", Encoding.UTF8.GetBytes("v"));
        Assert.True(_controller.Exists("default", "routeKey2"));
    }

    [Fact(DisplayName = "GetStore_DEFAULT大写不区分大小写")]
    public void GetStore_DefaultCaseInsensitive_UsesDefault()
    {
        _controller.Set("DEFAULT", "routeKey3", Encoding.UTF8.GetBytes("v"));
        Assert.True(_controller.Exists("default", "routeKey3"));

        _controller.Set("Default", "routeKey4", Encoding.UTF8.GetBytes("v"));
        Assert.True(_controller.Exists("default", "routeKey4"));
    }
    #endregion

    #region TTL集成
    [Fact(DisplayName = "TTL集成_设置TTL后过期验证")]
    public void Ttl_Integration_SetThenExpire()
    {
        _controller.Set("default", "shortTtl", Encoding.UTF8.GetBytes("expire_me"), 1);
        Assert.True(_controller.Exists("default", "shortTtl"));

        var ttl = _controller.GetExpire("default", "shortTtl");
        Assert.True(ttl > 0 && ttl <= 1, $"TTL should be between 0 and 1, got {ttl}");

        // 等待过期
        Thread.Sleep(1500);

        Assert.Null(_controller.Get("default", "shortTtl"));
    }
    #endregion
}
