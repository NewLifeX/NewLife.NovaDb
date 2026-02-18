using System;
using NewLife.NovaDb.Core;
using Xunit;

namespace XUnitTest.Core;

/// <summary>DataType 扩展方法测试</summary>
public class DataTypeExtensionsTests
{
    [Theory]
    [InlineData(DataType.Boolean, typeof(Boolean))]
    [InlineData(DataType.Int32, typeof(Int32))]
    [InlineData(DataType.Int64, typeof(Int64))]
    [InlineData(DataType.Double, typeof(Double))]
    [InlineData(DataType.Decimal, typeof(Decimal))]
    [InlineData(DataType.DateTime, typeof(DateTime))]
    [InlineData(DataType.String, typeof(String))]
    [InlineData(DataType.Binary, typeof(Byte[]))]
    [InlineData(DataType.GeoPoint, typeof(GeoPoint))]
    [InlineData(DataType.Vector, typeof(Single[]))]
    public void TestGetClrType(DataType dataType, Type expectedType)
    {
        var clrType = dataType.GetClrType();
        Assert.Equal(expectedType, clrType);
    }

    [Theory]
    [InlineData(typeof(Boolean), DataType.Boolean)]
    [InlineData(typeof(Int32), DataType.Int32)]
    [InlineData(typeof(Int64), DataType.Int64)]
    [InlineData(typeof(Double), DataType.Double)]
    [InlineData(typeof(Decimal), DataType.Decimal)]
    [InlineData(typeof(DateTime), DataType.DateTime)]
    [InlineData(typeof(String), DataType.String)]
    [InlineData(typeof(Byte[]), DataType.Binary)]
    [InlineData(typeof(GeoPoint), DataType.GeoPoint)]
    [InlineData(typeof(Single[]), DataType.Vector)]
    public void TestFromClrType(Type clrType, DataType expectedDataType)
    {
        var dataType = DataTypeExtensions.FromClrType(clrType);
        Assert.Equal(expectedDataType, dataType);
    }

    [Fact]
    public void TestRoundTripConversion()
    {
        // 测试所有支持的类型往返转换
        var allDataTypes = new[]
        {
            DataType.Boolean, DataType.Int32, DataType.Int64, DataType.Double,
            DataType.Decimal, DataType.DateTime, DataType.String, DataType.Binary,
            DataType.GeoPoint, DataType.Vector
        };

        foreach (var dataType in allDataTypes)
        {
            var clrType = dataType.GetClrType();
            var backToDataType = DataTypeExtensions.FromClrType(clrType);
            Assert.Equal(dataType, backToDataType);
        }
    }

    [Fact]
    public void TestGetClrTypeUnsupported()
    {
        var ex = Assert.Throws<NotSupportedException>(() => ((DataType)255).GetClrType());
        Assert.Contains("Unsupported data type", ex.Message);
    }

    [Fact]
    public void TestFromClrTypeUnsupported()
    {
        var ex = Assert.Throws<NotSupportedException>(() => DataTypeExtensions.FromClrType(typeof(Object)));
        Assert.Contains("Unsupported CLR type", ex.Message);
    }

    [Fact]
    public void TestDataTypeEnumValues()
    {
        // 验证基础类型的枚举值与 TypeCode 保持一致
        Assert.Equal(3, (Byte)DataType.Boolean);
        Assert.Equal(9, (Byte)DataType.Int32);
        Assert.Equal(11, (Byte)DataType.Int64);
        Assert.Equal(14, (Byte)DataType.Double);
        Assert.Equal(15, (Byte)DataType.Decimal);
        Assert.Equal(16, (Byte)DataType.DateTime);
        Assert.Equal(18, (Byte)DataType.String);

        // 自定义类型
        Assert.Equal(101, (Byte)DataType.Binary);
        Assert.Equal(102, (Byte)DataType.GeoPoint);
        Assert.Equal(103, (Byte)DataType.Vector);
    }
}

/// <summary>GeoPoint 结构体测试</summary>
public class GeoPointTests
{
    [Fact]
    public void TestConstructor()
    {
        var point = new GeoPoint(39.9042, 116.4074);

        Assert.Equal(39.9042, point.Latitude);
        Assert.Equal(116.4074, point.Longitude);
    }

    [Fact]
    public void TestToString()
    {
        var point = new GeoPoint(39.9042, 116.4074);
        var str = point.ToString();

        Assert.Contains("39.9042", str);
        Assert.Contains("116.4074", str);
    }

    [Fact]
    public void TestPropertySetter()
    {
        var point = new GeoPoint(31.2304, 121.4737);

        Assert.Equal(31.2304, point.Latitude);
        Assert.Equal(121.4737, point.Longitude);
    }

    [Fact]
    public void TestBoundaryValues()
    {
        // 测试边界值
        var northPole = new GeoPoint(90, 0);
        Assert.Equal(90, northPole.Latitude);

        var southPole = new GeoPoint(-90, 0);
        Assert.Equal(-90, southPole.Latitude);

        var dateLine = new GeoPoint(0, 180);
        Assert.Equal(180, dateLine.Longitude);

        var antiMeridian = new GeoPoint(0, -180);
        Assert.Equal(-180, antiMeridian.Longitude);
    }
}
