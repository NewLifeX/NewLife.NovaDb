using System;
using System.Data;
using System.Threading.Tasks;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Server;
using Xunit;

namespace XUnitTest.Client;

/// <summary>NovaDb 连接单元测试</summary>
[Collection("IntegrationTests")]
public class NovaConnectionTests
{
    [Fact(DisplayName = "测试打开和关闭嵌入模式连接")]
    public void TestOpenAndClose()
    {
        using var conn = new NovaConnection
        {
            ConnectionString = "Data Source=./test.db"
        };

        Assert.Equal(ConnectionState.Closed, conn.State);

        conn.Open();
        Assert.Equal(ConnectionState.Open, conn.State);
        Assert.Null(conn.Client); // embedded mode, no remote client

        conn.Close();
        Assert.Equal(ConnectionState.Closed, conn.State);
    }

    [Fact(DisplayName = "测试嵌入模式检测")]
    public void TestEmbeddedModeDetection()
    {
        using var conn = new NovaConnection
        {
            ConnectionString = "Data Source=./test.db"
        };

        Assert.True(conn.IsEmbedded);
        Assert.Equal("./test.db", conn.DataSource);
    }

    [Fact(DisplayName = "测试服务器模式检测")]
    public void TestServerModeDetection()
    {
        using var conn = new NovaConnection
        {
            ConnectionString = "Server=localhost;Port=3306"
        };

        Assert.False(conn.IsEmbedded);
        Assert.Equal("localhost", conn.DataSource);
    }

    [Fact(DisplayName = "测试创建命令")]
    public void TestCreateCommand()
    {
        using var conn = new NovaConnection
        {
            ConnectionString = "Data Source=./test.db"
        };

        using var cmd = conn.CreateCommand();
        Assert.NotNull(cmd);
        Assert.IsType<NovaCommand>(cmd);
    }

    [Fact(DisplayName = "测试事务")]
    public void TestTransaction()
    {
        using var conn = new NovaConnection
        {
            ConnectionString = "Data Source=./test.db"
        };
        conn.Open();

        using var tx = conn.BeginTransaction();
        Assert.NotNull(tx);
        Assert.IsType<NovaTransaction>(tx);

        var novaDbTx = (NovaTransaction)tx;
        Assert.False(novaDbTx.IsCompleted);
        Assert.Equal(IsolationLevel.ReadCommitted, tx.IsolationLevel);

        tx.Commit();
        Assert.True(novaDbTx.IsCompleted);
    }

    [Fact(DisplayName = "测试事务回滚")]
    public void TestTransactionRollback()
    {
        using var conn = new NovaConnection
        {
            ConnectionString = "Data Source=./test.db"
        };
        conn.Open();

        using var tx = conn.BeginTransaction();
        var novaDbTx = (NovaTransaction)tx;

        tx.Rollback();
        Assert.True(novaDbTx.IsCompleted);
    }

    [Fact(DisplayName = "测试切换数据库")]
    public void TestChangeDatabase()
    {
        using var conn = new NovaConnection
        {
            ConnectionString = "Data Source=./test.db"
        };

        conn.ChangeDatabase("newdb");
        Assert.Equal("newdb", conn.Database);
    }

    [Fact(DisplayName = "测试服务器版本")]
    public void TestServerVersion()
    {
        using var conn = new NovaConnection();
        Assert.Equal("1.0", conn.ServerVersion);
    }

    [Fact(DisplayName = "测试命令属性")]
    public void TestCommandProperties()
    {
        using var cmd = new NovaCommand
        {
            CommandText = "SELECT 1",
            CommandTimeout = 60,
            CommandType = CommandType.Text
        };

        Assert.Equal("SELECT 1", cmd.CommandText);
        Assert.Equal(60, cmd.CommandTimeout);
        Assert.Equal(CommandType.Text, cmd.CommandType);
    }

    [Fact(DisplayName = "测试命令参数")]
    public void TestCommandParameters()
    {
        using var cmd = new NovaCommand();
        var param = cmd.CreateParameter();

        Assert.NotNull(param);
        Assert.IsType<NovaParameter>(param);

        param.ParameterName = "@id";
        param.Value = 42;
        param.DbType = DbType.Int32;

        cmd.Parameters.Add(param);
        Assert.Equal(1, cmd.Parameters.Count);
    }

    [Fact(DisplayName = "测试数据读取器")]
    public void TestDataReader()
    {
        var reader = new NovaDataReader();
        reader.SetColumns("Id", "Name");
        reader.AddRow([1, "Alice"]);
        reader.AddRow([2, "Bob"]);

        Assert.Equal(2, reader.FieldCount);
        Assert.True(reader.HasRows);

        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("Alice", reader.GetString(1));

        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal("Bob", reader.GetString(1));

        Assert.False(reader.Read());

        reader.Close();
        Assert.True(reader.IsClosed);
    }

    [Fact(DisplayName = "测试参数集合操作")]
    public void TestParameterCollectionOperations()
    {
        var collection = new NovaParameterCollection();

        var p1 = new NovaParameter { ParameterName = "@id", Value = 1 };
        var p2 = new NovaParameter { ParameterName = "@name", Value = "test" };

        collection.Add(p1);
        collection.Add(p2);

        Assert.Equal(2, collection.Count);
        Assert.True(collection.Contains(p1));
        Assert.True(collection.Contains("@id"));
        Assert.Equal(0, collection.IndexOf("@id"));
        Assert.Equal(1, collection.IndexOf("@name"));

        collection.RemoveAt("@name");
        Assert.Equal(1, collection.Count);

        collection.Clear();
        Assert.Equal(0, collection.Count);
    }

    [Fact(DisplayName = "测试客户端服务端端到端通信")]
    public async Task TestClientServerEndToEnd()
    {
        // Start a server on a random port
        using var server = new NovaServer(0);
        server.Start();
        var port = server.Port;
        Assert.True(port > 0);

        // Create client and connect
        using var client = new NovaClient($"tcp://127.0.0.1:{port}");
        client.Open();
        Assert.True(client.IsConnected);

        // Test ping
        var result = await client.PingAsync();
        Assert.NotNull(result);
        Assert.NotEmpty(result);

        // Test execute
        var rows = await client.ExecuteAsync("SELECT 1");
        Assert.Equal(0, rows); // stub returns 0

        // Test begin transaction
        var txId = await client.BeginTransactionAsync();
        Assert.NotNull(txId);
        Assert.NotEmpty(txId);

        // Test commit
        var committed = await client.CommitTransactionAsync(txId!);
        Assert.True(committed);

        // Test rollback
        var txId2 = await client.BeginTransactionAsync();
        var rolledBack = await client.RollbackTransactionAsync(txId2!);
        Assert.True(rolledBack);

        // Close
        client.Close();
        Assert.False(client.IsConnected);
    }
}
