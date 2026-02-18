using System;
using NewLife.NovaDb.Core;
using Xunit;

namespace XUnitTest.Core;

/// <summary>GeoPoint 高级功能单元测试（距离、范围、解析、编解码）</summary>
public class GeoPointAdvancedTests
{
    [Fact(DisplayName = "GeoPoint 相等性 - 相同坐标")]
    public void TestEqualsSameCoords()
    {
        var a = new GeoPoint(39.9042, 116.4074);
        var b = new GeoPoint(39.9042, 116.4074);

        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact(DisplayName = "GeoPoint 相等性 - 不同坐标")]
    public void TestEqualsDifferentCoords()
    {
        var a = new GeoPoint(39.9042, 116.4074);
        var b = new GeoPoint(31.2304, 121.4737);

        Assert.False(a.Equals(b));
        Assert.False(a == b);
        Assert.True(a != b);
    }

    [Fact(DisplayName = "GeoPoint Equals(Object) 覆盖")]
    public void TestEqualsObject()
    {
        var point = new GeoPoint(39.9042, 116.4074);
        Object boxed = new GeoPoint(39.9042, 116.4074);

        Assert.True(point.Equals(boxed));
        Assert.False(point.Equals(null));
        Assert.False(point.Equals("not a geopoint"));
    }

    [Fact(DisplayName = "GeoPoint Distance - 北京到上海")]
    public void TestDistanceBeijingToShanghai()
    {
        var beijing = new GeoPoint(39.9042, 116.4074);
        var shanghai = new GeoPoint(31.2304, 121.4737);

        var distance = beijing.Distance(shanghai);

        // 北京到上海约 1068 km
        Assert.InRange(distance, 1_050_000, 1_090_000);
    }

    [Fact(DisplayName = "GeoPoint Distance - 同一点距离为零")]
    public void TestDistanceSamePoint()
    {
        var point = new GeoPoint(39.9042, 116.4074);

        Assert.Equal(0.0, point.Distance(point));
    }

    [Fact(DisplayName = "GeoPoint Distance - 对称性")]
    public void TestDistanceSymmetry()
    {
        var a = new GeoPoint(39.9042, 116.4074);
        var b = new GeoPoint(31.2304, 121.4737);

        Assert.Equal(a.Distance(b), b.Distance(a), 6);
    }

    [Fact(DisplayName = "GeoPoint WithinRadius - 在范围内")]
    public void TestWithinRadiusTrue()
    {
        var point = new GeoPoint(39.9042, 116.4074);
        var center = new GeoPoint(39.9142, 116.4174);

        Assert.True(point.WithinRadius(center, 50_000)); // 50km 范围内
    }

    [Fact(DisplayName = "GeoPoint WithinRadius - 超出范围")]
    public void TestWithinRadiusFalse()
    {
        var beijing = new GeoPoint(39.9042, 116.4074);
        var shanghai = new GeoPoint(31.2304, 121.4737);

        Assert.False(beijing.WithinRadius(shanghai, 100_000)); // 100km 范围外
    }

    [Fact(DisplayName = "GeoPoint Parse - 标准格式")]
    public void TestParse()
    {
        var point = GeoPoint.Parse("(39.9042, 116.4074)");

        Assert.Equal(39.9042, point.Latitude);
        Assert.Equal(116.4074, point.Longitude);
    }

    [Fact(DisplayName = "GeoPoint Parse - 无括号")]
    public void TestParseNoBrackets()
    {
        var point = GeoPoint.Parse("39.9042, 116.4074");

        Assert.Equal(39.9042, point.Latitude);
        Assert.Equal(116.4074, point.Longitude);
    }

    [Fact(DisplayName = "GeoPoint Parse - 错误格式抛出异常")]
    public void TestParseInvalidFormat()
    {
        Assert.Throws<FormatException>(() => GeoPoint.Parse("invalid"));
    }

    [Fact(DisplayName = "GeoPoint Parse - null 抛出 ArgumentNullException")]
    public void TestParseNull()
    {
        Assert.Throws<ArgumentNullException>(() => GeoPoint.Parse(null!));
    }

    [Fact(DisplayName = "GeoPoint 编解码往返")]
    public void TestCodecRoundTrip()
    {
        var codec = new DefaultDataCodec();
        var point = new GeoPoint(39.9042, 116.4074);

        var encoded = codec.Encode(point, DataType.GeoPoint);
        var decoded = (GeoPoint)codec.Decode(encoded, 0, DataType.GeoPoint)!;

        Assert.Equal(point, decoded);
    }

    [Fact(DisplayName = "GeoPoint 编解码 NULL")]
    public void TestCodecNull()
    {
        var codec = new DefaultDataCodec();

        var encoded = codec.Encode(null, DataType.GeoPoint);
        var decoded = codec.Decode(encoded, 0, DataType.GeoPoint);

        Assert.Null(decoded);
    }

    [Fact(DisplayName = "GeoPoint 编码长度")]
    public void TestCodecEncodedLength()
    {
        var codec = new DefaultDataCodec();
        var point = new GeoPoint(39.9042, 116.4074);

        Assert.Equal(16, codec.GetEncodedLength(point, DataType.GeoPoint));
        Assert.Equal(1, codec.GetEncodedLength(null, DataType.GeoPoint));
    }

    [Fact(DisplayName = "GeoPoint 默认值")]
    public void TestDefaultValue()
    {
        var point = new GeoPoint();

        Assert.Equal(0.0, point.Latitude);
        Assert.Equal(0.0, point.Longitude);
    }
}
