using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

#nullable enable

namespace XUnitTest.Sql;

/// <summary>ORDER BY 索引顺序与 LIMIT 下推测试（s4-sort-limit）</summary>
public class SortPushdownTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public SortPushdownTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"SortPushdownTests_{Guid.NewGuid():N}");
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

    private static String[][] Rows(SqlResult result)
        => result.Rows.Select(r => r.Select(e => Convert.ToString(e) ?? "").ToArray()).ToArray();

    private SqlResult Explain(String sql) => _engine.Execute("EXPLAIN " + sql);

    private String ExplainType(String sql) => Convert.ToString(Explain(sql).Rows[0][1])!;

    private String ExplainExtra(String sql) => Convert.ToString(Explain(sql).Rows[0][5]) ?? "";

    private static Boolean HasSortStep(SqlResult explain)
        => explain.Rows.Any(r => String.Equals(Convert.ToString(r[1]), "SORT", StringComparison.OrdinalIgnoreCase));

    /// <summary>建表并插入数据：score 与 id 逆序排列且大量重复，用于验证并列行顺序</summary>
    private void CreateItems(Int32 count = 200)
    {
        _engine.Execute("CREATE TABLE items (id INT PRIMARY KEY, score INT NOT NULL, bucket INT NOT NULL)");
        _engine.Execute("CREATE INDEX idx_score ON items (score)");

        for (var i = 1; i <= count; i++)
        {
            // score 重复（每 7 行循环一次），且与主键顺序不一致
            var score = (count - i) % 7;
            _engine.Execute($"INSERT INTO items (id, score, bucket) VALUES ({i}, {score}, {i % 5})");
        }
    }

    private void CreatePairs(Int32 count = 12)
    {
        _engine.Execute("CREATE TABLE pairs (id INT PRIMARY KEY, a INT NOT NULL, b INT NOT NULL)");
        _engine.Execute("CREATE INDEX idx_ab ON pairs (a, b)");

        for (var a = 1; a <= 4; a++)
        {
            for (var b = 1; b <= count / 4; b++)
                _engine.Execute($"INSERT INTO pairs (id, a, b) VALUES ({(a - 1) * (count / 4) + b}, {a}, {b})");
        }
    }

    /// <summary>全表按主键升序的行，作为排序切片与逆序切片的参照</summary>
    private String[][] AllById(String columns) => Rows(_engine.Execute($"SELECT {columns} FROM items"));

    private static String[][] Slice(String[][] rows, Int32 offset, Int32 limit)
        => rows.Skip(offset).Take(limit).ToArray();

    private static String[][] Reversed(String[][] rows) => rows.Reverse().ToArray();

    [Fact(DisplayName = "主键升序与降序 ORDER BY 走索引顺序且结果与切片一致")]
    public void PrimaryKeyOrder_AscAndDesc()
    {
        CreateItems();

        var all = AllById("id, score, bucket");

        foreach (var (sql, expected) in new[]
        {
            ("SELECT id, score, bucket FROM items ORDER BY id LIMIT 5", Slice(all, 0, 5)),
            ("SELECT id, score, bucket FROM items ORDER BY id LIMIT 5 OFFSET 3", Slice(all, 3, 5)),
            ("SELECT id, score, bucket FROM items ORDER BY id DESC LIMIT 5", Slice(Reversed(all), 0, 5)),
            ("SELECT id, score, bucket FROM items ORDER BY id DESC LIMIT 7 OFFSET 11", Slice(Reversed(all), 11, 7)),
            ("SELECT id, score, bucket FROM items ORDER BY id DESC", Reversed(all)),
        })
        {
            Assert.Equal(expected, Rows(_engine.Execute(sql)));
        }

        // 主键顺序由主键索引直接给出，执行计划不再排序
        var ascend = Explain("SELECT * FROM items ORDER BY id LIMIT 5");
        Assert.Equal("FULL SCAN", Convert.ToString(ascend.Rows[0][1]));
        Assert.Contains("using index order", Convert.ToString(ascend.Rows[0][5]));
        Assert.False(HasSortStep(ascend));

        var descend = Explain("SELECT * FROM items ORDER BY id DESC LIMIT 5");
        Assert.Contains("using index order", Convert.ToString(descend.Rows[0][5]));
        Assert.Contains("descending", Convert.ToString(descend.Rows[0][5]));
        Assert.False(HasSortStep(descend));
    }

    [Fact(DisplayName = "主键范围 + ORDER BY 降序 + LIMIT 反向扫描")]
    public void PrimaryKeyRange_DescendingWithLimit()
    {
        CreateItems();

        var sql = "SELECT id FROM items WHERE id >= 20 AND id <= 80 ORDER BY id DESC LIMIT 6";
        Assert.Equal(new[] { "80", "79", "78", "77", "76", "75" }, Rows(_engine.Execute(sql)).Select(e => e[0]).ToArray());

        var explain = Explain(sql);
        Assert.Equal("PK RANGE", Convert.ToString(explain.Rows[0][1]));
        Assert.Contains("using index order", Convert.ToString(explain.Rows[0][5]));
        Assert.False(HasSortStep(explain));

        // 参照：同区间升序结果反转后切片
        var expected = Rows(_engine.Execute("SELECT id FROM items WHERE id >= 20 AND id <= 80 ORDER BY id")).Reverse().Take(6).ToArray();
        Assert.Equal(expected.Select(e => e[0]).ToArray(), Rows(_engine.Execute(sql)).Select(e => e[0]).ToArray());
    }

    [Fact(DisplayName = "单列二级索引 ORDER BY 免排序且与删索引后结果逐行一致")]
    public void SecondaryIndexOrder_MatchesFullScan()
    {
        CreateItems();

        var sqls = new[]
        {
            "SELECT id, score FROM items ORDER BY score LIMIT 20",
            "SELECT id, score FROM items ORDER BY score DESC LIMIT 20",
            "SELECT id, score, bucket FROM items ORDER BY score",
            // 等值命中同一索引键：该键内所有行排序列取值相同，任意顺序都满足 ORDER BY
            "SELECT id, score FROM items WHERE score = 3 ORDER BY score DESC",
        };

        var withIndex = sqls.Select(sql => Rows(_engine.Execute(sql))).ToArray();
        Assert.Equal(20, withIndex[0].Length);
        Assert.Equal(20, withIndex[1].Length);
        Assert.Equal(200, withIndex[2].Length);
        Assert.NotEmpty(withIndex[3]);

        // 免排序判定：走索引顺序扫描，且执行计划里没有 SORT 步骤
        var explain = Explain(sqls[0]);
        Assert.Equal("INDEX ORDER", Convert.ToString(explain.Rows[0][1]));
        Assert.Equal("idx_score(score)", Convert.ToString(explain.Rows[0][3]));
        Assert.Contains("using index order", Convert.ToString(explain.Rows[0][5]));
        Assert.False(HasSortStep(explain));

        Assert.Contains("using index order", ExplainExtra(sqls[1]));
        Assert.False(HasSortStep(Explain(sqls[1])));

        // 等值命中时同样免排序
        Assert.Equal("INDEX LOOKUP", ExplainType(sqls[3]));
        Assert.Contains("using index order", ExplainExtra(sqls[3]));
        Assert.False(HasSortStep(Explain(sqls[3])));

        // 统计生效后仍按代价选择索引顺序，结果必须完全一致
        _engine.Execute("ANALYZE TABLE items");
        Assert.Equal(withIndex[0], Rows(_engine.Execute(sqls[0])));

        _engine.Execute("DROP INDEX idx_score ON items");

        // 无索引时退回全表扫描 + 排序，逐行结果必须一致（并列行按主键升序，与稳定排序一致）
        for (var i = 0; i < sqls.Length; i++)
        {
            Assert.Equal(withIndex[i], Rows(_engine.Execute(sqls[i])));
        }

        Assert.True(HasSortStep(Explain(sqls[0])));
    }

    [Fact(DisplayName = "二级索引 ORDER BY 降序确实降序")]
    public void SecondaryIndexOrder_DescendingReallyDescends()
    {
        CreateItems();

        var rows = Rows(_engine.Execute("SELECT id, score FROM items ORDER BY score DESC LIMIT 30"));
        Assert.Equal(30, rows.Length);

        var scores = rows.Select(e => Convert.ToInt32(e[1])).ToArray();
        for (var i = 1; i < scores.Length; i++)
        {
            Assert.True(scores[i - 1] >= scores[i], "降序结果必须单调不增");
        }

        // 头部应是最大 score 值
        var max = Convert.ToInt32(_engine.Execute("SELECT MAX(score) FROM items").GetScalar());
        Assert.Equal(max, scores[0]);
    }

    [Fact(DisplayName = "联合索引最左前缀 ORDER BY 免排序且结果正确")]
    public void CompositeIndexOrder_Prefix()
    {
        CreatePairs();

        var sqls = new[]
        {
            "SELECT id, a, b FROM pairs ORDER BY a, b LIMIT 7",
            "SELECT id, a, b FROM pairs ORDER BY a DESC, b DESC LIMIT 7",
            "SELECT id, a, b FROM pairs ORDER BY a, b",
            "SELECT id, a, b FROM pairs WHERE a = 2 ORDER BY a, b",
            "SELECT id, a, b FROM pairs WHERE a = 3 ORDER BY a, b DESC",
        };

        var withIndex = sqls.Select(sql => Rows(_engine.Execute(sql))).ToArray();

        var explain = Explain(sqls[0]);
        Assert.Equal("INDEX ORDER", Convert.ToString(explain.Rows[0][1]));
        Assert.Equal("idx_ab(a,b)", Convert.ToString(explain.Rows[0][3]));
        Assert.Contains("using index order", Convert.ToString(explain.Rows[0][5]));
        Assert.False(HasSortStep(explain));

        Assert.False(HasSortStep(Explain(sqls[1])));
        Assert.Equal(new[] { "12", "11", "10", "9", "8", "7", "6" }, withIndex[1].Select(e => e[0]).ToArray());
        Assert.Equal(new[] { "9", "8", "7" }, withIndex[4].Select(e => e[0]).ToArray());

        _engine.Execute("DROP INDEX idx_ab ON pairs");
        for (var i = 0; i < sqls.Length; i++)
        {
            Assert.Equal(withIndex[i], Rows(_engine.Execute(sqls[i])));
        }
    }

    [Fact(DisplayName = "非最左前缀 ORDER BY 仍排序且结果正确")]
    public void NonPrefixOrderBy_StillSorts()
    {
        CreatePairs();

        var sqls = new[]
        {
            "SELECT id, a, b FROM pairs ORDER BY b LIMIT 6",
            "SELECT id, a, b FROM pairs WHERE a = 2 ORDER BY b DESC",
            "SELECT id, a, b FROM pairs ORDER BY a, b DESC",
        };

        var withIndex = sqls.Select(sql => Rows(_engine.Execute(sql))).ToArray();

        // 排序列不是索引最左列，或方向不一致时无法免排序
        foreach (var sql in sqls)
        {
            Assert.True(HasSortStep(Explain(sql)));
            Assert.DoesNotContain("using index order", ExplainExtra(sql));
        }

        Assert.Equal(new[] { "1", "4", "7", "10", "2", "5" }, withIndex[0].Select(e => e[0]).ToArray());

        _engine.Execute("DROP INDEX idx_ab ON pairs");
        for (var i = 0; i < sqls.Length; i++)
        {
            Assert.Equal(withIndex[i], Rows(_engine.Execute(sqls[i])));
        }
    }

    [Fact(DisplayName = "索引顺序扫描 + 残余 WHERE + LIMIT 与全表扫描排序一致")]
    public void IndexOrder_WithResidualFilter()
    {
        CreateItems();

        var sqls = new[]
        {
            "SELECT id, score, bucket FROM items WHERE bucket = 2 ORDER BY score LIMIT 6",
            "SELECT id, score, bucket FROM items WHERE bucket > 2 ORDER BY score DESC LIMIT 9",
            "SELECT id, score, bucket FROM items WHERE bucket = 1 AND score > 2 ORDER BY score, bucket LIMIT 5",
        };

        var withIndex = sqls.Select(sql => Rows(_engine.Execute(sql))).ToArray();

        // 有残余条件时不标注免排序以外的截断保证，但顺序仍由索引提供
        var explain = Explain(sqls[0]);
        Assert.Equal("INDEX ORDER", Convert.ToString(explain.Rows[0][1]));
        Assert.Contains("Residual WHERE filter", Convert.ToString(explain.Rows[0][5]));
        Assert.Contains("using index order", Convert.ToString(explain.Rows[0][5]));
        Assert.False(HasSortStep(explain));

        Assert.Equal(6, withIndex[0].Length);
        Assert.Equal(9, withIndex[1].Length);

        _engine.Execute("DROP INDEX idx_score ON items");
        for (var i = 0; i < sqls.Length; i++)
        {
            Assert.Equal(withIndex[i], Rows(_engine.Execute(sqls[i])));
        }
    }

    [Fact(DisplayName = "索引顺序扫描叠加同列范围条件")]
    public void IndexOrder_WithSameColumnRange()
    {
        CreateItems();

        var sqls = new[]
        {
            "SELECT id, score FROM items WHERE score >= 5 ORDER BY score LIMIT 4",
            "SELECT id, score FROM items WHERE score >= 2 AND score <= 4 ORDER BY score DESC LIMIT 3",
        };

        var withIndex = sqls.Select(sql => Rows(_engine.Execute(sql))).ToArray();

        var explain = Explain(sqls[0]);
        Assert.Equal("INDEX ORDER", Convert.ToString(explain.Rows[0][1]));
        Assert.Contains("using index order", Convert.ToString(explain.Rows[0][5]));
        Assert.DoesNotContain("Residual", Convert.ToString(explain.Rows[0][5]));
        Assert.False(HasSortStep(explain));

        Assert.Equal(4, withIndex[0].Length);
        Assert.All(withIndex[0], r => Assert.True(Convert.ToInt32(r[1]) >= 5));
        Assert.Equal(3, withIndex[1].Length);
        Assert.All(withIndex[1], r => Assert.InRange(Convert.ToInt32(r[1]), 2, 4));

        _engine.Execute("DROP INDEX idx_score ON items");
        for (var i = 0; i < sqls.Length; i++)
        {
            Assert.Equal(withIndex[i], Rows(_engine.Execute(sqls[i])));
        }
    }

    [Fact(DisplayName = "空表、单行、LIMIT 0 与 OFFSET 超界")]
    public void OrderBy_EdgeCases()
    {
        _engine.Execute("CREATE TABLE empty_rows (id INT PRIMARY KEY, score INT NOT NULL)");
        _engine.Execute("CREATE INDEX idx_empty_score ON empty_rows (score)");

        Assert.Empty(_engine.Execute("SELECT id FROM empty_rows ORDER BY id DESC LIMIT 5").Rows);
        Assert.Empty(_engine.Execute("SELECT id FROM empty_rows ORDER BY score LIMIT 5").Rows);
        Assert.Empty(_engine.Execute("SELECT id FROM empty_rows ORDER BY score DESC").Rows);

        _engine.Execute("INSERT INTO empty_rows (id, score) VALUES (1, 42)");

        Assert.Equal(new[] { "1" }, Rows(_engine.Execute("SELECT id FROM empty_rows ORDER BY id DESC")).Select(e => e[0]).ToArray());
        Assert.Equal(new[] { "1" }, Rows(_engine.Execute("SELECT id FROM empty_rows ORDER BY score LIMIT 5")).Select(e => e[0]).ToArray());
        Assert.Equal(new[] { "1" }, Rows(_engine.Execute("SELECT id FROM empty_rows ORDER BY score DESC LIMIT 5")).Select(e => e[0]).ToArray());

        CreateItems();

        Assert.Empty(_engine.Execute("SELECT id FROM items ORDER BY id LIMIT 0").Rows);
        Assert.Empty(_engine.Execute("SELECT id FROM items ORDER BY score DESC LIMIT 0").Rows);
        Assert.Empty(_engine.Execute("SELECT id FROM items ORDER BY id LIMIT 5 OFFSET 200").Rows);
        Assert.Empty(_engine.Execute("SELECT id FROM items ORDER BY score LIMIT 5 OFFSET 500").Rows);

        // OFFSET 正好等于总行数时也为空
        Assert.Empty(_engine.Execute("SELECT id FROM items ORDER BY score DESC LIMIT 3 OFFSET 200").Rows);
    }

    [Fact(DisplayName = "主键顺序与二级索引顺序在参数化条件、UPDATE 后仍正确")]
    public void OrderBy_AfterUpdate()
    {
        CreateItems(50);

        _engine.Execute("UPDATE items SET score = 100 WHERE id <= 3");
        _engine.Execute("DELETE FROM items WHERE id = 10");

        var ascending = Rows(_engine.Execute("SELECT id, score FROM items ORDER BY score LIMIT 10"));
        var descending = Rows(_engine.Execute("SELECT id, score FROM items ORDER BY score DESC LIMIT 10"));

        var explain = Explain("SELECT id, score FROM items ORDER BY score LIMIT 10");
        Assert.Equal("INDEX ORDER", Convert.ToString(explain.Rows[0][1]));

        _engine.Execute("DROP INDEX idx_score ON items");

        Assert.Equal(ascending, Rows(_engine.Execute("SELECT id, score FROM items ORDER BY score LIMIT 10")));
        Assert.Equal(descending, Rows(_engine.Execute("SELECT id, score FROM items ORDER BY score DESC LIMIT 10")));
    }

    [Fact(DisplayName = "可空列与字符串列不按索引顺序免排序，结果仍正确")]
    public void UnsafeOrderColumns_FallBackToSort()
    {
        _engine.Execute("CREATE TABLE nulls (id INT PRIMARY KEY, val INT, name VARCHAR(20))");
        _engine.Execute("CREATE INDEX idx_val ON nulls (val)");
        _engine.Execute("CREATE INDEX idx_name ON nulls (name)");
        _engine.Execute("INSERT INTO nulls (id, val, name) VALUES (1, 5, 'b'), (2, NULL, 'A'), (3, 3, NULL)");

        var sqls = new[]
        {
            "SELECT id, val FROM nulls ORDER BY val",
            "SELECT id, val FROM nulls ORDER BY val DESC",
            "SELECT id, name FROM nulls ORDER BY name",
        };

        var withIndex = sqls.Select(sql => Rows(_engine.Execute(sql))).ToArray();

        // NULL 不进二级索引、字符串索引键是区域性敏感排序，两者都不能用索引顺序替代 SQL 排序
        foreach (var sql in sqls)
        {
            Assert.True(HasSortStep(Explain(sql)));
            Assert.DoesNotContain("using index order", ExplainExtra(sql));
        }

        // ASC 时 NULL 排在前，与 CompareValues 的 NULL 语义一致
        Assert.Equal(new[] { "2", "3", "1" }, withIndex[0].Select(e => e[0]).ToArray());
        Assert.Equal(new[] { "1", "3", "2" }, withIndex[1].Select(e => e[0]).ToArray());
        Assert.Equal(new[] { "3", "2", "1" }, withIndex[2].Select(e => e[0]).ToArray());

        _engine.Execute("DROP INDEX idx_val ON nulls");
        _engine.Execute("DROP INDEX idx_name ON nulls");
        for (var i = 0; i < sqls.Length; i++)
        {
            Assert.Equal(withIndex[i], Rows(_engine.Execute(sqls[i])));
        }
    }

    [Fact(DisplayName = "GROUP BY 上的 ORDER BY 不参与索引顺序免排序")]
    public void OrderBy_OnGroupedRowsStillSorts()
    {
        CreateItems(30);

        var sql = "SELECT bucket, COUNT(*) FROM items GROUP BY bucket ORDER BY bucket DESC";
        var explain = Explain(sql);

        // 分组后排序的是结果组，与表行顺序无关，不能声称已按索引顺序产出
        Assert.DoesNotContain("using index order", Convert.ToString(explain.Rows[0][5]));
        Assert.NotEqual("INDEX ORDER", Convert.ToString(explain.Rows[0][1]));

        var grouped = Rows(_engine.Execute(sql));
        Assert.Equal(5, grouped.Length);

        // 分组内容不能因计划变化而改变
        _engine.Execute("DROP INDEX idx_score ON items");
        Assert.Equal(grouped, Rows(_engine.Execute(sql)));
    }
}
