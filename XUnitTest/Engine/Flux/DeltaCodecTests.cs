using System;
using NewLife.NovaDb.Engine.Flux.Coding;
using Xunit;

namespace XUnitTest.Engine.Flux;

/// <summary>一阶差分编码器测试（F14）</summary>
public class DeltaCodecTests
{
    [Fact(DisplayName = "空序列编解码返回空数组")]
    public void Empty_RoundTrip()
    {
        Assert.Empty(DeltaCodec.EncodeInt64([]));
        Assert.Empty(DeltaCodec.DecodeInt64([], 0));
    }

    [Fact(DisplayName = "单调递增时间戳往返一致且显著紧凑")]
    public void MonotonicTimestamps_RoundTrip()
    {
        var values = new Int64[1000];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = 1700000000000 + i * 1000L;
        }

        var encoded = DeltaCodec.EncodeInt64(values);
        var decoded = DeltaCodec.DecodeInt64(encoded, values.Length);

        Assert.Equal(values, decoded);
        Assert.True(encoded.Length < values.Length * 3, $"编码长度 {encoded.Length} 未达到紧凑要求");
    }

    [Fact(DisplayName = "单值与双值序列往返一致")]
    public void SmallCount_RoundTrip()
    {
        var single = DeltaCodec.EncodeInt64([42L]);
        Assert.Equal(new[] { 42L }, DeltaCodec.DecodeInt64(single, 1));

        var two = DeltaCodec.EncodeInt64([42L, 40L]);
        Assert.Equal(new[] { 42L, 40L }, DeltaCodec.DecodeInt64(two, 2));
    }

    [Fact(DisplayName = "乱序与负值序列往返一致")]
    public void UnorderedAndNegative_RoundTrip()
    {
        Int64[] values = [100, -100, Int64.MaxValue, Int64.MinValue, 0, -1, 1];
        var encoded = DeltaCodec.EncodeInt64(values);
        Assert.Equal(values, DeltaCodec.DecodeInt64(encoded, values.Length));
    }

    [Fact(DisplayName = "随机序列编解码往返一致")]
    public void Random_RoundTrip()
    {
        var rnd = new Random(20261016);
        var values = new Int64[512];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = rnd.Next(Int32.MinValue, Int32.MaxValue) * (i % 3 == 0 ? -1L : 1L);
        }

        var encoded = DeltaCodec.EncodeInt64(values);
        Assert.Equal(values, DeltaCodec.DecodeInt64(encoded, values.Length));
    }
}
