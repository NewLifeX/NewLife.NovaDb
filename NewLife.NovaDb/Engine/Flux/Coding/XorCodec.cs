namespace NewLife.NovaDb.Engine.Flux.Coding;

/// <summary>Gorilla XOR 浮点编码器，适合缓变浮点序列（传感器读数、指标值）：与前一值异或后只写有效位区段</summary>
internal static class XorCodec
{
    /// <summary>单个浮点值的最大编码位数（1 位非零标记 + 1 位复用标记 + 5 位前导零 + 6 位有效长度 + 64 位数据）</summary>
    private const Int32 MaxBitsPerValue = 77;

    /// <summary>编码 Double 序列</summary>
    /// <param name="values">源序列</param>
    /// <returns>编码字节数组，空序列返回空数组</returns>
    public static Byte[] EncodeDouble(ReadOnlySpan<Double> values)
    {
        if (values.Length == 0) return [];

        var buffer = new Byte[8 + (values.Length * MaxBitsPerValue + 7) / 8];
        var writer = new BitWriter(buffer);

        var prevBits = BitConverter.DoubleToInt64Bits(values[0]);
        writer.WriteBits((UInt64)prevBits, 64);

        var prevLeading = -1;
        var prevTrailing = 0;
        for (var i = 1; i < values.Length; i++)
        {
            var bits = BitConverter.DoubleToInt64Bits(values[i]);
            var xor = (UInt64)(bits ^ prevBits);
            prevBits = bits;

            if (xor == 0)
            {
                writer.WriteBit(false);
                continue;
            }

            writer.WriteBit(true);
            var leading = CountLeadingZeros(xor);
            var trailing = CountTrailingZeros(xor);
            if (leading > 31) leading = 31;

            if (prevLeading >= 0 && leading >= prevLeading && trailing >= prevTrailing)
            {
                writer.WriteBit(false);
                var meaningful = 64 - prevLeading - prevTrailing;
                writer.WriteBits(xor >> prevTrailing, meaningful);
            }
            else
            {
                writer.WriteBit(true);
                writer.WriteBits((UInt64)leading, 5);
                var meaningful = 64 - leading - trailing;
                writer.WriteBits((UInt64)(meaningful - 1), 6);
                writer.WriteBits(xor >> trailing, meaningful);
                prevLeading = leading;
                prevTrailing = trailing;
            }
        }

        return buffer.AsSpan(0, writer.ByteLength).ToArray();
    }

    /// <summary>解码 Double 序列</summary>
    /// <param name="data">编码数据</param>
    /// <param name="count">元素个数</param>
    /// <returns>解码后的数组，个数为 0 时返回空数组</returns>
    public static Double[] DecodeDouble(ReadOnlySpan<Byte> data, Int32 count)
    {
        if (count <= 0) return [];

        var reader = new BitReader(data);
        var result = new Double[count];
        var prevBits = (Int64)reader.ReadBits(64);
        result[0] = BitConverter.Int64BitsToDouble(prevBits);

        var prevLeading = -1;
        var prevTrailing = 0;
        for (var i = 1; i < count; i++)
        {
            if (!reader.ReadBit())
            {
                result[i] = BitConverter.Int64BitsToDouble(prevBits);
                continue;
            }

            UInt64 xor;
            if (!reader.ReadBit())
            {
                var meaningful = 64 - prevLeading - prevTrailing;
                xor = reader.ReadBits(meaningful) << prevTrailing;
            }
            else
            {
                var leading = (Int32)reader.ReadBits(5);
                var meaningful = (Int32)reader.ReadBits(6) + 1;
                var trailing = 64 - leading - meaningful;
                xor = reader.ReadBits(meaningful) << trailing;
                prevLeading = leading;
                prevTrailing = trailing;
            }

            prevBits = (Int64)((UInt64)prevBits ^ xor);
            result[i] = BitConverter.Int64BitsToDouble(prevBits);
        }

        return result;
    }

    #region 辅助

    /// <summary>统计 64 位整数的前导零个数</summary>
    /// <param name="value">待统计数值，非零</param>
    /// <returns>前导零个数 0~63</returns>
    private static Int32 CountLeadingZeros(UInt64 value)
    {
        var count = 0;
        while ((value & 0x8000000000000000UL) == 0)
        {
            value <<= 1;
            count++;
        }

        return count;
    }

    /// <summary>统计 64 位整数的尾随零个数</summary>
    /// <param name="value">待统计数值，非零</param>
    /// <returns>尾随零个数 0~63</returns>
    private static Int32 CountTrailingZeros(UInt64 value)
    {
        var count = 0;
        while ((value & 1) == 0)
        {
            value >>= 1;
            count++;
        }

        return count;
    }

    #endregion

    #region 位写入器

    /// <summary>按位写入缓冲区的小工具，高位在前</summary>
    private struct BitWriter
    {
        private readonly Byte[] _buffer;
        private Int32 _bitPos;

        public BitWriter(Byte[] buffer)
        {
            _buffer = buffer;
            _bitPos = 0;
        }

        /// <summary>已写入字节数</summary>
        public readonly Int32 ByteLength => (_bitPos + 7) >> 3;

        /// <summary>写入一位</summary>
        /// <param name="value">位值</param>
        public void WriteBit(Boolean value)
        {
            if (value) _buffer[_bitPos >> 3] |= (Byte)(1 << (7 - (_bitPos & 7)));
            _bitPos++;
        }

        /// <summary>写入低位在后的指定位数</summary>
        /// <param name="value">数值</param>
        /// <param name="count">位数，最大 64</param>
        public void WriteBits(UInt64 value, Int32 count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                WriteBit((value >> i & 1) != 0);
            }
        }
    }

    #endregion

    #region 位读取器

    /// <summary>按位读取缓冲区的小工具，高位在前</summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<Byte> _buffer;
        private Int32 _bitPos;

        public BitReader(ReadOnlySpan<Byte> buffer)
        {
            _buffer = buffer;
            _bitPos = 0;
        }

        /// <summary>读取一位</summary>
        /// <returns>位值</returns>
        public Boolean ReadBit()
        {
            var value = (_buffer[_bitPos >> 3] & (1 << (7 - (_bitPos & 7)))) != 0;
            _bitPos++;
            return value;
        }

        /// <summary>读取指定位数，返回低位在后的数值</summary>
        /// <param name="count">位数，最大 64</param>
        /// <returns>读取到的数值</returns>
        public UInt64 ReadBits(Int32 count)
        {
            UInt64 value = 0;
            for (var i = 0; i < count; i++)
            {
                value = (value << 1) | (ReadBit() ? 1UL : 0UL);
            }

            return value;
        }
    }

    #endregion
}
