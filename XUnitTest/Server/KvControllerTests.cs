#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NewLife.Data;
using NewLife.NovaDb.Engine.KV;
using NewLife.NovaDb.Server;
using Xunit;

namespace XUnitTest.Server;

[Collection("IntegrationTests")]
public class KvControllerTests : IDisposable
{
    private readonly String _testDir;
    private readonly KvStore _kvStore;
    private readonly KvController _controller;
    private readonly KvStore? _originalKvStore;
    private readonly NovaServer? _originalServer;

    public KvControllerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "KvControllerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _originalKvStore = KvController.SharedKvStore;
        _originalServer = KvController.SharedServer;

        _kvStore = CreateStore();
        KvController.SharedKvStore = _kvStore;
        KvController.SharedServer = null;

        _controller = new KvController();
    }

    public void Dispose()
    {
        KvController.SharedKvStore = _originalKvStore;
        KvController.SharedServer = _originalServer;

        _kvStore.Dispose();

        try { Directory.Delete(_testDir, true); } catch { }
    }

    private KvStore CreateStore() => new KvStore(null, Path.Combine(_testDir, "controller.kvd"));

    #region 辅助方法（封装 IPacket 协议调用）

    private Boolean CtrlSet(String tableName, String key, Byte[]? value, Int32 ttl = 0)
        => KvPacket.DecodeBoolean(_controller.Set(KvPacket.EncodeSet(tableName, key, value, ttl)));

    private Byte[]? CtrlGet(String tableName, String key)
        => KvPacket.DecodeNullableValue(_controller.Get(KvPacket.EncodeTableKey(tableName, key)));

    private Boolean CtrlDelete(String tableName, String key)
        => KvPacket.DecodeBoolean(_controller.Delete(KvPacket.EncodeTableKey(tableName, key)));

    private Boolean CtrlExists(String tableName, String key)
        => KvPacket.DecodeBoolean(_controller.Exists(KvPacket.EncodeTableKey(tableName, key)));

    private Int32 CtrlDeleteByPattern(String tableName, String pattern)
        => KvPacket.DecodeInt32(_controller.DeleteByPattern(KvPacket.EncodeDeleteByPattern(tableName, pattern)));

    private Int32 CtrlGetCount(String tableName)
        => KvPacket.DecodeInt32(_controller.GetCount(KvPacket.EncodeTableOnly(tableName)));

    private String[] CtrlGetAllKeys(String tableName)
        => KvPacket.DecodeStringArray(_controller.GetAllKeys(KvPacket.EncodeTableOnly(tableName)));

    private void CtrlClear(String tableName)
        => _controller.Clear(KvPacket.EncodeTableOnly(tableName));

    private Boolean CtrlSetExpire(String tableName, String key, Int32 ttlSeconds)
        => KvPacket.DecodeBoolean(_controller.SetExpire(KvPacket.EncodeSetExpire(tableName, key, ttlSeconds)));

    private Double CtrlGetExpire(String tableName, String key)
        => KvPacket.DecodeDouble(_controller.GetExpire(KvPacket.EncodeTableKey(tableName, key)));

    private Int64 CtrlIncrement(String tableName, String key, Int64 delta)
        => KvPacket.DecodeInt64(_controller.Increment(KvPacket.EncodeIncrement(tableName, key, delta)));

    private Double CtrlIncrementDouble(String tableName, String key, Double delta)
        => KvPacket.DecodeDouble(_controller.IncrementDouble(KvPacket.EncodeIncrementDouble(tableName, key, delta)));

    private String[] CtrlSearch(String tableName, String pattern, Int32 offset = 0, Int32 count = -1)
        => KvPacket.DecodeStringArray(_controller.Search(KvPacket.EncodeSearch(tableName, pattern, offset, count)));

    private IDictionary<String, Byte[]?> CtrlGetAll(String tableName, String[] keys)
        => KvPacket.DecodeGetAllResponse(_controller.GetAll(KvPacket.EncodeGetAll(tableName, keys)));

    private Int32 CtrlSetAll(String tableName, IDictionary<String, Byte[]?> values, Int32 ttl = 0)
        => KvPacket.DecodeInt32(_controller.SetAll(KvPacket.EncodeSetAll(tableName, values, ttl)));

    #endregion

    #region Set
    [Fact(DisplayName = "Set_默认表设置键值对成功")]
    public void Set_DefaultTable_ReturnsTrue()
    {
        var data = Encoding.UTF8.GetBytes("hello");
        var result = CtrlSet("default", "key1", data);
        Assert.True(result);

        // 验证值已写入
        var valueBytes = CtrlGet("default", "key1");
        Assert.NotNull(valueBytes);
        Assert.Equal("hello", Encoding.UTF8.GetString(valueBytes));
    }

    [Fact(DisplayName = "Set_带TTL设置键值对成功")]
    public void Set_WithTtl_ReturnsTrue()
    {
        var data = Encoding.UTF8.GetBytes("ttl_value");
        var result = CtrlSet("default", "ttlKey", data, 60);
        Assert.True(result);

        var valueBytes = CtrlGet("default", "ttlKey");
        Assert.NotNull(valueBytes);
        Assert.Equal("ttl_value", Encoding.UTF8.GetString(valueBytes));
    }

    [Fact(DisplayName = "Set_空值设置成功")]
    public void Set_NullValue_ReturnsTrue()
    {
        var result = CtrlSet("default", "nullKey", null);
        Assert.True(result);
    }

    [Fact(DisplayName = "Set_存储未初始化返回false")]
    public void Set_StoreNotInitialized_ReturnsFalse()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            var result = CtrlSet("default", "key1", Encoding.UTF8.GetBytes("v"));
            Assert.False(result);
        }
        finally
        {
            KvController.SharedKvStore = original;
        }
    }
    #endregion

    #region Get
    [Fact(DisplayName = "Get_返回二进制值")]
    public void Get_ExistingKey_ReturnsBinary()
    {
        var data = Encoding.UTF8.GetBytes("world");
        CtrlSet("default", "getKey", data);

        var valueBytes = CtrlGet("default", "getKey");
        Assert.NotNull(valueBytes);
        Assert.Equal("world", Encoding.UTF8.GetString(valueBytes));
    }

    [Fact(DisplayName = "Get_不存在的键返回null")]
    public void Get_MissingKey_ReturnsNull()
    {
        var result = CtrlGet("default", "nonExistentKey");
        Assert.Null(result);
    }

    [Fact(DisplayName = "Get_存储未初始化返回null")]
    public void Get_StoreNotInitialized_ReturnsNull()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            var result = CtrlGet("default", "key1");
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
        CtrlSet("default", "delKey", Encoding.UTF8.GetBytes("v"));
        var result = CtrlDelete("default", "delKey");
        Assert.True(result);

        Assert.Null(CtrlGet("default", "delKey"));
    }

    [Fact(DisplayName = "Delete_不存在的键返回false")]
    public void Delete_MissingKey_ReturnsFalse()
    {
        var result = CtrlDelete("default", "neverSetKey");
        Assert.False(result);
    }

    [Fact(DisplayName = "Delete_存储未初始化返回false")]
    public void Delete_StoreNotInitialized_ReturnsFalse()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            var result = CtrlDelete("default", "key1");
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
        CtrlSet("default", "existKey", Encoding.UTF8.GetBytes("v"));
        Assert.True(CtrlExists("default", "existKey"));
    }

    [Fact(DisplayName = "Exists_不存在的键返回false")]
    public void Exists_MissingKey_ReturnsFalse()
    {
        Assert.False(CtrlExists("default", "missingKey"));
    }

    [Fact(DisplayName = "Exists_存储未初始化返回false")]
    public void Exists_StoreNotInitialized_ReturnsFalse()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            Assert.False(CtrlExists("default", "key1"));
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
        CtrlSet("default", "pat:a", Encoding.UTF8.GetBytes("1"));
        CtrlSet("default", "pat:b", Encoding.UTF8.GetBytes("2"));
        CtrlSet("default", "other", Encoding.UTF8.GetBytes("3"));

        var deleted = CtrlDeleteByPattern("default", "pat:*");
        Assert.Equal(2, deleted);
        Assert.False(CtrlExists("default", "pat:a"));
        Assert.True(CtrlExists("default", "other"));
    }

    [Fact(DisplayName = "DeleteByPattern_无匹配返回0")]
    public void DeleteByPattern_NoMatch_ReturnsZero()
    {
        var deleted = CtrlDeleteByPattern("default", "zzz:*");
        Assert.Equal(0, deleted);
    }

    [Fact(DisplayName = "DeleteByPattern_存储未初始化返回0")]
    public void DeleteByPattern_StoreNotInitialized_ReturnsZero()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            Assert.Equal(0, CtrlDeleteByPattern("default", "*"));
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
        CtrlSet("default", "c1", Encoding.UTF8.GetBytes("v1"));
        CtrlSet("default", "c2", Encoding.UTF8.GetBytes("v2"));
        CtrlSet("default", "c3", Encoding.UTF8.GetBytes("v3"));

        Assert.Equal(3, CtrlGetCount("default"));
    }

    [Fact(DisplayName = "GetCount_空存储返回0")]
    public void GetCount_EmptyStore_ReturnsZero()
    {
        Assert.Equal(0, CtrlGetCount("default"));
    }

    [Fact(DisplayName = "GetCount_存储未初始化返回0")]
    public void GetCount_StoreNotInitialized_ReturnsZero()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            Assert.Equal(0, CtrlGetCount("default"));
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
        CtrlSet("default", "ak1", Encoding.UTF8.GetBytes("v1"));
        CtrlSet("default", "ak2", Encoding.UTF8.GetBytes("v2"));

        var keys = CtrlGetAllKeys("default");
        Assert.Equal(2, keys.Length);
        Assert.Contains("ak1", keys);
        Assert.Contains("ak2", keys);
    }

    [Fact(DisplayName = "GetAllKeys_空存储返回空数组")]
    public void GetAllKeys_EmptyStore_ReturnsEmptyArray()
    {
        var keys = CtrlGetAllKeys("default");
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
            var keys = CtrlGetAllKeys("default");
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
        CtrlSet("default", "clr1", Encoding.UTF8.GetBytes("v1"));
        CtrlSet("default", "clr2", Encoding.UTF8.GetBytes("v2"));
        Assert.True(CtrlGetCount("default") > 0);

        CtrlClear("default");
        Assert.Equal(0, CtrlGetCount("default"));
    }

    [Fact(DisplayName = "Clear_清空空存储无异常")]
    public void Clear_EmptyStore_NoException()
    {
        var ex = Record.Exception(() => CtrlClear("default"));
        Assert.Null(ex);
    }
    #endregion

    #region SetExpire
    [Fact(DisplayName = "SetExpire_设置过期时间成功")]
    public void SetExpire_ExistingKey_ReturnsTrue()
    {
        CtrlSet("default", "expKey", Encoding.UTF8.GetBytes("v"));
        var result = CtrlSetExpire("default", "expKey", 120);
        Assert.True(result);
    }

    [Fact(DisplayName = "SetExpire_不存在的键返回false")]
    public void SetExpire_MissingKey_ReturnsFalse()
    {
        var result = CtrlSetExpire("default", "noKey", 60);
        Assert.False(result);
    }

    [Fact(DisplayName = "SetExpire_存储未初始化返回false")]
    public void SetExpire_StoreNotInitialized_ReturnsFalse()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            Assert.False(CtrlSetExpire("default", "key1", 60));
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
        CtrlSet("default", "ttlCheck", Encoding.UTF8.GetBytes("v"), 300);
        var ttl = CtrlGetExpire("default", "ttlCheck");
        Assert.True(ttl > 0, $"Expected positive TTL, got {ttl}");
    }

    [Fact(DisplayName = "GetExpire_不存在的键返回负值")]
    public void GetExpire_MissingKey_ReturnsNegative()
    {
        var ttl = CtrlGetExpire("default", "missingTtlKey");
        Assert.True(ttl < 0, $"Expected negative TTL for missing key, got {ttl}");
    }

    [Fact(DisplayName = "GetExpire_存储未初始化返回-1")]
    public void GetExpire_StoreNotInitialized_ReturnsMinusOne()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            Assert.Equal(-1, CtrlGetExpire("default", "key1"));
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
        CtrlSet("default", "incKey", BitConverter.GetBytes(10L));
        var result = CtrlIncrement("default", "incKey", 5);
        Assert.Equal(15, result);
    }

    [Fact(DisplayName = "Increment_新键从0开始递增")]
    public void Increment_NewKey_StartsFromZero()
    {
        var result = CtrlIncrement("default", "newIncKey", 3);
        Assert.Equal(3, result);
    }

    [Fact(DisplayName = "Increment_存储未初始化返回0")]
    public void Increment_StoreNotInitialized_ReturnsZero()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            Assert.Equal(0, CtrlIncrement("default", "key1", 1));
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
        var result = CtrlIncrementDouble("default", "dblKey", 1.5);
        Assert.Equal(1.5, result, 5);

        result = CtrlIncrementDouble("default", "dblKey", 2.3);
        Assert.Equal(3.8, result, 5);
    }

    [Fact(DisplayName = "IncrementDouble_负数递减")]
    public void IncrementDouble_NegativeDelta_Decrements()
    {
        CtrlIncrementDouble("default", "dblDec", 10.0);
        var result = CtrlIncrementDouble("default", "dblDec", -3.5);
        Assert.Equal(6.5, result, 5);
    }

    [Fact(DisplayName = "IncrementDouble_存储未初始化返回0")]
    public void IncrementDouble_StoreNotInitialized_ReturnsZero()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            Assert.Equal(0, CtrlIncrementDouble("default", "key1", 1.0));
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
        CtrlSet("default", "user:1", Encoding.UTF8.GetBytes("a"));
        CtrlSet("default", "user:2", Encoding.UTF8.GetBytes("b"));
        CtrlSet("default", "order:1", Encoding.UTF8.GetBytes("c"));

        var result = CtrlSearch("default", "user:*");
        Assert.Equal(2, result.Length);
        Assert.Contains("user:1", result);
        Assert.Contains("user:2", result);
    }

    [Fact(DisplayName = "Search_无匹配返回空数组")]
    public void Search_NoMatch_ReturnsEmptyArray()
    {
        var result = CtrlSearch("default", "zzz:*");
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
            var result = CtrlSearch("default", "*");
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
        CtrlSet("", "routeKey1", Encoding.UTF8.GetBytes("v"));
        Assert.True(CtrlExists("default", "routeKey1"));
    }

    [Fact(DisplayName = "GetStore_default表名使用默认存储")]
    public void GetStore_DefaultTableName_UsesDefault()
    {
        CtrlSet("default", "routeKey2", Encoding.UTF8.GetBytes("v"));
        Assert.True(CtrlExists("default", "routeKey2"));
    }

    [Fact(DisplayName = "GetStore_DEFAULT大写不区分大小写")]
    public void GetStore_DefaultCaseInsensitive_UsesDefault()
    {
        CtrlSet("DEFAULT", "routeKey3", Encoding.UTF8.GetBytes("v"));
        Assert.True(CtrlExists("default", "routeKey3"));

        CtrlSet("Default", "routeKey4", Encoding.UTF8.GetBytes("v"));
        Assert.True(CtrlExists("default", "routeKey4"));
    }
    #endregion

    #region TTL集成
    [Fact(DisplayName = "TTL集成_设置TTL后过期验证")]
    public void Ttl_Integration_SetThenExpire()
    {
        CtrlSet("default", "shortTtl", Encoding.UTF8.GetBytes("expire_me"), 1);
        Assert.True(CtrlExists("default", "shortTtl"));

        var ttl = CtrlGetExpire("default", "shortTtl");
        Assert.True(ttl > 0 && ttl <= 1, $"TTL should be between 0 and 1, got {ttl}");

        // 等待过期
        Thread.Sleep(1500);

        Assert.Null(CtrlGet("default", "shortTtl"));
    }
    #endregion

    #region GetAll (batch)
    [Fact(DisplayName = "GetAll_批量获取键值对")]
    public void GetAll_WithData_ReturnsDictionary()
    {
        CtrlSet("default", "ga1", Encoding.UTF8.GetBytes("v1"));
        CtrlSet("default", "ga2", Encoding.UTF8.GetBytes("v2"));

        var result = CtrlGetAll("default", ["ga1", "ga2", "ga_miss"]);
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.NotNull(result["ga1"]);
        Assert.Equal("v1", Encoding.UTF8.GetString(result["ga1"]!));
        Assert.NotNull(result["ga2"]);
        Assert.Equal("v2", Encoding.UTF8.GetString(result["ga2"]!));
        Assert.Null(result["ga_miss"]);
    }

    [Fact(DisplayName = "GetAll_存储未初始化返回空字典")]
    public void GetAll_StoreNotInitialized_ReturnsEmptyDictionary()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            var result = CtrlGetAll("default", ["k1"]);
            Assert.NotNull(result);
            Assert.Empty(result);
        }
        finally
        {
            KvController.SharedKvStore = original;
        }
    }
    #endregion

    #region SetAll (batch)
    [Fact(DisplayName = "SetAll_批量设置键值对")]
    public void SetAll_WithData_ReturnsCount()
    {
        var values = new Dictionary<String, Byte[]?>
        {
            ["sa1"] = Encoding.UTF8.GetBytes("v1"),
            ["sa2"] = Encoding.UTF8.GetBytes("v2"),
        };

        var count = CtrlSetAll("default", values);
        Assert.Equal(2, count);

        var b1 = CtrlGet("default", "sa1");
        Assert.NotNull(b1);
        Assert.Equal("v1", Encoding.UTF8.GetString(b1));
    }

    [Fact(DisplayName = "SetAll_带TTL批量设置")]
    public void SetAll_WithTtl_SetsExpiration()
    {
        var values = new Dictionary<String, Byte[]?>
        {
            ["sat1"] = Encoding.UTF8.GetBytes("ttl_v"),
        };

        var count = CtrlSetAll("default", values, 60);
        Assert.Equal(1, count);

        var ttl = CtrlGetExpire("default", "sat1");
        Assert.True(ttl > 50 && ttl <= 60);
    }

    [Fact(DisplayName = "SetAll_存储未初始化返回0")]
    public void SetAll_StoreNotInitialized_ReturnsZero()
    {
        var original = KvController.SharedKvStore;
        try
        {
            KvController.SharedKvStore = null;
            var count = CtrlSetAll("default", new Dictionary<String, Byte[]?> { ["k1"] = [1] });
            Assert.Equal(0, count);
        }
        finally
        {
            KvController.SharedKvStore = original;
        }
    }
    #endregion
}
