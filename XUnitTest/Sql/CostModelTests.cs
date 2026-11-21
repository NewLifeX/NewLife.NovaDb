using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

#nullable enable

namespace XUnitTest.Sql;

/// <summary>代价模型纯函数测试（s2-cost-model）</summary>
public class SqlCostModelTests
{
    private static TableAccessPlan FullScanPlan(Int64 rows) => new()
    {
        Kind = TableAccessKind.FullScan,
        EstimatedRows = rows,
        CoversWhere = false,
    };

    private static TableAccessPlan IndexLookupPlan(Int64 rows)
    {
        var plan = new TableAccessPlan
        {
            Kind = TableAccessKind.IndexLookup,
            IndexName = "idx_user",
            IndexColumn = "user_id",
            EstimatedRows = rows,
            CoversWhere = true,
        };
        plan.Keys.Add(1);
        return plan;
    }

    [Fact(DisplayName = "全扫 10000 行时索引等值命中 10 行成本更低")]
    public void EstimateCost_SelectiveIndexCheaperThanFullScan()
    {
        var fullScan = FullScanPlan(10000);
        var index = IndexLookupPlan(10);

        Assert.True(SqlCostModel.EstimateCost(index, null) < SqlCostModel.EstimateCost(fullScan, null));
    }

    [Fact(DisplayName = "索引命中 4000 行时全表扫描成本更低")]
    public void EstimateCost_UnselectiveIndexCostlierThanFullScan()
    {
        var fullScan = FullScanPlan(10000);
        var index = IndexLookupPlan(4000);

        Assert.True(SqlCostModel.EstimateCost(fullScan, null) < SqlCostModel.EstimateCost(index, null));
    }

    [Fact(DisplayName = "排序成本随行数增长且符合 0.1*n*log2(n)")]
    public void EstimateSortCost_GrowsWithRows()
    {
        Assert.Equal(0, SqlCostModel.EstimateSortCost(0));
        Assert.Equal(0, SqlCostModel.EstimateSortCost(1));

        var small = SqlCostModel.EstimateSortCost(100);
        var medium = SqlCostModel.EstimateSortCost(1000);
        var large = SqlCostModel.EstimateSortCost(10000);

        Assert.True(small > 0);
        Assert.True(small < medium);
        Assert.True(medium < large);
        Assert.Equal(0.1 * 1000 * Math.Log(1000) / Math.Log(2), medium, 6);
    }

    [Fact(DisplayName = "代价常量相对大小稳定且估算无副作用")]
    public void CostConstants_RelativeMagnitude()
    {
        Assert.True(SqlCostModel.LookupRowCost > SqlCostModel.ScanRowCost);
        Assert.True(SqlCostModel.ScanRowCost > SqlCostModel.IndexScanRowCost);
        Assert.True(SqlCostModel.IndexScanRowCost > 0);
        Assert.True(SqlCostModel.IndexProbeCost > 0);
        Assert.True(SqlCostModel.FilterRowCost < SqlCostModel.ScanRowCost);

        var plan = IndexLookupPlan(25);
        var first = SqlCostModel.EstimateCost(plan, null);
        var second = SqlCostModel.EstimateCost(plan, null);
        Assert.Equal(first, second);
    }
}

/// <summary>代价模型端到端测试：ANALYZE 后的访问计划选择（s2-cost-model）</summary>
public class CostModelTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public CostModelTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"CostModelTests_{Guid.NewGuid():N}");
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

    private void CreateAndFill(Int32 count = 1000)
    {
        _engine.Execute("CREATE TABLE metrics (id INT PRIMARY KEY, user_id INT, status INT)");
        _engine.Execute("CREATE INDEX idx_user ON metrics (user_id)");
        _engine.Execute("CREATE INDEX idx_status ON metrics (status)");

        var sb = new StringBuilder();
        for (var i = 1; i <= count; i++)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append($"({i}, {i % 10}, {i % 2})");

            if (i % 100 == 0 || i == count)
            {
                _engine.Execute($"INSERT INTO metrics (id, user_id, status) VALUES {sb}");
                sb.Clear();
            }
        }
    }

    private SqlResult Explain(String sql, Dictionary<String, Object?>? parameters = null) => _engine.Execute("EXPLAIN " + sql, parameters);

    [Fact(DisplayName = "ANALYZE 后高选择性索引列走 INDEX LOOKUP")]
    public void Explain_SelectiveIndexUsesIndexLookup()
    {
        CreateAndFill();
        _engine.Execute("ANALYZE TABLE metrics");

        var row = Explain("SELECT * FROM metrics WHERE user_id = @p", new Dictionary<String, Object?> { ["@p"] = 3 }).Rows[0];

        Assert.Equal("INDEX LOOKUP", Convert.ToString(row[1]));
        Assert.Equal("idx_user(user_id)", Convert.ToString(row[3]));
        Assert.Equal(100L, Convert.ToInt64(row[4]));
        Assert.Contains("cost=", Convert.ToString(row[5]));
    }

    [Fact(DisplayName = "ANALYZE 后低选择性索引列回退 FULL SCAN")]
    public void Explain_UnselectiveIndexFallsBackToFullScan()
    {
        CreateAndFill();
        _engine.Execute("ANALYZE TABLE metrics");

        // status 只有 2 个不同值，命中一半时随机回表比顺序扫描更慢
        var row = Explain("SELECT * FROM metrics WHERE status = @p", new Dictionary<String, Object?> { ["@p"] = 1 }).Rows[0];

        Assert.Equal("FULL SCAN", Convert.ToString(row[1]));
        Assert.Equal(String.Empty, Convert.ToString(row[3]));
        Assert.Equal(1000L, Convert.ToInt64(row[4]));
    }

    [Fact(DisplayName = "主键等值与范围仍选择主键路径")]
    public void Explain_PrimaryKeyPaths()
    {
        CreateAndFill();
        _engine.Execute("ANALYZE TABLE metrics");

        var lookup = Explain("SELECT * FROM metrics WHERE id = 5").Rows[0];
        Assert.Equal("PK LOOKUP", Convert.ToString(lookup[1]));
        Assert.StartsWith("PRIMARY", Convert.ToString(lookup[3]));
        Assert.Equal(1L, Convert.ToInt64(lookup[4]));

        var range = Explain("SELECT * FROM metrics WHERE id >= 200 AND id <= 400").Rows[0];
        Assert.Equal("PK RANGE", Convert.ToString(range[1]));
        Assert.InRange(Convert.ToInt64(range[4]), 150L, 260L);
    }

    [Fact(DisplayName = "ANALYZE 前后同一 SQL 结果集逐行一致")]
    public void Analyze_KeepsResultsUnchanged()
    {
        CreateAndFill();

        var sqls = new[]
        {
            "SELECT id, user_id, status FROM metrics WHERE user_id = 3 ORDER BY id",
            "SELECT id, user_id, status FROM metrics WHERE status = 1 ORDER BY id",
            "SELECT id, user_id, status FROM metrics WHERE id = 5",
            "SELECT id, user_id, status FROM metrics WHERE id >= 10 AND id <= 20",
        };

        var before = new SqlResult[sqls.Length];
        for (var i = 0; i < sqls.Length; i++) before[i] = _engine.Execute(sqls[i]);

        // 选择路径随统计变化，但结果必须完全相同
        _engine.Execute("ANALYZE TABLE metrics");

        for (var i = 0; i < sqls.Length; i++)
        {
            var after = _engine.Execute(sqls[i]);
            Assert.Equal(before[i].Rows.Count, after.Rows.Count);
            for (var r = 0; r < before[i].Rows.Count; r++)
                Assert.Equal(before[i].Rows[r], after.Rows[r]);
        }
    }
}
