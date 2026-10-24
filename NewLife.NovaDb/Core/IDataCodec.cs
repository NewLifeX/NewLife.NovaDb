using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using NewLife;

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
                DataType.Int32 => GetBytesLittleEndian((Int32)value),
                DataType.Int64 => GetBytesLittleEndian((Int64)value),
                DataType.Double => GetBytesLittleEndian((Double)value),
                DataType.Decimal => EncodeDecimal((Decimal)value),
                DataType.String => EncodeString((String)value),
                DataType.Binary => EncodeByteArray((Byte[])value),
                DataType.DateTime => GetBytesLittleEndian(((DateTime)value).Ticks),
                DataType.GeoPoint => EncodeGeoPoint((GeoPoint)value),
                DataType.Vector => EncodeVector((Single[])value),
                _ => throw new NotSupportedException(L.T("Unsupported data type: {0}", dataType))
            };
        }
        catch (InvalidCastException ex)
        {
            throw new NovaException(
                ErrorCode.InvalidArgument,
                "Cannot encode value of type {0} as {1}",
                ex,
                value.GetType().Name,
                dataType
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
                throw BufferTooShortEncode("NULL", offset + 1, buffer.Length);
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
                            throw BufferTooShortEncode("Boolean", offset + 1, buffer.Length);
                        // 使用 0x01=true, 0x02=false，避免与 NullFlag(0x00) 冲突
                        buffer[offset] = (Boolean)value ? ((Byte)1) : ((Byte)2);
                        return 1;
                    }
                case DataType.Int32:
                    {
                        if (buffer.Length < offset + 4)
                            throw BufferTooShortEncode("Int32", offset + 4, buffer.Length);
                        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), (Int32)value);
                        return 4;
                    }
                case DataType.Int64:
                    {
                        if (buffer.Length < offset + 8)
                            throw BufferTooShortEncode("Int64", offset + 8, buffer.Length);
                        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), (Int64)value);
                        return 8;
                    }
                case DataType.Double:
                    {
                        if (buffer.Length < offset + 8)
                            throw BufferTooShortEncode("Double", offset + 8, buffer.Length);
                        WriteDoubleLittleEndian(buffer.AsSpan(offset, 8), (Double)value);
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
                            throw BufferTooShortEncode("DateTime", offset + 8, buffer.Length);
                        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), ((DateTime)value).Ticks);
                        return 8;
                    }
                case DataType.GeoPoint:
                    return EncodeGeoPoint((GeoPoint)value, buffer, offset);
                case DataType.Vector:
                    return EncodeVector((Single[])value, buffer, offset);
                default:
                    throw new NotSupportedException(L.T("Unsupported data type: {0}", dataType));
            }
        }
        catch (InvalidCastException ex)
        {
            throw new NovaException(
                ErrorCode.InvalidArgument,
                "Cannot encode value of type {0} as {1}",
                ex,
                value.GetType().Name,
                dataType
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
            throw new ArgumentOutOfRangeException(nameof(offset), L.T("Offset cannot be negative"));
        if (offset >= buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), L.T("Offset exceeds buffer length"));

        // 检查 NULL 标记（单字节 0x00 表示 NULL）。仅当缓冲区剩余正好 1 字节时视为 null，避免误判 Int32.MinValue（首字节也是 0x00）
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
                _ => throw new NotSupportedException(L.T("Unsupported data type: {0}", dataType))
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
                "Failed to decode {0} at offset {1}",
                ex,
                dataType,
                offset
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
            _ => throw new NotSupportedException(L.T("Unsupported data type: {0}", dataType))
        };
    }

    private static Byte[] EncodeDecimal(Decimal value)
    {
        var buffer = new Byte[16];
        WriteDecimal(value, buffer.AsSpan());

        return buffer;
    }

    private static Int32 EncodeDecimal(Decimal value, Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 16)
            throw BufferTooShortEncode("Decimal", offset + 16, buffer.Length);

        WriteDecimal(value, buffer.AsSpan(offset, 16));

        return 16;
    }

    private static void WriteDecimal(Decimal value, Span<Byte> destination)
    {
        var bits = Decimal.GetBits(value);

        // Decimal 的 4 个 32 位分量按小端写入，保证跨大端/小端平台的文件格式一致
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(i * 4, 4), bits[i]);
        }
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
            throw BufferTooShortEncode("String", offset + 4 + valueBytesLength, buffer.Length);

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
            throw BufferTooShortEncode("ByteArray", offset + 4 + value.Length, buffer.Length);

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
            throw BufferTooShortEncode("GeoPoint", offset + 16, buffer.Length);

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
            throw BufferTooShortEncode("Vector", offset + sizeof(Int32) + byteLen, buffer.Length);

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, sizeof(Int32)), value.Length);
        ReadOnlySpan<Byte> srcBytes = MemoryMarshal.AsBytes(value.AsSpan());
        srcBytes.CopyTo(buffer.AsSpan(offset + sizeof(Int32)));
        return sizeof(Int32) + byteLen;
    }

    private static Boolean DecodeBoolean(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 1)
            throw BufferTooShortRead("Boolean", offset + 1, buffer.Length);
        var b = buffer[offset];
        if (b == NullFlag)
            throw new NovaException(ErrorCode.ParseFailed, "Unexpected null value for Boolean column");
        return b == 1;
    }

    private static Int32 DecodeInt32(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 4)
            throw BufferTooShortRead("Int32", offset + 4, buffer.Length);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4));
    }

    private static Int64 DecodeInt64(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 8)
            throw BufferTooShortRead("Int64", offset + 8, buffer.Length);
        return BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, 8));
    }

    private static Double DecodeDouble(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 8)
            throw BufferTooShortRead("Double", offset + 8, buffer.Length);
        return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, 8)));
    }

    private static DateTime DecodeDateTime(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 8)
            throw BufferTooShortRead("DateTime", offset + 8, buffer.Length);
        return new DateTime(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, 8)));
    }

    private static Decimal DecodeDecimal(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 16)
            throw BufferTooShortRead("Decimal", offset + 16, buffer.Length);
        var bits = new Int32[4];
        for (var i = 0; i < 4; i++)
        {
            bits[i] = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset + i * 4, 4));
        }
        return new Decimal(bits);
    }

    private static String DecodeString(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 4)
            throw BufferTooShortRead("String length", offset + 4, buffer.Length);

        var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4));

        if (length < 0)
            throw InvalidLength("String", length);

        if (buffer.Length < offset + 4 + length)
            throw BufferTooShortRead("String data", offset + 4 + length, buffer.Length);

        return _encoding.GetString(buffer, offset + 4, length);
    }

    private static GeoPoint DecodeGeoPoint(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 16)
            throw BufferTooShortRead("GeoPoint", offset + 16, buffer.Length);
        var lat = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, 8)));
        var lon = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset + 8, 8)));
        return new GeoPoint(lat, lon);
    }

    private static Single[] DecodeVector(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 4)
            throw BufferTooShortRead("Vector length", offset + 4, buffer.Length);
        var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4));
        if (length < 0)
            throw InvalidLength("Vector", length);
        if (buffer.Length < offset + 4 + length * 4)
            throw BufferTooShortRead("Vector", offset + 4 + length * 4, buffer.Length);
        var result = new Single[length];
        Buffer.BlockCopy(buffer, offset + 4, result, 0, length * 4);
        return result;
    }

    private static Byte[] DecodeByteArray(Byte[] buffer, Int32 offset)
    {
        if (buffer.Length < offset + 4)
            throw BufferTooShortRead("ByteArray length", offset + 4, buffer.Length);

        var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4));
        if (length < 0)
            throw InvalidLength("ByteArray", length);
        if (buffer.Length < offset + 4 + length)
            throw BufferTooShortRead("ByteArray", offset + 4 + length, buffer.Length);

        var result = new Byte[length];
        Buffer.BlockCopy(buffer, offset + 4, result, 0, length);
        return result;
    }

    private static ArgumentException BufferTooShortEncode(String type, Int32 need, Int32 actual) =>
        new(L.T("Buffer too short to encode {0} (need {1} bytes, got {2})", type, need, actual));

    private static ArgumentException BufferTooShortRead(String type, Int32 need, Int32 actual) =>
        new(L.T("Buffer too short to read {0} (need {1} bytes, got {2})", type, need, actual));

    private static ArgumentException InvalidLength(String kind, Int32 length) =>
        new(L.T("Invalid {0} length: {1}", kind, length));

    private static Byte[] GetBytesLittleEndian(Int32 value)
    {
        var buffer = new Byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        return buffer;
    }

    private static Byte[] GetBytesLittleEndian(Int64 value)
    {
        var buffer = new Byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        return buffer;
    }

    private static Byte[] GetBytesLittleEndian(Double value)
    {
        var buffer = new Byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, BitConverter.DoubleToInt64Bits(value));
        return buffer;
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
