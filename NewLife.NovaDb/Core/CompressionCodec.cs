using System.IO.Compression;

namespace NewLife.NovaDb.Core;

/// <summary>数据压缩编解码器，支持 GZip 和 Deflate 两种压缩算法</summary>
/// <remarks>
/// 用于网络传输、页面存储等场景的数据压缩。
/// 自动跳过小于阈值的数据以避免压缩后反而变大。
/// </remarks>
public class CompressionCodec
{
    /// <summary>默认压缩阈值（字节），小于此值不压缩</summary>
    public const Int32 DefaultThreshold = 256;

    /// <summary>压缩算法</summary>
    public CompressionAlgorithm Algorithm { get; set; } = CompressionAlgorithm.GZip;

    /// <summary>压缩级别</summary>
    public CompressionLevel Level { get; set; } = CompressionLevel.Fastest;

    /// <summary>压缩阈值（字节），小于此值不压缩</summary>
    public Int32 Threshold { get; set; } = DefaultThreshold;

    /// <summary>使用 GZip 算法的默认实例</summary>
    public static CompressionCodec Default { get; } = new();

    /// <summary>压缩数据</summary>
    /// <param name="data">原始数据</param>
    /// <returns>压缩后的数据，如果原始数据小于阈值则返回原数据</returns>
    public Byte[] Compress(Byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (data.Length < Threshold) return data;

        using var output = new MemoryStream();

        // 写入 1 字节压缩标记（方便解压时判断是否压缩）
        output.WriteByte((Byte)Algorithm);

        // 写入原始长度（4 字节，用于预分配解压缓冲区）
        var lenBytes = BitConverter.GetBytes(data.Length);
        output.Write(lenBytes, 0, 4);

        // 压缩
        using (var compressStream = CreateCompressStream(output))
        {
            compressStream.Write(data, 0, data.Length);
        }

        var compressed = output.ToArray();

        // 如果压缩后反而变大，返回原数据
        if (compressed.Length >= data.Length) return data;

        return compressed;
    }

    /// <summary>解压数据</summary>
    /// <param name="data">待解压数据</param>
    /// <returns>解压后的原始数据</returns>
    public Byte[] Decompress(Byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (data.Length < 6) return data; // 太短，不可能是压缩数据

        // 检查第一个字节是否为压缩标记
        var algo = (CompressionAlgorithm)data[0];
        if (algo != CompressionAlgorithm.GZip && algo != CompressionAlgorithm.Deflate)
            return data; // 不是压缩数据，直接返回

        // 读取原始长度
        var originalLength = BitConverter.ToInt32(data, 1);
        if (originalLength <= 0 || originalLength > 128 * 1024 * 1024) // 最大 128MB
            return data;

        using var input = new MemoryStream(data, 5, data.Length - 5);
        using var decompressStream = CreateDecompressStream(input, algo);

        var result = new Byte[originalLength];
        var totalRead = 0;
        while (totalRead < originalLength)
        {
            var read = decompressStream.Read(result, totalRead, originalLength - totalRead);
            if (read <= 0) break;
            totalRead += read;
        }

        return result;
    }

    /// <summary>尝试压缩数据</summary>
    /// <param name="data">原始数据</param>
    /// <param name="compressed">压缩后的数据</param>
    /// <returns>是否实际进行了压缩</returns>
    public Boolean TryCompress(Byte[] data, out Byte[] compressed)
    {
        compressed = Compress(data);
        return !ReferenceEquals(compressed, data);
    }

    /// <summary>判断数据是否经过压缩</summary>
    /// <param name="data">待检测数据</param>
    /// <returns>是否为压缩数据</returns>
    public static Boolean IsCompressed(Byte[] data)
    {
        if (data == null || data.Length < 6) return false;
        var algo = (CompressionAlgorithm)data[0];
        return algo == CompressionAlgorithm.GZip || algo == CompressionAlgorithm.Deflate;
    }

    #region 辅助

    /// <summary>创建压缩流</summary>
    private Stream CreateCompressStream(Stream output)
    {
        return Algorithm switch
        {
            CompressionAlgorithm.GZip => new GZipStream(output, Level, leaveOpen: true),
            CompressionAlgorithm.Deflate => new DeflateStream(output, Level, leaveOpen: true),
            _ => throw new NotSupportedException($"Unsupported compression algorithm: {Algorithm}")
        };
    }

    /// <summary>创建解压流</summary>
    private static Stream CreateDecompressStream(Stream input, CompressionAlgorithm algo)
    {
        return algo switch
        {
            CompressionAlgorithm.GZip => new GZipStream(input, CompressionMode.Decompress, leaveOpen: true),
            CompressionAlgorithm.Deflate => new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true),
            _ => throw new NotSupportedException($"Unsupported compression algorithm: {algo}")
        };
    }

    #endregion
}

/// <summary>压缩算法</summary>
public enum CompressionAlgorithm : Byte
{
    /// <summary>不压缩</summary>
    None = 0,

    /// <summary>GZip 压缩</summary>
    GZip = 1,

    /// <summary>Deflate 压缩</summary>
    Deflate = 2
}
