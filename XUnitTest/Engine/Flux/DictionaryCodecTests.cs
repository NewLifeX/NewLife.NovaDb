using System;
using NewLife.NovaDb.Engine.Flux.Coding;
using Xunit;

namespace XUnitTest.Engine.Flux;

/// <summary>字符串字典编码器测试（F14）</summary>
public class DictionaryCodecTests
{
    [Fact(DisplayName = "空序列编解码返回空数组")]
    public void Empty_RoundTrip()
    {
        Assert.Empty(DictionaryCodec.EncodeStrings([]));
        Assert.Empty(DictionaryCodec.DecodeStrings([], 0));
    }

    [Fact(DisplayName = "低基数列去重后显著紧凑")]
    public void LowCardinality_HighCompression()
    {
        var values = new String[1000];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (i % 3) switch { 0 => "running", 1 => "stopped", _ => "unknown" };
        }

        var encoded = DictionaryCodec.EncodeStrings(values);
        var decoded = DictionaryCodec.DecodeStrings(encoded, values.Length);

        Assert.Equal(values, decoded);
        Assert.True(encoded.Length < 2000, $"编码长度 {encoded.Length} 未达到紧凑要求");
    }

    [Fact(DisplayName = "null 与空字符串分别往返一致")]
    public void NullAndEmpty_RoundTrip()
    {
        String?[] values = [null, "", "a", null, ""];
        var encoded = DictionaryCodec.EncodeStrings(values);
        var decoded = DictionaryCodec.DecodeStrings(encoded, values.Length);

        Assert.Equal(values, decoded);
    }

    [Fact(DisplayName = "中文与多字节字符往返一致")]
    public void Unicode_RoundTrip()
    {
        String?[] values = ["设备温度", "设备温度", "湿度传感器", "🌡️🌡️", ""];
        var encoded = DictionaryCodec.EncodeStrings(values);
        Assert.Equal(values, DictionaryCodec.DecodeStrings(encoded, values.Length));
    }

    [Fact(DisplayName = "全部唯一值时索引与字典等长")]
    public void AllUnique_RoundTrip()
    {
        var values = new String[256];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = $"device-{i:D4}";
        }

        var encoded = DictionaryCodec.EncodeStrings(values);
        Assert.Equal(values, DictionaryCodec.DecodeStrings(encoded, values.Length));
    }
}
