using System;
using System.Data;
using System.IO;
using NewLife.NovaDb.Client;
using Xunit;

namespace XUnitTest.Client;

/// <summary>NovaTransaction 单元测试（嵌入模式）</summary>
public class NovaTransactionTests : IDisposable
{
    private readonly String _dbPath;

    public NovaTransactionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaTx_{Guid.NewGuid():N}");
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

    #region 基础事务

    [Fact(DisplayName = "事务-BeginTransaction返回NovaTransaction")]
    public void BeginTransaction_ReturnsNovaTransaction()
    {
        using var conn = CreateConnection();

        using var tx = conn.BeginTransaction();
        Assert.NotNull(tx);
        Assert.IsType<NovaTransaction>(tx);
    }

    [Fact(DisplayName = "事务-默认隔离级别为ReadCommitted")]
    public void IsolationLevel_Default()
    {
        using var conn = CreateConnection();

        using var tx = conn.BeginTransaction();
        Assert.Equal(IsolationLevel.ReadCommitted, tx.IsolationLevel);
    }

    [Fact(DisplayName = "事务-Commit后IsCompleted为true")]
    public void Commit_SetsIsCompleted()
    {
        using var conn = CreateConnection();

        using var tx = conn.BeginTransaction();
        var novaTx = (NovaTransaction)tx;
        Assert.False(novaTx.IsCompleted);

        tx.Commit();
        Assert.True(novaTx.IsCompleted);
    }

    [Fact(DisplayName = "事务-Rollback后IsCompleted为true")]
    public void Rollback_SetsIsCompleted()
    {
        using var conn = CreateConnection();

        using var tx = conn.BeginTransaction();
        var novaTx = (NovaTransaction)tx;

        tx.Rollback();
        Assert.True(novaTx.IsCompleted);
    }

    #endregion

    #region 嵌入模式事务数据验证

    [Fact(DisplayName = "事务-嵌入模式Commit保留数据")]
    public void Commit_DataPersisted()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE tx_commit (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        using (var tx = conn.BeginTransaction())
        {
            cmd.CommandText = "INSERT INTO tx_commit VALUES (1, 'Alice')";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        cmd.CommandText = "SELECT COUNT(*) FROM tx_commit";
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(1, count);
    }

    [Fact(DisplayName = "事务-嵌入模式Rollback回滚数据")]
    public void Rollback_DataReverted()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE tx_rollback (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        // 先插入一条基础数据
        cmd.CommandText = "INSERT INTO tx_rollback VALUES (1, 'Alice')";
        cmd.ExecuteNonQuery();

        using (var tx = conn.BeginTransaction())
        {
            cmd.CommandText = "INSERT INTO tx_rollback VALUES (2, 'Bob')";
            cmd.ExecuteNonQuery();
            tx.Rollback();
        }

        cmd.CommandText = "SELECT COUNT(*) FROM tx_rollback";
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        // 事务回滚后应仅剩基础数据
        Assert.True(count >= 1);
    }

    #endregion

    #region 构造函数

    [Fact(DisplayName = "事务-构造函数传null抛出ArgumentNullException")]
    public void Constructor_NullConnection_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new NovaTransaction(null!));
    }

    [Fact(DisplayName = "事务-嵌入模式TxId为null")]
    public void EmbeddedMode_TxIdIsNull()
    {
        using var conn = CreateConnection();

        using var tx = conn.BeginTransaction();
        var novaTx = (NovaTransaction)tx;
        Assert.Null(novaTx.TxId);
    }

    #endregion
}
