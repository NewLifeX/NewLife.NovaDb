namespace NewLife.NovaDb.Core;

/// <summary>数据值编解码器接口</summary>
public interface IDataCodec
{
    /// <summary>编码值到二进制</summary>
    /// <param name="value">要编码的值</param>
    /// <param name="dataType">数据类型</param>
    /// <returns>编码后的字节数组</returns>
    Byte[] Encode(Object? value, DataType dataType);

    /// <summary>从二进制解码值</summary>
    /// <param name="buffer">字节数组</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="dataType">数据类型</param>
    /// <returns>解码后的值</returns>
    Object? Decode(Byte[] buffer, Int32 offset, DataType dataType);

    /// <summary>获取编码后的长度</summary>
    /// <param name="value">要编码的值</param>
    /// <param name="dataType">数据类型</param>
    /// <returns>编码后的字节长度</returns>
    Int32 GetEncodedLength(Object? value, DataType dataType);
}

/// <summary>默认数据编解码器实现</summary>
public class DefaultDataCodec : IDataCodec
{
    /// <summary>NULL 标记字节</summary>
    private const Byte NullFlag = 0x00;

    /// <summary>非 NULL 标记字节</summary>
    private const Byte NotNullFlag = 0x01;

    /// <summary>编码值到二进制</summary>
    /// <param name="value">要编码的值</param>
    /// <param name="dataType">数据类型</param>
    /// <returns>编码后的字节数组</returns>
    public Byte[] Encode(Object? value, DataType dataType)
    {
        if (value == null)
            return [NullFlag];

        try
        {
            return dataType switch
            {
                DataType.Boolean => BitConverter.GetBytes((Boolean)value),
                DataType.Int32 => BitConverter.GetBytes((Int32)value),
                DataType.Int64 => BitConverter.GetBytes((Int64)value),
                DataType.Double => BitConverter.GetBytes((Double)value),
                DataType.Decimal => EncodeDecimal((Decimal)value),
                DataType.String => EncodeString((String)value),
                DataType.Binary => EncodeByteArray((Byte[])value),
                DataType.DateTime => BitConverter.GetBytes(((DateTime)value).Ticks),
                DataType.GeoPoint => EncodeGeoPoint((GeoPoint)value),
                DataType.Vector => EncodeVector((Single[])value),
                _ => throw new NotSupportedException($"Unsupported data type: {dataType}")
            };
        }
        catch (InvalidCastException ex)
        {
            throw new NovaException(
                ErrorCode.InvalidArgument,
                $"Cannot encode value of type {value.GetType().Name} as {dataType}",
                ex
            );
        }
    }

    /// <summary>从二进制解码值</summary>
    /// <param name="buffer">字节数组</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="dataType">数据类型</param>
    /// <returns>解码后的值</returns>
    public Object? Decode(Byte[] buffer, Int32 offset, DataType dataType)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");
        if (offset >= buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset exceeds buffer length");

        // 检查 NULL 标记（单字节 0x00 表示 NULL）
        if (buffer[offset] == NullFlag && buffer.Length - offset == 1)
            return null;

        try
        {
            return dataType switch
            {
                DataType.Boolean => DecodeBoolean(buffer, offset),
                DataType.Int32 => DecodeInt32(buffer, offset),
                DataType.Int64 => DecodeInt64(buffer, offset),
                DataType.Double => DecodeDouble(buffer, offset),
                DataType.Decimal => DecodeDecimal(buffer, offset),
                DataType.String => DecodeString(buffer, offset),
                DataType.Binary => DecodeByteArray(buffer, offset),
                DataType.DateTime => DecodeDateTime(buffer, offset),
                DataType.GeoPoint => DecodeGeoPoint(buffer, offset),
                DataType.Vector => DecodeVector(buffer, offset),
                _ => throw new NotSupportedException($"Unsupported data type: {dataType}")
            };
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not NotSupportedException)
        {
            throw new NovaException(
                ErrorCode.ParseFailed,
                $"Failed to decode {dataType} at offset {offset}",
                ex
            );
        }
    }

    /// <summary>获取编码后的长度</summary>
    /// <param name="value">要编码的值</param>
    /// <param name="dataType">数据类型</param>
    /// <returns>编码后的字节长度</returns>
    public Int32 GetEncodedLength(Object? value, DataType dataType)
    {
        if (value == null)
        {
            return 1; // NULL 标记单字节
        }

        return dataType switch
        {
            DataType.Boolean => 1,
            DataType.Int32 => 4,
            DataType.Int64 => 8,
            DataType.Double => 8,
            DataType.Decimal => 16, // 128-bit
            DataType.String => 4 + System.Text.Encoding.UTF8.GetByteCount((String)value), // 长度前缀 + UTF-8
            DataType.Binary => 4 + ((Byte[])value).Length, // 长度前缀 + 数据
            DataType.DateTime => 8, // Ticks
            DataType.GeoPoint => 16, // 2 × Double
            DataType.Vector => 4 + ((Single[])value).Length * 4, // 长度前缀 + Single[]
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

    private Boolean DecodeBoolean(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 1)
            throw new ArgumentException($"Buffer too short to read Boolean (need {offset + 1} bytes, got {buffer.Length})");
        return BitConverter.ToBoolean(buffer, offset);
    }

    private Int32 DecodeInt32(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 4)
            throw new ArgumentException($"Buffer too short to read Int32 (need {offset + 4} bytes, got {buffer.Length})");
        return BitConverter.ToInt32(buffer, offset);
    }

    private Int64 DecodeInt64(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 8)
            throw new ArgumentException($"Buffer too short to read Int64 (need {offset + 8} bytes, got {buffer.Length})");
        return BitConverter.ToInt64(buffer, offset);
    }

    private Double DecodeDouble(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 8)
            throw new ArgumentException($"Buffer too short to read Double (need {offset + 8} bytes, got {buffer.Length})");
        return BitConverter.ToDouble(buffer, offset);
    }

    private DateTime DecodeDateTime(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 8)
            throw new ArgumentException($"Buffer too short to read DateTime (need {offset + 8} bytes, got {buffer.Length})");
        return new DateTime(BitConverter.ToInt64(buffer, offset));
    }

    private Decimal DecodeDecimal(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 16)
            throw new ArgumentException($"Buffer too short to read Decimal (need {offset + 16} bytes, got {buffer.Length})");
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

    private Byte[] EncodeGeoPoint(GeoPoint value)
    {
        var buffer = new Byte[16];
        Buffer.BlockCopy(BitConverter.GetBytes(value.Latitude), 0, buffer, 0, 8);
        Buffer.BlockCopy(BitConverter.GetBytes(value.Longitude), 0, buffer, 8, 8);
        return buffer;
    }

    private GeoPoint DecodeGeoPoint(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 16)
            throw new ArgumentException($"Buffer too short to read GeoPoint (need {offset + 16} bytes, got {buffer.Length})");
        var lat = BitConverter.ToDouble(buffer, offset);
        var lon = BitConverter.ToDouble(buffer, offset + 8);
        return new GeoPoint(lat, lon);
    }

    private Byte[] EncodeVector(Single[] value)
    {
        var buffer = new Byte[4 + value.Length * 4];
        Buffer.BlockCopy(BitConverter.GetBytes(value.Length), 0, buffer, 0, 4);
        Buffer.BlockCopy(value, 0, buffer, 4, value.Length * 4);
        return buffer;
    }

    private Single[] DecodeVector(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 4)
            throw new ArgumentException($"Buffer too short to read Vector length (need {offset + 4} bytes, got {buffer.Length})");
        var length = BitConverter.ToInt32(buffer, offset);
        if (length < 0)
            throw new ArgumentException($"Invalid vector length: {length}");
        if (buffer.Length < offset + 4 + length * 4)
            throw new ArgumentException($"Buffer too short to read Vector (need {offset + 4 + length * 4} bytes, got {buffer.Length})");
        var result = new Single[length];
        Buffer.BlockCopy(buffer, offset + 4, result, 0, length * 4);
        return result;
    }

    private Byte[] DecodeByteArray(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 4)
            throw new ArgumentException($"Buffer too short to read ByteArray length (need {offset + 4} bytes, got {buffer.Length})");

        var length = BitConverter.ToInt32(buffer, offset);
        if (length < 0)
            throw new ArgumentException($"Invalid byte array length: {length}");
        if (buffer.Length < offset + 4 + length)
            throw new ArgumentException($"Buffer too short to read ByteArray (need {offset + 4 + length} bytes, got {buffer.Length})");

        var result = new Byte[length];
        Buffer.BlockCopy(buffer, offset + 4, result, 0, length);
        return result;
    }
}
