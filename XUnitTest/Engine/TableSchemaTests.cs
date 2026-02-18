using System;
using Xunit;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;

namespace XUnitTest.Engine;

/// <summary>
/// 表架构单元测试
/// </summary>
public class TableSchemaTests
{
    [Fact(DisplayName = "测试创建表架构")]
    public void TestCreateTableSchema()
    {
        var schema = new TableSchema("users");

        Assert.Equal("users", schema.TableName);
        Assert.Empty(schema.Columns);
        Assert.Null(schema.PrimaryKeyIndex);
    }

    [Fact(DisplayName = "测试添加列")]
    public void TestAddColumn()
    {
        var schema = new TableSchema("users");
        var col1 = new ColumnDefinition("id", DataType.Int32, nullable: false, isPrimaryKey: true);
        var col2 = new ColumnDefinition("name", DataType.String);

        schema.AddColumn(col1);
        schema.AddColumn(col2);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal(0, col1.Ordinal);
        Assert.Equal(1, col2.Ordinal);
        Assert.Equal(0, schema.PrimaryKeyIndex);
    }

    [Fact(DisplayName = "测试重复列名")]
    public void TestDuplicateColumnName()
    {
        var schema = new TableSchema("users");
        var col1 = new ColumnDefinition("id", DataType.Int32);
        var col2 = new ColumnDefinition("id", DataType.String);

        schema.AddColumn(col1);

        Assert.Throws<NovaDbException>(() => schema.AddColumn(col2));
    }

    [Fact(DisplayName = "测试多个主键")]
    public void TestMultiplePrimaryKeys()
    {
        var schema = new TableSchema("users");
        var col1 = new ColumnDefinition("id1", DataType.Int32, isPrimaryKey: true);
        var col2 = new ColumnDefinition("id2", DataType.Int32, isPrimaryKey: true);

        schema.AddColumn(col1);

        Assert.Throws<NovaDbException>(() => schema.AddColumn(col2));
    }

    [Fact(DisplayName = "测试获取列")]
    public void TestGetColumn()
    {
        var schema = new TableSchema("users");
        schema.AddColumn(new ColumnDefinition("id", DataType.Int32, isPrimaryKey: true));
        schema.AddColumn(new ColumnDefinition("name", DataType.String));

        var col = schema.GetColumn("name");
        Assert.Equal("name", col.Name);
        Assert.Equal(DataType.String, col.DataType);
        Assert.Equal(1, col.Ordinal);
    }

    [Fact(DisplayName = "测试获取不存在的列")]
    public void TestGetNonExistentColumn()
    {
        var schema = new TableSchema("users");
        schema.AddColumn(new ColumnDefinition("id", DataType.Int32));

        Assert.Throws<NovaDbException>(() => schema.GetColumn("unknown"));
    }

    [Fact(DisplayName = "测试获取主键列")]
    public void TestGetPrimaryKeyColumn()
    {
        var schema = new TableSchema("users");
        schema.AddColumn(new ColumnDefinition("id", DataType.Int32, isPrimaryKey: true));
        schema.AddColumn(new ColumnDefinition("name", DataType.String));

        var pk = schema.GetPrimaryKeyColumn();
        Assert.NotNull(pk);
        Assert.Equal("id", pk!.Name);
        Assert.True(pk.IsPrimaryKey);
    }

    [Fact(DisplayName = "测试没有主键")]
    public void TestNoPrimaryKey()
    {
        var schema = new TableSchema("users");
        schema.AddColumn(new ColumnDefinition("name", DataType.String));

        var pk = schema.GetPrimaryKeyColumn();
        Assert.Null(pk);
    }

    [Fact(DisplayName = "测试检查列是否存在")]
    public void TestHasColumn()
    {
        var schema = new TableSchema("users");
        schema.AddColumn(new ColumnDefinition("id", DataType.Int32));

        Assert.True(schema.HasColumn("id"));
        Assert.False(schema.HasColumn("unknown"));
        Assert.False(schema.HasColumn(null!));
    }
}
