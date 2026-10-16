namespace NewLife.NovaDb.Engine.Flux.Coding;

/// <summary>ZigZag 有符号整数压缩映射，让小绝对值的负数也能用少量字节表示</summary>
internal static class ZigZag
{
    /// <summary>将有符号整数映射为无符号整数</summary>
    /// <param name="value">有符号数值</param>
    /// <returns>映射后的无符号数值</returns>
    public static UInt64 Encode(Int64 value) => (UInt64)((value >> 63) ^ (value << 1));

    /// <summary>将无符号整数还原为有符号整数</summary>
    /// <param name="value">映射后的无符号数值</param>
    /// <returns>原始有符号数值</returns>
    public static Int64 Decode(UInt64 value) => (Int64)(value >> 1) ^ -(Int64)(value & 1);
}
