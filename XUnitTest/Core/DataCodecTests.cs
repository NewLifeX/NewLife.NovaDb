using System;
using NewLife.NovaDb.Core;
using Xunit;

namespace XUnitTest.Core;

public class DataCodecTests
{
    private readonly IDataCodec _codec = new DefaultDataCodec();

    [Fact]
    public void TestEncodeDecodeBoolean()
    {
        var value = true;
        var encoded = _codec.Encode(value, DataType.Boolean);
        var decoded = _codec.Decode(encoded, 0, DataType.Boolean);

        Assert.Equal(value, decoded);
    }

    [Fact]
    public void TestEncodeDecodeInt32()
    {
        var value = 12345;
        var encoded = _codec.Encode(value, DataType.Int32);
        var decoded = _codec.Decode(encoded, 0, DataType.Int32);

        Assert.Equal(value, decoded);
    }

    [Fact]
    public void TestEncodeDecodeInt64()
    {
        var value = 9876543210L;
        var encoded = _codec.Encode(value, DataType.Int64);
        var decoded = _codec.Decode(encoded, 0, DataType.Int64);

        Assert.Equal(value, decoded);
    }

    [Fact]
    public void TestEncodeDecodeDouble()
    {
        var value = 3.14159;
        var encoded = _codec.Encode(value, DataType.Double);
        var decoded = _codec.Decode(encoded, 0, DataType.Double);

        Assert.Equal(value, decoded);
    }

    [Fact]
    public void TestEncodeDecodeDecimal()
    {
        var value = 123.456789M;
        var encoded = _codec.Encode(value, DataType.Decimal);
        var decoded = _codec.Decode(encoded, 0, DataType.Decimal);

        Assert.Equal(value, decoded);
    }

    [Fact]
    public void TestEncodeDecodeString()
    {
        var value = "Hello, NovaDb!";
        var encoded = _codec.Encode(value, DataType.String);
        var decoded = _codec.Decode(encoded, 0, DataType.String);

        Assert.Equal(value, decoded);
    }

    [Fact]
    public void TestEncodeDecodeByteArray()
    {
        var value = new byte[] { 1, 2, 3, 4, 5 };
        var encoded = _codec.Encode(value, DataType.Binary);
        var decoded = (byte[])_codec.Decode(encoded, 0, DataType.Binary)!;

        Assert.Equal(value, decoded);
    }

    [Fact]
    public void TestEncodeDecodeDateTime()
    {
        var value = new DateTime(2026, 2, 18, 12, 30, 45, DateTimeKind.Utc);
        var encoded = _codec.Encode(value, DataType.DateTime);
        var decoded = _codec.Decode(encoded, 0, DataType.DateTime);

        Assert.Equal(value, decoded);
    }

    [Fact]
    public void TestEncodeDecodeNull()
    {
        var encoded = _codec.Encode(null, DataType.String);
        var decoded = _codec.Decode(encoded, 0, DataType.String);

        Assert.Null(decoded);
    }

    [Fact]
    public void TestGetEncodedLength()
    {
        Assert.Equal(1, _codec.GetEncodedLength(true, DataType.Boolean));
        Assert.Equal(4, _codec.GetEncodedLength(123, DataType.Int32));
        Assert.Equal(8, _codec.GetEncodedLength(123L, DataType.Int64));
        Assert.Equal(8, _codec.GetEncodedLength(3.14, DataType.Double));
        Assert.Equal(16, _codec.GetEncodedLength(123.45M, DataType.Decimal));
        Assert.Equal(4 + 5, _codec.GetEncodedLength("Hello", DataType.String));
        Assert.Equal(4 + 3, _codec.GetEncodedLength(new byte[] { 1, 2, 3 }, DataType.Binary));
        Assert.Equal(8, _codec.GetEncodedLength(DateTime.Now, DataType.DateTime));
        Assert.Equal(1, _codec.GetEncodedLength(null, DataType.String));
    }

    [Fact]
    public void TestEncodeDecodeEmptyString()
    {
        var value = "";
        var encoded = _codec.Encode(value, DataType.String);
        var decoded = _codec.Decode(encoded, 0, DataType.String);

        Assert.Equal(value, decoded);
        Assert.Equal(4, encoded.Length); // 长度前缀 4 字节 + 0 字节数据
    }

    [Fact]
    public void TestEncodeDecodeEmptyByteArray()
    {
        var value = Array.Empty<Byte>();
        var encoded = _codec.Encode(value, DataType.Binary);
        var decoded = (Byte[])_codec.Decode(encoded, 0, DataType.Binary)!;

        Assert.Equal(value, decoded);
        Assert.Equal(4, encoded.Length); // 长度前缀 4 字节 + 0 字节数据
    }

    [Fact]
    public void TestEncodeDecodeUnicodeString()
    {
        var value = "你好，NovaDb！🚀";
        var encoded = _codec.Encode(value, DataType.String);
        var decoded = _codec.Decode(encoded, 0, DataType.String);

        Assert.Equal(value, decoded);
    }

    [Fact]
    public void TestEncodeDecodeMinMaxInt32()
    {
        var minEncoded = _codec.Encode(Int32.MinValue, DataType.Int32);
        var minDecoded = _codec.Decode(minEncoded, 0, DataType.Int32);
        Assert.Equal(Int32.MinValue, minDecoded);

        var maxEncoded = _codec.Encode(Int32.MaxValue, DataType.Int32);
        var maxDecoded = _codec.Decode(maxEncoded, 0, DataType.Int32);
        Assert.Equal(Int32.MaxValue, maxDecoded);
    }

    [Fact]
    public void TestEncodeDecodeMinMaxInt64()
    {
        var minEncoded = _codec.Encode(Int64.MinValue, DataType.Int64);
        var minDecoded = _codec.Decode(minEncoded, 0, DataType.Int64);
        Assert.Equal(Int64.MinValue, minDecoded);

        var maxEncoded = _codec.Encode(Int64.MaxValue, DataType.Int64);
        var maxDecoded = _codec.Decode(maxEncoded, 0, DataType.Int64);
        Assert.Equal(Int64.MaxValue, maxDecoded);
    }

    [Fact]
    public void TestEncodeDecodeMinMaxDouble()
    {
        var minEncoded = _codec.Encode(Double.MinValue, DataType.Double);
        var minDecoded = _codec.Decode(minEncoded, 0, DataType.Double);
        Assert.Equal(Double.MinValue, minDecoded);

        var maxEncoded = _codec.Encode(Double.MaxValue, DataType.Double);
        var maxDecoded = _codec.Decode(maxEncoded, 0, DataType.Double);
        Assert.Equal(Double.MaxValue, maxDecoded);
    }

    [Fact]
    public void TestEncodeDecodeMinMaxDecimal()
    {
        var minEncoded = _codec.Encode(Decimal.MinValue, DataType.Decimal);
        var minDecoded = _codec.Decode(minEncoded, 0, DataType.Decimal);
        Assert.Equal(Decimal.MinValue, minDecoded);

        var maxEncoded = _codec.Encode(Decimal.MaxValue, DataType.Decimal);
        var maxDecoded = _codec.Decode(maxEncoded, 0, DataType.Decimal);
        Assert.Equal(Decimal.MaxValue, maxDecoded);
    }

    [Fact]
    public void TestEncodeDecodeMinMaxDateTime()
    {
        var minEncoded = _codec.Encode(DateTime.MinValue, DataType.DateTime);
        var minDecoded = _codec.Decode(minEncoded, 0, DataType.DateTime);
        Assert.Equal(DateTime.MinValue, minDecoded);

        var maxEncoded = _codec.Encode(DateTime.MaxValue, DataType.DateTime);
        var maxDecoded = _codec.Decode(maxEncoded, 0, DataType.DateTime);
        Assert.Equal(DateTime.MaxValue, maxDecoded);
    }

    [Fact]
    public void TestDecodeWithOffset()
    {
        // 构造一个包含多个值的缓冲区
        var value1 = 12345;
        var value2 = 67890;
        var encoded1 = _codec.Encode(value1, DataType.Int32);
        var encoded2 = _codec.Encode(value2, DataType.Int32);

        var buffer = new Byte[encoded1.Length + encoded2.Length];
        Buffer.BlockCopy(encoded1, 0, buffer, 0, encoded1.Length);
        Buffer.BlockCopy(encoded2, 0, buffer, encoded1.Length, encoded2.Length);

        // 从不同偏移解码
        var decoded1 = _codec.Decode(buffer, 0, DataType.Int32);
        var decoded2 = _codec.Decode(buffer, encoded1.Length, DataType.Int32);

        Assert.Equal(value1, decoded1);
        Assert.Equal(value2, decoded2);
    }

    [Fact]
    public void TestEncodeDecodeNullForAllTypes()
    {
        // 测试所有支持 NULL 的类型
        var types = new[] { DataType.String, DataType.Binary };
        foreach (var type in types)
        {
            var encoded = _codec.Encode(null, type);
            var decoded = _codec.Decode(encoded, 0, type);
            Assert.Null(decoded);
        }
    }

    [Fact]
    public void TestEncodeUnsupportedType()
    {
        // 测试不支持的类型
        var ex = Assert.Throws<NotSupportedException>(() => _codec.Encode(123, (DataType)255));
        Assert.Contains("Unsupported data type", ex.Message);
    }

    [Fact]
    public void TestDecodeUnsupportedType()
    {
        var buffer = new Byte[4];
        var ex = Assert.Throws<NotSupportedException>(() => _codec.Decode(buffer, 0, (DataType)255));
        Assert.Contains("Unsupported data type", ex.Message);
    }

    [Fact]
    public void TestGetEncodedLengthUnsupportedType()
    {
        var ex = Assert.Throws<NotSupportedException>(() => _codec.GetEncodedLength(123, (DataType)255));
        Assert.Contains("Unsupported data type", ex.Message);
    }

    [Fact]
    public void TestEncodeDecodeGeoPoint()
    {
        var value = new GeoPoint(39.9042, 116.4074);
        var encoded = _codec.Encode(value, DataType.GeoPoint);
        var decoded = (GeoPoint)_codec.Decode(encoded, 0, DataType.GeoPoint)!;

        Assert.Equal(value.Latitude, decoded.Latitude);
        Assert.Equal(value.Longitude, decoded.Longitude);
    }

    [Fact]
    public void TestEncodeDecodeGeoPointNull()
    {
        var encoded = _codec.Encode(null, DataType.GeoPoint);
        var decoded = _codec.Decode(encoded, 0, DataType.GeoPoint);

        Assert.Null(decoded);
    }

    [Fact]
    public void TestEncodeDecodeVector()
    {
        var value = new Single[] { 1.0f, 2.5f, -3.14f, 0f };
        var encoded = _codec.Encode(value, DataType.Vector);
        var decoded = (Single[])_codec.Decode(encoded, 0, DataType.Vector)!;

        Assert.Equal(value, decoded);
    }

    [Fact]
    public void TestEncodeDecodeVectorNull()
    {
        var encoded = _codec.Encode(null, DataType.Vector);
        var decoded = _codec.Decode(encoded, 0, DataType.Vector);

        Assert.Null(decoded);
    }

    [Fact]
    public void TestEncodeDecodeEmptyVector()
    {
        var value = Array.Empty<Single>();
        var encoded = _codec.Encode(value, DataType.Vector);
        var decoded = (Single[])_codec.Decode(encoded, 0, DataType.Vector)!;

        Assert.Equal(value, decoded);
        Assert.Equal(4, encoded.Length); // 长度前缀 4 字节 + 0 字节数据
    }

    [Fact]
    public void TestGetEncodedLengthGeoPoint()
    {
        var value = new GeoPoint(39.9042, 116.4074);
        Assert.Equal(16, _codec.GetEncodedLength(value, DataType.GeoPoint));
    }

    [Fact]
    public void TestGetEncodedLengthVector()
    {
        var value = new Single[] { 1.0f, 2.0f, 3.0f };
        Assert.Equal(4 + 3 * 4, _codec.GetEncodedLength(value, DataType.Vector));
    }
}
