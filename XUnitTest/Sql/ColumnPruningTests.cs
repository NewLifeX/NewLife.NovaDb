using System;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

#nullable enable

namespace XUnitTest.Sql;

/// <summary>全表扫描列裁剪测试：SQL 只反序列化语句引用到的列，结果必须与整行读取一致</summary>
public class ColumnPruningTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public ColumnPruningTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"ColumnPruningTests_{Guid.NewGuid():N}");
        _engine = new SqlEngine(_testDir, new DbOptions { Path = _testDir, WalMode = WalMode.None });

        _engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, a INT NOT NULL, b INT NOT NULL, name VARCHAR(50), note VARCHAR(200))");
        _engine.Execute("CREATE INDEX idx_b ON t (b)");
        _engine.Execute("CREATE INDEX idx_ba ON t (b, a)");

        for (var i = 1; i <= 200; i++)
            _engine.Execute($"INSERT INTO t (id, a, b, name, note) VALUES ({i}, {i % 7}, {i % 5}, 'name-{i}', 'note-{i}')");
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

    private static String Cells(SqlResult result)
        => String.Join("|", result.Rows.Select(r => String.Join(",", r.Select(e => Convert.ToString(e) ?? ""))));

    [Fact(DisplayName = "只查部分列时结果正确（WHERE 引用未投影列）")]
    public void SelectSubset_WithWhereOnOtherColumn()
    {
        var result = _engine.Execute("SELECT id FROM t WHERE b = 3 AND a = 1");

        Assert.Equal("8|43|78|113|148|183", Cells(result));
    }

    [Fact(DisplayName = "ORDER BY 引用未投影列时排序仍正确")]
    public void OrderBy_UsesColumnOutsideProjection()
    {
        var result = _engine.Execute("SELECT id FROM t WHERE b = 1 AND id <= 10 ORDER BY a DESC, id");

        // a = id % 7，id 1..10 中 b = id % 5 = 1 的行是 1、6；a 分别 1、6，降序后 6 在前
        Assert.Equal("6|1", Cells(result));
    }

    [Fact(DisplayName = "聚合与分组引用未投影列时结果正确")]
    public void Aggregate_UsesColumnOutsideProjection()
    {
        var count = Convert.ToInt64(_engine.Execute("SELECT COUNT(*) FROM t WHERE note = 'note-7'").GetScalar());
        Assert.Equal(1, count);

        var groups = _engine.Execute("SELECT b, COUNT(*) FROM t GROUP BY b ORDER BY b");
        Assert.Equal("0,40|1,40|2,40|3,40|4,40", Cells(groups));
    }

    [Fact(DisplayName = "SELECT * 退回整行读取且结果正确")]
    public void Wildcard_StillReturnsWholeRow()
    {
        var star = _engine.Execute("SELECT * FROM t WHERE id = 1");
        Assert.Single(star.Rows);
        Assert.Equal(["1", "1", "1", "name-1", "note-1"], star.Rows[0].Select(e => Convert.ToString(e) ?? "").ToArray());
    }

    [Fact(DisplayName = "索引回表与索引顺序扫描同样按列裁剪且结果正确")]
    public void IndexPaths_RespectProjection()
    {
        // 单列索引等值 → 回表
        var byIndex = _engine.Execute("SELECT id FROM t WHERE b IN (2, 4) AND id <= 6 ORDER BY id");
        Assert.Equal("2|4", Cells(byIndex));

        // 排序路径（索引顺序或索引回表 + 排序）都必须按投影裁剪后仍给出正确顺序
        var byOrder = _engine.Execute("SELECT id FROM t WHERE b = 2 ORDER BY a DESC LIMIT 3");
        Assert.Equal("27|62|97", Cells(byOrder));
    }

    [Fact(DisplayName = "CASE 与函数表达式引用的列不会被裁掉")]
    public void Expressions_CollectAllReferencedColumns()
    {
        var result = _engine.Execute("SELECT id, CASE WHEN a > 3 THEN name ELSE note END AS v FROM t WHERE id = 4");
        Assert.Equal("4,name-4", Cells(result));

        var casted = _engine.Execute("SELECT id FROM t WHERE CAST(a AS INT) = 4 AND id <= 11 ORDER BY id");
        Assert.Equal("4|11", Cells(casted));
    }
}
