using System;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

namespace XUnitTest.Sql;

/// <summary>EXPLAIN 查询计划测试（O01）</summary>
public class ExplainTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public ExplainTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"ExplainTests_{Guid.NewGuid():N}");
        _engine = new SqlEngine(_testDir, new DbOptions { Path = _testDir, WalMode = WalMode.None });
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); }
            catch { }
        }
    }

    private void CreateTables()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name STRING(50), age INT)");
        _engine.Execute("CREATE TABLE orders (id INT PRIMARY KEY, user_id INT, total DOUBLE)");
    }

    [Fact(DisplayName = "EXPLAIN SELECT 全表扫描")]
    public void ExplainSelect_FullScan()
    {
        CreateTables();
        var r = _engine.Execute("EXPLAIN SELECT * FROM users");

        // 返回计划列
        Assert.True(r.IsQuery);
        Assert.Equal(new[] { "id", "type", "table", "key", "rows", "extra" }, r.ColumnNames);

        // 首行应为 FULL SCAN
        var first = r.Rows[0];
        Assert.Equal("1", first[0]);
        Assert.Equal("FULL SCAN", first[1]);
        Assert.Equal("users", first[2]);
    }

    [Fact(DisplayName = "EXPLAIN SELECT 主键查找")]
    public void ExplainSelect_PkLookup()
    {
        CreateTables();
        var r = _engine.Execute("EXPLAIN SELECT * FROM users WHERE id = 1");

        Assert.Equal("PK LOOKUP", r.Rows[0][1]);
        Assert.Equal("users", r.Rows[0][2]);
        Assert.StartsWith("PRIMARY", (String)r.Rows[0][3]!);
        Assert.Equal("1", r.Rows[0][4]);
    }

    [Fact(DisplayName = "EXPLAIN SELECT 无表")]
    public void ExplainSelect_NoTable()
    {
        var r = _engine.Execute("EXPLAIN SELECT 1");
        Assert.Equal("NO TABLE", r.Rows[0][1]);
        Assert.Equal("1", r.Rows[0][4]);
    }

    [Fact(DisplayName = "EXPLAIN SELECT 表不存在仍返回计划")]
    public void ExplainSelect_UnknownTable()
    {
        var r = _engine.Execute("EXPLAIN SELECT * FROM not_exist");
        Assert.Equal("FULL SCAN", r.Rows[0][1]);
        Assert.Equal("not_exist", r.Rows[0][2]);
    }

    [Fact(DisplayName = "EXPLAIN SELECT GROUP BY 步骤")]
    public void ExplainSelect_GroupBy()
    {
        CreateTables();
        var r = _engine.Execute("EXPLAIN SELECT age, COUNT(*) FROM users GROUP BY age");

        // 首行为 FULL SCAN，后续应含 GROUP BY 步骤
        Assert.Contains(r.Rows, row => row[1] as String == "GROUP BY");
        var groupRow = r.Rows.First(row => row[1] as String == "GROUP BY");
        Assert.Contains("Columns: age", (String)groupRow[5]!);
    }

    [Fact(DisplayName = "EXPLAIN SELECT ORDER BY 步骤")]
    public void ExplainSelect_OrderBy()
    {
        CreateTables();
        var r = _engine.Execute("EXPLAIN SELECT * FROM users ORDER BY age DESC");
        Assert.Contains(r.Rows, row => row[1] as String == "SORT");
    }

    [Fact(DisplayName = "EXPLAIN SELECT JOIN 步骤")]
    public void ExplainSelect_Join()
    {
        CreateTables();
        var r = _engine.Execute(
            "EXPLAIN SELECT o.id, u.name FROM orders o INNER JOIN users u ON o.user_id = u.id");

        Assert.Contains(r.Rows, row =>
        {
            var type = row[1] as String;
            return type != null && type.Contains("JOIN");
        });
    }

    [Fact(DisplayName = "EXPLAIN INSERT 计划")]
    public void ExplainInsert()
    {
        CreateTables();
        var r = _engine.Execute("EXPLAIN INSERT INTO users (id, name, age) VALUES (1, 'Alice', 25)");

        Assert.Equal("INSERT", r.Rows[0][1]);
        Assert.Equal("users", r.Rows[0][2]);
        Assert.Equal("1", r.Rows[0][4]);
    }

    [Fact(DisplayName = "EXPLAIN UPDATE 主键查找")]
    public void ExplainUpdate_PkLookup()
    {
        CreateTables();
        var r = _engine.Execute("EXPLAIN UPDATE users SET age = 30 WHERE id = 1");
        Assert.Equal("PK LOOKUP", r.Rows[0][1]);
        Assert.Equal("users", r.Rows[0][2]);
    }

    [Fact(DisplayName = "EXPLAIN UPDATE 无 WHERE 全表扫描")]
    public void ExplainUpdate_FullScan()
    {
        CreateTables();
        var r = _engine.Execute("EXPLAIN UPDATE users SET age = 30");
        Assert.Equal("FULL SCAN", r.Rows[0][1]);
    }

    [Fact(DisplayName = "EXPLAIN DELETE 主键查找")]
    public void ExplainDelete_PkLookup()
    {
        CreateTables();
        var r = _engine.Execute("EXPLAIN DELETE FROM users WHERE id = 1");
        Assert.Equal("PK LOOKUP", r.Rows[0][1]);
        Assert.Equal("users", r.Rows[0][2]);
    }

    [Fact(DisplayName = "EXPLAIN DELETE 带 WHERE 过滤扫描")]
    public void ExplainDelete_FilteredScan()
    {
        CreateTables();
        var r = _engine.Execute("EXPLAIN DELETE FROM users WHERE age > 18");
        Assert.Equal("FILTERED SCAN", r.Rows[0][1]);
    }

    [Fact(DisplayName = "EXPLAIN 与真实查询相互独立")]
    public void Explain_DoesNotMutateData()
    {
        CreateTables();
        _engine.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 25)");

        // EXPLAIN 不产生数据变更
        _engine.Execute("EXPLAIN DELETE FROM users WHERE id = 1");
        var count = _engine.Execute("SELECT COUNT(*) FROM users").GetScalar();
        Assert.Equal(1, Convert.ToInt32(count));
    }
}
