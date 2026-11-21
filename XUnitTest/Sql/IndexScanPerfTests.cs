using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Sql;
using Xunit;

#nullable enable

namespace XUnitTest.Sql;

/// <summary>基准问题修复后的回归测试：选择性估算、索引 LIMIT 提前停止、可见性快照</summary>
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

    private static String JoinRows(SqlResult result)
        => String.Join("|", result.Rows.Select(r => String.Join(",", r.Select(e => Convert.ToString(e) ?? ""))));

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

    [Fact(DisplayName = "等值前缀 + ORDER BY 下一列走索引顺序")]
    public void EqualityPrefix_OrderUsesIndexOrder()
    {
        CreateTable(2000);

        var sql = "SELECT id FROM t WHERE status = 2 ORDER BY created_at DESC LIMIT 20";
        var plan = _engine.Execute("EXPLAIN " + sql);
        Assert.Equal("INDEX ORDER", Convert.ToString(plan.Rows[0][1]));
        Assert.DoesNotContain(plan.Rows, r => Convert.ToString(r[1]) == "SORT");

        var fast = JoinRows(_engine.Execute(sql));
        _engine.Execute("DROP INDEX idx_status_created ON t");
        var slow = JoinRows(_engine.Execute(sql));
        Assert.Equal(slow, fast);
    }

    [Fact(DisplayName = "索引前缀查询带 LIMIT 时结果稳定")]
    public void IndexPrefix_LimitKeepsStableOrder()
    {
        CreateTable(5000);

        // status=2 命中约 1000 行；LIMIT 仍按主键升序取前 10 行，与全表扫描一致
        var sql = "SELECT id FROM t WHERE status = 2 LIMIT 10";
        var fast = JoinRows(_engine.Execute(sql));

        _engine.Execute("DROP INDEX idx_status_created ON t");
        var slow = JoinRows(_engine.Execute(sql));

        Assert.Equal(slow, fast);
        Assert.Equal("2|7|12|17|22|27|32|37|42|47", fast);
    }

    [Fact(DisplayName = "可见性快照与逐行判断结果一致")]
    public void VisibilitySnapshot_MatchesRowLevelCheck()
    {
        CreateTable(300);

        _engine.Execute("UPDATE t SET status = 4 WHERE id <= 20");
        _engine.Execute("DELETE FROM t WHERE id > 280");

        var byIndex = Convert.ToInt32(_engine.Execute("SELECT COUNT(*) FROM t WHERE status = 4").GetScalar());
        var byScan = Convert.ToInt32(_engine.Execute("SELECT COUNT(*) FROM t").GetScalar());

        // 索引计数与全表计数在同事务内一致（无并发写）
        var expected = Convert.ToInt32(_engine.Execute("SELECT COUNT(*) FROM t WHERE status = 4").GetScalar());
        Assert.Equal(expected, byIndex);
        Assert.Equal(300 - 20, byScan);
    }

    [Fact(DisplayName = "索引计数快路径与回表计数结果一致")]
    public void IndexOnlyCount_MatchesRowLookupCount()
    {
        CreateTable(3000);

        var fast = Convert.ToInt64(_engine.Execute("SELECT COUNT(*) FROM t WHERE status = 3").GetScalar());
        var slow = _engine.Execute("SELECT id FROM t WHERE status = 3").Rows.Count;

        Assert.Equal(600, fast);
        Assert.Equal(slow, fast);

        // 更新与删除之后仍一致
        _engine.Execute("UPDATE t SET status = 1 WHERE id <= 500");
        _engine.Execute("DELETE FROM t WHERE id >= 501 AND id <= 600");

        var fast2 = Convert.ToInt64(_engine.Execute("SELECT COUNT(*) FROM t WHERE status = 3").GetScalar());
        var slow2 = _engine.Execute("SELECT id FROM t WHERE status = 3").Rows.Count;
        Assert.Equal(slow2, fast2);
    }

    [Fact(DisplayName = "存在并发未提交写入时不使用索引计数快路径")]
    public void IndexOnlyCount_FallsBackWithConcurrentWriter()
    {
        var txManager = new NewLife.NovaDb.Tx.TransactionManager();
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var schema = new TableSchema("t");
        schema.AddColumn(new ColumnDefinition("id", DataType.Int32, nullable: false, isPrimaryKey: true));
        schema.AddColumn(new ColumnDefinition("status", DataType.Int32, nullable: false));
        var indexDef = new IndexDefinition("idx_status", ["status"]);
        schema.AddIndex(indexDef);

        using var table = new NovaTable(schema, _testDir, options, txManager);

        using (var tx = txManager.BeginTransaction())
        {
            for (var i = 1; i <= 100; i++)
                table.Insert(tx, [i, 3]);
            tx.Commit();
        }

        using var reader = txManager.BeginTransaction();
        Assert.False(txManager.HasOtherActiveTransactions(reader.TxId));

        // 另一个事务插入未提交数据：索引里已有条目，但不可见，必须回表按可见性判断
        var writer = txManager.BeginTransaction();
        table.Insert(writer, [9001, 3]);

        Assert.True(txManager.HasOtherActiveTransactions(reader.TxId));

        var entries = table.CountIndexKey("idx_status", 3);
        var visible = table.CountVisibleRows(reader, Enumerable.Range(1, 100).Cast<Object>().Concat([9001]).ToList());

        Assert.Equal(101, entries);   // 索引条目包含未提交行
        Assert.Equal(100, visible);   // 可见行不含未提交行

        writer.Rollback();
    }
}
