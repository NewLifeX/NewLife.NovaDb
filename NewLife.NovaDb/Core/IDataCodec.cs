namespace NewLife.NovaDb.Core;

/// <summary>
/// 数据值编解码器接口
/// </summary>
public interface IDataCodec
{
    /// <summary>
    /// 编码值到二进制
    /// </summary>
    /// <param name="value">要编码的值</param>
    /// <param name="dataType">数据类型</param>
    /// <returns>编码后的字节数组</returns>
    Byte[] Encode(Object? value, DataType dataType);

    /// <summary>
    /// 从二进制解码值
    /// </summary>
    /// <param name="buffer">字节数组</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="dataType">数据类型</param>
    /// <returns>解码后的值</returns>
    Object? Decode(Byte[] buffer, Int32 offset, DataType dataType);

    /// <summary>
    /// 获取编码后的长度
    /// </summary>
    /// <param name="value">要编码的值</param>
    /// <param name="dataType">数据类型</param>
    /// <returns>编码后的字节长度</returns>
    Int32 GetEncodedLength(Object? value, DataType dataType);
}

/// <summary>
/// 默认数据编解码器实现
/// </summary>
public class DefaultDataCodec : IDataCodec
{
    public Byte[] Encode(Object? value, DataType dataType)
    {
        if (value == null)
        {
            // NULL 值标记为长度 -1
            return BitConverter.GetBytes(-1);
        }

        return dataType switch
        {
            DataType.Boolean => BitConverter.GetBytes((Boolean)value),
            DataType.Int32 => BitConverter.GetBytes((Int32)value),
            DataType.Int64 => BitConverter.GetBytes((Int64)value),
            DataType.Double => BitConverter.GetBytes((Double)value),
            DataType.Decimal => EncodeDecimal((Decimal)value),
            DataType.String => EncodeString((String)value),
            DataType.ByteArray => EncodeByteArray((Byte[])value),
            DataType.DateTime => BitConverter.GetBytes(((DateTime)value).Ticks),
            _ => throw new NotSupportedException($"Unsupported data type: {dataType}")
        };
    }

    public Object? Decode(Byte[] buffer, Int32 offset, DataType dataType)
    {
        // 检查是否为 NULL
        if (buffer.Length >= offset + 4)
        {
            var length = BitConverter.ToInt32(buffer, offset);
            if (length == -1)
            {
                return null;
            }
        }

        return dataType switch
        {
            DataType.Boolean => BitConverter.ToBoolean(buffer, offset),
            DataType.Int32 => BitConverter.ToInt32(buffer, offset),
            DataType.Int64 => BitConverter.ToInt64(buffer, offset),
            DataType.Double => BitConverter.ToDouble(buffer, offset),
            DataType.Decimal => DecodeDecimal(buffer, offset),
            DataType.String => DecodeString(buffer, offset),
            DataType.ByteArray => DecodeByteArray(buffer, offset),
            DataType.DateTime => new DateTime(BitConverter.ToInt64(buffer, offset)),
            _ => throw new NotSupportedException($"Unsupported data type: {dataType}")
        };
    }

    public Int32 GetEncodedLength(Object? value, DataType dataType)
    {
        if (value == null)
        {
            return 4; // -1 标记
        }

        return dataType switch
        {
            DataType.Boolean => 1,
            DataType.Int32 => 4,
            DataType.Int64 => 8,
            DataType.Double => 8,
            DataType.Decimal => 16, // 128-bit
            DataType.String => 4 + System.Text.Encoding.UTF8.GetByteCount((String)value), // 长度前缀 + UTF-8
            DataType.ByteArray => 4 + ((Byte[])value).Length, // 长度前缀 + 数据
            DataType.DateTime => 8, // Ticks
            _ => throw new NotSupportedException($"Unsupported data type: {dataType}")
        };
    }

    private Byte[] EncodeDecimal(Decimal value)
    {
        var bits = Decimal.GetBits(value);
        var buffer = new Byte[16];
        Buffer.BlockCopy(bits, 0, buffer, 0, 16);
        return buffer;
    }

    private Decimal DecodeDecimal(Byte[] buffer, Int32 offset)
    {
        var bits = new Int32[4];
        Buffer.BlockCopy(buffer, offset, bits, 0, 16);
        return new Decimal(bits);
    }

    private Byte[] EncodeString(String value)
    {
        var utf8Bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var buffer = new Byte[4 + utf8Bytes.Length];
        Buffer.BlockCopy(BitConverter.GetBytes(utf8Bytes.Length), 0, buffer, 0, 4);
        Buffer.BlockCopy(utf8Bytes, 0, buffer, 4, utf8Bytes.Length);
        return buffer;
    }

    private String DecodeString(Byte[] buffer, Int32 offset)
    {
        var length = BitConverter.ToInt32(buffer, offset);
        return System.Text.Encoding.UTF8.GetString(buffer, offset + 4, length);
    }

    private Byte[] EncodeByteArray(Byte[] value)
    {
        var buffer = new Byte[4 + value.Length];
        Buffer.BlockCopy(BitConverter.GetBytes(value.Length), 0, buffer, 0, 4);
        Buffer.BlockCopy(value, 0, buffer, 4, value.Length);
        return buffer;
    }

    private Byte[] DecodeByteArray(Byte[] buffer, Int32 offset)
    {
        var length = BitConverter.ToInt32(buffer, offset);
        var result = new Byte[length];
        Buffer.BlockCopy(buffer, offset + 4, result, 0, length);
        return result;
    }
}
