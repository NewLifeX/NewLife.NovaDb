using System;
using System.IO;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

#nullable enable

namespace XUnitTest.Sql;

/// <summary>基准问题修复后的回归测试：选择性估算</summary>
public class IndexScanPerfTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public IndexScanPerfTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"IndexScanPerfTests_{Guid.NewGuid():N}");
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

    private Int64 EstimatedRows(String sql)
    {
        var plan = _engine.Execute("EXPLAIN " + sql);
        return Convert.ToInt64(plan.Rows[0][4]);
    }

    private void CreateTable(Int32 count)
    {
        _engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, status INT NOT NULL, created_at DATETIME NOT NULL, remark VARCHAR(50))");
        _engine.Execute("CREATE INDEX idx_status_created ON t (status, created_at)");

        var anchor = new DateTime(2026, 11, 21);
        for (var i = 1; i <= count; i++)
            _engine.Execute($"INSERT INTO t (id, status, created_at, remark) VALUES ({i}, {i % 5}, '{anchor.AddMinutes(-i):yyyy-MM-dd HH:mm:ss}', 'r{i % 100}')");

        _engine.Execute("ANALYZE TABLE t");
    }

    [Fact(DisplayName = "低基数列的采样 NDV 不再被线性放大")]
    public void LowCardinalityNdv_NotOverScaled()
    {
        CreateTable(5000);

        // status 只有 5 个取值，采样应估出 5 而不是 5*放大倍数；条件估算 ≈ 行数/5
        var estimated = EstimatedRows("SELECT id FROM t WHERE status = 3");
        Assert.InRange(estimated, 100, 1500);

        // 对比：实际行数 1000
        Assert.Equal(1000, Convert.ToInt32(_engine.Execute("SELECT COUNT(*) FROM t WHERE status = 3").GetScalar()));
    }
}
