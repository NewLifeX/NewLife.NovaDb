using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Sql;
using NewLife.NovaDb.Tx;
using Xunit;

#nullable enable

namespace XUnitTest.Sql;

/// <summary>联合索引键的结构化比较测试（s3-index-capabilities）</summary>
public class ComparableObjectTests
{
    private static ComparableObject Key(params Object?[] values) => new(values);

    [Fact(DisplayName = "联合索引键按元素排序")]
    public void CompareTo_ArrayElements()
    {
        var keys = new List<ComparableObject>
        {
            Key(2, 1),
            Key(1, 2),
            Key(2, 0),
            Key(1, 1),
        };

        keys.Sort((x, y) => x.CompareTo(y));

        Assert.Equal(0, keys[0].CompareTo(Key(1, 1)));
        Assert.Equal(0, keys[1].CompareTo(Key(1, 2)));
        Assert.Equal(0, keys[2].CompareTo(Key(2, 0)));
        Assert.Equal(0, keys[3].CompareTo(Key(2, 1)));
    }

    [Fact(DisplayName = "联合索引键跨数值类型按值相等")]
    public void CompareTo_ArrayNumericCrossType()
    {
        Assert.Equal(0, Key(1, 2).CompareTo(Key(1L, 2d)));
        Assert.True(Key(1, 2).CompareTo(Key(1, 3)) < 0);
        Assert.True(Key(1.5, 0).CompareTo(Key(1, 9)) > 0);
    }

    [Fact(DisplayName = "前缀数组排在等前缀的完整键之前")]
    public void CompareTo_PrefixOrdering()
    {
        Assert.True(Key(1).CompareTo(Key(1, 0)) < 0);
        Assert.True(Key(1, 9).CompareTo(Key(2)) < 0);
        Assert.True(Key(2).CompareTo(Key(1, 9)) > 0);
    }

    [Fact(DisplayName = "联合索引键的等值与哈希按元素结构比较")]
    public void Equals_HashCode_Structural()
    {
        var left = Key(1, "ABC");
        var right = Key(1, "ABC");
        var other = Key(1, "ABD");

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, other);
    }

    [Fact(DisplayName = "向量与二进制键按元素比较且长度参与排序")]
    public void CompareTo_VectorAndBinary()
    {
        Assert.True(new ComparableObject(new Single[] { 1f, 2f }).CompareTo(new ComparableObject(new Single[] { 1f, 3f })) < 0);
        Assert.True(new ComparableObject(new Single[] { 1f, 2f }).CompareTo(new ComparableObject(new Single[] { 1f })) > 0);
        Assert.True(new ComparableObject(new Byte[] { 1, 2 }).CompareTo(new ComparableObject(new Byte[] { 1, 3 })) < 0);
        Assert.True(new ComparableObject(new Byte[] { 1, 2 }).CompareTo(new ComparableObject(new Byte[] { 1, 2, 3 })) < 0);
        Assert.Equal(0, new ComparableObject(new Byte[] { 1, 2 }).CompareTo(new ComparableObject(new Byte[] { 1, 2 })));
    }

    [Fact(DisplayName = "标量键比较行为保持不变")]
    public void CompareTo_ScalarUnchanged()
    {
        Assert.Equal(0, new ComparableObject(1).CompareTo(new ComparableObject(1L)));
        Assert.True(new ComparableObject("Alice").CompareTo(new ComparableObject("Bob")) < 0);
        Assert.True(new ComparableObject(1).CompareTo(null) > 0);
    }
}

/// <summary>联合索引最左前缀、OR 索引合并与索引覆盖 COUNT 测试（s3-index-capabilities）</summary>
public class CompositeIndexTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public CompositeIndexTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"CompositeIndexTests_{Guid.NewGuid():N}");
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

    private SqlResult Explain(String sql) => _engine.Execute("EXPLAIN " + sql);

    private String ExplainType(String sql) => Convert.ToString(Explain(sql).Rows[0][1])!;

    private static String[][] Rows(SqlResult result) => result.Rows.Select(r => r.Select(e => Convert.ToString(e) ?? "").ToArray()).ToArray();

    private void CreateOrders()
    {
        _engine.Execute("CREATE TABLE orders (id INT PRIMARY KEY, a INT, b INT, c INT)");
        _engine.Execute("CREATE INDEX idx_ab ON orders (a, b)");

        // a 取 1..3、b 取 1..4，id 递增
        for (var a = 1; a <= 3; a++)
        {
            for (var b = 1; b <= 4; b++)
                _engine.Execute($"INSERT INTO orders (id, a, b, c) VALUES ({(a - 1) * 4 + b}, {a}, {b}, {a * 10 + b})");
        }
    }

    [Fact(DisplayName = "联合索引前缀等值走 INDEX LOOKUP 且结果与全表扫描一致")]
    public void CompositeIndex_PrefixEquality()
    {
        CreateOrders();

        var explain = Explain("SELECT * FROM orders WHERE a = 2");
        Assert.Equal("INDEX LOOKUP", Convert.ToString(explain.Rows[0][1]));
        Assert.Equal("idx_ab(a,b)", Convert.ToString(explain.Rows[0][3]));

        var withIndex = Rows(_engine.Execute("SELECT id, a, b FROM orders WHERE a = 2 ORDER BY id"));

        _engine.Execute("DROP INDEX idx_ab ON orders");
        var fullScan = Rows(_engine.Execute("SELECT id, a, b FROM orders WHERE a = 2 ORDER BY id"));

        Assert.Equal(4, fullScan.Length);
        Assert.Equal(fullScan, withIndex);
    }

    [Fact(DisplayName = "联合索引前缀等值 + 下一列范围扫描结果与全表扫描一致")]
    public void CompositeIndex_PrefixWithRange()
    {
        CreateOrders();

        var sqls = new[]
        {
            "SELECT id, a, b FROM orders WHERE a = 2 AND b >= 2 AND b <= 3 ORDER BY id",
            "SELECT id, a, b FROM orders WHERE a = 2 AND b > 2 ORDER BY id",
            "SELECT id, a, b FROM orders WHERE a = 2 AND b < 3 ORDER BY id",
            "SELECT id, a, b FROM orders WHERE a = 2 AND b >= 1 AND b <= 2 ORDER BY id",
            "SELECT id, a, b FROM orders WHERE a = 3 AND b = 4 ORDER BY id",
        };

        var withIndex = sqls.Select(sql => Rows(_engine.Execute(sql))).ToArray();

        Assert.Equal("INDEX LOOKUP", ExplainType(sqls[0]));
        Assert.Equal(2, withIndex[0].Length);
        Assert.Single(withIndex[4]);

        _engine.Execute("DROP INDEX idx_ab ON orders");
        for (var i = 0; i < sqls.Length; i++)
        {
            Assert.Equal(Rows(_engine.Execute(sqls[i])), withIndex[i]);
        }
    }

    [Fact(DisplayName = "联合索引全列等值查询命中索引且结果一致")]
    public void CompositeIndex_FullEquality()
    {
        CreateOrders();

        var withIndex = Rows(_engine.Execute("SELECT id, a, b FROM orders WHERE a = 2 AND b = 3"));

        Assert.Equal("INDEX LOOKUP", ExplainType("SELECT id FROM orders WHERE a = 2 AND b = 3"));
        Assert.Single(withIndex);
        Assert.Equal("7", withIndex[0][0]);

        _engine.Execute("DROP INDEX idx_ab ON orders");
        Assert.Equal(Rows(_engine.Execute("SELECT id, a, b FROM orders WHERE a = 2 AND b = 3")), withIndex);
    }

    [Fact(DisplayName = "联合唯一索引仍拦截重复组合")]
    public void CompositeIndex_UniqueConstraint()
    {
        _engine.Execute("CREATE TABLE items (id INT PRIMARY KEY, x INT, y INT)");
        _engine.Execute("CREATE UNIQUE INDEX ux_xy ON items (x, y)");
        _engine.Execute("INSERT INTO items (id, x, y) VALUES (1, 1, 1)");
        _engine.Execute("INSERT INTO items (id, x, y) VALUES (2, 1, 2)");

        Assert.Throws<NovaException>(() => _engine.Execute("INSERT INTO items (id, x, y) VALUES (3, 1, 1)"));

        var result = _engine.Execute("SELECT id FROM items WHERE x = 1 AND y = 1");
        Assert.Single(result.Rows);
        Assert.Equal("1", Convert.ToString(result.Rows[0][0]));
    }

    [Fact(DisplayName = "联合索引前缀对字符串列大小写不敏感")]
    public void CompositeIndex_StringPrefixIsCaseInsensitive()
    {
        _engine.Execute("CREATE TABLE people (id INT PRIMARY KEY, name VARCHAR(20), age INT)");
        _engine.Execute("CREATE INDEX idx_name_age ON people (name, age)");
        _engine.Execute("INSERT INTO people (id, name, age) VALUES (1, 'Alice', 30), (2, 'ALICE', 31), (3, 'Bob', 30)");

        var prefix = _engine.Execute("SELECT id FROM people WHERE name = 'alice' ORDER BY id");
        Assert.Equal(2, prefix.Rows.Count);

        var full = _engine.Execute("SELECT id FROM people WHERE name = 'alice' AND age = 30");
        Assert.Single(full.Rows);
        Assert.Equal("1", Convert.ToString(full.Rows[0][0]));
    }

    [Fact(DisplayName = "联合索引前缀 IN 列表展开为多组探测且结果一致")]
    public void CompositeIndex_InPrefix()
    {
        CreateOrders();

        var sqls = new[]
        {
            "SELECT id, a, b FROM orders WHERE a IN (1, 2) AND b = 2 ORDER BY id",
            "SELECT id, a, b FROM orders WHERE a IN (1, 2) ORDER BY id",
        };

        var withIndex = sqls.Select(sql => Rows(_engine.Execute(sql))).ToArray();
        Assert.Equal(2, withIndex[0].Length);
        Assert.Equal(8, withIndex[1].Length);

        _engine.Execute("DROP INDEX idx_ab ON orders");
        for (var i = 0; i < sqls.Length; i++)
        {
            Assert.Equal(Rows(_engine.Execute(sqls[i])), withIndex[i]);
        }
    }

    [Fact(DisplayName = "ANALYZE 前后联合索引查询结果保持一致")]
    public void CompositeIndex_AfterAnalyze()
    {
        CreateOrders();

        var sqls = new[]
        {
            "SELECT id, a, b FROM orders WHERE a = 2 AND b = 1 ORDER BY id",
            "SELECT id, a, b FROM orders WHERE a = 2 ORDER BY id",
            "SELECT id, a, b FROM orders WHERE a = 2 AND b >= 2 AND b <= 3 ORDER BY id",
        };

        var before = sqls.Select(sql => Rows(_engine.Execute(sql))).ToArray();

        _engine.Execute("ANALYZE TABLE orders");

        // 有统计时按代价选择路径，但结果必须一致
        for (var i = 0; i < sqls.Length; i++)
        {
            Assert.Equal(before[i], Rows(_engine.Execute(sqls[i])));
        }

        Assert.Equal("INDEX LOOKUP", ExplainType(sqls[0]));
        Assert.Equal("idx_ab(a,b)", Convert.ToString(Explain(sqls[0]).Rows[0][3]));
    }

    [Fact(DisplayName = "联合索引重启后按行日志重建且结果一致")]
    public void CompositeIndex_ReloadFromRowLog()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"CompositeReload_{Guid.NewGuid():N}");
        try
        {
            using (var engine = new SqlEngine(dir, new DbOptions { Path = dir, WalMode = WalMode.None }))
            {
                engine.Execute("CREATE TABLE orders (id INT PRIMARY KEY, a INT, b INT)");
                engine.Execute("CREATE INDEX idx_ab ON orders (a, b)");
                for (var i = 1; i <= 6; i++)
                    engine.Execute($"INSERT INTO orders (id, a, b) VALUES ({i}, {i % 3}, {i})");
            }

            using (var engine = new SqlEngine(dir, new DbOptions { Path = dir, WalMode = WalMode.None }))
            {
                Assert.Equal("INDEX LOOKUP", Convert.ToString(engine.Execute("EXPLAIN SELECT * FROM orders WHERE a = 1").Rows[0][1]));

                var result = engine.Execute("SELECT id FROM orders WHERE a = 1 AND b = 4");
                Assert.Single(result.Rows);
                Assert.Equal("4", Convert.ToString(result.Rows[0][0]));
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

    [Fact(DisplayName = "OR 等值条件合并为 INDEX MERGE 并返回去重并集")]
    public void IndexMerge_UnionOfBothSides()
    {
        _engine.Execute("CREATE TABLE events (id INT PRIMARY KEY, a INT, b INT)");
        _engine.Execute("CREATE INDEX idx_a ON events (a)");
        _engine.Execute("CREATE INDEX idx_b ON events (b)");
        for (var i = 1; i <= 20; i++)
            _engine.Execute($"INSERT INTO events (id, a, b) VALUES ({i}, {i}, {i % 5})");

        // id=3 同时满足两侧条件，结果必须去重
        var sql = "SELECT id, a, b FROM events WHERE a = 3 OR b = 2 ORDER BY id";
        var withMerge = Rows(_engine.Execute(sql));

        var explain = Explain(sql);
        Assert.Equal("INDEX MERGE", Convert.ToString(explain.Rows[0][1]));
        var key = Convert.ToString(explain.Rows[0][3]);
        Assert.Contains("idx_a(a)", key);
        Assert.Contains("idx_b(b)", key);

        Assert.Equal(new[] { "2", "3", "7", "12", "17" }, withMerge.Select(e => e[0]).ToArray());

        _engine.Execute("DROP INDEX idx_a ON events");
        _engine.Execute("DROP INDEX idx_b ON events");
        Assert.Equal(Rows(_engine.Execute(sql)), withMerge);
    }

    [Fact(DisplayName = "OR 任一侧无索引时回退全表扫描")]
    public void IndexMerge_FallsBackWhenOneSideUnindexed()
    {
        _engine.Execute("CREATE TABLE events (id INT PRIMARY KEY, a INT, b INT)");
        _engine.Execute("CREATE INDEX idx_a ON events (a)");
        for (var i = 1; i <= 10; i++)
            _engine.Execute($"INSERT INTO events (id, a, b) VALUES ({i}, {i}, {i % 3})");

        Assert.Equal("FULL SCAN", ExplainType("SELECT * FROM events WHERE a = 3 OR b = 2"));
        Assert.Equal(new[] { "2", "3", "5", "8" }, Rows(_engine.Execute("SELECT id FROM events WHERE a = 3 OR b = 2 ORDER BY id")).Select(e => e[0]).ToArray());
    }

    [Fact(DisplayName = "索引覆盖 COUNT 不回表且与全表扫描一致")]
    public void IndexOnlyCount_MatchesFullScan()
    {
        CreateOrders();

        var sql = "SELECT COUNT(*) FROM orders WHERE a = 2";
        var explain = Explain(sql);
        Assert.Equal("INDEX LOOKUP", Convert.ToString(explain.Rows[0][1]));
        Assert.Contains("Index-only COUNT", Convert.ToString(explain.Rows[0][5]));

        Assert.Equal(4, Convert.ToInt32(_engine.Execute(sql).GetScalar()));

        _engine.Execute("DROP INDEX idx_ab ON orders");
        Assert.Equal(4, Convert.ToInt32(_engine.Execute(sql).GetScalar()));
    }

    [Fact(DisplayName = "索引覆盖 COUNT 支持复合等值、前缀与 IN 且与全表扫描一致")]
    public void IndexOnlyCount_CompositeAndIn()
    {
        CreateOrders();

        var sqls = new[]
        {
            "SELECT COUNT(*) FROM orders WHERE a = 2 AND b = 3",
            "SELECT COUNT(*) FROM orders WHERE a = 2 AND b IN (1, 3, 9)",
            "SELECT COUNT(*) FROM orders WHERE a = 3",
            "SELECT COUNT(*) FROM orders WHERE a = 2 AND b >= 2 AND b <= 3",
        };

        var withIndex = sqls.Select(sql => Convert.ToInt32(_engine.Execute(sql).GetScalar())).ToArray();
        Assert.Equal(new[] { 1, 2, 4, 2 }, withIndex);

        _engine.Execute("DROP INDEX idx_ab ON orders");
        var fullScan = sqls.Select(sql => Convert.ToInt32(_engine.Execute(sql).GetScalar())).ToArray();
        Assert.Equal(fullScan, withIndex);
    }

    [Fact(DisplayName = "索引覆盖 COUNT 在 UPDATE/DELETE 后仍与全表扫描一致")]
    public void IndexOnlyCount_AfterUpdateAndDelete()
    {
        CreateOrders();
        _engine.Execute("UPDATE orders SET a = 9 WHERE a = 1 AND b = 1");
        _engine.Execute("DELETE FROM orders WHERE a = 1 AND b = 2");

        var sql = "SELECT COUNT(*) FROM orders WHERE a = 1";
        Assert.Equal(2, Convert.ToInt32(_engine.Execute(sql).GetScalar()));

        _engine.Execute("DROP INDEX idx_ab ON orders");
        Assert.Equal(2, Convert.ToInt32(_engine.Execute(sql).GetScalar()));
        Assert.Equal(11, Convert.ToInt32(_engine.Execute("SELECT COUNT(*) FROM orders").GetScalar()));
    }

    [Fact(DisplayName = "索引计数只统计可见行版本，未提交与已删除行不计入")]
    public void CountVisibleRows_RespectsVisibility()
    {
        var schema = new TableSchema("vis");
        schema.AddColumn(new ColumnDefinition("id", DataType.Int32, nullable: false, isPrimaryKey: true));
        schema.AddColumn(new ColumnDefinition("a", DataType.Int32, nullable: true));
        schema.AddIndex(new IndexDefinition("idx_a", ["a"]));

        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();
        using var table = new NovaTable(schema, _testDir, options, txManager);

        using var writer = txManager.BeginTransaction();
        table.Insert(writer, [1, 5]);
        table.Insert(writer, [2, 5]);

        var keys = table.LookupByIndex("idx_a", [5])!;
        Assert.Equal(2, keys.Count);

        // 未提交的插入对其他事务不可见，对本事务可见
        using var reader = txManager.BeginTransaction();
        Assert.Equal(0, table.CountVisibleRows(reader, keys));
        Assert.Equal(2, table.CountVisibleRows(writer, keys));

        writer.Commit();
        Assert.Equal(2, table.CountVisibleRows(reader, keys));

        // 删除未提交时其他事务仍可见；提交后不再计入
        using var deleter = txManager.BeginTransaction();
        Assert.True(table.Delete(deleter, 1));
        Assert.Equal(2, table.CountVisibleRows(reader, keys));

        deleter.Commit();
        using var afterDelete = txManager.BeginTransaction();
        Assert.Equal(1, table.CountVisibleRows(afterDelete, keys));
    }
}
