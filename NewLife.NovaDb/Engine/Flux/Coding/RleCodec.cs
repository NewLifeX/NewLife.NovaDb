namespace NewLife.NovaDb.Engine.Flux.Coding;

/// <summary>游程长度编码器（RLE），适合大量重复的整数与布尔序列：(数值, 重复次数) 成对写入</summary>
internal static class RleCodec
{
    /// <summary>编码 Int64 序列</summary>
    /// <param name="values">源序列</param>
    /// <returns>编码字节数组，空序列返回空数组</returns>
    public static Byte[] EncodeInt64(ReadOnlySpan<Int64> values)
    {
        if (values.Length == 0) return [];

        var size = 0;
        var i = 0;
        while (i < values.Length)
        {
            var v = values[i];
            var run = 1;
            while (i + run < values.Length && values[i + run] == v) run++;

            size += VarInt.GetSize(ZigZag.Encode(v)) + VarInt.GetSize((UInt64)run);
            i += run;
        }

        var buffer = new Byte[size];
        var pos = 0;
        i = 0;
        while (i < values.Length)
        {
            var v = values[i];
            var run = 1;
            while (i + run < values.Length && values[i + run] == v) run++;

            pos += VarInt.Write(buffer.AsSpan(pos), ZigZag.Encode(v));
            pos += VarInt.Write(buffer.AsSpan(pos), (UInt64)run);
            i += run;
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
        var written = 0;
        while (written < count)
        {
            var value = ZigZag.Decode(VarInt.Read(data, ref pos));
            var run = (Int32)VarInt.Read(data, ref pos);
            for (var i = 0; i < run && written < count; i++)
            {
                result[written++] = value;
            }
        }

        return result;
    }

    /// <summary>编码布尔序列</summary>
    /// <param name="values">源序列</param>
    /// <returns>编码字节数组，空序列返回空数组</returns>
    public static Byte[] EncodeBoolean(ReadOnlySpan<Boolean> values)
    {
        if (values.Length == 0) return [];

        var size = 0;
        var i = 0;
        while (i < values.Length)
        {
            var v = values[i];
            var run = 1;
            while (i + run < values.Length && values[i + run] == v) run++;

            size += 1 + VarInt.GetSize((UInt64)run);
            i += run;
        }

        var buffer = new Byte[size];
        var pos = 0;
        i = 0;
        while (i < values.Length)
        {
            var v = values[i];
            var run = 1;
            while (i + run < values.Length && values[i + run] == v) run++;

            buffer[pos++] = v ? (Byte)1 : (Byte)0;
            pos += VarInt.Write(buffer.AsSpan(pos), (UInt64)run);
            i += run;
        }

        return buffer;
    }

    /// <summary>解码布尔序列</summary>
    /// <param name="data">编码数据</param>
    /// <param name="count">元素个数</param>
    /// <returns>解码后的数组，个数为 0 时返回空数组</returns>
    public static Boolean[] DecodeBoolean(ReadOnlySpan<Byte> data, Int32 count)
    {
        if (count <= 0) return [];

        var result = new Boolean[count];
        var pos = 0;
        var written = 0;
        while (written < count)
        {
            var value = data[pos++] != 0;
            var run = (Int32)VarInt.Read(data, ref pos);
            for (var i = 0; i < run && written < count; i++)
            {
                result[written++] = value;
            }
        }

        return result;
    }
}
