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
        var encoded = _codec.Encode(value, DataType.ByteArray);
        var decoded = (byte[])_codec.Decode(encoded, 0, DataType.ByteArray)!;

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
        Assert.Equal(4 + 3, _codec.GetEncodedLength(new byte[] { 1, 2, 3 }, DataType.ByteArray));
        Assert.Equal(8, _codec.GetEncodedLength(DateTime.Now, DataType.DateTime));
        Assert.Equal(4, _codec.GetEncodedLength(null, DataType.String));
    }
}
