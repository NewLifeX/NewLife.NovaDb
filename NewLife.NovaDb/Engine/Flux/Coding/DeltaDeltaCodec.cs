namespace NewLife.NovaDb.Engine.Flux.Coding;

/// <summary>二阶差分编码器，适合等间隔时间戳：首值 + 首差分 + 后续二阶差分 ZigZag 变长写入，恒定间隔时每值约 1 字节</summary>
internal static class DeltaDeltaCodec
{
    /// <summary>编码 Int64 序列</summary>
    /// <param name="values">源序列</param>
    /// <returns>编码字节数组，空序列返回空数组</returns>
    public static Byte[] EncodeInt64(ReadOnlySpan<Int64> values)
    {
        if (values.Length == 0) return [];

        var size = VarInt.GetSize(ZigZag.Encode(values[0]));
        if (values.Length > 1) size += VarInt.GetSize(ZigZag.Encode(values[1] - values[0]));
        for (var i = 2; i < values.Length; i++)
        {
            var dd = (values[i] - values[i - 1]) - (values[i - 1] - values[i - 2]);
            size += VarInt.GetSize(ZigZag.Encode(dd));
        }

        var buffer = new Byte[size];
        var pos = 0;
        pos += VarInt.Write(buffer.AsSpan(pos), ZigZag.Encode(values[0]));
        if (values.Length > 1) pos += VarInt.Write(buffer.AsSpan(pos), ZigZag.Encode(values[1] - values[0]));
        for (var i = 2; i < values.Length; i++)
        {
            var dd = (values[i] - values[i - 1]) - (values[i - 1] - values[i - 2]);
            pos += VarInt.Write(buffer.AsSpan(pos), ZigZag.Encode(dd));
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
        if (count == 1) return result;

        var delta = ZigZag.Decode(VarInt.Read(data, ref pos));
        current += delta;
        result[1] = current;
        for (var i = 2; i < count; i++)
        {
            delta += ZigZag.Decode(VarInt.Read(data, ref pos));
            current += delta;
            result[i] = current;
        }

        return result;
    }
}
