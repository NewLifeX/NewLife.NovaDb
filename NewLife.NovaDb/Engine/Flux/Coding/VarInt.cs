namespace NewLife.NovaDb.Engine.Flux.Coding;

/// <summary>变长整数（LEB128）编解码工具</summary>
internal static class VarInt
{
    /// <summary>获取无符号变长整数编码后的字节数</summary>
    /// <param name="value">待编码数值</param>
    /// <returns>字节数</returns>
    public static Int32 GetSize(UInt64 value)
    {
        var size = 1;
        while (value >= 0x80)
        {
            size++;
            value >>= 7;
        }

        return size;
    }

    /// <summary>写入无符号变长整数</summary>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="value">待写入数值</param>
    /// <returns>写入字节数</returns>
    public static Int32 Write(Span<Byte> buffer, UInt64 value)
    {
        var pos = 0;
        while (value >= 0x80)
        {
            buffer[pos++] = (Byte)(value | 0x80);
            value >>= 7;
        }

        buffer[pos++] = (Byte)value;
        return pos;
    }

    /// <summary>从缓冲区读取无符号变长整数</summary>
    /// <param name="buffer">源缓冲区</param>
    /// <param name="position">读取位置，读取后自动前进</param>
    /// <returns>读取到的数值</returns>
    public static UInt64 Read(ReadOnlySpan<Byte> buffer, ref Int32 position)
    {
        var shift = 0;
        UInt64 value = 0;
        while (true)
        {
            var b = buffer[position++];
            value |= (UInt64)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;

            shift += 7;
        }

        return value;
    }
}
