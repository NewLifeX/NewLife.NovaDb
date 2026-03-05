using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace NewLife.NovaDb.Core;

/// <summary>数据值编解码器接口</summary>
public interface IDataCodec
{
    /// <summary>编码值到二进制</summary>
    /// <param name="value">要编码的值</param>
    /// <param name="dataType">数据类型</param>
    /// <returns>编码后的字节数组</returns>
    Byte[] Encode(Object? value, DataType dataType);

    /// <summary>编码值到二进制</summary>
    /// <param name="value">要编码的值</param>
    /// <param name="dataType">数据类型</param>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="offset">起始偏移</param>
    /// <returns>编码后的字节长度</returns>
    Int32 Encode(Object? value, DataType dataType, Byte[] buffer, Int32 offset);

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

    private static readonly Encoding _encoding = Encoding.UTF8;

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

    /// <summary>编码值到二进制</summary>
    /// <param name="value">要编码的值</param>
    /// <param name="dataType">数据类型</param>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="offset">起始偏移</param>
    /// <returns>编码后的字节长度</returns>
    public Int32 Encode(Object? value, DataType dataType, Byte[] buffer, Int32 offset)
    {
        if (value == null)
        {
            if (buffer.Length < offset + 1)
                throw new ArgumentException($"Buffer too short to encode NULL (need {offset + 1} bytes, got {buffer.Length})");
            buffer[offset] = NullFlag;
            return 1;
        }

        try
        {
            switch (dataType)
            {
                case DataType.Boolean:
                    {
                        if (buffer.Length < offset + 1)
                            throw new ArgumentException($"Buffer too short to encode Boolean (need {offset + 1} bytes, got {buffer.Length})");
                        buffer[offset] = (Boolean)value ? ((Byte)1) : ((Byte)0);
                        return 1;
                    }
                case DataType.Int32:
                    {
                        if (buffer.Length < offset + 4)
                            throw new ArgumentException($"Buffer too short to encode Int32 (need {offset + 4} bytes, got {buffer.Length})");
                        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(buffer.AsSpan(offset)), (Int32)value);
                        return 4;
                    }
                case DataType.Int64:
                    {
                        if (buffer.Length < offset + 8)
                            throw new ArgumentException($"Buffer too short to encode Int64 (need {offset + 8} bytes, got {buffer.Length})");
                        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(buffer.AsSpan(offset)), (Int64)value);
                        return 8;
                    }
                case DataType.Double:
                    {
                        if (buffer.Length < offset + 8)
                            throw new ArgumentException($"Buffer too short to encode Double (need {offset + 8} bytes, got {buffer.Length})");
                        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(buffer.AsSpan(offset)), (Double)value);
                        return 8;
                    }
                case DataType.Decimal:
                    return EncodeDecimal((Decimal)value, buffer, offset);
                case DataType.String:
                    return EncodeString((String)value, buffer, offset);
                case DataType.Binary:
                    return EncodeByteArray((Byte[])value, buffer, offset);
                case DataType.DateTime:
                    {
                        if (buffer.Length < offset + 8)
                            throw new ArgumentException($"Buffer too short to encode Int64 (need {offset + 8} bytes, got {buffer.Length})");
                        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(buffer.AsSpan(offset)), ((DateTime)value).Ticks);
                        return 8;
                    }
                case DataType.GeoPoint:
                    return EncodeGeoPoint((GeoPoint)value, buffer, offset);
                case DataType.Vector:
                    return EncodeVector((Single[])value, buffer, offset);
                default:
                    throw new NotSupportedException($"Unsupported data type: {dataType}");
            }
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
            DataType.String => 4 + _encoding.GetByteCount((String)value), // 长度前缀 + UTF-8
            DataType.Binary => 4 + ((Byte[])value).Length, // 长度前缀 + 数据
            DataType.DateTime => 8, // Ticks
            DataType.GeoPoint => 16, // 2 × Double
            DataType.Vector => 4 + ((Single[])value).Length * 4, // 长度前缀 + Single[]
            _ => throw new NotSupportedException($"Unsupported data type: {dataType}")
        };
    }

    private static Byte[] EncodeDecimal(Decimal value)
    {
        var buffer = new Byte[16];

#if NET5_0_OR_GREATER
        Span<Int32> bits = stackalloc Int32[4];
        Decimal.GetBits(value, bits);

        // 把 4 个 int 的原始 16 字节拷贝到 byte[16]
        MemoryMarshal.AsBytes(bits).CopyTo(buffer);
#else
        var bits = Decimal.GetBits(value);
        Buffer.BlockCopy(bits, 0, buffer, 0, 16);
#endif

        return buffer;
    }

    private static Int32 EncodeDecimal(Decimal value, Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 16)
            throw new ArgumentException($"Buffer too short to encode Decimal (need {offset + 16} bytes, got {buffer.Length})");

#if NET5_0_OR_GREATER
        Span<Int32> bits = stackalloc Int32[4];
        Decimal.GetBits(value, bits);

        // 16 bytes 写入目标 buffer
        MemoryMarshal.AsBytes(bits).CopyTo(buffer.AsSpan(offset, 16));
#else
        var bits = Decimal.GetBits(value);
        Buffer.BlockCopy(bits, 0, buffer, offset, 16);
#endif

        return 16;
    }

    private static Byte[] EncodeString(String value)
    {
        var valueBytesLength = _encoding.GetByteCount(value);
        var buffer = new Byte[4 + valueBytesLength];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), valueBytesLength);
        _encoding.GetBytes(value, buffer.AsSpan(4));
        return buffer;
    }

    private static Int32 EncodeString(String value, Byte[] buffer, Int32 offset)
    {
        var valueBytesLength = _encoding.GetByteCount(value);
        if (buffer.Length < offset + 4 + valueBytesLength)
            throw new ArgumentException($"Buffer too short to encode String (need {offset + 4 + valueBytesLength} bytes, got {buffer.Length})");

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), valueBytesLength);
        _encoding.GetBytes(value, buffer.AsSpan(offset + 4));
        return 4 + valueBytesLength;
    }

    private static Byte[] EncodeByteArray(Byte[] value)
    {
        var buffer = new Byte[4 + value.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), value.Length);
        value.AsSpan().CopyTo(buffer.AsSpan(4));
        return buffer;
    }

    private static Int32 EncodeByteArray(Byte[] value, Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 4 + value.Length)
            throw new ArgumentException($"Buffer too short to encode ByteArray (need {offset + 4 + value.Length} bytes, got {buffer.Length})");

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), value.Length);
        value.AsSpan().CopyTo(buffer.AsSpan(offset + 4));
        return 4 + value.Length;
    }

    private static Byte[] EncodeGeoPoint(GeoPoint value)
    {
        var buffer = new Byte[16];
        WriteDoubleLittleEndian(buffer.AsSpan(0, 8), value.Latitude);
        WriteDoubleLittleEndian(buffer.AsSpan(8, 8), value.Longitude);
        return buffer;
    }

    private static Int32 EncodeGeoPoint(GeoPoint value, Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 16)
            throw new ArgumentException($"Buffer too short to encode GeoPoint (need {offset + 16} bytes, got {buffer.Length})");

        WriteDoubleLittleEndian(buffer.AsSpan(offset, 8), value.Latitude);
        WriteDoubleLittleEndian(buffer.AsSpan(offset + 8, 8), value.Longitude);
        return 16;
    }

    private static Byte[] EncodeVector(Single[] value)
    {
        var byteLen = checked(value.Length * sizeof(Single));
        var buffer = new Byte[sizeof(Int32) + byteLen];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, sizeof(Int32)), value.Length);
        ReadOnlySpan<Byte> srcBytes = MemoryMarshal.AsBytes(value.AsSpan());
        srcBytes.CopyTo(buffer.AsSpan(sizeof(Int32)));
        return buffer;
    }

    private static Int32 EncodeVector(Single[] value, Byte[] buffer, Int32 offset)
    {
        var byteLen = checked(value.Length * sizeof(Single));
        if (buffer.Length < offset + sizeof(Int32) + byteLen)
            throw new ArgumentException($"Buffer too short to encode Vector (need {offset + sizeof(Int32) + byteLen} bytes, got {buffer.Length})");

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, sizeof(Int32)), value.Length);
        ReadOnlySpan<Byte> srcBytes = MemoryMarshal.AsBytes(value.AsSpan());
        srcBytes.CopyTo(buffer.AsSpan(offset + sizeof(Int32)));
        return sizeof(Int32) + byteLen;
    }

    private static Boolean DecodeBoolean(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 1)
            throw new ArgumentException($"Buffer too short to read Boolean (need {offset + 1} bytes, got {buffer.Length})");
        return BitConverter.ToBoolean(buffer, offset);
    }

    private static Int32 DecodeInt32(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 4)
            throw new ArgumentException($"Buffer too short to read Int32 (need {offset + 4} bytes, got {buffer.Length})");
        return BitConverter.ToInt32(buffer, offset);
    }

    private static Int64 DecodeInt64(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 8)
            throw new ArgumentException($"Buffer too short to read Int64 (need {offset + 8} bytes, got {buffer.Length})");
        return BitConverter.ToInt64(buffer, offset);
    }

    private static Double DecodeDouble(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 8)
            throw new ArgumentException($"Buffer too short to read Double (need {offset + 8} bytes, got {buffer.Length})");
        return BitConverter.ToDouble(buffer, offset);
    }

    private static DateTime DecodeDateTime(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 8)
            throw new ArgumentException($"Buffer too short to read DateTime (need {offset + 8} bytes, got {buffer.Length})");
        return new DateTime(BitConverter.ToInt64(buffer, offset));
    }

    private static Decimal DecodeDecimal(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 16)
            throw new ArgumentException($"Buffer too short to read Decimal (need {offset + 16} bytes, got {buffer.Length})");
        var bits = new Int32[4];
        Buffer.BlockCopy(buffer, offset, bits, 0, 16);
        return new Decimal(bits);
    }

    private static String DecodeString(Byte[] buffer, Int32 offset)
    {
        var length = BitConverter.ToInt32(buffer, offset);
        return _encoding.GetString(buffer, offset + 4, length);
    }

    private static GeoPoint DecodeGeoPoint(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 16)
            throw new ArgumentException($"Buffer too short to read GeoPoint (need {offset + 16} bytes, got {buffer.Length})");
        var lat = BitConverter.ToDouble(buffer, offset);
        var lon = BitConverter.ToDouble(buffer, offset + 8);
        return new GeoPoint(lat, lon);
    }

    private static Single[] DecodeVector(Byte[] buffer, Int32 offset)
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

    private static Byte[] DecodeByteArray(Byte[] buffer, Int32 offset)
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

    private static void WriteDoubleLittleEndian(Span<Byte> destination, Double value)
    {
#if NET6_0_OR_GREATER
        BinaryPrimitives.WriteDoubleLittleEndian(destination, value);
#else
        var bits = BitConverter.DoubleToInt64Bits(value);
        BinaryPrimitives.WriteInt64LittleEndian(destination, bits);
#endif
    }
}
