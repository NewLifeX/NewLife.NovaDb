using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NewLife.Caching;
using NewLife.NovaDb.Caching;
using NewLife.NovaDb.Client;
using Xunit;

#nullable enable

namespace XUnitTest.Caching;

/// <summary>KV 缓存集成测试夹具。维护固定测试目录，每次启动前清空</summary>
public sealed class KvDbFixture
{
    /// <summary>固定测试目录，位于项目根目录 TestData/KvDb/</summary>
    public static readonly String DbPath = "../TestData/KvDb/".GetFullPath();

    /// <summary>嵌入式连接字符串</summary>
    public String ConnectionString => $"Data Source={DbPath}";

    public KvDbFixture()
    {
        // 每次测试启动前清空目录
        if (Directory.Exists(DbPath))
            Directory.Delete(DbPath, recursive: true);
        Directory.CreateDirectory(DbPath);
    }
}

/// <summary>KV 缓存（NovaCache）嵌入模式集成测试，覆盖 ICache 各核心接口及数据文件验证</summary>
/// <remarks>
/// 通过 NovaClientFactory.GetCache() 统一入口创建 NovaCache。
/// 测试数据保存到 TestData/KvDb/ 目录（项目根目录），每次运行前清空，
/// 测试后 .kvd 文件留存，可人工检查 KV 存储文件内容。
/// 每个测试方法使用不同的 key 前缀，互不干扰。
/// </remarks>
public class NovaCacheIntegrationTests : IClassFixture<KvDbFixture>
{
    private readonly KvDbFixture _fixture;
    private readonly NovaClientFactory _factory = NovaClientFactory.Instance;

    public NovaCacheIntegrationTests(KvDbFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>获取默认 KV 表缓存实例</summary>
    private ICache Cache => _factory.GetCache(_fixture.ConnectionString, "default");

    /// <summary>获取默认 KV 表文件路径（tableName.kvd 平铺在数据库目录下）</summary>
    private static String KvFilePath => Path.Combine(KvDbFixture.DbPath, "default.kvd");

    #region 测试用内嵌对象

    private sealed class UserInfo
    {
        public String Name { get; set; } = String.Empty;
        public Int32 Age { get; set; }
        public String Email { get; set; } = String.Empty;
    }

    #endregion

    #region 文件验证测试

    [Fact(DisplayName = "KV集成-首次Set后KVD文件已创建")]
    public void FileCreated_AfterFirstSet()
    {
        Cache.Set("file_check_key", "file_check_value");

        Assert.True(File.Exists(KvFilePath), "default.kvd 文件应在首次 Set 后创建");
        Assert.True(new FileInfo(KvFilePath).Length > 0, "default.kvd 文件应有内容");
    }

    [Fact(DisplayName = "KV集成-批量写入后文件大小增长")]
    public void FileGrows_AfterBatchSet()
    {
        // 确保文件先存在
        Cache.Set("grow_seed", "seed");
        var sizeBefore = new FileInfo(KvFilePath).Length;

        var data = new Dictionary<String, String>();
        for (var i = 0; i < 50; i++)
            data[$"grow_key_{i}"] = new String('X', 200);  // 200 字节值
        Cache.SetAll(data);

        var sizeAfter = new FileInfo(KvFilePath).Length;
        Assert.True(sizeAfter > sizeBefore, "批量写入 50 条后 .kvd 文件大小应增长");
    }

    #endregion

    #region 基本 Set / Get

    [Fact(DisplayName = "KV集成-Set和Get字符串值")]
    public void Set_Get_String()
    {
        Cache.Set("str_key", "hello_novadb");
        Assert.Equal("hello_novadb", Cache.Get<String>("str_key"));
    }

    [Fact(DisplayName = "KV集成-Set和Get整数值")]
    public void Set_Get_Int()
    {
        Cache.Set("int_key", 42);
        Assert.Equal(42, Cache.Get<Int32>("int_key"));
    }

    [Fact(DisplayName = "KV集成-Set和Get布尔值")]
    public void Set_Get_Bool()
    {
        Cache.Set("bool_key", true);
        Assert.True(Cache.Get<Boolean>("bool_key"));
    }

    [Fact(DisplayName = "KV集成-Set和Get对象（复杂类型）")]
    public void Set_Get_Object()
    {
        var user = new UserInfo { Name = "Alice", Age = 30, Email = "alice@example.com" };
        Cache.Set("user_obj_key", user);

        var result = Cache.Get<UserInfo>("user_obj_key");
        Assert.NotNull(result);
        Assert.Equal("Alice", result!.Name);
        Assert.Equal(30, result.Age);
        Assert.Equal("alice@example.com", result.Email);
    }

    [Fact(DisplayName = "KV集成-获取不存在键返回null")]
    public void Get_NonExistentKey_ReturnsNull()
    {
        Assert.Null(Cache.Get<String>("nonexistent_key_xyz"));
    }

    [Fact(DisplayName = "KV集成-Set覆盖已有值")]
    public void Set_Overwrite_ExistingValue()
    {
        Cache.Set("overwrite_key", "original");
        Cache.Set("overwrite_key", "updated");

        Assert.Equal("updated", Cache.Get<String>("overwrite_key"));
    }

    #endregion

    #region ContainsKey

    [Fact(DisplayName = "KV集成-ContainsKey正确识别存在与不存在的键")]
    public void ContainsKey_ExistAndNot()
    {
        Cache.Set("exists_key", "value");

        Assert.True(Cache.ContainsKey("exists_key"));
        Assert.False(Cache.ContainsKey("not_exists_key_abc"));
    }

    [Fact(DisplayName = "KV集成-删除后ContainsKey返回false")]
    public void ContainsKey_False_AfterRemove()
    {
        Cache.Set("ck_remove", "val");
        Assert.True(Cache.ContainsKey("ck_remove"));

        Cache.Remove("ck_remove");
        Assert.False(Cache.ContainsKey("ck_remove"));
    }

    #endregion

    #region Remove

    [Fact(DisplayName = "KV集成-Remove单个键")]
    public void Remove_SingleKey()
    {
        Cache.Set("rm_key_1", "v1");
        var removed = Cache.Remove("rm_key_1");

        Assert.Equal(1, removed);
        Assert.False(Cache.ContainsKey("rm_key_1"));
    }

    [Fact(DisplayName = "KV集成-Remove多个键")]
    public void Remove_MultipleKeys()
    {
        Cache.Set("rm_a", "1");
        Cache.Set("rm_b", "2");
        Cache.Set("rm_c", "3");

        var removed = Cache.Remove("rm_a", "rm_b");

        Assert.Equal(2, removed);
        Assert.False(Cache.ContainsKey("rm_a"));
        Assert.False(Cache.ContainsKey("rm_b"));
        Assert.True(Cache.ContainsKey("rm_c"));
    }

    [Fact(DisplayName = "KV集成-Remove不存在的键返回0")]
    public void Remove_NonExistentKey_ReturnsZero()
    {
        Assert.Equal(0, Cache.Remove("does_not_exist_key_qwerty"));
    }

    #endregion

    #region Clear

    [Fact(DisplayName = "KV集成-Clear后Count为0")]
    public void Clear_AllKeys_CountZero()
    {
        // 每次测试使用独立 KV 表以避免 Count 被其他测试污染
        var isolatedCache = _factory.GetCache(_fixture.ConnectionString, "clear_table");

        isolatedCache.Set("c1", "v1");
        isolatedCache.Set("c2", "v2");
        isolatedCache.Set("c3", "v3");
        Assert.Equal(3, isolatedCache.Count);

        isolatedCache.Clear();
        Assert.Equal(0, isolatedCache.Count);
        Assert.False(isolatedCache.ContainsKey("c1"));
    }

    #endregion

    #region TTL 过期

    [Fact(DisplayName = "KV集成-TTL过期后Get返回null")]
    public void TTL_ExpiredKey_GetReturnsNull()
    {
        Cache.Set("ttl_key", "expire_soon", expire: 1);  // 1 秒后过期

        // 立即查询应能获取
        Assert.NotNull(Cache.Get<String>("ttl_key"));

        // 等待过期
        Thread.Sleep(1200);

        Assert.Null(Cache.Get<String>("ttl_key"));
    }

    [Fact(DisplayName = "KV集成-TTL未过期时可正常获取")]
    public void TTL_NotExpired_GetReturnsValue()
    {
        Cache.Set("ttl_valid_key", "still_alive", expire: 3600);

        Assert.Equal("still_alive", Cache.Get<String>("ttl_valid_key"));
    }

    [Fact(DisplayName = "KV集成-GetExpire返回剩余TTL秒数")]
    public void GetExpire_ReturnsRemainingTtl()
    {
        Cache.Set("ttl_check_key", "value", expire: 3600);

        var ttl = Cache.GetExpire("ttl_check_key");
        Assert.True(ttl.TotalSeconds > 3590, "TTL 应接近 3600 秒");
        Assert.True(ttl.TotalSeconds <= 3600, "TTL 不应超过设定值");
    }

    [Fact(DisplayName = "KV集成-SetExpire修改键的过期时间")]
    public void SetExpire_ChangesKeyExpiry()
    {
        Cache.Set("setexpire_key", "value");

        Cache.SetExpire("setexpire_key", TimeSpan.FromSeconds(3600));
        var ttl = Cache.GetExpire("setexpire_key");
        Assert.True(ttl.TotalSeconds > 3590);
    }

    #endregion

    #region Count

    [Fact(DisplayName = "KV集成-Count随Set和Remove变化")]
    public void Count_ChangesWithSetAndRemove()
    {
        var countTable = _factory.GetCache(_fixture.ConnectionString, "count_table");
        var initial = countTable.Count;

        countTable.Set("cnt_k1", "v1");
        countTable.Set("cnt_k2", "v2");
        Assert.Equal(initial + 2, countTable.Count);

        countTable.Remove("cnt_k1");
        Assert.Equal(initial + 1, countTable.Count);
    }

    #endregion

    #region Increment / Decrement

    [Fact(DisplayName = "KV集成-Increment整数计数器")]
    public void Increment_IntCounter()
    {
        var cache = _factory.GetCache(_fixture.ConnectionString, "incr_table");

        var v1 = cache.Increment("counter", 1L);
        var v2 = cache.Increment("counter", 1L);
        var v3 = cache.Increment("counter", 1L);

        Assert.Equal(1L, v1);
        Assert.Equal(2L, v2);
        Assert.Equal(3L, v3);
    }

    [Fact(DisplayName = "KV集成-Increment步长增加")]
    public void Increment_WithStep()
    {
        var cache = _factory.GetCache(_fixture.ConnectionString, "incr_step_table");

        cache.Increment("step_counter", 10L);
        cache.Increment("step_counter", 10L);
        var val = cache.Increment("step_counter", 10L);

        Assert.Equal(30L, val);
    }

    [Fact(DisplayName = "KV集成-Increment浮点计数器")]
    public void Increment_Double()
    {
        var cache = _factory.GetCache(_fixture.ConnectionString, "incr_double_table");

        cache.Increment("score", 1.5);
        cache.Increment("score", 2.5);
        var val = cache.Increment("score", 1.0);

        Assert.Equal(5.0, val, precision: 10);
    }

    [Fact(DisplayName = "KV集成-Decrement整数计数器")]
    public void Decrement_IntCounter()
    {
        var cache = _factory.GetCache(_fixture.ConnectionString, "decr_table");

        cache.Increment("decr_key", 10L);
        var result = cache.Decrement("decr_key", 3L);

        Assert.Equal(7L, result);
    }

    #endregion

    #region 批量操作 GetAll / SetAll

    [Fact(DisplayName = "KV集成-SetAll批量写入并GetAll批量读取")]
    public void SetAll_And_GetAll()
    {
        var batch = new Dictionary<String, String>
        {
            ["batch_a"] = "value_a",
            ["batch_b"] = "value_b",
            ["batch_c"] = "value_c",
            ["batch_d"] = "value_d",
            ["batch_e"] = "value_e"
        };
        Cache.SetAll(batch);

        var keys = new[] { "batch_a", "batch_c", "batch_e" };
        var results = Cache.GetAll<String>(keys);

        Assert.Equal(3, results.Count);
        Assert.Equal("value_a", results["batch_a"]);
        Assert.Equal("value_c", results["batch_c"]);
        Assert.Equal("value_e", results["batch_e"]);
    }

    [Fact(DisplayName = "KV集成-GetAll含不存在键时返回null占位")]
    public void GetAll_WithMissingKeys_NullValues()
    {
        Cache.Set("getall_exists", "present");

        var keys = new[] { "getall_exists", "getall_missing_xyz" };
        var results = Cache.GetAll<String>(keys);

        Assert.Equal("present", results["getall_exists"]);
        Assert.True(!results.ContainsKey("getall_missing_xyz") || results["getall_missing_xyz"] == null,
            "不存在的键对应值应为 null 或不包含该键");
    }

    #endregion

    #region 通配符搜索

    [Fact(DisplayName = "KV集成-Search通配符返回匹配键")]
    public void Search_Wildcard_ReturnsMatchingKeys()
    {
        Cache.Set("search:user:1001", "Alice");
        Cache.Set("search:user:1002", "Bob");
        Cache.Set("search:user:1003", "Charlie");
        Cache.Set("search:order:2001", "Order1");

        var userKeys = Cache.Search("search:user:*").ToList();

        Assert.Equal(3, userKeys.Count);
        Assert.All(userKeys, k => Assert.StartsWith("search:user:", k));
    }

    [Fact(DisplayName = "KV集成-Search不匹配时返回空集合")]
    public void Search_NoMatch_ReturnsEmpty()
    {
        var results = Cache.Search("nosuchprefix_xyz_*").ToList();
        Assert.Empty(results);
    }

    #endregion

    #region 多 KV 表隔离

    [Fact(DisplayName = "KV集成-不同表名互相隔离")]
    public void MultiTable_Isolation()
    {
        var cacheA = _factory.GetCache(_fixture.ConnectionString, "table_iso_A");
        var cacheB = _factory.GetCache(_fixture.ConnectionString, "table_iso_B");

        cacheA.Set("shared_key", "from_A");
        cacheB.Set("shared_key", "from_B");

        Assert.Equal("from_A", cacheA.Get<String>("shared_key"));
        Assert.Equal("from_B", cacheB.Get<String>("shared_key"));

        // 各自的 .kvd 文件也应独立存在
        Assert.True(File.Exists(Path.Combine(KvDbFixture.DbPath, "table_iso_A.kvd")), "table_iso_A.kvd 应存在");
        Assert.True(File.Exists(Path.Combine(KvDbFixture.DbPath, "table_iso_B.kvd")), "table_iso_B.kvd 应存在");
    }

    #endregion

    #region Keys 属性

    [Fact(DisplayName = "KV集成-Keys包含所有已设置的键")]
    public void Keys_ContainsAllSetKeys()
    {
        var keysTable = _factory.GetCache(_fixture.ConnectionString, "keys_table");
        keysTable.Set("keys_a", "1");
        keysTable.Set("keys_b", "2");
        keysTable.Set("keys_c", "3");

        var allKeys = keysTable.Keys.ToList();

        Assert.Contains("keys_a", allKeys);
        Assert.Contains("keys_b", allKeys);
        Assert.Contains("keys_c", allKeys);
    }

    #endregion
}
