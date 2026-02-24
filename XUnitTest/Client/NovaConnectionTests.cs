using System;
using System.Data;
using System.IO;
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

    [Fact(DisplayName = "测试网络模式检测")]
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
        Assert.Single(cmd.Parameters);
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"NovaConnTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dbPath);
        using var server = new NovaServer(0) { DbPath = dbPath };
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

        server.Stop();
        try { Directory.Delete(dbPath, recursive: true); }
        catch { }
    }

    #region 状态转换与边界测试

    [Fact(DisplayName = "测试重复打开连接幂等")]
    public void TestOpenIdempotent()
    {
        using var conn = new NovaConnection
        {
            ConnectionString = "Data Source=./test.db"
        };

        conn.Open();
        Assert.Equal(ConnectionState.Open, conn.State);

        // 重复 Open 不应抛异常
        conn.Open();
        Assert.Equal(ConnectionState.Open, conn.State);
    }

    [Fact(DisplayName = "测试重复关闭连接幂等")]
    public void TestCloseIdempotent()
    {
        using var conn = new NovaConnection
        {
            ConnectionString = "Data Source=./test.db"
        };

        conn.Open();
        conn.Close();
        Assert.Equal(ConnectionState.Closed, conn.State);

        // 重复 Close 不应抛异常
        conn.Close();
        Assert.Equal(ConnectionState.Closed, conn.State);
    }

    [Fact(DisplayName = "测试未Open就Close不抛异常")]
    public void TestCloseWithoutOpen()
    {
        using var conn = new NovaConnection
        {
            ConnectionString = "Data Source=./test.db"
        };

        // 未 Open 直接 Close 不应抛异常
        conn.Close();
        Assert.Equal(ConnectionState.Closed, conn.State);
    }

    [Fact(DisplayName = "测试Dispose释放资源")]
    public void TestDispose()
    {
        var conn = new NovaConnection
        {
            ConnectionString = "Data Source=./test.db"
        };
        conn.Open();
        Assert.Equal(ConnectionState.Open, conn.State);

        conn.Dispose();
        // Dispose 后不应抛异常
    }

    [Fact(DisplayName = "测试Dispose重复调用不抛异常")]
    public void TestDisposeMultipleTimes()
    {
        var conn = new NovaConnection
        {
            ConnectionString = "Data Source=./test.db"
        };
        conn.Open();

        conn.Dispose();
        conn.Dispose();
        // 多次 Dispose 不应抛异常
    }

    [Fact(DisplayName = "测试默认连接字符串包含默认值")]
    public void TestDefaultConnectionString()
    {
        using var conn = new NovaConnection();
        // NovaConnectionStringBuilder 默认构造会设置 Port/ConnectionTimeout/CommandTimeout
        var connStr = conn.ConnectionString;
        Assert.Contains("Port=3306", connStr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ConnectionTimeout=15", connStr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CommandTimeout=30", connStr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "测试默认Factory不为空")]
    public void TestDefaultFactory()
    {
        using var conn = new NovaConnection();
        Assert.NotNull(conn.Factory);
        Assert.Same(NovaClientFactory.Instance, conn.Factory);
    }

    [Fact(DisplayName = "测试默认State为Closed")]
    public void TestDefaultState()
    {
        using var conn = new NovaConnection();
        Assert.Equal(ConnectionState.Closed, conn.State);
    }

    [Fact(DisplayName = "测试默认Database为空")]
    public void TestDefaultDatabase()
    {
        using var conn = new NovaConnection();
        Assert.Equal(String.Empty, conn.Database);
    }

    [Fact(DisplayName = "测试ChangeDatabase保留新值")]
    public void TestChangeDatabaseRetainsValue()
    {
        using var conn = new NovaConnection
        {
            ConnectionString = "Data Source=./test.db;Database=original"
        };

        Assert.Equal("original", conn.Database);
        conn.ChangeDatabase("newdb");
        Assert.Equal("newdb", conn.Database);
    }

    [Fact(DisplayName = "测试ConnectionString设置null变为空")]
    public void TestConnectionStringSetNull()
    {
        using var conn = new NovaConnection("Data Source=./test.db");
        conn.ConnectionString = null!;
        Assert.Equal(String.Empty, conn.ConnectionString);
    }

    [Fact(DisplayName = "测试ConnectionTimeout来自Setting")]
    public void TestConnectionTimeoutFromSetting()
    {
        using var conn = new NovaConnection("Data Source=./test.db;ConnectionTimeout=45");
        Assert.Equal(45, conn.ConnectionTimeout);
    }

    [Fact(DisplayName = "测试嵌入模式Open后SqlEngine不为空")]
    public void TestEmbeddedModeHasSqlEngine()
    {
        using var conn = new NovaConnection
        {
            ConnectionString = "Data Source=./test.db"
        };
        conn.Open();

        Assert.NotNull(conn.SqlEngine);
        Assert.Null(conn.Client);
    }

    [Fact(DisplayName = "测试嵌入模式Close后SqlEngine为空")]
    public void TestEmbeddedModeCloseReleaseSqlEngine()
    {
        using var conn = new NovaConnection
        {
            ConnectionString = "Data Source=./test.db"
        };
        conn.Open();
        Assert.NotNull(conn.SqlEngine);

        conn.Close();
        Assert.Null(conn.SqlEngine);
    }

    #endregion

    #region 参数集合边界测试

    [Fact(DisplayName = "测试参数集合Insert操作")]
    public void TestParameterCollectionInsert()
    {
        var collection = new NovaParameterCollection();

        var p1 = new NovaParameter { ParameterName = "@a", Value = 1 };
        var p2 = new NovaParameter { ParameterName = "@b", Value = 2 };
        var p3 = new NovaParameter { ParameterName = "@c", Value = 3 };

        collection.Add(p1);
        collection.Add(p3);
        collection.Insert(1, p2);

        Assert.Equal(3, collection.Count);
        Assert.Equal(1, collection.IndexOf("@b"));
    }

    [Fact(DisplayName = "测试参数集合Remove操作")]
    public void TestParameterCollectionRemove()
    {
        var collection = new NovaParameterCollection();

        var p1 = new NovaParameter { ParameterName = "@a", Value = 1 };
        var p2 = new NovaParameter { ParameterName = "@b", Value = 2 };

        collection.Add(p1);
        collection.Add(p2);

        collection.Remove(p1);
        Assert.Equal(1, collection.Count);
        Assert.False(collection.Contains(p1));
        Assert.True(collection.Contains(p2));
    }

    [Fact(DisplayName = "测试参数集合IndexOf返回-1未找到")]
    public void TestParameterCollectionIndexOfNotFound()
    {
        var collection = new NovaParameterCollection();
        Assert.Equal(-1, collection.IndexOf("@nonexistent"));
    }

    [Fact(DisplayName = "测试参数集合AddRange操作")]
    public void TestParameterCollectionAddRange()
    {
        var collection = new NovaParameterCollection();

        var parameters = new NovaParameter[]
        {
            new() { ParameterName = "@a", Value = 1 },
            new() { ParameterName = "@b", Value = 2 },
            new() { ParameterName = "@c", Value = 3 }
        };

        collection.AddRange(parameters);
        Assert.Equal(3, collection.Count);
    }

    [Fact(DisplayName = "测试参数集合CopyTo操作")]
    public void TestParameterCollectionCopyTo()
    {
        var collection = new NovaParameterCollection();
        collection.Add(new NovaParameter { ParameterName = "@a" });
        collection.Add(new NovaParameter { ParameterName = "@b" });

        var arr = new Object[2];
        collection.CopyTo(arr, 0);
        Assert.Equal(2, arr.Length);
        Assert.NotNull(arr[0]);
        Assert.NotNull(arr[1]);
    }

    [Fact(DisplayName = "测试参数集合SyncRoot不为空")]
    public void TestParameterCollectionSyncRoot()
    {
        var collection = new NovaParameterCollection();
        Assert.NotNull(collection.SyncRoot);
    }

    [Fact(DisplayName = "测试参数集合IsFixedSize为false")]
    public void TestParameterCollectionIsFixedSize()
    {
        var collection = new NovaParameterCollection();
        Assert.False(collection.IsFixedSize);
    }

    [Fact(DisplayName = "测试参数集合IsReadOnly为false")]
    public void TestParameterCollectionIsReadOnly()
    {
        var collection = new NovaParameterCollection();
        Assert.False(collection.IsReadOnly);
    }

    #endregion
}
