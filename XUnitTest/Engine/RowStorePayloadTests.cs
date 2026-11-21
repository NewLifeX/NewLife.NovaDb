using System;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Sql;
using NewLife.NovaDb.Tx;
using Xunit;

#nullable enable

namespace XUnitTest.Engine;

/// <summary>行载荷按偏移从行日志读取（内存映射窗口）的回归测试</summary>
public class RowStorePayloadTests : IDisposable
{
    private readonly String _testDir;

    public RowStorePayloadTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"RowStorePayloadTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); }
            catch { }
        }
    }

    private TableSchema CreateSchema()
    {
        var schema = new TableSchema("docs");
        schema.AddColumn(new ColumnDefinition("id", DataType.Int32, nullable: false, isPrimaryKey: true));
        schema.AddColumn(new ColumnDefinition("title", DataType.String, nullable: false));
        schema.AddColumn(new ColumnDefinition("body", DataType.String, nullable: true));
        return schema;
    }

    [Fact(DisplayName = "提交后载荷改由行日志读取，更新与删除结果正确")]
    public void PayloadOffloadedThenUpdatedAndDeleted()
    {
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();
        var schema = CreateSchema();

        using (var table = new NovaTable(schema, _testDir, options, txManager))
        {
            using (var tx = txManager.BeginTransaction())
            {
                table.Insert(tx, [1, "first", "body-1"]);
                table.Insert(tx, [2, "second", "body-2"]);
                tx.Commit();
            }

            // 提交后载荷已落盘，读取必须走行日志偏移
            using (var tx = txManager.BeginTransaction())
            {
                var row = table.Get(tx, 1);
                Assert.NotNull(row);
                Assert.Equal("first", row![1]);
                Assert.Equal("body-1", row[2]);

                table.Update(tx, 2, [2, "second-updated", "body-2b"]);
                tx.Commit();
            }

            using (var tx = txManager.BeginTransaction())
            {
                var row = table.Get(tx, 2);
                Assert.NotNull(row);
                Assert.Equal("second-updated", row![1]);

                table.Delete(tx, 1);
                tx.Commit();
            }

            using (var tx = txManager.BeginTransaction())
            {
                Assert.Null(table.Get(tx, 1));
                Assert.Equal("body-2b", table.Get(tx, 2)![2]);
                Assert.Single(table.GetAll(tx));
            }
        }
    }

    [Fact(DisplayName = "关闭重开后按偏移恢复载荷")]
    public void PayloadSurvivesReopen()
    {
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();
        var schema = CreateSchema();

        using (var table = new NovaTable(schema, _testDir, options, txManager))
        {
            using var tx = txManager.BeginTransaction();
            for (var i = 1; i <= 200; i++)
                table.Insert(tx, [i, $"title-{i}", $"body-{i}"]);
            tx.Commit();
        }

        var txManager2 = new TransactionManager();
        using (var table = new NovaTable(schema, _testDir, options, txManager2))
        {
            using var tx = txManager2.BeginTransaction();
            Assert.Equal(200, table.GetAll(tx).Count);
            Assert.Equal("body-150", table.Get(tx, 150)![2]);
        }
    }

    [Fact(DisplayName = "行日志压缩后载荷偏移重映射")]
    public void PayloadRemappedAfterCompact()
    {
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();
        var schema = CreateSchema();

        using var table = new NovaTable(schema, _testDir, options, txManager);

        using (var tx = txManager.BeginTransaction())
        {
            for (var i = 1; i <= 100; i++)
                table.Insert(tx, [i, $"title-{i}", $"body-{i}"]);
            tx.Commit();
        }

        using (var tx = txManager.BeginTransaction())
        {
            for (var i = 1; i <= 50; i++)
                table.Delete(tx, i);
            tx.Commit();
        }

        table.CompactRowLog();

        using var readTx = txManager.BeginTransaction();
        Assert.Equal(50, table.GetAll(readTx).Count);
        Assert.Equal("body-80", table.Get(readTx, 80)![2]);
        Assert.Null(table.Get(readTx, 10));
    }

    [Fact(DisplayName = "清空表后重新写入仍可读")]
    public void PayloadReadableAfterTruncate()
    {
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();
        var schema = CreateSchema();

        using var table = new NovaTable(schema, _testDir, options, txManager);

        using (var tx = txManager.BeginTransaction())
        {
            for (var i = 1; i <= 20; i++)
                table.Insert(tx, [i, $"title-{i}", $"body-{i}"]);
            tx.Commit();
        }

        table.Truncate();

        using (var tx = txManager.BeginTransaction())
        {
            table.Insert(tx, [1000, "after-truncate", "body-x"]);
            tx.Commit();
        }

        using (var tx = txManager.BeginTransaction())
        {
            Assert.Single(table.GetAll(tx));
            Assert.Equal("body-x", table.Get(tx, 1000)![2]);
        }
    }

    [Fact(DisplayName = "超过映射窗口的大表按需滑动窗口读取")]
    public void PayloadWindowSlidesOverLargeRows()
    {
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();
        var schema = CreateSchema();

        // 每行约 10KB，合计超过 8MB 的映射窗口，强制窗口滑动
        var body = new String('x', 10 * 1024);

        using var table = new NovaTable(schema, _testDir, options, txManager);

        using (var tx = txManager.BeginTransaction())
        {
            for (var i = 1; i <= 1000; i++)
                table.Insert(tx, [i, $"title-{i}", body + i]);
            tx.Commit();
        }

        using var readTx = txManager.BeginTransaction();
        Assert.Equal(1000, table.GetAll(readTx).Count);
        Assert.Equal(body + 1, table.Get(readTx, 1)![2]);
        Assert.Equal(body + 999, table.Get(readTx, 999)![2]);
    }

    [Fact(DisplayName = "ANALYZE 在载荷落盘后仍能统计")]
    public void AnalyzeWorksWithOffloadedPayload()
    {
        var engine = new SqlEngine(_testDir, new DbOptions { Path = _testDir, WalMode = WalMode.None });

        try
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, status INT NOT NULL, remark VARCHAR(50))");
            for (var i = 1; i <= 500; i++)
                engine.Execute($"INSERT INTO t (id, status, remark) VALUES ({i}, {i % 5}, 'r{i % 10}')");

            engine.Execute("ANALYZE TABLE t");

            var stats = engine.Execute("SELECT table_name, column_name, row_count, ndv FROM _sys.stats WHERE table_name = 't'");
            var rowCount = Convert.ToInt64(stats.Rows[0][2]);
            Assert.Equal(500, rowCount);

            var statusRow = stats.Rows.FirstOrDefault(r => String.Equals(Convert.ToString(r[1]), "status", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(statusRow);
            Assert.Equal(5, Convert.ToInt64(statusRow![3]));
        }
        finally
        {
            engine.Dispose();
        }
    }
}
