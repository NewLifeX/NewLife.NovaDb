using System;
using System.Data;
using System.IO;
using NewLife.NovaDb.Client;
using Xunit;

namespace XUnitTest.Client;

/// <summary>NovaCommand 单元测试</summary>
public class NovaCommandTests : IDisposable
{
    private readonly String _dbPath;

    public NovaCommandTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaCmd_{Guid.NewGuid():N}");
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

    #region 属性测试

    [Fact(DisplayName = "命令-默认CommandText为空")]
    public void CommandText_DefaultEmpty()
    {
        using var cmd = new NovaCommand();
        Assert.Equal(String.Empty, cmd.CommandText);
    }

    [Fact(DisplayName = "命令-CommandText读写")]
    public void CommandText_GetSet()
    {
        using var cmd = new NovaCommand { CommandText = "SELECT 1" };
        Assert.Equal("SELECT 1", cmd.CommandText);
    }

    [Fact(DisplayName = "命令-CommandText设置null变为空")]
    public void CommandText_SetNull_BecomesEmpty()
    {
        using var cmd = new NovaCommand { CommandText = "SELECT 1" };
        cmd.CommandText = null!;
        Assert.Equal(String.Empty, cmd.CommandText);
    }

    [Fact(DisplayName = "命令-默认CommandTimeout为30")]
    public void CommandTimeout_Default()
    {
        using var cmd = new NovaCommand();
        Assert.Equal(30, cmd.CommandTimeout);
    }

    [Fact(DisplayName = "命令-CommandTimeout读写")]
    public void CommandTimeout_GetSet()
    {
        using var cmd = new NovaCommand { CommandTimeout = 60 };
        Assert.Equal(60, cmd.CommandTimeout);
    }

    [Fact(DisplayName = "命令-默认CommandType为Text")]
    public void CommandType_Default()
    {
        using var cmd = new NovaCommand();
        Assert.Equal(CommandType.Text, cmd.CommandType);
    }

    [Fact(DisplayName = "命令-CommandType读写")]
    public void CommandType_GetSet()
    {
        using var cmd = new NovaCommand { CommandType = CommandType.StoredProcedure };
        Assert.Equal(CommandType.StoredProcedure, cmd.CommandType);
    }

    [Fact(DisplayName = "命令-DesignTimeVisible读写")]
    public void DesignTimeVisible_GetSet()
    {
        using var cmd = new NovaCommand { DesignTimeVisible = true };
        Assert.True(cmd.DesignTimeVisible);
    }

    [Fact(DisplayName = "命令-UpdatedRowSource读写")]
    public void UpdatedRowSource_GetSet()
    {
        using var cmd = new NovaCommand { UpdatedRowSource = UpdateRowSource.FirstReturnedRecord };
        Assert.Equal(UpdateRowSource.FirstReturnedRecord, cmd.UpdatedRowSource);
    }

    #endregion

    #region 参数测试

    [Fact(DisplayName = "命令-CreateParameter返回NovaParameter")]
    public void CreateParameter_ReturnsNovaParameter()
    {
        using var cmd = new NovaCommand();
        var param = cmd.CreateParameter();

        Assert.NotNull(param);
        Assert.IsType<NovaParameter>(param);
    }

    [Fact(DisplayName = "命令-Parameters添加和读取")]
    public void Parameters_AddAndRead()
    {
        using var cmd = new NovaCommand();
        var param = cmd.CreateParameter();
        param.ParameterName = "@id";
        param.Value = 42;
        param.DbType = DbType.Int32;

        cmd.Parameters.Add(param);
        Assert.Single(cmd.Parameters);
    }

    [Fact(DisplayName = "命令-Parameters默认为空")]
    public void Parameters_DefaultEmpty()
    {
        using var cmd = new NovaCommand();
        Assert.Empty(cmd.Parameters);
    }

    #endregion

    #region Cancel / Prepare

    [Fact(DisplayName = "命令-Cancel不抛异常")]
    public void Cancel_DoesNotThrow()
    {
        using var cmd = new NovaCommand();
        cmd.Cancel();
    }

    [Fact(DisplayName = "命令-Prepare不抛异常")]
    public void Prepare_DoesNotThrow()
    {
        using var cmd = new NovaCommand();
        cmd.Prepare();
    }

    [Fact(DisplayName = "命令-正常执行不超时")]
    public void ExecuteWithTimeout_NormalExecution()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE timeout_test (id INT PRIMARY KEY, name VARCHAR)";
        cmd.CommandTimeout = 30;
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO timeout_test (id, name) VALUES (1, 'test')";
        var affected = cmd.ExecuteNonQuery();
        Assert.Equal(1, affected);
    }

    [Fact(DisplayName = "命令-CommandTimeout为0不超时")]
    public void ExecuteWithTimeout_ZeroMeansNoTimeout()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE no_timeout (id INT PRIMARY KEY)";
        cmd.CommandTimeout = 0;
        cmd.ExecuteNonQuery();
    }

    #endregion

    #region ExecuteNonQuery 嵌入模式

    [Fact(DisplayName = "命令-ExecuteNonQuery创建表")]
    public void ExecuteNonQuery_CreateTable()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE cmd_create (id INT PRIMARY KEY, name VARCHAR)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(0, rows);
    }

    [Fact(DisplayName = "命令-ExecuteNonQuery插入")]
    public void ExecuteNonQuery_Insert()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE cmd_ins (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO cmd_ins VALUES (1, 'Alice')";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);
    }

    [Fact(DisplayName = "命令-ExecuteNonQuery更新")]
    public void ExecuteNonQuery_Update()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE cmd_upd (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO cmd_upd VALUES (1, 'Alice'), (2, 'Bob')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "UPDATE cmd_upd SET name = 'Alice2' WHERE id = 1";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);
    }

    [Fact(DisplayName = "命令-ExecuteNonQuery删除")]
    public void ExecuteNonQuery_Delete()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE cmd_del (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO cmd_del VALUES (1, 'Alice'), (2, 'Bob')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "DELETE FROM cmd_del WHERE id = 1";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);
    }

    [Fact(DisplayName = "命令-ExecuteNonQuery无连接返回0")]
    public void ExecuteNonQuery_NoConnection_ReturnsZero()
    {
        using var cmd = new NovaCommand { CommandText = "SELECT 1" };
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(0, rows);
    }

    #endregion

    #region ExecuteScalar 嵌入模式

    [Fact(DisplayName = "命令-ExecuteScalar返回标量值")]
    public void ExecuteScalar_ReturnsScalar()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE cmd_scalar (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO cmd_scalar VALUES (1, 'Alice'), (2, 'Bob')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT COUNT(*) FROM cmd_scalar";
        var result = cmd.ExecuteScalar();
        Assert.Equal(2, Convert.ToInt32(result));
    }

    [Fact(DisplayName = "命令-ExecuteScalar无连接返回null")]
    public void ExecuteScalar_NoConnection_ReturnsNull()
    {
        using var cmd = new NovaCommand { CommandText = "SELECT 1" };
        var result = cmd.ExecuteScalar();
        Assert.Null(result);
    }

    #endregion

    #region ExecuteReader 嵌入模式

    [Fact(DisplayName = "命令-ExecuteReader返回NovaDataReader")]
    public void ExecuteReader_ReturnsNovaDataReader()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE cmd_rdr (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO cmd_rdr VALUES (1, 'Alice')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT * FROM cmd_rdr";
        using var reader = cmd.ExecuteReader();
        Assert.NotNull(reader);
        Assert.IsType<NovaDataReader>(reader);
    }

    [Fact(DisplayName = "命令-ExecuteReader遍历行")]
    public void ExecuteReader_ReadRows()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE cmd_rdr2 (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO cmd_rdr2 VALUES (1, 'Alice'), (2, 'Bob')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT * FROM cmd_rdr2";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.HasRows);

        var count = 0;
        while (reader.Read()) count++;
        Assert.Equal(2, count);
    }

    [Fact(DisplayName = "命令-ExecuteReader读取列名")]
    public void ExecuteReader_ReadColumnNames()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE cmd_rdr3 (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO cmd_rdr3 VALUES (1, 'Alice', 25)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT id, name, age FROM cmd_rdr3";
        using var reader = cmd.ExecuteReader();
        Assert.Equal(3, reader.FieldCount);
        Assert.Equal("id", reader.GetName(0));
        Assert.Equal("name", reader.GetName(1));
        Assert.Equal("age", reader.GetName(2));
    }

    [Fact(DisplayName = "命令-ExecuteReader读取值")]
    public void ExecuteReader_ReadValues()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE cmd_rdr4 (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO cmd_rdr4 VALUES (1, 'Alice')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT id, name FROM cmd_rdr4";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("Alice", reader.GetString(1));
        Assert.False(reader.Read());
    }

    #endregion

    #region Connection.ExecuteNonQuery 便捷方法

    [Fact(DisplayName = "命令-Connection.ExecuteNonQuery便捷方法")]
    public void Connection_ExecuteNonQuery()
    {
        using var conn = CreateConnection();

        var rows = conn.ExecuteNonQuery("CREATE TABLE cmd_conn_exec (id INT PRIMARY KEY)");
        Assert.Equal(0, rows);

        rows = conn.ExecuteNonQuery("INSERT INTO cmd_conn_exec VALUES (1)");
        Assert.Equal(1, rows);
    }

    #endregion
}
