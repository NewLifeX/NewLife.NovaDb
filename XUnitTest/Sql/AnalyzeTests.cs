using System;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

namespace XUnitTest.Sql;

/// <summary>ANALYZE 统计目录与查询计划估算测试（s1-stats）</summary>
public class AnalyzeTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public AnalyzeTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"AnalyzeTests_{Guid.NewGuid():N}");
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

    private void CreateTable()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, user_id INT, name VARCHAR(50))");
        _engine.Execute("CREATE INDEX idx_user ON users (user_id)");
    }

    private void InsertRows(Int32 count, Int32 startId = 1)
    {
        for (var i = 0; i < count; i++)
        {
            var id = startId + i;
            var userId = id % 10;
            _engine.Execute($"INSERT INTO users (id, user_id, name) VALUES ({id}, {userId}, 'user{id}')");
        }
    }

    [Fact(DisplayName = "ANALYZE TABLE 写入 _sys.stats 且行数与 NDV 正确")]
    public void AnalyzeTable_WritesStats()
    {
        CreateTable();
        InsertRows(100);

        var result = _engine.Execute("ANALYZE TABLE users");
        Assert.Equal(1, result.AffectedRows);

        var stats = _engine.Execute("SELECT table_name, column_name, row_count, ndv FROM _sys.stats WHERE table_name = 'users'");
        Assert.True(stats.Rows.Count > 0);

        var userIdRow = stats.Rows.FirstOrDefault(r => String.Equals(Convert.ToString(r[1]), "user_id", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(userIdRow);
        Assert.Equal(100L, Convert.ToInt64(userIdRow![2]));
        Assert.Equal(10L, Convert.ToInt64(userIdRow[3]));

        var idRow = stats.Rows.FirstOrDefault(r => String.Equals(Convert.ToString(r[1]), "id", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(idRow);
        Assert.Equal(100L, Convert.ToInt64(idRow![3]));
    }

    [Fact(DisplayName = "EXPLAIN 的 rows 使用统计估算等值命中行数")]
    public void Explain_UsesStatistics()
    {
        CreateTable();
        InsertRows(100);
        _engine.Execute("ANALYZE TABLE users");

        var explain = _engine.Execute("EXPLAIN SELECT * FROM users WHERE user_id = 3");
        Assert.Equal("INDEX LOOKUP", Convert.ToString(explain.Rows[0][1]));
        Assert.Equal(10L, Convert.ToInt64(explain.Rows[0][4]));

        // 未建索引的条件仍走全表扫描，行数取统计行数
        var fullScan = _engine.Execute("EXPLAIN SELECT * FROM users WHERE name LIKE 'user%'");
        Assert.Equal("FULL SCAN", Convert.ToString(fullScan.Rows[0][1]));
        Assert.Equal(100L, Convert.ToInt64(fullScan.Rows[0][4]));

        // 主键范围用等深直方图估算，100 行中 id > 50 应约为 50 行（无统计时会显示全表 100）
        var range = _engine.Execute("EXPLAIN SELECT * FROM users WHERE id > 50");
        Assert.Equal("PK RANGE", Convert.ToString(range.Rows[0][1]));
        Assert.InRange(Convert.ToInt64(range.Rows[0][4]), 40L, 60L);
    }

    [Fact(DisplayName = "数据大量变化后统计自动失效并回退粗估")]
    public void Statistics_InvalidatedAfterBulkChange()
    {
        CreateTable();
        InsertRows(100);
        _engine.Execute("ANALYZE TABLE users");

        // 再插 400 行，user_id = 3 的真实命中数约 40，过期统计仍会说 10
        InsertRows(400, startId: 101);

        var explain = _engine.Execute("EXPLAIN SELECT * FROM users WHERE user_id = 3");
        Assert.Equal("INDEX LOOKUP", Convert.ToString(explain.Rows[0][1]));

        var rows = Convert.ToInt64(explain.Rows[0][4]);
        Assert.True(rows > 10, $"统计应已失效并回退粗估，实际估算 {rows}");
    }

    [Fact(DisplayName = "ANALYZE 不带表名可分析全库")]
    public void Analyze_AllTables()
    {
        CreateTable();
        _engine.Execute("CREATE TABLE orders (id INT PRIMARY KEY, total DOUBLE)");
        InsertRows(10);

        var result = _engine.Execute("ANALYZE");
        Assert.Equal(2, result.AffectedRows);

        var count = Convert.ToInt64(_engine.Execute("SELECT COUNT(*) FROM _sys.stats").GetScalar());
        Assert.True(count > 0);
    }

    [Fact(DisplayName = "information_schema.statistics 的 CARDINALITY 使用真实 NDV")]
    public void InformationSchema_CardinalityUsesNdv()
    {
        CreateTable();
        InsertRows(100);
        _engine.Execute("ANALYZE TABLE users");

        var result = _engine.Execute("SELECT index_name, cardinality FROM information_schema.statistics WHERE table_name = 'users' AND index_name = 'idx_user'");
        Assert.Single(result.Rows);
        Assert.Equal(10L, Convert.ToInt64(result.Rows[0][1]));
    }

    [Fact(DisplayName = "解析器支持 ANALYZE TABLE t 与 ANALYZE t")]
    public void Parser_Analyze()
    {
        var withTable = Assert.IsType<AnalyzeStatement>(new SqlParser("ANALYZE TABLE users").Parse());
        Assert.Equal("users", withTable.TableName);

        var withoutTable = Assert.IsType<AnalyzeStatement>(new SqlParser("ANALYZE users").Parse());
        Assert.Equal("users", withoutTable.TableName);

        var allTables = Assert.IsType<AnalyzeStatement>(new SqlParser("ANALYZE").Parse());
        Assert.Null(allTables.TableName);
    }
}
