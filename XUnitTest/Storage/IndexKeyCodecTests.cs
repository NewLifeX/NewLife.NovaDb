using System;
using System.Collections.Generic;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using Xunit;

#nullable enable

namespace XUnitTest.Storage;

public class IndexKeyCodecTests
{
    private static IndexKeyCodec Create(params DataType[] types) => new(new DefaultDataCodec(), types);

    private static Object Roundtrip(IndexKeyCodec codec, Object key)
    {
        var buffer = new Byte[codec.GetEncodedLength(key)];
        var written = codec.Encode(key, buffer, 0);
        Assert.Equal(buffer.Length, written);

        return codec.Decode(buffer, 0, buffer.Length)!;
    }

    private static Int64 Prefix(IndexKeyCodec codec, Object key)
    {
        Assert.True(codec.TryGetSortPrefix(key, out var prefix));
        return prefix;
    }

    [Fact(DisplayName = "单列整数键编码往返一致")]
    public void Int32Roundtrip()
    {
        var codec = Create(DataType.Int32);

        Assert.Equal(42, Assert.IsType<Int32>(Roundtrip(codec, 42)));
        Assert.Equal(-7, Assert.IsType<Int32>(Roundtrip(codec, -7)));
    }

    [Fact(DisplayName = "外部传入 Int64 参数时按 Int32 列归一，编码一致")]
    public void Int32ColumnNormalizesInt64()
    {
        var codec = Create(DataType.Int32);

        var a = new Byte[codec.GetEncodedLength(42)];
        var b = new Byte[codec.GetEncodedLength(42L)];
        codec.Encode(42, a, 0);
        codec.Encode(42L, b, 0);

        Assert.Equal(a, b);
        Assert.Equal(0, codec.Compare(a, 0, a.Length, true, Prefix(codec, 42), b, 0, b.Length, true, Prefix(codec, 42L)));
    }

    [Fact(DisplayName = "字符串键编码往返一致，含中文与空串")]
    public void StringRoundtrip()
    {
        var codec = Create(DataType.String);

        Assert.Equal("Alice", Assert.IsType<String>(Roundtrip(codec, "Alice")));
        Assert.Equal("张三", Assert.IsType<String>(Roundtrip(codec, "张三")));
        Assert.Equal("", Assert.IsType<String>(Roundtrip(codec, "")));
    }

    [Fact(DisplayName = "联合索引键编码往返一致")]
    public void CompositeRoundtrip()
    {
        var codec = Create(DataType.String, DataType.Int32, DataType.DateTime);
        var key = new Object?[] { "A", 5, new DateTime(2026, 11, 21, 10, 30, 0) };

        var decoded = Assert.IsType<Object?[]>(Roundtrip(codec, key));

        Assert.Equal(key.Length, decoded.Length);
        Assert.Equal("A", decoded[0]);
        Assert.Equal(5, decoded[1]);
        Assert.Equal((DateTime)key[2]!, (DateTime)decoded[2]!);
    }

    [Fact(DisplayName = "联合索引键的 NULL 段可正确还原")]
    public void CompositeWithNullPart()
    {
        var codec = Create(DataType.Int32, DataType.String);

        var decoded = Assert.IsType<Object?[]>(Roundtrip(codec, new Object?[] { null, "B" }));

        Assert.Null(decoded[0]);
        Assert.Equal("B", decoded[1]);
    }

    [Fact(DisplayName = "联合索引键段数不匹配时抛异常")]
    public void CompositePartCountMismatch()
    {
        var codec = Create(DataType.Int32, DataType.String);

        Assert.Throws<NovaException>(() => codec.GetEncodedLength(new Object?[] { 1 }));
    }

    [Fact(DisplayName = "稳定哈希对同一字节串保持不变")]
    public void HashIsStable()
    {
        var buffer = new Byte[] { (Byte)'a', (Byte)'b', (Byte)'c' };

        var hash = IndexKeyCodec.Hash(buffer, 0, buffer.Length);

        // 硬编码期望值，防止算法被无意改动导致持久化索引全部失效
        Assert.Equal(0x33EBAF9927CBC5BDUL, hash);
        Assert.Equal(hash, IndexKeyCodec.Hash(buffer, 0, buffer.Length));
    }

    [Fact(DisplayName = "整数前缀按数值升序排列")]
    public void Int32PrefixOrder()
    {
        var codec = Create(DataType.Int32);

        Assert.True(Prefix(codec, -100) < Prefix(codec, -1));
        Assert.True(Prefix(codec, -1) < Prefix(codec, 0));
        Assert.True(Prefix(codec, 0) < Prefix(codec, 100));
    }

    [Fact(DisplayName = "时间前缀按时间轴升序排列")]
    public void DateTimePrefixOrder()
    {
        var codec = Create(DataType.DateTime);
        var now = DateTime.Now;

        Assert.True(Prefix(codec, now.AddDays(-1)) < Prefix(codec, now));
        Assert.True(Prefix(codec, now) < Prefix(codec, now.AddDays(1)));
    }

    [Fact(DisplayName = "浮点前缀正确处理负数与零")]
    public void DoublePrefixOrder()
    {
        var codec = Create(DataType.Double);

        Assert.True(Prefix(codec, -2.5) < Prefix(codec, -1.0));
        Assert.True(Prefix(codec, -1.0) < Prefix(codec, 0.0));
        Assert.True(Prefix(codec, 0.0) < Prefix(codec, 1.0));
        Assert.True(Prefix(codec, 1.0) < Prefix(codec, 2.5));
    }

    [Fact(DisplayName = "字符串键不支持前缀，回退到解码比较")]
    public void StringHasNoPrefix()
    {
        var codec = Create(DataType.String);

        Assert.False(codec.HasSortPrefix);
        Assert.False(codec.TryGetSortPrefix("A", out _));
    }

    [Fact(DisplayName = "比较结果与内存跳表的比较语义一致")]
    public void CompareMatchesDecodedCompare()
    {
        var codec = Create(DataType.Int32);
        var keys = new List<Int32> { -5, -1, 0, 1, 9 };

        for (var i = 0; i < keys.Count; i++)
        {
            for (var j = 0; j < keys.Count; j++)
            {
                var a = new Byte[codec.GetEncodedLength(keys[i])];
                var b = new Byte[codec.GetEncodedLength(keys[j])];
                codec.Encode(keys[i], a, 0);
                codec.Encode(keys[j], b, 0);

                var expected = keys[i].CompareTo(keys[j]);
                var actual = codec.Compare(a, 0, a.Length, true, Prefix(codec, keys[i]), b, 0, b.Length, true, Prefix(codec, keys[j]));

                Assert.Equal(Math.Sign(expected), Math.Sign(actual));
            }
        }
    }

    [Fact(DisplayName = "前缀相同时仍做精确比较")]
    public void EqualPrefixFallsBackToDecodedCompare()
    {
        var codec = Create(DataType.Int32);
        var a = new Byte[codec.GetEncodedLength(7)];
        var b = new Byte[codec.GetEncodedLength(8)];
        codec.Encode(7, a, 0);
        codec.Encode(8, b, 0);

        // 人为传入相同前缀，验证不会仅凭前缀判定相等
        Assert.NotEqual(0, codec.Compare(a, 0, a.Length, true, 100, b, 0, b.Length, true, 100));
    }
}
