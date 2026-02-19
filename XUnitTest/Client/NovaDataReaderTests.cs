using System;
using System.Collections;
using System.Data;
using System.Data.Common;
using NewLife.NovaDb.Client;
using Xunit;

namespace XUnitTest.Client;

/// <summary>NovaDataReader 单元测试</summary>
public class NovaDataReaderTests
{
    #region 辅助

    /// <summary>创建带有标准测试数据的 Reader</summary>
    private static NovaDataReader CreateTestReader()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("Id", "Name", "Age", "Score", "Active");
        reader.AddRow([1, "Alice", 25, 89.5, true]);
        reader.AddRow([2, "Bob", 30, 92.3, false]);
        reader.AddRow([3, "Charlie", null, 78.0, true]);
        return reader;
    }

    #endregion

    #region 基础属性

    [Fact(DisplayName = "Reader-FieldCount返回列数")]
    public void FieldCount_ReturnsColumnCount()
    {
        var reader = CreateTestReader();
        Assert.Equal(5, reader.FieldCount);
    }

    [Fact(DisplayName = "Reader-空Reader的FieldCount为0")]
    public void FieldCount_EmptyReader()
    {
        var reader = new NovaDataReader();
        Assert.Equal(0, reader.FieldCount);
    }

    [Fact(DisplayName = "Reader-HasRows有数据为true")]
    public void HasRows_WithData_ReturnsTrue()
    {
        var reader = CreateTestReader();
        Assert.True(reader.HasRows);
    }

    [Fact(DisplayName = "Reader-HasRows无数据为false")]
    public void HasRows_Empty_ReturnsFalse()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("Id");
        Assert.False(reader.HasRows);
    }

    [Fact(DisplayName = "Reader-RecordsAffected返回-1")]
    public void RecordsAffected_ReturnsNegativeOne()
    {
        var reader = new NovaDataReader();
        Assert.Equal(-1, reader.RecordsAffected);
    }

    [Fact(DisplayName = "Reader-Depth返回0")]
    public void Depth_ReturnsZero()
    {
        var reader = new NovaDataReader();
        Assert.Equal(0, reader.Depth);
    }

    [Fact(DisplayName = "Reader-IsClosed初始为false")]
    public void IsClosed_InitiallyFalse()
    {
        var reader = new NovaDataReader();
        Assert.False(reader.IsClosed);
    }

    #endregion

    #region Read / Close

    [Fact(DisplayName = "Reader-Read遍历所有行")]
    public void Read_TraversesAllRows()
    {
        var reader = CreateTestReader();

        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.False(reader.Read());
    }

    [Fact(DisplayName = "Reader-Close后IsClosed为true")]
    public void Close_SetsIsClosed()
    {
        var reader = CreateTestReader();
        reader.Close();
        Assert.True(reader.IsClosed);
    }

    [Fact(DisplayName = "Reader-Close后Read返回false")]
    public void Close_ThenRead_ReturnsFalse()
    {
        var reader = CreateTestReader();
        reader.Close();
        Assert.False(reader.Read());
    }

    [Fact(DisplayName = "Reader-NextResult返回false")]
    public void NextResult_ReturnsFalse()
    {
        var reader = CreateTestReader();
        Assert.False(reader.NextResult());
    }

    #endregion

    #region GetName / GetOrdinal

    [Fact(DisplayName = "Reader-GetName返回列名")]
    public void GetName_ReturnsColumnName()
    {
        var reader = CreateTestReader();
        Assert.Equal("Id", reader.GetName(0));
        Assert.Equal("Name", reader.GetName(1));
        Assert.Equal("Age", reader.GetName(2));
    }

    [Fact(DisplayName = "Reader-GetOrdinal返回列索引")]
    public void GetOrdinal_ReturnsColumnIndex()
    {
        var reader = CreateTestReader();
        Assert.Equal(0, reader.GetOrdinal("Id"));
        Assert.Equal(1, reader.GetOrdinal("Name"));
        Assert.Equal(2, reader.GetOrdinal("Age"));
    }

    [Fact(DisplayName = "Reader-GetOrdinal大小写不敏感")]
    public void GetOrdinal_CaseInsensitive()
    {
        var reader = CreateTestReader();
        Assert.Equal(0, reader.GetOrdinal("id"));
        Assert.Equal(0, reader.GetOrdinal("ID"));
        Assert.Equal(1, reader.GetOrdinal("name"));
        Assert.Equal(1, reader.GetOrdinal("NAME"));
    }

    [Fact(DisplayName = "Reader-GetOrdinal未知列抛异常")]
    public void GetOrdinal_UnknownColumn_Throws()
    {
        var reader = CreateTestReader();
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetOrdinal("NonExistent"));
    }

    #endregion

    #region 类型化读取方法

    [Fact(DisplayName = "Reader-GetInt32")]
    public void GetInt32_ReturnsInt()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("Id");
        reader.AddRow([42]);
        reader.Read();

        Assert.Equal(42, reader.GetInt32(0));
    }

    [Fact(DisplayName = "Reader-GetInt64")]
    public void GetInt64_ReturnsLong()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("Id");
        reader.AddRow([Int64.MaxValue]);
        reader.Read();

        Assert.Equal(Int64.MaxValue, reader.GetInt64(0));
    }

    [Fact(DisplayName = "Reader-GetInt16")]
    public void GetInt16_ReturnsShort()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("Val");
        reader.AddRow([(Int16)123]);
        reader.Read();

        Assert.Equal((Int16)123, reader.GetInt16(0));
    }

    [Fact(DisplayName = "Reader-GetString")]
    public void GetString_ReturnsString()
    {
        var reader = CreateTestReader();
        reader.Read();

        Assert.Equal("Alice", reader.GetString(1));
    }

    [Fact(DisplayName = "Reader-GetBoolean")]
    public void GetBoolean_ReturnsBool()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("Active");
        reader.AddRow([true]);
        reader.Read();

        Assert.True(reader.GetBoolean(0));
    }

    [Fact(DisplayName = "Reader-GetByte")]
    public void GetByte_ReturnsByte()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("Val");
        reader.AddRow([(Byte)255]);
        reader.Read();

        Assert.Equal((Byte)255, reader.GetByte(0));
    }

    [Fact(DisplayName = "Reader-GetDouble")]
    public void GetDouble_ReturnsDouble()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("Score");
        reader.AddRow([3.14]);
        reader.Read();

        Assert.Equal(3.14, reader.GetDouble(0));
    }

    [Fact(DisplayName = "Reader-GetFloat")]
    public void GetFloat_ReturnsFloat()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("Val");
        reader.AddRow([2.5f]);
        reader.Read();

        Assert.Equal(2.5f, reader.GetFloat(0));
    }

    [Fact(DisplayName = "Reader-GetDecimal")]
    public void GetDecimal_ReturnsDecimal()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("Price");
        reader.AddRow([99.99m]);
        reader.Read();

        Assert.Equal(99.99m, reader.GetDecimal(0));
    }

    [Fact(DisplayName = "Reader-GetDateTime")]
    public void GetDateTime_ReturnsDateTime()
    {
        var now = DateTime.Now;
        var reader = new NovaDataReader();
        reader.SetColumns("Created");
        reader.AddRow([now]);
        reader.Read();

        Assert.Equal(now, reader.GetDateTime(0));
    }

    [Fact(DisplayName = "Reader-GetGuid")]
    public void GetGuid_ReturnsGuid()
    {
        var guid = Guid.NewGuid();
        var reader = new NovaDataReader();
        reader.SetColumns("UniqueId");
        reader.AddRow([guid.ToString()]);
        reader.Read();

        Assert.Equal(guid, reader.GetGuid(0));
    }

    [Fact(DisplayName = "Reader-GetChar")]
    public void GetChar_ReturnsChar()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("Letter");
        reader.AddRow(['A']);
        reader.Read();

        Assert.Equal('A', reader.GetChar(0));
    }

    #endregion

    #region GetValue / GetValues / IsDBNull

    [Fact(DisplayName = "Reader-GetValue返回原始值")]
    public void GetValue_ReturnsRawValue()
    {
        var reader = CreateTestReader();
        reader.Read();

        Assert.Equal(1, reader.GetValue(0));
        Assert.Equal("Alice", reader.GetValue(1));
    }

    [Fact(DisplayName = "Reader-GetValue对null返回DBNull")]
    public void GetValue_NullReturnsDBNull()
    {
        var reader = CreateTestReader();
        reader.Read();
        reader.Read();
        reader.Read(); // 第三行 Age 为 null

        Assert.Equal(DBNull.Value, reader.GetValue(2));
    }

    [Fact(DisplayName = "Reader-GetValues填充数组")]
    public void GetValues_FillsArray()
    {
        var reader = CreateTestReader();
        reader.Read();

        var values = new Object[5];
        var count = reader.GetValues(values);
        Assert.Equal(5, count);
        Assert.Equal(1, values[0]);
        Assert.Equal("Alice", values[1]);
    }

    [Fact(DisplayName = "Reader-GetValues数组较小时部分填充")]
    public void GetValues_SmallerArray_PartialFill()
    {
        var reader = CreateTestReader();
        reader.Read();

        var values = new Object[2];
        var count = reader.GetValues(values);
        Assert.Equal(2, count);
        Assert.Equal(1, values[0]);
        Assert.Equal("Alice", values[1]);
    }

    [Fact(DisplayName = "Reader-IsDBNull判断null值")]
    public void IsDBNull_ReturnsTrue()
    {
        var reader = CreateTestReader();
        reader.Read();
        reader.Read();
        reader.Read(); // 第三行 Age 为 null

        Assert.True(reader.IsDBNull(2));
        Assert.False(reader.IsDBNull(0));
    }

    #endregion

    #region 索引器

    [Fact(DisplayName = "Reader-按列索引访问")]
    public void Indexer_ByOrdinal()
    {
        var reader = CreateTestReader();
        reader.Read();

        Assert.Equal(1, reader[0]);
        Assert.Equal("Alice", reader[1]);
    }

    [Fact(DisplayName = "Reader-按列名访问")]
    public void Indexer_ByName()
    {
        var reader = CreateTestReader();
        reader.Read();

        Assert.Equal(1, reader["Id"]);
        Assert.Equal("Alice", reader["Name"]);
    }

    #endregion

    #region GetFieldType / GetDataTypeName

    [Fact(DisplayName = "Reader-GetFieldType返回值类型")]
    public void GetFieldType_ReturnsValueType()
    {
        var reader = CreateTestReader();
        reader.Read();

        Assert.Equal(typeof(Int32), reader.GetFieldType(0));
        Assert.Equal(typeof(String), reader.GetFieldType(1));
    }

    [Fact(DisplayName = "Reader-GetDataTypeName返回类型名")]
    public void GetDataTypeName_ReturnsTypeName()
    {
        var reader = CreateTestReader();
        reader.Read();

        Assert.Equal("Int32", reader.GetDataTypeName(0));
        Assert.Equal("String", reader.GetDataTypeName(1));
    }

    #endregion

    #region GetBytes / GetChars

    [Fact(DisplayName = "Reader-GetBytes读取字节数组")]
    public void GetBytes_ReadsByteArray()
    {
        var data = new Byte[] { 1, 2, 3, 4, 5 };
        var reader = new NovaDataReader();
        reader.SetColumns("Data");
        reader.AddRow([data]);
        reader.Read();

        var buffer = new Byte[5];
        var count = reader.GetBytes(0, 0, buffer, 0, 5);
        Assert.Equal(5, count);
        Assert.Equal(data, buffer);
    }

    [Fact(DisplayName = "Reader-GetBytes部分读取")]
    public void GetBytes_PartialRead()
    {
        var data = new Byte[] { 1, 2, 3, 4, 5 };
        var reader = new NovaDataReader();
        reader.SetColumns("Data");
        reader.AddRow([data]);
        reader.Read();

        var buffer = new Byte[3];
        var count = reader.GetBytes(0, 1, buffer, 0, 3);
        Assert.Equal(3, count);
        Assert.Equal(new Byte[] { 2, 3, 4 }, buffer);
    }

    [Fact(DisplayName = "Reader-GetBytes传null返回0")]
    public void GetBytes_NullBuffer_ReturnsZero()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("Data");
        reader.AddRow([new Byte[] { 1, 2, 3 }]);
        reader.Read();

        var count = reader.GetBytes(0, 0, null, 0, 3);
        Assert.Equal(0, count);
    }

    [Fact(DisplayName = "Reader-GetChars读取字符")]
    public void GetChars_ReadsChars()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("Text");
        reader.AddRow(["Hello"]);
        reader.Read();

        var buffer = new Char[5];
        var count = reader.GetChars(0, 0, buffer, 0, 5);
        Assert.Equal(5, count);
        Assert.Equal("Hello".ToCharArray(), buffer);
    }

    [Fact(DisplayName = "Reader-GetChars传null返回0")]
    public void GetChars_NullBuffer_ReturnsZero()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("Text");
        reader.AddRow(["Hello"]);
        reader.Read();

        var count = reader.GetChars(0, 0, null, 0, 5);
        Assert.Equal(0, count);
    }

    #endregion

    #region GetEnumerator

    [Fact(DisplayName = "Reader-GetEnumerator返回枚举器")]
    public void GetEnumerator_ReturnsEnumerator()
    {
        var reader = CreateTestReader();
        var enumerator = reader.GetEnumerator();

        Assert.NotNull(enumerator);
        Assert.IsType<DbEnumerator>(enumerator);
    }

    #endregion

    #region AddRow 异常

    [Fact(DisplayName = "Reader-AddRow传null抛异常")]
    public void AddRow_Null_Throws()
    {
        var reader = new NovaDataReader();
        Assert.Throws<ArgumentNullException>(() => reader.AddRow(null!));
    }

    #endregion

    #region SetColumns 重置

    [Fact(DisplayName = "Reader-SetColumns可重复设置")]
    public void SetColumns_CanReset()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("A", "B");
        Assert.Equal(2, reader.FieldCount);

        reader.SetColumns("X", "Y", "Z");
        Assert.Equal(3, reader.FieldCount);
    }

    #endregion
}
