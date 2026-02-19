using System;
using System.Data;
using System.Data.Common;
using NewLife.NovaDb.Client;
using Xunit;

namespace XUnitTest.Client;

/// <summary>NovaDb 客户端工厂单元测试</summary>
public class NovaClientFactoryTests
{
    #region NovaClientFactory 测试

    [Fact(DisplayName = "工厂-默认实例不为空")]
    public void FactoryInstanceNotNull()
    {
        Assert.NotNull(NovaClientFactory.Instance);
    }

    [Fact(DisplayName = "工厂-创建连接")]
    public void FactoryCreateConnection()
    {
        var conn = NovaClientFactory.Instance.CreateConnection();

        Assert.NotNull(conn);
        Assert.IsType<NovaConnection>(conn);
    }

    [Fact(DisplayName = "工厂-创建命令")]
    public void FactoryCreateCommand()
    {
        var cmd = NovaClientFactory.Instance.CreateCommand();

        Assert.NotNull(cmd);
        Assert.IsType<NovaCommand>(cmd);
    }

    [Fact(DisplayName = "工厂-创建参数")]
    public void FactoryCreateParameter()
    {
        var param = NovaClientFactory.Instance.CreateParameter();

        Assert.NotNull(param);
        Assert.IsType<NovaParameter>(param);
    }

    [Fact(DisplayName = "工厂-创建连接字符串构建器")]
    public void FactoryCreateConnectionStringBuilder()
    {
        var builder = NovaClientFactory.Instance.CreateConnectionStringBuilder();

        Assert.NotNull(builder);
        Assert.IsType<NovaConnectionStringBuilder>(builder);
    }

    [Fact(DisplayName = "工厂-创建数据适配器")]
    public void FactoryCreateDataAdapter()
    {
        var adapter = NovaClientFactory.Instance.CreateDataAdapter();

        Assert.NotNull(adapter);
        Assert.IsType<NovaDataAdapter>(adapter);
    }

    [Fact(DisplayName = "工厂-不支持数据源枚举")]
    public void FactoryCannotCreateDataSourceEnumerator()
    {
        Assert.False(NovaClientFactory.Instance.CanCreateDataSourceEnumerator);
    }

// NovaBatch 需要 NET6_0_OR_GREATER 目标，当前主项目最高 netstandard2.1，暂不测试批量命令

    [Fact(DisplayName = "工厂-创建的连接关联工厂")]
    public void FactoryConnectionHasFactory()
    {
        var conn = (NovaConnection)NovaClientFactory.Instance.CreateConnection();

        Assert.Same(NovaClientFactory.Instance, conn.Factory);
    }

    #endregion

    #region NovaConnectionStringBuilder 测试

    [Fact(DisplayName = "连接字符串-嵌入模式解析")]
    public void ConnectionStringEmbeddedMode()
    {
        var builder = new NovaConnectionStringBuilder("Data Source=./test.db;Database=mydb");

        Assert.True(builder.IsEmbedded);
        Assert.Equal("./test.db", builder.DataSource);
        Assert.Equal("mydb", builder.Database);
    }

    [Fact(DisplayName = "连接字符串-服务器模式解析")]
    public void ConnectionStringServerMode()
    {
        var builder = new NovaConnectionStringBuilder("Server=localhost;Port=5678;Database=mydb");

        Assert.False(builder.IsEmbedded);
        Assert.Equal("localhost", builder.Server);
        Assert.Equal(5678, builder.Port);
        Assert.Equal("mydb", builder.Database);
    }

    [Fact(DisplayName = "连接字符串-默认端口")]
    public void ConnectionStringDefaultPort()
    {
        var builder = new NovaConnectionStringBuilder("Server=localhost");

        Assert.Equal(3306, builder.Port);
    }

    [Fact(DisplayName = "连接字符串-默认超时")]
    public void ConnectionStringDefaultTimeout()
    {
        var builder = new NovaConnectionStringBuilder();

        Assert.Equal(15, builder.ConnectionTimeout);
        Assert.Equal(30, builder.CommandTimeout);
    }

    [Fact(DisplayName = "连接字符串-自定义超时")]
    public void ConnectionStringCustomTimeout()
    {
        var builder = new NovaConnectionStringBuilder("Server=localhost;ConnectionTimeout=60;CommandTimeout=120");

        Assert.Equal(60, builder.ConnectionTimeout);
        Assert.Equal(120, builder.CommandTimeout);
    }

    [Fact(DisplayName = "连接字符串-别名映射")]
    public void ConnectionStringAliasMapping()
    {
        // "data source" 应映射到 DataSource
        var builder = new NovaConnectionStringBuilder();
        builder["data source"] = "./mydb";

        Assert.Equal("./mydb", builder.DataSource);
        Assert.True(builder.IsEmbedded);
    }

    [Fact(DisplayName = "连接字符串-往返一致性")]
    public void ConnectionStringRoundTrip()
    {
        var original = "Server=localhost;Port=5678;Database=testdb";
        var builder = new NovaConnectionStringBuilder(original);

        Assert.Equal("localhost", builder.Server);
        Assert.Equal(5678, builder.Port);
        Assert.Equal("testdb", builder.Database);

        // 通过 builder 设置后应能重新输出
        var connStr = builder.ConnectionString;
        Assert.Contains("Server=localhost", connStr, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region NovaConnection 集成工厂测试

    [Fact(DisplayName = "连接-构造函数传入连接字符串")]
    public void ConnectionConstructorWithConnString()
    {
        using var conn = new NovaConnection("Data Source=./test.db");

        Assert.True(conn.IsEmbedded);
        Assert.Equal("./test.db", conn.DataSource);
    }

    [Fact(DisplayName = "连接-嵌入模式DataSource属性")]
    public void ConnectionEmbeddedDataSource()
    {
        using var conn = new NovaConnection { ConnectionString = "Data Source=./test.db" };

        Assert.True(conn.IsEmbedded);
        Assert.Equal("./test.db", conn.DataSource);
    }

    [Fact(DisplayName = "连接-服务器模式DataSource属性")]
    public void ConnectionServerDataSource()
    {
        using var conn = new NovaConnection { ConnectionString = "Server=192.168.1.1;Port=5678" };

        Assert.False(conn.IsEmbedded);
        Assert.Equal("192.168.1.1", conn.DataSource);
    }

    [Fact(DisplayName = "连接-数据库名称")]
    public void ConnectionDatabaseName()
    {
        using var conn = new NovaConnection("Data Source=./test.db;Database=mydb");

        Assert.Equal("mydb", conn.Database);
    }

    [Fact(DisplayName = "连接-连接超时")]
    public void ConnectionTimeout()
    {
        using var conn = new NovaConnection("Data Source=./test.db;ConnectionTimeout=60");

        Assert.Equal(60, conn.ConnectionTimeout);
    }

    [Fact(DisplayName = "连接-DbProviderFactory属性")]
    public void ConnectionDbProviderFactory()
    {
        using var conn = new NovaConnection();

        // 验证 DbProviderFactory 可通过 DbConnection.DbProviderFactory 获取
        Assert.Same(NovaClientFactory.Instance, conn.Factory);
    }

    #endregion

    #region NovaDataAdapter 测试

    [Fact(DisplayName = "数据适配器-默认构造")]
    public void DataAdapterDefaultConstructor()
    {
        var adapter = new NovaDataAdapter();
        Assert.NotNull(adapter);
    }

    [Fact(DisplayName = "数据适配器-使用命令构造")]
    public void DataAdapterWithCommand()
    {
        var cmd = new NovaCommand { CommandText = "SELECT 1" };
        var adapter = new NovaDataAdapter(cmd);

        Assert.NotNull(adapter);
        Assert.Same(cmd, adapter.SelectCommand);
    }

    #endregion

    #region 多次创建独立实例

    [Fact(DisplayName = "工厂-CreateCommand每次返回新实例")]
    public void CreateCommand_ReturnsNewInstanceEachTime()
    {
        var factory = NovaClientFactory.Instance;
        var a = factory.CreateCommand();
        var b = factory.CreateCommand();

        Assert.NotSame(a, b);
    }

    [Fact(DisplayName = "工厂-CreateConnection每次返回新实例")]
    public void CreateConnection_ReturnsNewInstanceEachTime()
    {
        var factory = NovaClientFactory.Instance;
        var a = factory.CreateConnection();
        var b = factory.CreateConnection();

        Assert.NotSame(a, b);
    }

    [Fact(DisplayName = "工厂-CreateParameter每次返回新实例")]
    public void CreateParameter_ReturnsNewInstanceEachTime()
    {
        var factory = NovaClientFactory.Instance;
        var a = factory.CreateParameter();
        var b = factory.CreateParameter();

        Assert.NotSame(a, b);
    }

    [Fact(DisplayName = "工厂-CreateConnectionStringBuilder每次返回新实例")]
    public void CreateConnectionStringBuilder_ReturnsNewInstanceEachTime()
    {
        var factory = NovaClientFactory.Instance;
        var a = factory.CreateConnectionStringBuilder();
        var b = factory.CreateConnectionStringBuilder();

        Assert.NotSame(a, b);
    }

    [Fact(DisplayName = "工厂-CreateDataAdapter每次返回新实例")]
    public void CreateDataAdapter_ReturnsNewInstanceEachTime()
    {
        var factory = NovaClientFactory.Instance;
        var a = factory.CreateDataAdapter();
        var b = factory.CreateDataAdapter();

        Assert.NotSame(a, b);
    }

    #endregion

    #region 通过基类接口调用

    [Fact(DisplayName = "工厂-通过DbProviderFactory创建命令")]
    public void AsDbProviderFactory_CreateCommand()
    {
        DbProviderFactory factory = NovaClientFactory.Instance;

        var cmd = factory.CreateCommand();

        Assert.IsType<NovaCommand>(cmd);
    }

    [Fact(DisplayName = "工厂-通过DbProviderFactory创建连接")]
    public void AsDbProviderFactory_CreateConnection()
    {
        DbProviderFactory factory = NovaClientFactory.Instance;

        var conn = factory.CreateConnection();

        Assert.IsType<NovaConnection>(conn);
    }

    [Fact(DisplayName = "工厂-通过DbProviderFactory创建参数")]
    public void AsDbProviderFactory_CreateParameter()
    {
        DbProviderFactory factory = NovaClientFactory.Instance;

        var param = factory.CreateParameter();

        Assert.IsType<NovaParameter>(param);
    }

    [Fact(DisplayName = "工厂-通过DbProviderFactory创建连接字符串构建器")]
    public void AsDbProviderFactory_CreateConnectionStringBuilder()
    {
        DbProviderFactory factory = NovaClientFactory.Instance;

        var builder = factory.CreateConnectionStringBuilder();

        Assert.IsType<NovaConnectionStringBuilder>(builder);
    }

    [Fact(DisplayName = "工厂-通过DbProviderFactory创建数据适配器")]
    public void AsDbProviderFactory_CreateDataAdapter()
    {
        DbProviderFactory factory = NovaClientFactory.Instance;

        var adapter = factory.CreateDataAdapter();

        Assert.IsType<NovaDataAdapter>(adapter);
    }

    #endregion

    #region 能力属性

    [Fact(DisplayName = "工厂-CanCreateCommandBuilder为false")]
    public void CanCreateCommandBuilder_ReturnsFalse()
    {
        Assert.False(NovaClientFactory.Instance.CanCreateCommandBuilder);
    }

    [Fact(DisplayName = "工厂-CanCreateDataAdapter为true")]
    public void CanCreateDataAdapter_ReturnsTrue()
    {
        Assert.True(NovaClientFactory.Instance.CanCreateDataAdapter);
    }

    #endregion
}
