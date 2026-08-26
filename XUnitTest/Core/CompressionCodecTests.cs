using System;
using System.Text;
using NewLife.NovaDb.Core;
using Xunit;

namespace XUnitTest.Core;

/// <summary>数据压缩编解码器测试（P03）</summary>
public class CompressionCodecTests
{
    [Fact(DisplayName = "小于阈值的数据不压缩返回原数据")]
    public void Compress_SmallData_ReturnsOriginal()
    {
        var codec = new CompressionCodec { Threshold = 256 };
        var data = Encoding.UTF8.GetBytes("hello");

        var result = codec.Compress(data);

        Assert.Same(data, result); // 引用相同 = 未压缩
    }

    [Fact(DisplayName = "大数据 GZip 压缩解压往返一致")]
    public void GZip_RoundTrip()
    {
        var codec = new CompressionCodec { Algorithm = CompressionAlgorithm.GZip };
        var data = Encoding.UTF8.GetBytes(new String('A', 4096));

        var compressed = codec.Compress(data);
        Assert.NotSame(data, compressed);
        Assert.True(compressed.Length < data.Length);

        var decompressed = codec.Decompress(compressed);
        Assert.Equal(data, decompressed);
    }

    [Fact(DisplayName = "大数据 Deflate 压缩解压往返一致")]
    public void Deflate_RoundTrip()
    {
        var codec = new CompressionCodec { Algorithm = CompressionAlgorithm.Deflate };
        var data = Encoding.UTF8.GetBytes(new String('B', 4096));

        var compressed = codec.Compress(data);
        Assert.NotSame(data, compressed);

        var decompressed = codec.Decompress(compressed);
        Assert.Equal(data, decompressed);
    }

    [Fact(DisplayName = "重复数据压缩率显著")]
    public void Compress_RepeatedData_GoodRatio()
    {
        var codec = new CompressionCodec();
        var data = Encoding.UTF8.GetBytes(new String('C', 16384));

        var compressed = codec.Compress(data);

        Assert.True(compressed.Length < data.Length / 4, $"压缩率不足，压缩后 {compressed.Length} / 原始 {data.Length}");
    }

    [Fact(DisplayName = "IsCompressed 正确识别压缩数据")]
    public void IsCompressed_DetectsCompressed()
    {
        var codec = new CompressionCodec();
        var data = Encoding.UTF8.GetBytes(new String('D', 4096));

        var compressed = codec.Compress(data);
        Assert.True(CompressionCodec.IsCompressed(compressed));

        var small = Encoding.UTF8.GetBytes("small");
        Assert.False(CompressionCodec.IsCompressed(small));
        Assert.False(CompressionCodec.IsCompressed(null!));
    }

    [Fact(DisplayName = "TryCompress 返回是否实际压缩")]
    public void TryCompress_ReportsWhetherCompressed()
    {
        var codec = new CompressionCodec { Threshold = 256 };
        var big = Encoding.UTF8.GetBytes(new String('E', 4096));
        var small = Encoding.UTF8.GetBytes("tiny");

        Assert.True(codec.TryCompress(big, out var compressedBig));
        Assert.False(codec.TryCompress(small, out var compressedSmall));
        Assert.Same(small, compressedSmall);
    }

    [Fact(DisplayName = "压缩后反而变大时回退返回原数据")]
    public void Compress_Inflates_FallsBackToOriginal()
    {
        // 随机不可压缩数据，压缩后可能变大
        var codec = new CompressionCodec();
        var data = new Byte[4096];
        new Random(42).NextBytes(data);

        var result = codec.Compress(data);

        // 压缩后变大则应返回原数据（引用相同）
        if (result.Length >= data.Length)
            Assert.Same(data, result);
    }

    [Fact(DisplayName = "Null 数据抛异常")]
    public void Compress_Null_Throws()
    {
        var codec = new CompressionCodec();
        Assert.Throws<ArgumentNullException>(() => codec.Compress(null!));
        Assert.Throws<ArgumentNullException>(() => codec.Decompress(null!));
    }

    [Fact(DisplayName = "非压缩数据解压原样返回")]
    public void Decompress_PlainData_ReturnsOriginal()
    {
        var codec = new CompressionCodec();
        var data = Encoding.UTF8.GetBytes("plain text, not compressed");

        var result = codec.Decompress(data);
        Assert.Equal(data, result);
    }

    [Fact(DisplayName = "空数组压缩返回原数据")]
    public void Compress_Empty_ReturnsOriginal()
    {
        var codec = new CompressionCodec();
        var data = Array.Empty<Byte>();

        var result = codec.Compress(data);
        Assert.Same(data, result);
    }
}
