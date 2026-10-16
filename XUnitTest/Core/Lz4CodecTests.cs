using System;
using System.Text;
using NewLife.NovaDb.Core;
using Xunit;

namespace XUnitTest.Core;

/// <summary>LZ4 块压缩编解码器测试（F15）</summary>
public class Lz4CodecTests
{
    [Fact(DisplayName = "规范测试向量：四字节字面量解码正确")]
    public void Spec_LiteralsOnly()
    {
        // 块格式规范示例：token 高四位 4 表示 4 个字节字面量，末尾序列无偏移量
        Byte[] block = [0x40, 0x61, 0x62, 0x63, 0x64];
        var data = Lz4Codec.Decompress(block, 4);
        Assert.Equal("abcd", Encoding.ASCII.GetString(data));
    }

    [Fact(DisplayName = "规范测试向量：单字面量加重复匹配解码为十个 a")]
    public void Spec_MatchWithOffsetOne()
    {
        // token 0x15：字面量长度 1、匹配长度 5+4=9，偏移量 1（重叠复制），共 10 字节
        Byte[] block = [0x15, 0x61, 0x01, 0x00];
        var data = Lz4Codec.Decompress(block, 10);
        Assert.Equal(new String('a', 10), Encoding.ASCII.GetString(data));
    }

    [Fact(DisplayName = "规范测试向量：两段序列解码为重叠重复串")]
    public void Spec_TwoSequences()
    {
        // token 0x44：4 个字节字面量 + 匹配长度 4+4=8，偏移量 4 → abcdabcdabcd
        Byte[] block = [0x44, 0x61, 0x62, 0x63, 0x64, 0x04, 0x00];
        var data = Lz4Codec.Decompress(block, 12);
        Assert.Equal("abcdabcdabcd", Encoding.ASCII.GetString(data));
    }

    [Fact(DisplayName = "空数据压缩为空且解压为空")]
    public void Empty_RoundTrip()
    {
        Assert.Empty(Lz4Codec.Compress([]));
        Assert.Empty(Lz4Codec.Decompress([], 0));
        Assert.False(Lz4Codec.TryDecompress([], 5, out var data));
        Assert.Empty(data);
    }

    [Fact(DisplayName = "小数据与任意长度数据往返一致")]
    public void SmallData_RoundTrip()
    {
        for (var len = 1; len <= 40; len++)
        {
            var source = new Byte[len];
            for (var i = 0; i < len; i++)
            {
                source[i] = (Byte)(i * 7 % 251);
            }

            var compressed = Lz4Codec.Compress(source);
            Assert.Equal(source, Lz4Codec.Decompress(compressed, len));
        }
    }

    [Fact(DisplayName = "大量重复数据压缩率超过五十倍")]
    public void HighlyRepetitive_HighRatio()
    {
        var source = new Byte[1 << 20];
        for (var i = 0; i < source.Length; i++)
        {
            source[i] = (Byte)'A';
        }

        var compressed = Lz4Codec.Compress(source);
        Assert.True(compressed.Length < source.Length / 50, $"压缩长度 {compressed.Length} 未达到高压缩率");
        Assert.Equal(source, Lz4Codec.Decompress(compressed, source.Length));
    }

    [Fact(DisplayName = "结构化文本重复内容压缩率超过十倍")]
    public void StructuredText_HighRatio()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 1000; i++)
        {
            builder.Append("GET /api/device/report?id=12345 HTTP/1.1\r\n");
        }

        var source = Encoding.UTF8.GetBytes(builder.ToString());
        var compressed = Lz4Codec.Compress(source);
        Assert.True(compressed.Length < source.Length / 10, $"压缩长度 {compressed.Length} 未达到压缩要求");
        Assert.Equal(source, Lz4Codec.Decompress(compressed, source.Length));
    }

    [Fact(DisplayName = "随机数据往返一致且收益不足时回退原样")]
    public void RandomData_RoundTrip()
    {
        var rnd = new Random(20261016);
        var source = new Byte[4096];
        rnd.NextBytes(source);

        var ok = Lz4Codec.TryCompress(source, out var compressed);
        Assert.False(ok);
        Assert.Equal(source, compressed);

        var raw = Lz4Codec.Compress(source);
        Assert.Equal(source, Lz4Codec.Decompress(raw, source.Length));
    }

    [Fact(DisplayName = "可压缩数据尝试压缩成功且往返一致")]
    public void TryCompress_Success()
    {
        var source = Encoding.UTF8.GetBytes(new String('x', 2048));
        var ok = Lz4Codec.TryCompress(source, out var compressed);

        Assert.True(ok);
        Assert.NotEqual(source.Length, compressed.Length);
        Assert.Equal(source, Lz4Codec.Decompress(compressed, source.Length));
    }

    [Fact(DisplayName = "非法块数据解压失败不抛出")]
    public void InvalidData_ReturnsFalse()
    {
        // 偏移量超出已解压范围
        Assert.False(Lz4Codec.TryDecompress([0x00, 0x01, 0x00], 4, out _));

        // 字面量越界（声明 2 字节但仅提供 1 字节）
        Assert.False(Lz4Codec.TryDecompress([0x20, 0x61], 2, out _));

        // 解压长度与期望不一致
        Assert.False(Lz4Codec.TryDecompress([0x20, 0x61, 0x62], 3, out _));

        // 偏移量数据缺失
        Assert.False(Lz4Codec.TryDecompress([0x10, 0x61], 5, out _));
    }

    [Fact(DisplayName = "最大压缩长度覆盖实际压缩结果")]
    public void MaxCompressedLength_Covers()
    {
        var rnd = new Random(20261016);
        foreach (var len in new[] { 0, 1, 13, 64, 1000, 100000 })
        {
            var source = new Byte[len];
            rnd.NextBytes(source);

            var compressed = Lz4Codec.Compress(source);
            Assert.True(compressed.Length <= Lz4Codec.GetMaxCompressedLength(len));
        }
    }
}
