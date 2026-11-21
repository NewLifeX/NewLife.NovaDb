using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

#nullable enable

namespace XUnitTest.Sql;

/// <summary>查询访问计划测试：索引选择、范围扫描、LIMIT 下推</summary>
public class IndexPlanTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public IndexPlanTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"IndexPlanTests_{Guid.NewGuid():N}");
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

    private void CreateUsersTable()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR(50), age INT)");
        _engine.Execute("CREATE INDEX idx_name ON users (name)");
    }

    private void InsertUsers(Int32 count)
    {
        for (var i = 1; i <= count; i++)
            _engine.Execute($"INSERT INTO users (id, name, age) VALUES ({i}, 'user{i}', {i})");
    }

    private String[] QueryColumn(String sql)
    {
        var result = _engine.Execute(sql);
        return result.Rows.Select(r => Convert.ToString(r[0])!).ToArray();
    }

    private String ExplainType(String sql) => Convert.ToString(_engine.Execute("EXPLAIN " + sql).Rows[0][1])!;

    [Fact(DisplayName = "二级索引等值查询走 INDEX LOOKUP")]
    public void ExplainSelect_IndexLookup()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30)");

        var result = _engine.Execute("EXPLAIN SELECT * FROM users WHERE name = 'Alice'");

        Assert.Equal("INDEX LOOKUP", result.Rows[0][1]);
        Assert.Equal("users", result.Rows[0][2]);
        Assert.Equal("idx_name(name)", result.Rows[0][3]);
        Assert.Equal("1", result.Rows[0][4]);
    }

    [Fact(DisplayName = "主键范围查询走 PK RANGE")]
    public void ExplainSelect_PrimaryKeyRange()
    {
        CreateUsersTable();

        Assert.Equal("PK RANGE", ExplainType("SELECT * FROM users WHERE id > 5"));
        Assert.Equal("PK RANGE", ExplainType("SELECT * FROM users WHERE id >= 5 AND id < 10"));
        Assert.Equal("PK RANGE", ExplainType("SELECT * FROM users WHERE 5 < id AND id <= 10"));
    }

    [Fact(DisplayName = "未建索引的列仍为全表扫描")]
    public void ExplainSelect_UnindexedColumnUsesFullScan()
    {
        CreateUsersTable();
        var result = _engine.Execute("EXPLAIN SELECT * FROM users WHERE age = 30");

        Assert.Equal("FULL SCAN", result.Rows[0][1]);
        Assert.Equal(String.Empty, result.Rows[0][3]);
        Assert.Contains("Residual", Convert.ToString(result.Rows[0][5])!);
    }

    [Fact(DisplayName = "索引查询结果与全表扫描完全一致")]
    public void IndexLookup_SameResultAsFullScan()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'Alice', 25), (3, 'Bob', 40)");

        var withIndex = _engine.Execute("SELECT id, name, age FROM users WHERE name = 'Alice'");

        // 删除索引后同一语句退化为全表扫描，结果必须一致
        _engine.Execute("DROP INDEX idx_name ON users");
        var withoutIndex = _engine.Execute("SELECT id, name, age FROM users WHERE name = 'Alice'");

        Assert.Equal(2, withIndex.Rows.Count);
        Assert.Equal(withoutIndex.Rows.Count, withIndex.Rows.Count);
        for (var i = 0; i < withIndex.Rows.Count; i++)
            Assert.Equal(withoutIndex.Rows[i], withIndex.Rows[i]);
    }

    [Fact(DisplayName = "索引等值查询保持大小写不敏感语义")]
    public void IndexLookup_CaseInsensitive()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'ALICE', 31), (3, 'alice', 32), (4, 'Bob', 40)");

        Assert.Equal(3, Convert.ToInt32(_engine.Execute("SELECT COUNT(*) FROM users WHERE name = 'aLiCe'").GetScalar()));

        // 大小写不同的 IN 值规范化后是同一个键，不能重复返回
        Assert.Equal(new[] { "1", "2", "3" }, QueryColumn("SELECT id FROM users WHERE name IN ('alice', 'ALICE')").OrderBy(e => e).ToArray());
    }

    [Fact(DisplayName = "唯一索引对字符串大小写不敏感")]
    public void UniqueIndex_CaseInsensitive()
    {
        _engine.Execute("CREATE TABLE codes (id INT PRIMARY KEY, code VARCHAR(20), UNIQUE KEY ux_code (code))");
        _engine.Execute("INSERT INTO codes (id, code) VALUES (1, 'Abc')");

        Assert.Throws<NovaException>(() => _engine.Execute("INSERT INTO codes (id, code) VALUES (2, 'ABC')"));

        // 更新为大小写不同的同值不视为冲突
        _engine.Execute("UPDATE codes SET code = 'aBC' WHERE id = 1");
        var result = _engine.Execute("SELECT id FROM codes WHERE code = 'abc'");
        Assert.Single(result.Rows);
    }

    [Fact(DisplayName = "索引 IN 查询只返回匹配行")]
    public void IndexLookup_InList()
    {
        CreateUsersTable();
        InsertUsers(5);

        Assert.Equal("INDEX LOOKUP", ExplainType("SELECT * FROM users WHERE name IN ('user2', 'user4')"));
        Assert.Equal(new[] { "2", "4" }, QueryColumn("SELECT id FROM users WHERE name IN ('user2', 'user4')").OrderBy(e => e).ToArray());
    }

    [Fact(DisplayName = "索引命中的候选行仍按完整 WHERE 复核")]
    public void IndexLookup_ResidualFilterApplied()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'Alice', 25)");

        var result = _engine.Execute("SELECT id FROM users WHERE name = 'Alice' AND age > 26");
        Assert.Single(result.Rows);
        Assert.Equal("1", Convert.ToString(result.Rows[0][0])!);

        // 存在残余条件时不能做 LIMIT 提前截断
        var plan = _engine.Execute("EXPLAIN SELECT id FROM users WHERE name = 'Alice' AND age > 26");
        Assert.Contains("Residual", Convert.ToString(plan.Rows[0][5])!);
    }

    [Fact(DisplayName = "主键范围查询边界与结果正确")]
    public void PrimaryKeyRange_Bounds()
    {
        CreateUsersTable();
        InsertUsers(10);

        Assert.Equal(new[] { "4", "5", "6", "7" }, QueryColumn("SELECT id FROM users WHERE id > 3 AND id <= 7"));
        Assert.Equal(new[] { "1", "2", "3" }, QueryColumn("SELECT id FROM users WHERE id < 4"));
        Assert.Equal(new[] { "8", "9", "10" }, QueryColumn("SELECT id FROM users WHERE 7 < id"));
        Assert.Equal(new[] { "5" }, QueryColumn("SELECT id FROM users WHERE id >= 5 AND id <= 5"));
        Assert.Empty(QueryColumn("SELECT id FROM users WHERE id > 10"));
        Assert.Empty(QueryColumn("SELECT id FROM users WHERE id >= 8 AND id <= 3"));
    }

    [Fact(DisplayName = "主键范围查询多个边界取更紧的一侧")]
    public void PrimaryKeyRange_TighterBoundWins()
    {
        CreateUsersTable();
        InsertUsers(10);

        Assert.Equal(new[] { "6", "7", "8", "9", "10" }, QueryColumn("SELECT id FROM users WHERE id > 3 AND id >= 6"));
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, QueryColumn("SELECT id FROM users WHERE id < 8 AND id <= 5"));
        Assert.Equal(new[] { "6", "7" }, QueryColumn("SELECT id FROM users WHERE id >= 6 AND id < 8"));
        Assert.Empty(QueryColumn("SELECT id FROM users WHERE id > 8 AND id < 6"));
    }

    [Fact(DisplayName = "整数主键遇小数边界不漏行")]
    public void PrimaryKeyRange_FractionalBound()
    {
        CreateUsersTable();
        InsertUsers(10);

        Assert.Equal(new[] { "2", "3", "4" }, QueryColumn("SELECT id FROM users WHERE id > 1.5 AND id < 4.5"));
        Assert.Equal(new[] { "1", "2" }, QueryColumn("SELECT id FROM users WHERE id <= 2.5"));
        Assert.Equal(new[] { "2", "3", "4", "5", "6", "7", "8", "9", "10" }, QueryColumn("SELECT id FROM users WHERE id > 1.4"));
        Assert.Empty(QueryColumn("SELECT id FROM users WHERE id = 1.5"));
    }

    [Fact(DisplayName = "参数化等值查询走索引且结果正确")]
    public void IndexLookup_WithParameter()
    {
        CreateUsersTable();
        InsertUsers(5);

        var parameters = new Dictionary<String, Object?> { ["@name"] = "user3" };
        var sql = "SELECT id FROM users WHERE name = @name";

        Assert.Equal("INDEX LOOKUP", Convert.ToString(_engine.Execute("EXPLAIN " + sql, parameters).Rows[0][1])!);
        Assert.Equal("3", Convert.ToString(_engine.Execute(sql, parameters).Rows[0][0])!);
    }

    [Fact(DisplayName = "同一列多个等值条件互相矛盾时返回空集")]
    public void IndexLookup_ContradictoryEquality()
    {
        CreateUsersTable();
        InsertUsers(5);

        Assert.Empty(QueryColumn("SELECT id FROM users WHERE id = 2 AND id = 3"));
        Assert.Empty(QueryColumn("SELECT id FROM users WHERE name = 'user2' AND name = 'user3'"));
        Assert.Single(QueryColumn("SELECT id FROM users WHERE id = 2 AND id = 2"));
    }

    [Fact(DisplayName = "LIMIT 下推结果正确")]
    public void LimitPushdown_ResultCorrect()
    {
        CreateUsersTable();
        InsertUsers(100);

        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, QueryColumn("SELECT id FROM users LIMIT 5"));
        Assert.Equal(new[] { "3", "4", "5", "6", "7" }, QueryColumn("SELECT id FROM users LIMIT 5 OFFSET 2"));
        Assert.Equal(new[] { "91", "92", "93" }, QueryColumn("SELECT id FROM users WHERE id > 90 LIMIT 3"));
        Assert.Equal(new[] { "11", "12" }, QueryColumn("SELECT id FROM users WHERE id > 10 LIMIT 2"));
    }

    [Fact(DisplayName = "低选择性索引回退全表扫描")]
    public void UnselectiveIndex_FallsBackToFullScan()
    {
        _engine.Execute("CREATE TABLE logs (id INT PRIMARY KEY, level INT)");
        for (var i = 1; i <= 100; i++)
            _engine.Execute($"INSERT INTO logs (id, level) VALUES ({i}, {(i == 100 ? 9 : 1)})");
        _engine.Execute("CREATE INDEX idx_level ON logs (level)");

        // 条件命中 99/100 行，回表比顺序扫描更慢
        Assert.Equal("FULL SCAN", ExplainType("SELECT * FROM logs WHERE level = 1"));
        Assert.Equal("INDEX LOOKUP", ExplainType("SELECT * FROM logs WHERE level = 9"));
        Assert.Single(_engine.Execute("SELECT id FROM logs WHERE level = 9").Rows);
    }

    [Fact(DisplayName = "主键范围更新与删除只影响命中行")]
    public void PrimaryKeyRange_UpdateAndDelete()
    {
        CreateUsersTable();
        InsertUsers(10);

        Assert.Equal(3, _engine.Execute("UPDATE users SET age = 0 WHERE id > 7").AffectedRows);
        Assert.Equal(3, Convert.ToInt32(_engine.Execute("SELECT COUNT(*) FROM users WHERE age = 0").GetScalar()));

        Assert.Equal(2, _engine.Execute("DELETE FROM users WHERE id <= 2").AffectedRows);
        Assert.Equal(8, Convert.ToInt32(_engine.Execute("SELECT COUNT(*) FROM users").GetScalar()));
        Assert.Equal("PK RANGE", ExplainType("DELETE FROM users WHERE id <= 2"));
    }

    [Fact(DisplayName = "二级索引文件重载后查询结果一致")]
    public void SecondaryIndex_ReloadFromFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"IndexPlanReload_{Guid.NewGuid():N}");
        try
        {
            using (var engine = new SqlEngine(dir, new DbOptions { Path = dir, WalMode = WalMode.None }))
            {
                engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR(50), age INT)");
                engine.Execute("CREATE INDEX idx_name ON users (name)");
                engine.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'ALICE', 31), (3, 'Bob', 40)");
            }

            using (var engine = new SqlEngine(dir, new DbOptions { Path = dir, WalMode = WalMode.None }))
            {
                Assert.Equal("INDEX LOOKUP", Convert.ToString(engine.Execute("EXPLAIN SELECT * FROM users WHERE name = 'alice'").Rows[0][1])!);

                var result = engine.Execute("SELECT id FROM users WHERE name = 'alice'");
                Assert.Equal(new[] { "1", "2" }, result.Rows.Select(r => Convert.ToString(r[0])!).OrderBy(e => e).ToArray());
            }
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { }
            }
        }
    }
}

