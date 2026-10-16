using System;
using NewLife.NovaDb.Engine.Flux.Coding;
using Xunit;

namespace XUnitTest.Engine.Flux;

/// <summary>二阶差分编码器测试（F14）</summary>
public class DeltaDeltaCodecTests
{
    [Fact(DisplayName = "空序列编解码返回空数组")]
    public void Empty_RoundTrip()
    {
        Assert.Empty(DeltaDeltaCodec.EncodeInt64([]));
        Assert.Empty(DeltaDeltaCodec.DecodeInt64([], 0));
    }

    [Fact(DisplayName = "等间隔时间戳每值约一字节")]
    public void ConstantInterval_HighCompression()
    {
        var values = new Int64[1000];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = 1700000000000 + i * 1000L;
        }

        var encoded = DeltaDeltaCodec.EncodeInt64(values);
        var decoded = DeltaDeltaCodec.DecodeInt64(encoded, values.Length);

        Assert.Equal(values, decoded);
        Assert.True(encoded.Length < values.Length * 2, $"编码长度 {encoded.Length} 未达到紧凑要求");
    }

    [Fact(DisplayName = "单值与双值序列往返一致")]
    public void SmallCount_RoundTrip()
    {
        var single = DeltaDeltaCodec.EncodeInt64([7L]);
        Assert.Equal(new[] { 7L }, DeltaDeltaCodec.DecodeInt64(single, 1));

        var two = DeltaDeltaCodec.EncodeInt64([7L, 3L]);
        Assert.Equal(new[] { 7L, 3L }, DeltaDeltaCodec.DecodeInt64(two, 2));
    }

    [Fact(DisplayName = "抖动间隔序列往返一致")]
    public void Jitter_RoundTrip()
    {
        var rnd = new Random(20261016);
        var values = new Int64[256];
        var current = 1700000000000L;
        values[0] = current;
        for (var i = 1; i < values.Length; i++)
        {
            current += 1000 + rnd.Next(-50, 50);
            values[i] = current;
        }

        var encoded = DeltaDeltaCodec.EncodeInt64(values);
        Assert.Equal(values, DeltaDeltaCodec.DecodeInt64(encoded, values.Length));
    }

    [Fact(DisplayName = "极值与回退序列往返一致")]
    public void Extremes_RoundTrip()
    {
        Int64[] values = [0, Int64.MaxValue, Int64.MinValue, 0, Int64.MaxValue - 1];
        var encoded = DeltaDeltaCodec.EncodeInt64(values);
        Assert.Equal(values, DeltaDeltaCodec.DecodeInt64(encoded, values.Length));
    }
}
