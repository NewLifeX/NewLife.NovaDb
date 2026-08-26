using System;
using System.IO;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

namespace XUnitTest.Core;

/// <summary>NovaMetrics 指标追踪测试</summary>
public class NovaMetricsTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public NovaMetricsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"NovaMetricsTests_{Guid.NewGuid():N}");
        _engine = new SqlEngine(_testDir, new DbOptions { Path = _testDir, WalMode = WalMode.None });
    }

    public void Dispose()
    {
        _engine.Dispose();

        if (Directory.Exists(_testDir))
        {
            try
            {
                Directory.Delete(_testDir, recursive: true);
            }
            catch
            {
                // 忽略清理错误
            }
        }
    }

    [Fact(DisplayName = "测试初始指标值")]
    public void TestInitialMetrics()
    {
        var metrics = _engine.Metrics;

        Assert.Equal(0, metrics.ExecuteCount);
        Assert.Equal(0, metrics.QueryCount);
        Assert.Equal(0, metrics.InsertCount);
        Assert.Equal(0, metrics.DdlCount);
        Assert.True(metrics.StartTime <= DateTime.Now);
        Assert.True(metrics.Uptime.TotalSeconds >= 0);
    }

    [Fact(DisplayName = "测试 DDL 计数")]
    public void TestDdlCount()
    {
        _engine.Execute("CREATE TABLE t1 (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("CREATE TABLE t2 (id INT PRIMARY KEY, value INT)");

        Assert.Equal(2, _engine.Metrics.DdlCount);
        Assert.Equal(2, _engine.Metrics.ExecuteCount);
    }

    [Fact(DisplayName = "测试插入计数")]
    public void TestInsertCount()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice')");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob')");

        Assert.Equal(2, _engine.Metrics.InsertCount);
    }

    [Fact(DisplayName = "测试查询计数")]
    public void TestQueryCount()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice')");
        _engine.Execute("SELECT * FROM users");
        _engine.Execute("SELECT * FROM users WHERE id = 1");

        Assert.Equal(2, _engine.Metrics.QueryCount);
    }

    [Fact(DisplayName = "测试更新计数")]
    public void TestUpdateCount()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice')");
        _engine.Execute("UPDATE users SET name = 'Bob' WHERE id = 1");

        Assert.Equal(1, _engine.Metrics.UpdateCount);
    }

    [Fact(DisplayName = "测试删除计数")]
    public void TestDeleteCount()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice')");
        _engine.Execute("DELETE FROM users WHERE id = 1");

        Assert.Equal(1, _engine.Metrics.DeleteCount);
    }

    [Fact(DisplayName = "测试综合执行计数")]
    public void TestTotalExecuteCount()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice')");
        _engine.Execute("SELECT * FROM users");
        _engine.Execute("UPDATE users SET name = 'Bob' WHERE id = 1");
        _engine.Execute("DELETE FROM users WHERE id = 1");

        Assert.Equal(5, _engine.Metrics.ExecuteCount);
        Assert.Equal(1, _engine.Metrics.DdlCount);
        Assert.Equal(1, _engine.Metrics.InsertCount);
        Assert.Equal(1, _engine.Metrics.QueryCount);
        Assert.Equal(1, _engine.Metrics.UpdateCount);
        Assert.Equal(1, _engine.Metrics.DeleteCount);
    }

    [Fact(DisplayName = "测试运行时长")]
    public void TestUptime()
    {
        var metrics = _engine.Metrics;

        Assert.True(metrics.Uptime.TotalMilliseconds >= 0);
        Assert.True(metrics.Uptime.TotalHours < 1);
    }

    [Fact(DisplayName = "TotalRows 随 INSERT 增加")]
    public void TestTotalRows_InsertIncreases()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice')");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob'), (3, 'Charlie')");

        Assert.Equal(3, _engine.Metrics.TotalRows);
    }

    [Fact(DisplayName = "TotalRows 随 DELETE 减少")]
    public void TestTotalRows_DeleteDecreases()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice'), (2, 'Bob'), (3, 'Charlie')");
        _engine.Execute("DELETE FROM users WHERE id >= 2");

        Assert.Equal(1, _engine.Metrics.TotalRows);
    }

    [Fact(DisplayName = "TotalRows 不因 UPDATE 变化")]
    public void TestTotalRows_UpdateUnchanged()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice')");
        _engine.Execute("UPDATE users SET name = 'Bob' WHERE id = 1");

        Assert.Equal(1, _engine.Metrics.TotalRows);
    }

    [Fact(DisplayName = "CommitCount 随 COMMIT 增加")]
    public void TestCommitCount()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("BEGIN");
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice')");
        _engine.Execute("COMMIT");

        Assert.Equal(1, _engine.Metrics.CommitCount);
        Assert.Equal(0, _engine.Metrics.RollbackCount);
    }

    [Fact(DisplayName = "RollbackCount 随 ROLLBACK 增加")]
    public void TestRollbackCount()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("BEGIN");
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice')");
        _engine.Execute("ROLLBACK");

        Assert.Equal(1, _engine.Metrics.RollbackCount);
        Assert.Equal(0, _engine.Metrics.CommitCount);
    }

    [Fact(DisplayName = "AddTotalRows 线程安全累加")]
    public void TestAddTotalRows_ThreadSafe()
    {
        var metrics = new NovaMetrics();
        var tasks = new System.Threading.Tasks.Task[8];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = System.Threading.Tasks.Task.Run(() =>
            {
                for (var j = 0; j < 1000; j++)
                    metrics.AddTotalRows(1);
            });
        }
        System.Threading.Tasks.Task.WaitAll(tasks);

        Assert.Equal(8000, metrics.TotalRows);
    }
}
