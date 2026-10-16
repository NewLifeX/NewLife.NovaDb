using System;
using NewLife.NovaDb.Engine.Flux.Coding;
using Xunit;

namespace XUnitTest.Engine.Flux;

/// <summary>Gorilla XOR 浮点编码器测试（F14）</summary>
public class XorCodecTests
{
    [Fact(DisplayName = "空序列编解码返回空数组")]
    public void Empty_RoundTrip()
    {
        Assert.Empty(XorCodec.EncodeDouble([]));
        Assert.Empty(XorCodec.DecodeDouble([], 0));
    }

    [Fact(DisplayName = "单值往返一致")]
    public void Single_RoundTrip()
    {
        var encoded = XorCodec.EncodeDouble([3.14]);
        Assert.Equal(new[] { 3.14 }, XorCodec.DecodeDouble(encoded, 1));
    }

    [Fact(DisplayName = "重复值每值约一位")]
    public void Constant_HighCompression()
    {
        var values = new Double[1000];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = 25.5;
        }

        var encoded = XorCodec.EncodeDouble(values);
        var decoded = XorCodec.DecodeDouble(encoded, values.Length);

        Assert.Equal(values, decoded);
        Assert.True(encoded.Length < values.Length, $"编码长度 {encoded.Length} 未达到紧凑要求");
    }

    [Fact(DisplayName = "缓变量化浮点序列压缩率超过两倍")]
    public void SlowChanging_Compressed()
    {
        var values = new Double[1024];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = 3000 + (i * 7 % 256);
        }

        var encoded = XorCodec.EncodeDouble(values);
        Assert.Equal(values, XorCodec.DecodeDouble(encoded, values.Length));
        Assert.True(encoded.Length < values.Length * 4, $"编码长度 {encoded.Length} 未达到压缩要求");
    }

    [Fact(DisplayName = "特殊浮点位模式原样往返")]
    public void SpecialValues_RoundTrip()
    {
        Double[] values = [Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity, -0.0, 0.0, Double.MaxValue, Double.MinValue, Double.Epsilon];
        var encoded = XorCodec.EncodeDouble(values);
        var decoded = XorCodec.DecodeDouble(encoded, values.Length);

        Assert.Equal(values.Length, decoded.Length);
        for (var i = 0; i < values.Length; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(values[i]), BitConverter.DoubleToInt64Bits(decoded[i]));
        }
    }

    [Fact(DisplayName = "随机浮点序列编解码往返一致")]
    public void Random_RoundTrip()
    {
        var rnd = new Random(20261016);
        var values = new Double[512];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = rnd.NextDouble() * 1000 - 500;
        }

        var encoded = XorCodec.EncodeDouble(values);
        var decoded = XorCodec.DecodeDouble(encoded, values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], decoded[i]);
        }
    }
}
