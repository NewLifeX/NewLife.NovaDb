using System;
using NewLife.NovaDb.Client;
using Xunit;

namespace XUnitTest.Client;

/// <summary>NovaConnectionStringBuilder 单元测试</summary>
public class NovaConnectionStringBuilderTests
{
    #region 默认构造

    [Fact(DisplayName = "连接字符串构建器-默认构造属性")]
    public void DefaultConstructor_Properties()
    {
        var builder = new NovaConnectionStringBuilder();

        Assert.Null(builder.DataSource);
        Assert.Null(builder.Server);
        Assert.Equal(3306, builder.Port);
        Assert.Null(builder.Database);
        Assert.Equal(15, builder.ConnectionTimeout);
        Assert.Equal(30, builder.CommandTimeout);
        Assert.False(builder.IsEmbedded);
    }

    #endregion

    #region 带参构造

    [Fact(DisplayName = "连接字符串构建器-嵌入模式解析")]
    public void ConstructorWithEmbeddedConnStr()
    {
        var connStr = "Data Source=./test.db;Database=mydb";
        var builder = new NovaConnectionStringBuilder(connStr);

        Assert.True(builder.IsEmbedded);
        Assert.Equal("./test.db", builder.DataSource);
        Assert.Equal("mydb", builder.Database);
    }

    [Fact(DisplayName = "连接字符串构建器-服务器模式解析")]
    public void ConstructorWithServerConnStr()
    {
        var connStr = "Server=localhost;Port=5678;Database=testdb";
        var builder = new NovaConnectionStringBuilder(connStr);

        Assert.False(builder.IsEmbedded);
        Assert.Equal("localhost", builder.Server);
        Assert.Equal(5678, builder.Port);
        Assert.Equal("testdb", builder.Database);
    }

    [Fact(DisplayName = "连接字符串构建器-完整连接字符串解析")]
    public void ConstructorWithFullConnStr()
    {
        var connStr = "Server=192.168.1.1;Port=3307;Database=nova;ConnectionTimeout=60;CommandTimeout=120";
        var builder = new NovaConnectionStringBuilder(connStr);

        Assert.Equal("192.168.1.1", builder.Server);
        Assert.Equal(3307, builder.Port);
        Assert.Equal("nova", builder.Database);
        Assert.Equal(60, builder.ConnectionTimeout);
        Assert.Equal(120, builder.CommandTimeout);
    }

    #endregion

    #region 索引器

    [Fact(DisplayName = "连接字符串构建器-索引器读写")]
    public void IndexerGetSet()
    {
        var builder = new NovaConnectionStringBuilder
        {
            ["server"] = "localhost",
            ["port"] = 3306,
            ["database"] = "testdb",
            ["connectiontimeout"] = 15,
            ["command timeout"] = 30
        };

        Assert.Equal("localhost", builder.Server);
        Assert.Equal(3306, builder.Port);
        Assert.Equal("testdb", builder.Database);
        Assert.Equal(15, builder.ConnectionTimeout);
        Assert.Equal(30, builder.CommandTimeout);
    }

    #endregion

    #region 别名映射

    [Fact(DisplayName = "连接字符串构建器-DataSource别名映射")]
    public void AliasMapping_DataSource()
    {
        var builder = new NovaConnectionStringBuilder();
        builder["data source"] = "./mydb";

        Assert.Equal("./mydb", builder.DataSource);
        Assert.True(builder.IsEmbedded);
    }

    [Fact(DisplayName = "连接字符串构建器-datasource别名映射")]
    public void AliasMapping_DataSourceNoSpace()
    {
        var builder = new NovaConnectionStringBuilder();
        builder["datasource"] = "/tmp/data";

        Assert.Equal("/tmp/data", builder.DataSource);
    }

    [Fact(DisplayName = "连接字符串构建器-ConnectionTimeout别名映射")]
    public void AliasMapping_ConnectionTimeout()
    {
        var builder = new NovaConnectionStringBuilder();
        builder["connection timeout"] = 45;

        Assert.Equal(45, builder.ConnectionTimeout);
    }

    [Fact(DisplayName = "连接字符串构建器-CommandTimeout别名映射")]
    public void AliasMapping_CommandTimeout()
    {
        var builder = new NovaConnectionStringBuilder();
        builder["default command timeout"] = 90;

        Assert.Equal(90, builder.CommandTimeout);
    }

    #endregion

    #region 默认值

    [Fact(DisplayName = "连接字符串构建器-默认端口")]
    public void DefaultPort()
    {
        var builder = new NovaConnectionStringBuilder("Server=localhost");

        Assert.Equal(3306, builder.Port);
    }

    [Fact(DisplayName = "连接字符串构建器-默认超时")]
    public void DefaultTimeout()
    {
        var builder = new NovaConnectionStringBuilder();

        Assert.Equal(15, builder.ConnectionTimeout);
        Assert.Equal(30, builder.CommandTimeout);
    }

    #endregion

    #region 往返一致性

    [Fact(DisplayName = "连接字符串构建器-往返一致性")]
    public void RoundTrip()
    {
        var original = "Server=localhost;Port=5678;Database=testdb";
        var builder = new NovaConnectionStringBuilder(original);

        Assert.Equal("localhost", builder.Server);
        Assert.Equal(5678, builder.Port);
        Assert.Equal("testdb", builder.Database);

        var connStr = builder.ConnectionString;
        Assert.Contains("Server=localhost", connStr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "连接字符串构建器-属性修改后反映到ConnectionString")]
    public void PropertyChangeReflectedInConnectionString()
    {
        var builder = new NovaConnectionStringBuilder("Server=host1;Port=3306");
        builder.Server = "host2";
        builder.Port = 5678;

        var connStr = builder.ConnectionString;
        Assert.Contains("host2", connStr, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region IsEmbedded 判断

    [Fact(DisplayName = "连接字符串构建器-有DataSource时为嵌入模式")]
    public void IsEmbedded_WithDataSource()
    {
        var builder = new NovaConnectionStringBuilder("Data Source=./db");
        Assert.True(builder.IsEmbedded);
    }

    [Fact(DisplayName = "连接字符串构建器-无DataSource时非嵌入模式")]
    public void IsEmbedded_WithoutDataSource()
    {
        var builder = new NovaConnectionStringBuilder("Server=localhost");
        Assert.False(builder.IsEmbedded);
    }

    [Fact(DisplayName = "连接字符串构建器-空构造非嵌入模式")]
    public void IsEmbedded_EmptyBuilder()
    {
        var builder = new NovaConnectionStringBuilder();
        Assert.False(builder.IsEmbedded);
    }

    #endregion
}
