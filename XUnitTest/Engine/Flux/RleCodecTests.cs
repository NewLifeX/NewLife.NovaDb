using System;
using NewLife.NovaDb.Engine.Flux.Coding;
using Xunit;

namespace XUnitTest.Engine.Flux;

/// <summary>游程长度编码器测试（F14）</summary>
public class RleCodecTests
{
    [Fact(DisplayName = "空序列编解码返回空数组")]
    public void Empty_RoundTrip()
    {
        Assert.Empty(RleCodec.EncodeInt64([]));
        Assert.Empty(RleCodec.DecodeInt64([], 0));
        Assert.Empty(RleCodec.EncodeBoolean([]));
        Assert.Empty(RleCodec.DecodeBoolean([], 0));
    }

    [Fact(DisplayName = "单一重复整数压缩到数字节")]
    public void ConstantInt64_HighCompression()
    {
        var values = new Int64[1000];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = 42;
        }

        var encoded = RleCodec.EncodeInt64(values);
        Assert.Equal(values, RleCodec.DecodeInt64(encoded, values.Length));
        Assert.True(encoded.Length <= 4, $"编码长度 {encoded.Length} 未达到紧凑要求");
    }

    [Fact(DisplayName = "交替整数序列往返一致")]
    public void Alternating_RoundTrip()
    {
        var values = new Int64[101];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i % 2 == 0 ? 1 : -1;
        }

        var encoded = RleCodec.EncodeInt64(values);
        Assert.Equal(values, RleCodec.DecodeInt64(encoded, values.Length));
    }

    [Fact(DisplayName = "布尔序列往返一致且长游程紧凑")]
    public void Boolean_RoundTrip()
    {
        var values = new Boolean[1000];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i < 700;
        }

        var encoded = RleCodec.EncodeBoolean(values);
        Assert.Equal(values, RleCodec.DecodeBoolean(encoded, values.Length));
        Assert.True(encoded.Length <= 8, $"编码长度 {encoded.Length} 未达到紧凑要求");
    }

    [Fact(DisplayName = "随机游程序列编解码往返一致")]
    public void RandomRuns_RoundTrip()
    {
        var rnd = new Random(20261016);
        var values = new Int64[512];
        var pos = 0;
        while (pos < values.Length)
        {
            var v = rnd.Next(-5, 5);
            var run = rnd.Next(1, 6);
            for (var i = 0; i < run && pos < values.Length; i++)
            {
                values[pos++] = v;
            }
        }

        var encoded = RleCodec.EncodeInt64(values);
        Assert.Equal(values, RleCodec.DecodeInt64(encoded, values.Length));
    }
}
