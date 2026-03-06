using System.Buffers;
using System.Text;

namespace NewLife.NovaDb.Utilities;

/// <summary>
/// 提供字符串与 UTF-8 编码字节数组之间的转换扩展方法，使用对象池管理字节数组以提高性能。
/// </summary>
internal static class EncodingExtensions
{
    private static readonly Encoding Encoding = Encoding.UTF8;

    /// <summary>
    /// 将字符串转换为使用对象池管理的 UTF-8 编码字节数组。
    /// </summary>
    /// <param name="value">要转换的字符串。</param>
    /// <returns>返回一个 <see cref="PooledBytes"/> 实例，包含 UTF-8 编码的字节数组。</returns>
    public static PooledBytes ToPooledUtf8Bytes(this String value) => Encoding.GetPooledEncodedBytes(value);

    /// <summary>
    /// 将字符串转换为使用对象池管理的指定编码的字节数组。
    /// </summary>
    /// <param name="encoding">要使用的编码。</param>
    /// <param name="value">要转换的字符串。</param>
    /// <returns>返回一个 <see cref="PooledBytes"/> 实例，包含指定编码的字节数组。</returns>
    public static PooledBytes GetPooledEncodedBytes(this Encoding encoding, String value)
    {
        var length = encoding.GetByteCount(value);
        var pooledBytes = ArrayPool<Byte>.Shared.Rent(length);
        encoding.GetBytes(value, 0, value.Length, pooledBytes, 0);
        return new PooledBytes(pooledBytes, length);
    }
}