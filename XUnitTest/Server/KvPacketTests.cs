#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NewLife.Data;
using NewLife.NovaDb.Server;
using Xunit;

namespace XUnitTest.Server;

/// <summary>KvPacket 二进制协议编解码往返测试</summary>
public class KvPacketTests
{
    #region 请求编解码往返

    [Fact(DisplayName = "Set编解码往返_含值和TTL")]
    public void EncodeDecodeSet_WithValueAndTtl()
    {
        var value = Encoding.UTF8.GetBytes("hello-world");
        using var pk = KvPacket.EncodeSet("myTable", "key1", value, 300);
        var (tableName, key, decodedValue, ttl) = KvPacket.DecodeSet(pk);

        Assert.Equal("myTable", tableName);
        Assert.Equal("key1", key);
        Assert.NotNull(decodedValue);
        Assert.Equal(value, decodedValue);
        Assert.Equal(300, ttl);
    }

    [Fact(DisplayName = "Set编解码往返_空值")]
    public void EncodeDecodeSet_NullValue()
    {
        using var pk = KvPacket.EncodeSet("default", "nullKey", null, 0);
        var (tableName, key, decodedValue, ttl) = KvPacket.DecodeSet(pk);

        Assert.Equal("default", tableName);
        Assert.Equal("nullKey", key);
        Assert.Null(decodedValue);
        Assert.Equal(0, ttl);
    }

    [Fact(DisplayName = "Set编解码往返_空字节数组")]
    public void EncodeDecodeSet_EmptyValue()
    {
        using var pk = KvPacket.EncodeSet("default", "emptyKey", Array.Empty<Byte>(), 60);
        var (_, _, decodedValue, _) = KvPacket.DecodeSet(pk);

        Assert.NotNull(decodedValue);
        Assert.Empty(decodedValue);
    }

    [Fact(DisplayName = "Set 大值压缩编解码往返")]
    public void EncodeDecodeSet_LargeValue_RoundTrip()
    {
        var value = Encoding.UTF8.GetBytes(new String('A', 4096));

        using var pk = KvPacket.EncodeSet("default", "bigKey", value, 60);
        // 请求包应小于原始值 + 键名（传输省带宽）
        Assert.True(pk.Length < value.Length, $"Set 请求未压缩，包长度 {pk.Length} >= 原始 {value.Length}");

        var (_, _, decodedValue, _) = KvPacket.DecodeSet(pk);
        Assert.Equal(value, decodedValue);
    }

    [Fact(DisplayName = "Set 小值不压缩")]
    public void EncodeDecodeSet_SmallValue_NoCompression()
    {
        var value = Encoding.UTF8.GetBytes("tiny");
        using var pk = KvPacket.EncodeSet("default", "smallKey", value, 60);

        var (_, _, decodedValue, _) = KvPacket.DecodeSet(pk);
        Assert.Equal(value, decodedValue);
    }

    [Fact(DisplayName = "Get 响应大值压缩往返")]
    public void EncodeValue_LargeValue_RoundTrip()
    {
        var value = Encoding.UTF8.GetBytes(new String('B', 8192));

        using var pk = KvPacket.EncodeValue(value);
        Assert.True(pk.Length < value.Length, $"Get 响应未压缩，包长度 {pk.Length} >= 原始 {value.Length}");

        var decoded = KvPacket.DecodeNullableValue(pk);
        Assert.Equal(value, decoded);
    }

    [Fact(DisplayName = "Get 响应小值不压缩")]
    public void EncodeValue_SmallValue_NoCompression()
    {
        var value = Encoding.UTF8.GetBytes("small");

        using var pk = KvPacket.EncodeValue(value);
        // 格式：[1B flag][EncodedInt n][n 字节]，小值 flag 应为 0
        Assert.Equal(0, pk.GetSpan()[0]);

        var decoded = KvPacket.DecodeNullableValue(pk);
        Assert.Equal(value, decoded);
    }

    [Fact(DisplayName = "Get 响应空包解码为 null")]
    public void DecodeNullableValue_Empty_ReturnsNull()
    {
        Assert.Null(KvPacket.DecodeNullableValue(null));
    }

    [Fact(DisplayName = "TableKey编解码往返")]
    public void EncodeDecodeTableKey_RoundTrip()
    {
        using var pk = KvPacket.EncodeTableKey("users", "user:123");
        var (tableName, key) = KvPacket.DecodeTableKey(pk);

        Assert.Equal("users", tableName);
        Assert.Equal("user:123", key);
    }

    [Fact(DisplayName = "TableOnly编解码往返")]
    public void EncodeDecodeTableOnly_RoundTrip()
    {
        using var pk = KvPacket.EncodeTableOnly("sessions");
        var tableName = KvPacket.DecodeTableOnly(pk);

        Assert.Equal("sessions", tableName);
    }

    [Fact(DisplayName = "SetExpire编解码往返")]
    public void EncodeDecodeSetExpire_RoundTrip()
    {
        using var pk = KvPacket.EncodeSetExpire("cache", "token", 3600);
        var (tableName, key, ttl) = KvPacket.DecodeSetExpire(pk);

        Assert.Equal("cache", tableName);
        Assert.Equal("token", key);
        Assert.Equal(3600, ttl);
    }

    [Fact(DisplayName = "Increment编解码往返_正数")]
    public void EncodeDecodeIncrement_PositiveDelta()
    {
        using var pk = KvPacket.EncodeIncrement("counters", "visits", 42);
        var (tableName, key, delta) = KvPacket.DecodeIncrement(pk);

        Assert.Equal("counters", tableName);
        Assert.Equal("visits", key);
        Assert.Equal(42L, delta);
    }

    [Fact(DisplayName = "Increment编解码往返_负数")]
    public void EncodeDecodeIncrement_NegativeDelta()
    {
        using var pk = KvPacket.EncodeIncrement("counters", "stock", -5);
        var (_, _, delta) = KvPacket.DecodeIncrement(pk);

        Assert.Equal(-5L, delta);
    }

    [Fact(DisplayName = "IncrementDouble编解码往返")]
    public void EncodeDecodeIncrementDouble_RoundTrip()
    {
        using var pk = KvPacket.EncodeIncrementDouble("metrics", "temperature", 0.75);
        var (tableName, key, delta) = KvPacket.DecodeIncrementDouble(pk);

        Assert.Equal("metrics", tableName);
        Assert.Equal("temperature", key);
        Assert.Equal(0.75, delta);
    }

    [Fact(DisplayName = "Search编解码往返")]
    public void EncodeDecodeSearch_RoundTrip()
    {
        using var pk = KvPacket.EncodeSearch("logs", "user:*", 10, 50);
        var (tableName, pattern, offset, count) = KvPacket.DecodeSearch(pk);

        Assert.Equal("logs", tableName);
        Assert.Equal("user:*", pattern);
        Assert.Equal(10, offset);
        Assert.Equal(50, count);
    }

    [Fact(DisplayName = "DeleteByPattern编解码往返")]
    public void EncodeDecodeDeleteByPattern_RoundTrip()
    {
        using var pk = KvPacket.EncodeDeleteByPattern("temp", "session:*");
        var (tableName, pattern) = KvPacket.DecodeDeleteByPattern(pk);

        Assert.Equal("temp", tableName);
        Assert.Equal("session:*", pattern);
    }

    [Fact(DisplayName = "GetAll编解码往返")]
    public void EncodeDecodeGetAll_RoundTrip()
    {
        var keys = new[] { "key1", "key2", "key3" };
        using var pk = KvPacket.EncodeGetAll("default", keys);
        var (tableName, decodedKeys) = KvPacket.DecodeGetAll(pk);

        Assert.Equal("default", tableName);
        Assert.Equal(keys, decodedKeys);
    }

    [Fact(DisplayName = "SetAll编解码往返")]
    public void EncodeDecodeSetAll_RoundTrip()
    {
        var values = new Dictionary<String, Byte[]?>
        {
            ["k1"] = Encoding.UTF8.GetBytes("v1"),
            ["k2"] = null,
            ["k3"] = Array.Empty<Byte>(),
        };
        using var pk = KvPacket.EncodeSetAll("batch", values, 120);
        var (tableName, decoded, ttl) = KvPacket.DecodeSetAll(pk);

        Assert.Equal("batch", tableName);
        Assert.Equal(120, ttl);
        Assert.Equal(3, decoded.Count);
        Assert.Equal(values["k1"], decoded["k1"]);
        Assert.Null(decoded["k2"]);
        Assert.NotNull(decoded["k3"]);
        Assert.Empty(decoded["k3"]!);
    }

    #endregion

    #region 响应编解码

    [Theory(DisplayName = "Boolean响应编解码")]
    [InlineData(true)]
    [InlineData(false)]
    public void EncodeDecodeBoolean_RoundTrip(Boolean value)
    {
        var pk = KvPacket.EncodeBoolean(value);
        Assert.Equal(value, KvPacket.DecodeBoolean(pk));
    }

    [Fact(DisplayName = "Int32响应编解码")]
    public void EncodeDecodeInt32_RoundTrip()
    {
        using var pk = KvPacket.EncodeInt32(12345);
        Assert.Equal(12345, KvPacket.DecodeInt32(pk));
    }

    [Fact(DisplayName = "Int64响应编解码")]
    public void EncodeDecodeInt64_RoundTrip()
    {
        using var pk = KvPacket.EncodeInt64(9876543210L);
        Assert.Equal(9876543210L, KvPacket.DecodeInt64(pk));
    }

    [Fact(DisplayName = "Double响应编解码")]
    public void EncodeDecodeDouble_RoundTrip()
    {
        using var pk = KvPacket.EncodeDouble(3.14159);
        Assert.Equal(3.14159, KvPacket.DecodeDouble(pk));
    }

    [Fact(DisplayName = "Empty响应编解码")]
    public void EncodeEmpty_ReturnsEmptyPacket()
    {
        var pk = KvPacket.EncodeEmpty();
        Assert.Equal(0, pk.Length);
        Assert.Null(KvPacket.DecodeNullableValue(pk));
    }

    [Fact(DisplayName = "StringArray响应编解码_非空数组")]
    public void EncodeDecodeStringArray_NonEmpty()
    {
        var keys = new[] { "alpha", "beta", "gamma" };
        using var pk = KvPacket.EncodeStringArray(keys);
        var decoded = KvPacket.DecodeStringArray(pk);

        Assert.Equal(keys, decoded);
    }

    [Fact(DisplayName = "StringArray响应编解码_空数组")]
    public void EncodeDecodeStringArray_Empty()
    {
        using var pk = KvPacket.EncodeStringArray([]);
        var decoded = KvPacket.DecodeStringArray(pk);

        Assert.Empty(decoded);
    }

    [Fact(DisplayName = "GetAllResponse响应编解码")]
    public void EncodeDecodeGetAllResponse_RoundTrip()
    {
        var keys = new[] { "exist", "missing", "empty" };
        var existPk = new OwnerPacket(5);
        Encoding.UTF8.GetBytes("hello").CopyTo(existPk.GetSpan());
        var emptyPk = new OwnerPacket(0);
        var data = new Dictionary<String, IOwnerPacket?>
        {
            ["exist"] = existPk,
            ["missing"] = null,
            ["empty"] = emptyPk,
        };

        using var pk = KvPacket.EncodeGetAllResponse(keys, data);
        var decoded = KvPacket.DecodeGetAllResponse(pk);
        existPk.Dispose();
        emptyPk.Dispose();

        Assert.Equal(3, decoded.Count);
        Assert.NotNull(decoded["exist"]);
        Assert.Equal("hello", Encoding.UTF8.GetString(decoded["exist"]!));
        Assert.Null(decoded["missing"]);
    }

    #endregion

    #region 中文和特殊字符

    [Fact(DisplayName = "中文键值编解码往返")]
    public void EncodeDecodeSet_ChineseKeyValue()
    {
        var value = Encoding.UTF8.GetBytes("你好世界");
        using var pk = KvPacket.EncodeSet("表", "键", value, 0);
        var (tableName, key, decodedValue, _) = KvPacket.DecodeSet(pk);

        Assert.Equal("表", tableName);
        Assert.Equal("键", key);
        Assert.Equal(value, decodedValue);
    }

    [Fact(DisplayName = "null表名默认为default")]
    public void EncodeTableOnly_NullTableName_DefaultsToDefault()
    {
        using var pk = KvPacket.EncodeTableOnly(null!);
        var tableName = KvPacket.DecodeTableOnly(pk);

        Assert.Equal("default", tableName);
    }

    #endregion

    #region 边界值

    [Fact(DisplayName = "DecodeBoolean_null包返回false")]
    public void DecodeBoolean_NullPacket_ReturnsFalse()
    {
        Assert.False(KvPacket.DecodeBoolean(null));
    }

    [Fact(DisplayName = "DecodeInt32_null包返回0")]
    public void DecodeInt32_NullPacket_ReturnsZero()
    {
        Assert.Equal(0, KvPacket.DecodeInt32(null));
    }

    [Fact(DisplayName = "DecodeInt64_null包返回0")]
    public void DecodeInt64_NullPacket_ReturnsZero()
    {
        Assert.Equal(0L, KvPacket.DecodeInt64(null));
    }

    [Fact(DisplayName = "DecodeDouble_null包返回0")]
    public void DecodeDouble_NullPacket_ReturnsZero()
    {
        Assert.Equal(0.0, KvPacket.DecodeDouble(null));
    }

    [Fact(DisplayName = "DecodeStringArray_null包返回空数组")]
    public void DecodeStringArray_NullPacket_ReturnsEmpty()
    {
        Assert.Empty(KvPacket.DecodeStringArray(null));
    }

    [Fact(DisplayName = "DecodeGetAllResponse_null包返回空字典")]
    public void DecodeGetAllResponse_NullPacket_ReturnsEmptyDict()
    {
        Assert.Empty(KvPacket.DecodeGetAllResponse(null));
    }

    [Fact(DisplayName = "大数据Set编解码往返")]
    public void EncodeDecodeSet_LargeValue()
    {
        var value = new Byte[64 * 1024];
        new Random(42).NextBytes(value);

        using var pk = KvPacket.EncodeSet("default", "bigKey", value, 0);
        var (_, key, decodedValue, _) = KvPacket.DecodeSet(pk);

        Assert.Equal("bigKey", key);
        Assert.Equal(value, decodedValue);
    }

    #endregion
}
