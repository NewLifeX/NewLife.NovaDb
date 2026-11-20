using System;
using NewLife.NovaDb.Core;
using Xunit;

namespace XUnitTest.Core;

/// <summary>数据类型名称表单元测试</summary>
public class DataTypeNameTests
{
    [Theory(DisplayName = "别名解析到对应数据类型")]
    [InlineData("BOOL", DataType.Boolean)]
    [InlineData("boolean", DataType.Boolean)]
    [InlineData("BIT", DataType.Boolean)]
    [InlineData("int", DataType.Int32)]
    [InlineData("INT32", DataType.Int32)]
    [InlineData("integer", DataType.Int32)]
    [InlineData("TINYINT", DataType.Int32)]
    [InlineData("BIGINT", DataType.Int64)]
    [InlineData("long", DataType.Int64)]
    [InlineData("INT64", DataType.Int64)]
    [InlineData("float", DataType.Double)]
    [InlineData("DOUBLE", DataType.Double)]
    [InlineData("numeric", DataType.Decimal)]
    [InlineData("timestamp", DataType.DateTime)]
    [InlineData("varchar", DataType.String)]
    [InlineData("LONGTEXT", DataType.String)]
    [InlineData("json", DataType.String)]
    [InlineData("blob", DataType.Binary)]
    [InlineData("VARBINARY", DataType.Binary)]
    [InlineData("geopoint", DataType.GeoPoint)]
    [InlineData("Vector", DataType.Vector)]
    public void Parse_AliasesResolveToDataType(String name, DataType expected)
    {
        Assert.True(DataTypeNames.TryParse(name, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact(DisplayName = "未知类型名抛出语法错误")]
    public void Parse_UnknownTypeThrows()
    {
        var ex = Assert.Throws<NovaException>(() => DataTypeNames.Parse("FOOBAR"));

        Assert.Equal(ErrorCode.SyntaxError, ex.Code);
        Assert.Contains("FOOBAR", ex.Message);
    }

    [Fact(DisplayName = "规范名大写下发")]
    public void GetName_ReturnsCanonicalName()
    {
        Assert.Equal("BOOL", DataTypeNames.GetName(DataType.Boolean));
        Assert.Equal("INT", DataTypeNames.GetName(DataType.Int32));
        Assert.Equal("BIGINT", DataTypeNames.GetName(DataType.Int64));
        Assert.Equal("STRING", DataTypeNames.GetName(DataType.String));
        Assert.Equal("BINARY", DataTypeNames.GetName(DataType.Binary));
    }

    [Fact(DisplayName = "展示名带长度且只对字符串/二进制/向量生效")]
    public void Format_AppendsLengthOnlyForSupportedTypes()
    {
        Assert.Equal("string(50)", DataTypeNames.Format(DataType.String, 50));
        Assert.Equal("binary(16)", DataTypeNames.Format(DataType.Binary, 16));
        Assert.Equal("vector(768)", DataTypeNames.Format(DataType.Vector, 768));

        // INT(11) 的显示宽度、DECIMAL(18,4) 的精度都不是长度约束
        Assert.Equal("int", DataTypeNames.Format(DataType.Int32, 11));
        Assert.Equal("decimal", DataTypeNames.Format(DataType.Decimal, 18));
        Assert.Equal("bigint", DataTypeNames.Format(DataType.Int64));
    }
}
