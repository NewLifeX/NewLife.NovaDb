namespace NewLife.NovaDb.Engine.Flux.Coding;

/// <summary>一阶差分编码器，适合递增时间戳与缓变整数序列：首值绝对值 + 后续差分 ZigZag 变长写入</summary>
internal static class DeltaCodec
{
    /// <summary>编码 Int64 序列</summary>
    /// <param name="values">源序列</param>
    /// <returns>编码字节数组，空序列返回空数组</returns>
    public static Byte[] EncodeInt64(ReadOnlySpan<Int64> values)
    {
        if (values.Length == 0) return [];

        var size = VarInt.GetSize(ZigZag.Encode(values[0]));
        for (var i = 1; i < values.Length; i++)
        {
            size += VarInt.GetSize(ZigZag.Encode(values[i] - values[i - 1]));
        }

        var buffer = new Byte[size];
        var pos = 0;
        pos += VarInt.Write(buffer.AsSpan(pos), ZigZag.Encode(values[0]));
        for (var i = 1; i < values.Length; i++)
        {
            pos += VarInt.Write(buffer.AsSpan(pos), ZigZag.Encode(values[i] - values[i - 1]));
        }

        return buffer;
    }

    /// <summary>解码 Int64 序列</summary>
    /// <param name="data">编码数据</param>
    /// <param name="count">元素个数</param>
    /// <returns>解码后的数组，个数为 0 时返回空数组</returns>
    public static Int64[] DecodeInt64(ReadOnlySpan<Byte> data, Int32 count)
    {
        if (count <= 0) return [];

        var result = new Int64[count];
        var pos = 0;
        var current = ZigZag.Decode(VarInt.Read(data, ref pos));
        result[0] = current;
        for (var i = 1; i < count; i++)
        {
            current += ZigZag.Decode(VarInt.Read(data, ref pos));
            result[i] = current;
        }

        return result;
    }
}
