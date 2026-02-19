using System;
using System.Data;
using NewLife.NovaDb.Client;
using Xunit;

namespace XUnitTest.Client;

/// <summary>NovaParameter 单元测试</summary>
public class NovaParameterTests
{
    #region 属性测试

    [Fact(DisplayName = "参数-ParameterName读写")]
    public void ParameterName_GetSet()
    {
        var param = new NovaParameter { ParameterName = "TestParam" };
        Assert.Equal("TestParam", param.ParameterName);
    }

    [Fact(DisplayName = "参数-ParameterName默认值")]
    public void ParameterName_Default()
    {
        var param = new NovaParameter();
        Assert.Equal(String.Empty, param.ParameterName);
    }

    [Fact(DisplayName = "参数-Value读写")]
    public void Value_GetSet()
    {
        var param = new NovaParameter { Value = 123 };
        Assert.Equal(123, param.Value);
    }

    [Fact(DisplayName = "参数-Value为null")]
    public void Value_Null()
    {
        var param = new NovaParameter { Value = null };
        Assert.Null(param.Value);
    }

    [Fact(DisplayName = "参数-Value为字符串")]
    public void Value_String()
    {
        var param = new NovaParameter { Value = "hello" };
        Assert.Equal("hello", param.Value);
    }

    [Fact(DisplayName = "参数-DbType读写")]
    public void DbType_GetSet()
    {
        var param = new NovaParameter { DbType = DbType.Int32 };
        Assert.Equal(DbType.Int32, param.DbType);
    }

    [Fact(DisplayName = "参数-Direction读写")]
    public void Direction_GetSet()
    {
        var param = new NovaParameter { Direction = ParameterDirection.Output };
        Assert.Equal(ParameterDirection.Output, param.Direction);
    }

    [Fact(DisplayName = "参数-Direction默认值为Input")]
    public void Direction_DefaultIsInput()
    {
        var param = new NovaParameter();
        Assert.Equal(ParameterDirection.Input, param.Direction);
    }

    [Fact(DisplayName = "参数-IsNullable读写")]
    public void IsNullable_GetSet()
    {
        var param = new NovaParameter { IsNullable = true };
        Assert.True(param.IsNullable);
    }

    [Fact(DisplayName = "参数-IsNullable默认为false")]
    public void IsNullable_DefaultIsFalse()
    {
        var param = new NovaParameter();
        Assert.False(param.IsNullable);
    }

    [Fact(DisplayName = "参数-Size读写")]
    public void Size_GetSet()
    {
        var param = new NovaParameter { Size = 100 };
        Assert.Equal(100, param.Size);
    }

    [Fact(DisplayName = "参数-SourceColumn读写")]
    public void SourceColumn_GetSet()
    {
        var param = new NovaParameter { SourceColumn = "TestColumn" };
        Assert.Equal("TestColumn", param.SourceColumn);
    }

    [Fact(DisplayName = "参数-SourceColumn默认值")]
    public void SourceColumn_Default()
    {
        var param = new NovaParameter();
        Assert.Equal(String.Empty, param.SourceColumn);
    }

    [Fact(DisplayName = "参数-SourceColumnNullMapping读写")]
    public void SourceColumnNullMapping_GetSet()
    {
        var param = new NovaParameter { SourceColumnNullMapping = true };
        Assert.True(param.SourceColumnNullMapping);
    }

    [Fact(DisplayName = "参数-SourceVersion读写")]
    public void SourceVersion_GetSet()
    {
        var param = new NovaParameter { SourceVersion = DataRowVersion.Original };
        Assert.Equal(DataRowVersion.Original, param.SourceVersion);
    }

    #endregion

    #region ResetDbType

    [Fact(DisplayName = "参数-ResetDbType重置为String")]
    public void ResetDbType_ResetsToString()
    {
        var param = new NovaParameter { DbType = DbType.Int32 };
        param.ResetDbType();
        Assert.Equal(DbType.String, param.DbType);
    }

    #endregion

    #region 各种值类型

    [Theory(DisplayName = "参数-支持多种DbType")]
    [InlineData(DbType.String)]
    [InlineData(DbType.Int32)]
    [InlineData(DbType.Int64)]
    [InlineData(DbType.Boolean)]
    [InlineData(DbType.DateTime)]
    [InlineData(DbType.Decimal)]
    [InlineData(DbType.Double)]
    [InlineData(DbType.Guid)]
    [InlineData(DbType.Binary)]
    public void DbType_SupportsMultipleTypes(DbType dbType)
    {
        var param = new NovaParameter { DbType = dbType };
        Assert.Equal(dbType, param.DbType);
    }

    [Theory(DisplayName = "参数-支持多种Direction")]
    [InlineData(ParameterDirection.Input)]
    [InlineData(ParameterDirection.Output)]
    [InlineData(ParameterDirection.InputOutput)]
    [InlineData(ParameterDirection.ReturnValue)]
    public void Direction_SupportsAll(ParameterDirection direction)
    {
        var param = new NovaParameter { Direction = direction };
        Assert.Equal(direction, param.Direction);
    }

    #endregion
}
