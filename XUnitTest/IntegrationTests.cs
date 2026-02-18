using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Server;
using Xunit;

namespace XUnitTest;

/// <summary>集成测试服务器夹具，在所有集成测试之间共享同一个 NovaServer 实例</summary>
public class IntegrationServerFixture : IDisposable
{
    /// <summary>NovaDb 服务器实例</summary>
    public NovaDbServer Server { get; }

    /// <summary>服务器端口</summary>
    public Int32 Port { get; }

    /// <summary>数据库路径</summary>
    public String DbPath { get; }

    public IntegrationServerFixture()
    {
        DbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"NovaDbIntegration_{Guid.NewGuid():N}");
        Server = new NovaDbServer(0) { DbPath = DbPath };
        Server.Start();
        Port = Server.Port;
    }

    public void Dispose()
    {
        Server.Dispose();

        if (!String.IsNullOrEmpty(DbPath) && System.IO.Directory.Exists(DbPath))
        {
            try { System.IO.Directory.Delete(DbPath, recursive: true); }
            catch { }
        }
    }
}

/// <summary>NovaDb 集成大测试，启动 NovaServer 后通过客户端完成完整数据库操作</summary>
[Collection("IntegrationTests")]
public class IntegrationTests : IClassFixture<IntegrationServerFixture>
{
    private readonly IntegrationServerFixture _fixture;
    private Int32 _port => _fixture.Port;

    public IntegrationTests(IntegrationServerFixture fixture)
    {
        _fixture = fixture;
    }

    #region 连接测试

    [Fact(DisplayName = "集成测试-服务器启动与客户端连接")]
    public async Task TestServerStartAndClientConnect()
    {
        Assert.True(_fixture.Server.IsRunning);
        Assert.True(_port > 0);

        using var client = new NovaDbClient($"tcp://127.0.0.1:{_port}");
        client.Open();
        Assert.True(client.IsConnected);

        var result = await client.PingAsync();
        Assert.NotNull(result);
        Assert.NotEmpty(result);

        client.Close();
        Assert.False(client.IsConnected);
    }

    [Fact(DisplayName = "集成测试-ADO.NET连接")]
    public void TestAdoNetConnection()
    {
        using var conn = new NovaDbConnection
        {
            ConnectionString = $"Server=127.0.0.1;Port={_port}"
        };

        Assert.Equal(ConnectionState.Closed, conn.State);
        Assert.False(conn.IsEmbedded);

        conn.Open();
        Assert.Equal(ConnectionState.Open, conn.State);
        Assert.NotNull(conn.Client);
        Assert.True(conn.Client!.IsConnected);

        conn.Close();
        Assert.Equal(ConnectionState.Closed, conn.State);
    }

    #endregion

    #region 创建表测试

    [Fact(DisplayName = "集成测试-RPC创建表")]
    public async Task TestRpcCreateTable()
    {
        using var client = new NovaDbClient($"tcp://127.0.0.1:{_port}");
        client.Open();

        var rows = await client.ExecuteAsync("CREATE TABLE t_rpc_create (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT)");
        Assert.Equal(0, rows);

        // 再次创建 IF NOT EXISTS 不报错
        rows = await client.ExecuteAsync("CREATE TABLE IF NOT EXISTS t_rpc_create (id INT PRIMARY KEY, name VARCHAR)");
        Assert.Equal(0, rows);
    }

    [Fact(DisplayName = "集成测试-ADO.NET创建表")]
    public void TestAdoNetCreateTable()
    {
        using var conn = new NovaDbConnection { ConnectionString = $"Server=127.0.0.1;Port={_port}" };
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE t_ado_create (id INT PRIMARY KEY, name VARCHAR NOT NULL, score DOUBLE)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(0, rows);
    }

    #endregion

    #region 添删改查测试

    [Fact(DisplayName = "集成测试-RPC添删改查")]
    public async Task TestRpcCrud()
    {
        using var client = new NovaDbClient($"tcp://127.0.0.1:{_port}");
        client.Open();

        // 创建表
        await client.ExecuteAsync("CREATE TABLE t_rpc_crud (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT)");

        // INSERT
        var rows = await client.ExecuteAsync("INSERT INTO t_rpc_crud (id, name, age) VALUES (1, 'Alice', 25)");
        Assert.Equal(1, rows);

        rows = await client.ExecuteAsync("INSERT INTO t_rpc_crud VALUES (2, 'Bob', 30)");
        Assert.Equal(1, rows);

        // SELECT - 通过 Query 验证
        var result = await client.QueryAsync<IDictionary<String, Object>>("SELECT * FROM t_rpc_crud");
        Assert.NotNull(result);

        // UPDATE
        rows = await client.ExecuteAsync("UPDATE t_rpc_crud SET name = 'Alice Smith', age = 26 WHERE id = 1");
        Assert.Equal(1, rows);

        // 验证 UPDATE 结果
        result = await client.QueryAsync<IDictionary<String, Object>>("SELECT name, age FROM t_rpc_crud WHERE id = 1");
        Assert.NotNull(result);

        // DELETE
        rows = await client.ExecuteAsync("DELETE FROM t_rpc_crud WHERE id = 2");
        Assert.Equal(1, rows);

        // 验证 DELETE 后只剩一条
        result = await client.QueryAsync<IDictionary<String, Object>>("SELECT COUNT(*) FROM t_rpc_crud");
        Assert.NotNull(result);
    }

    [Fact(DisplayName = "集成测试-ADO.NET添删改查")]
    public void TestAdoNetCrud()
    {
        using var conn = new NovaDbConnection { ConnectionString = $"Server=127.0.0.1;Port={_port}" };
        conn.Open();
        using var cmd = conn.CreateCommand();

        // 创建表
        cmd.CommandText = "CREATE TABLE t_ado_crud (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT)";
        cmd.ExecuteNonQuery();

        // INSERT
        cmd.CommandText = "INSERT INTO t_ado_crud (id, name, age) VALUES (1, 'Alice', 25)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);

        cmd.CommandText = "INSERT INTO t_ado_crud VALUES (2, 'Bob', 30)";
        rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);

        cmd.CommandText = "INSERT INTO t_ado_crud VALUES (3, 'Charlie', 35)";
        rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);

        // UPDATE
        cmd.CommandText = "UPDATE t_ado_crud SET name = 'Alice Smith', age = 26 WHERE id = 1";
        rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);

        // DELETE
        cmd.CommandText = "DELETE FROM t_ado_crud WHERE id = 3";
        rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);
    }

    [Fact(DisplayName = "集成测试-ADO.NET查询DataReader")]
    public void TestAdoNetQueryDataReader()
    {
        using var conn = new NovaDbConnection { ConnectionString = $"Server=127.0.0.1;Port={_port}" };
        conn.Open();
        using var cmd = conn.CreateCommand();

        // 创建表并插入数据
        cmd.CommandText = "CREATE TABLE t_ado_reader (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO t_ado_reader VALUES (1, 'Alice', 25)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO t_ado_reader VALUES (2, 'Bob', 30)";
        cmd.ExecuteNonQuery();

        // SELECT 查询
        cmd.CommandText = "SELECT * FROM t_ado_reader ORDER BY id ASC";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.HasRows);
        Assert.Equal(3, reader.FieldCount);

        Assert.True(reader.Read());
        Assert.Equal("Alice", reader.GetString(1));

        Assert.True(reader.Read());
        Assert.Equal("Bob", reader.GetString(1));

        Assert.False(reader.Read());
    }

    [Fact(DisplayName = "集成测试-ADO.NET ExecuteScalar")]
    public void TestAdoNetExecuteScalar()
    {
        using var conn = new NovaDbConnection { ConnectionString = $"Server=127.0.0.1;Port={_port}" };
        conn.Open();
        using var cmd = conn.CreateCommand();

        // 创建表并插入数据
        cmd.CommandText = "CREATE TABLE t_ado_scalar (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO t_ado_scalar VALUES (1, 'Alice')";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO t_ado_scalar VALUES (2, 'Bob')";
        cmd.ExecuteNonQuery();

        // COUNT 标量查询
        cmd.CommandText = "SELECT COUNT(*) FROM t_ado_scalar";
        var count = cmd.ExecuteScalar();
        Assert.NotNull(count);
    }

    #endregion

    #region 批量操作测试

    [Fact(DisplayName = "集成测试-RPC批量插入")]
    public async Task TestRpcBatchInsert()
    {
        using var client = new NovaDbClient($"tcp://127.0.0.1:{_port}");
        client.Open();

        await client.ExecuteAsync("CREATE TABLE t_rpc_batch (id INT PRIMARY KEY, name VARCHAR NOT NULL, score INT)");

        // 批量插入多行
        var rows = await client.ExecuteAsync("INSERT INTO t_rpc_batch VALUES (1, 'Alice', 90), (2, 'Bob', 85), (3, 'Charlie', 92), (4, 'Diana', 88), (5, 'Eve', 95)");
        Assert.Equal(5, rows);
    }

    [Fact(DisplayName = "集成测试-ADO.NET批量插入")]
    public void TestAdoNetBatchInsert()
    {
        using var conn = new NovaDbConnection { ConnectionString = $"Server=127.0.0.1;Port={_port}" };
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE t_ado_batch (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT)";
        cmd.ExecuteNonQuery();

        // 多行 INSERT
        cmd.CommandText = "INSERT INTO t_ado_batch VALUES (1, 'Alice', 25), (2, 'Bob', 30), (3, 'Charlie', 35), (4, 'Diana', 28), (5, 'Eve', 32)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(5, rows);
    }

    [Fact(DisplayName = "集成测试-RPC批量查询")]
    public async Task TestRpcBatchQuery()
    {
        using var client = new NovaDbClient($"tcp://127.0.0.1:{_port}");
        client.Open();

        await client.ExecuteAsync("CREATE TABLE t_rpc_bquery (id INT PRIMARY KEY, name VARCHAR, score INT)");
        await client.ExecuteAsync("INSERT INTO t_rpc_bquery VALUES (1, 'Alice', 90), (2, 'Bob', 85), (3, 'Charlie', 92), (4, 'Diana', 88), (5, 'Eve', 95)");

        // 查询全部
        var result = await client.QueryAsync<IDictionary<String, Object>>("SELECT * FROM t_rpc_bquery");
        Assert.NotNull(result);

        // 条件查询
        result = await client.QueryAsync<IDictionary<String, Object>>("SELECT * FROM t_rpc_bquery WHERE score >= 90");
        Assert.NotNull(result);

        // 排序查询
        result = await client.QueryAsync<IDictionary<String, Object>>("SELECT * FROM t_rpc_bquery ORDER BY score DESC");
        Assert.NotNull(result);

        // LIMIT 查询
        result = await client.QueryAsync<IDictionary<String, Object>>("SELECT * FROM t_rpc_bquery ORDER BY score DESC LIMIT 3");
        Assert.NotNull(result);
    }

    [Fact(DisplayName = "集成测试-ADO.NET批量查询")]
    public void TestAdoNetBatchQuery()
    {
        using var conn = new NovaDbConnection { ConnectionString = $"Server=127.0.0.1;Port={_port}" };
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE t_ado_bquery (id INT PRIMARY KEY, name VARCHAR, score INT)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO t_ado_bquery VALUES (1, 'Alice', 90), (2, 'Bob', 85), (3, 'Charlie', 92)";
        cmd.ExecuteNonQuery();

        // 查询全部
        cmd.CommandText = "SELECT * FROM t_ado_bquery ORDER BY id ASC";
        using var reader = cmd.ExecuteReader();
        var rowCount = 0;
        while (reader.Read()) rowCount++;
        Assert.Equal(3, rowCount);
    }

    [Fact(DisplayName = "集成测试-RPC批量修改")]
    public async Task TestRpcBatchUpdate()
    {
        using var client = new NovaDbClient($"tcp://127.0.0.1:{_port}");
        client.Open();

        await client.ExecuteAsync("CREATE TABLE t_rpc_bupd (id INT PRIMARY KEY, name VARCHAR, status INT)");
        await client.ExecuteAsync("INSERT INTO t_rpc_bupd VALUES (1, 'Alice', 0), (2, 'Bob', 0), (3, 'Charlie', 0), (4, 'Diana', 1)");

        // 批量更新：将 status=0 的全部改为 1
        var rows = await client.ExecuteAsync("UPDATE t_rpc_bupd SET status = 1 WHERE status = 0");
        Assert.Equal(3, rows);
    }

    [Fact(DisplayName = "集成测试-ADO.NET批量修改")]
    public void TestAdoNetBatchUpdate()
    {
        using var conn = new NovaDbConnection { ConnectionString = $"Server=127.0.0.1;Port={_port}" };
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE t_ado_bupd (id INT PRIMARY KEY, name VARCHAR, status INT)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO t_ado_bupd VALUES (1, 'Alice', 0), (2, 'Bob', 0), (3, 'Charlie', 1)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "UPDATE t_ado_bupd SET status = 2 WHERE status = 0";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(2, rows);
    }

    [Fact(DisplayName = "集成测试-RPC批量删除")]
    public async Task TestRpcBatchDelete()
    {
        using var client = new NovaDbClient($"tcp://127.0.0.1:{_port}");
        client.Open();

        await client.ExecuteAsync("CREATE TABLE t_rpc_bdel (id INT PRIMARY KEY, name VARCHAR, age INT)");
        await client.ExecuteAsync("INSERT INTO t_rpc_bdel VALUES (1, 'Alice', 20), (2, 'Bob', 30), (3, 'Charlie', 40), (4, 'Diana', 25)");

        // 批量删除 age < 30 的记录
        var rows = await client.ExecuteAsync("DELETE FROM t_rpc_bdel WHERE age < 30");
        Assert.Equal(2, rows);
    }

    [Fact(DisplayName = "集成测试-ADO.NET批量删除")]
    public void TestAdoNetBatchDelete()
    {
        using var conn = new NovaDbConnection { ConnectionString = $"Server=127.0.0.1;Port={_port}" };
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE t_ado_bdel (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO t_ado_bdel VALUES (1, 'Alice', 20), (2, 'Bob', 30), (3, 'Charlie', 40)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "DELETE FROM t_ado_bdel WHERE age >= 30";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(2, rows);
    }

    #endregion

    #region 事务测试

    [Fact(DisplayName = "集成测试-RPC事务提交")]
    public async Task TestRpcTransactionCommit()
    {
        using var client = new NovaDbClient($"tcp://127.0.0.1:{_port}");
        client.Open();

        // 开始事务
        var txId = await client.BeginTransactionAsync();
        Assert.NotNull(txId);
        Assert.NotEmpty(txId);

        // 提交事务
        var committed = await client.CommitTransactionAsync(txId!);
        Assert.True(committed);
    }

    [Fact(DisplayName = "集成测试-RPC事务回滚")]
    public async Task TestRpcTransactionRollback()
    {
        using var client = new NovaDbClient($"tcp://127.0.0.1:{_port}");
        client.Open();

        // 开始事务
        var txId = await client.BeginTransactionAsync();
        Assert.NotNull(txId);

        // 回滚事务
        var rolledBack = await client.RollbackTransactionAsync(txId!);
        Assert.True(rolledBack);
    }

    [Fact(DisplayName = "集成测试-ADO.NET事务提交")]
    public void TestAdoNetTransactionCommit()
    {
        using var conn = new NovaDbConnection { ConnectionString = $"Server=127.0.0.1;Port={_port}" };
        conn.Open();

        // 通过 ADO.NET 开始事务
        using var tx = conn.BeginTransaction();
        Assert.NotNull(tx);
        Assert.IsType<NovaDbTransaction>(tx);

        var novaDbTx = (NovaDbTransaction)tx;
        Assert.False(novaDbTx.IsCompleted);
        Assert.NotNull(novaDbTx.TxId);
        Assert.NotEmpty(novaDbTx.TxId);

        // 提交
        tx.Commit();
        Assert.True(novaDbTx.IsCompleted);
    }

    [Fact(DisplayName = "集成测试-ADO.NET事务回滚")]
    public void TestAdoNetTransactionRollback()
    {
        using var conn = new NovaDbConnection { ConnectionString = $"Server=127.0.0.1;Port={_port}" };
        conn.Open();

        using var tx = conn.BeginTransaction();
        var novaDbTx = (NovaDbTransaction)tx;
        Assert.False(novaDbTx.IsCompleted);

        // 回滚
        tx.Rollback();
        Assert.True(novaDbTx.IsCompleted);
    }

    [Fact(DisplayName = "集成测试-RPC多次事务")]
    public async Task TestRpcMultipleTransactions()
    {
        using var client = new NovaDbClient($"tcp://127.0.0.1:{_port}");
        client.Open();

        // 第一个事务：提交
        var txId1 = await client.BeginTransactionAsync();
        Assert.NotNull(txId1);
        var committed = await client.CommitTransactionAsync(txId1!);
        Assert.True(committed);

        // 第二个事务：回滚
        var txId2 = await client.BeginTransactionAsync();
        Assert.NotNull(txId2);
        var rolledBack = await client.RollbackTransactionAsync(txId2!);
        Assert.True(rolledBack);

        // 第三个事务：提交
        var txId3 = await client.BeginTransactionAsync();
        Assert.NotNull(txId3);
        committed = await client.CommitTransactionAsync(txId3!);
        Assert.True(committed);

        // 所有事务 ID 应不同
        Assert.NotEqual(txId1, txId2);
        Assert.NotEqual(txId2, txId3);
    }

    #endregion

    #region 综合端到端测试

    [Fact(DisplayName = "集成测试-完整业务流程")]
    public async Task TestFullBusinessFlow()
    {
        using var client = new NovaDbClient($"tcp://127.0.0.1:{_port}");
        client.Open();

        // 1. Ping
        var ping = await client.PingAsync();
        Assert.NotNull(ping);

        // 2. 创建表
        await client.ExecuteAsync("CREATE TABLE t_flow (id INT PRIMARY KEY, name VARCHAR NOT NULL, price DOUBLE, quantity INT)");

        // 3. 插入数据
        await client.ExecuteAsync("INSERT INTO t_flow VALUES (1, 'Widget', 9.99, 100)");
        await client.ExecuteAsync("INSERT INTO t_flow VALUES (2, 'Gadget', 19.99, 50)");
        await client.ExecuteAsync("INSERT INTO t_flow VALUES (3, 'Doohickey', 4.99, 200)");

        // 4. 查询所有数据
        var result = await client.QueryAsync<IDictionary<String, Object>>("SELECT * FROM t_flow");
        Assert.NotNull(result);

        // 5. 条件查询
        result = await client.QueryAsync<IDictionary<String, Object>>("SELECT name, price FROM t_flow WHERE price > 5.0");
        Assert.NotNull(result);

        // 6. 更新数据
        var rows = await client.ExecuteAsync("UPDATE t_flow SET quantity = 150 WHERE id = 1");
        Assert.Equal(1, rows);

        // 7. 删除数据
        rows = await client.ExecuteAsync("DELETE FROM t_flow WHERE id = 3");
        Assert.Equal(1, rows);

        // 8. 验证最终状态
        result = await client.QueryAsync<IDictionary<String, Object>>("SELECT COUNT(*) FROM t_flow");
        Assert.NotNull(result);

        // 9. 事务操作
        var txId = await client.BeginTransactionAsync();
        Assert.NotNull(txId);
        var committed = await client.CommitTransactionAsync(txId!);
        Assert.True(committed);

        // 10. DROP 表
        rows = await client.ExecuteAsync("DROP TABLE t_flow");
        Assert.Equal(0, rows);

        // 11. 验证表已删除
        rows = await client.ExecuteAsync("DROP TABLE IF EXISTS t_flow");
        Assert.Equal(0, rows);
    }

    [Fact(DisplayName = "集成测试-ADO.NET完整流程")]
    public void TestAdoNetFullFlow()
    {
        using var conn = new NovaDbConnection { ConnectionString = $"Server=127.0.0.1;Port={_port}" };
        conn.Open();

        using var cmd = conn.CreateCommand();

        // 1. 创建表
        cmd.CommandText = "CREATE TABLE t_ado_flow (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT)";
        cmd.ExecuteNonQuery();

        // 2. 插入数据
        cmd.CommandText = "INSERT INTO t_ado_flow VALUES (1, 'Alice', 25), (2, 'Bob', 30), (3, 'Charlie', 35)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(3, rows);

        // 3. 查询数据
        cmd.CommandText = "SELECT * FROM t_ado_flow ORDER BY id ASC";
        using (var reader = cmd.ExecuteReader())
        {
            Assert.True(reader.HasRows);
            Assert.True(reader.Read());
            Assert.True(reader.Read());
            Assert.True(reader.Read());
            Assert.False(reader.Read());
        }

        // 4. 修改数据
        cmd.CommandText = "UPDATE t_ado_flow SET age = 26 WHERE id = 1";
        rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);

        // 5. 删除数据
        cmd.CommandText = "DELETE FROM t_ado_flow WHERE id = 3";
        rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);

        // 6. 事务操作
        using (var tx = conn.BeginTransaction())
        {
            var novaDbTx = (NovaDbTransaction)tx;
            Assert.False(novaDbTx.IsCompleted);
            tx.Commit();
            Assert.True(novaDbTx.IsCompleted);
        }

        // 7. DROP 表
        cmd.CommandText = "DROP TABLE t_ado_flow";
        cmd.ExecuteNonQuery();
    }

    #endregion
}
