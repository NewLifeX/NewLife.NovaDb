using System.Buffers;

namespace NewLife.NovaDb.Utilities;

/// <summary>流读取辅助扩展方法</summary>
internal static class StreamHelper
{
    /// <summary>从流中读取一个"4 字节小端长度前缀 + 数据体"的记录块到池化缓冲区</summary>
    /// <remarks>
    /// 读取流程：
    /// 1. 读取 4 字节长度前缀到 buf[0..4] 并解析为 Int32
    /// 2. 验证长度范围（0 &lt; length ≤ maxLength）
    /// 3. 若缓冲区空间不足，自动归还旧缓冲区并从 ArrayPool 租借更大的缓冲区
    /// 4. 读取 length 字节数据到 buf[0..length]（覆盖前 4 字节）
    ///
    /// 调用方负责在循环外 Rent 初始缓冲区，循环结束后 Return。
    /// </remarks>
    /// <param name="stream">源数据流</param>
    /// <param name="buf">池化缓冲区引用，空间不足时自动扩容（归还旧缓冲区并租借新缓冲区）</param>
    /// <param name="length">实际读取的数据体长度（不含 4 字节前缀）</param>
    /// <param name="maxLength">允许的最大长度，超过视为数据损坏，默认 10MB</param>
    /// <returns>是否成功读取完整记录</returns>
    public static Boolean TryReadLengthPrefixedBlock(this Stream stream, ref Byte[] buf, out Int32 length, Int32 maxLength = 10 * 1024 * 1024)
    {
        length = 0;

        // 读取 4 字节长度前缀
        if (stream.ReadAtLeast(buf, 0, 4, 4, false) != 4) return false;
        length = BitConverter.ToInt32(buf, 0);
        if (length <= 0 || length > maxLength)
        {
            length = 0;
            return false;
        }

        // 确保缓冲区足够大
        if (buf.Length < length)
        {
            ArrayPool<Byte>.Shared.Return(buf);
            buf = ArrayPool<Byte>.Shared.Rent(length);
        }

        // 读取数据体
        if (stream.Read(buf, 0, length) != length)
        {
            length = 0;
            return false;
        }

        return true;
    }
}
