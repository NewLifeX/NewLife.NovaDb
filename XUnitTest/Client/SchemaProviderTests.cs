using System;
using System.Data;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Client;
using Xunit;

namespace XUnitTest.Client;

/// <summary>SchemaProvider 单元测试</summary>
public class SchemaProviderTests : IDisposable
{
    private readonly String _dbPath;

    public SchemaProviderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaSchema_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbPath);
    }

    public void Dispose()
    {
        if (!String.IsNullOrEmpty(_dbPath) && Directory.Exists(_dbPath))
        {
            try { Directory.Delete(_dbPath, recursive: true); }
            catch { }
        }
    }

    /// <summary>创建并打开嵌入式连接</summary>
    private NovaConnection CreateConnection()
    {
        var conn = new NovaConnection { ConnectionString = $"Data Source={_dbPath}" };
        conn.Open();
        return conn;
    }

    #region MetaDataCollections

    [Fact(DisplayName = "Schema-GetSchema()返回MetaDataCollections")]
    public void GetSchema_NoArgs_ReturnsMetaDataCollections()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema();

        Assert.NotNull(dt);
        Assert.Equal("MetaDataCollections", dt.TableName);
        Assert.True(dt.Rows.Count > 0);
    }

    [Fact(DisplayName = "Schema-MetaDataCollections包含标准集合名")]
    public void GetSchema_MetaDataCollections_ContainsStandardCollections()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("MetaDataCollections");

        var names = dt.Rows.Cast<DataRow>().Select(r => r["CollectionName"]?.ToString()).ToList();
        Assert.Contains("MetaDataCollections", names);
        Assert.Contains("DataTypes", names);
        Assert.Contains("Restrictions", names);
        Assert.Contains("ReservedWords", names);
        Assert.Contains("Tables", names);
        Assert.Contains("Columns", names);
        Assert.Contains("Indexes", names);
        Assert.Contains("IndexColumns", names);
    }

    [Fact(DisplayName = "Schema-MetaDataCollections有正确的列")]
    public void GetSchema_MetaDataCollections_HasCorrectColumns()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("MetaDataCollections");

        Assert.True(dt.Columns.Contains("CollectionName"));
        Assert.True(dt.Columns.Contains("NumberOfRestrictions"));
        Assert.True(dt.Columns.Contains("NumberOfIdentifierParts"));
    }

    [Fact(DisplayName = "Schema-collectionName大小写不敏感")]
    public void GetSchema_CollectionNameCaseInsensitive()
    {
        using var conn = CreateConnection();
        var dt1 = conn.GetSchema("MetaDataCollections");
        var dt2 = conn.GetSchema("metadatacollections");
        var dt3 = conn.GetSchema("METADATACOLLECTIONS");

        Assert.Equal(dt1.Rows.Count, dt2.Rows.Count);
        Assert.Equal(dt1.Rows.Count, dt3.Rows.Count);
    }

    [Fact(DisplayName = "Schema-未知collection返回MetaDataCollections")]
    public void GetSchema_UnknownCollection_ReturnsFallback()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("UnknownCollection");

        Assert.NotNull(dt);
        Assert.Equal("MetaDataCollections", dt.TableName);
    }

    #endregion

    #region DataSourceInformation

    [Fact(DisplayName = "Schema-DataSourceInformation返回产品名")]
    public void GetSchema_DataSourceInformation_HasProductName()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("DataSourceInformation");

        Assert.NotNull(dt);
        Assert.Equal(1, dt.Rows.Count);
        Assert.Equal("NovaDb", dt.Rows[0]["DataSourceProductName"]);
    }

    [Fact(DisplayName = "Schema-DataSourceInformation包含版本信息")]
    public void GetSchema_DataSourceInformation_HasVersionInfo()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("DataSourceInformation");

        var version = dt.Rows[0]["DataSourceProductVersion"]?.ToString();
        Assert.NotNull(version);
        Assert.NotEmpty(version);
    }

    [Fact(DisplayName = "Schema-DataSourceInformation包含IdentifierPattern")]
    public void GetSchema_DataSourceInformation_HasIdentifierPattern()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("DataSourceInformation");

        var pattern = dt.Rows[0]["IdentifierPattern"]?.ToString();
        Assert.NotNull(pattern);
        Assert.NotEmpty(pattern);
        // 确认不包含错误的字符串拼接残留
        Assert.DoesNotContain("\" + [", pattern);
    }

    #endregion

    #region Restrictions

    [Fact(DisplayName = "Schema-Restrictions返回限制描述")]
    public void GetSchema_Restrictions_HasRestrictionRows()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("Restrictions");

        Assert.NotNull(dt);
        Assert.True(dt.Rows.Count > 0);
    }

    [Fact(DisplayName = "Schema-Restrictions包含正确的列")]
    public void GetSchema_Restrictions_HasCorrectColumns()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("Restrictions");

        Assert.True(dt.Columns.Contains("CollectionName"));
        Assert.True(dt.Columns.Contains("RestrictionName"));
        Assert.True(dt.Columns.Contains("RestrictionDefault"));
        Assert.True(dt.Columns.Contains("RestrictionNumber"));
    }

    [Fact(DisplayName = "Schema-Restrictions包含Tables相关限制")]
    public void GetSchema_Restrictions_ContainsTablesRestrictions()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("Restrictions");

        var tablesRows = dt.Rows.Cast<DataRow>().Where(r => r["CollectionName"]?.ToString() == "Tables").ToList();
        Assert.True(tablesRows.Count >= 4);
    }

    #endregion

    #region DataTypes

    [Fact(DisplayName = "Schema-DataTypes返回数据类型列表")]
    public void GetSchema_DataTypes_ReturnsTypes()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("DataTypes");

        Assert.NotNull(dt);
        Assert.True(dt.Rows.Count > 0);
    }

    [Fact(DisplayName = "Schema-DataTypes包含正确的列")]
    public void GetSchema_DataTypes_HasRequiredColumns()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("DataTypes");

        Assert.True(dt.Columns.Contains("TypeName"));
        Assert.True(dt.Columns.Contains("ProviderDbType"));
        Assert.True(dt.Columns.Contains("DataType"));
        Assert.True(dt.Columns.Contains("IsNullable"));
    }

    [Fact(DisplayName = "Schema-DataTypes包含INT类型")]
    public void GetSchema_DataTypes_ContainsInt()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("DataTypes");

        var names = dt.Rows.Cast<DataRow>().Select(r => r["TypeName"]?.ToString()).ToList();
        Assert.Contains("INT", names);
    }

    [Fact(DisplayName = "Schema-DataTypes包含VARCHAR类型")]
    public void GetSchema_DataTypes_ContainsVarchar()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("DataTypes");

        var names = dt.Rows.Cast<DataRow>().Select(r => r["TypeName"]?.ToString()).ToList();
        Assert.Contains("VARCHAR", names);
    }

    [Fact(DisplayName = "Schema-DataTypes包含BIGINT类型")]
    public void GetSchema_DataTypes_ContainsBigint()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("DataTypes");

        var names = dt.Rows.Cast<DataRow>().Select(r => r["TypeName"]?.ToString()).ToList();
        Assert.Contains("BIGINT", names);
    }

    #endregion

    #region ReservedWords

    [Fact(DisplayName = "Schema-ReservedWords返回保留字列表")]
    public void GetSchema_ReservedWords_ReturnsWords()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("ReservedWords");

        Assert.NotNull(dt);
        Assert.True(dt.Rows.Count > 0);
    }

    [Fact(DisplayName = "Schema-ReservedWords包含ReservedWord列")]
    public void GetSchema_ReservedWords_HasReservedWordColumn()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("ReservedWords");

        Assert.True(dt.Columns.Contains("ReservedWord"));
    }

    [Fact(DisplayName = "Schema-ReservedWords包含SELECT关键字")]
    public void GetSchema_ReservedWords_ContainsSelect()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("ReservedWords");

        var words = dt.Rows.Cast<DataRow>().Select(r => r["ReservedWord"]?.ToString()).ToList();
        Assert.Contains("SELECT", words);
    }

    [Fact(DisplayName = "Schema-ReservedWords包含CREATE关键字")]
    public void GetSchema_ReservedWords_ContainsCreate()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("ReservedWords");

        var words = dt.Rows.Cast<DataRow>().Select(r => r["ReservedWord"]?.ToString()).ToList();
        Assert.Contains("CREATE", words);
    }

    #endregion

    #region Databases

    [Fact(DisplayName = "Schema-GetDatabases返回数据库列表")]
    public void GetSchema_Databases_ReturnsList()
    {
        using var conn = CreateConnection();
        var dt = conn.GetSchema("Databases");

        Assert.NotNull(dt);
        Assert.True(dt.Columns.Contains("SCHEMA_NAME"));
    }

    #endregion

    #region Tables

    [Fact(DisplayName = "Schema-GetTables返回表列表")]
    public void GetSchema_Tables_ReturnsList()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE schema_test_tbl (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        var dt = conn.GetSchema("Tables");

        Assert.NotNull(dt);
        Assert.True(dt.Columns.Contains("TABLE_NAME"));
    }

    [Fact(DisplayName = "Schema-GetTables包含已创建的表")]
    public void GetSchema_Tables_ContainsCreatedTable()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE schema_visible_tbl (id INT PRIMARY KEY, val INT)";
        cmd.ExecuteNonQuery();

        var dt = conn.GetSchema("Tables");

        var tableNames = dt.Rows.Cast<DataRow>().Select(r => r["TABLE_NAME"]?.ToString()).ToList();
        Assert.Contains("schema_visible_tbl", tableNames);
    }

    #endregion

    #region Columns

    [Fact(DisplayName = "Schema-GetColumns返回列信息")]
    public void GetSchema_Columns_ReturnsList()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE schema_col_tbl (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        var dt = conn.GetSchema("Columns");

        Assert.NotNull(dt);
        Assert.True(dt.Columns.Contains("COLUMN_NAME"));
        Assert.True(dt.Columns.Contains("TABLE_NAME"));
    }

    [Fact(DisplayName = "Schema-GetColumns包含已创建表的列")]
    public void GetSchema_Columns_ContainsTableColumns()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE schema_col2_tbl (id INT PRIMARY KEY, title VARCHAR, score DOUBLE)";
        cmd.ExecuteNonQuery();

        var dt = conn.GetSchema("Columns");

        var rows = dt.Rows.Cast<DataRow>().Where(r => r["TABLE_NAME"]?.ToString() == "schema_col2_tbl").ToList();
        Assert.True(rows.Count >= 3);
        var colNames = rows.Select(r => r["COLUMN_NAME"]?.ToString()).ToList();
        Assert.Contains("id", colNames);
        Assert.Contains("title", colNames);
        Assert.Contains("score", colNames);
    }

    #endregion
}
