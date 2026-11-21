using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using NewLife.NovaDb.Storage;
using Xunit;

#nullable enable

namespace XUnitTest.Sql;

/// <summary>二级索引增量持久化测试：基线 + 增量回放、压缩、损坏回退与写入性能</summary>
public class SecondaryIndexDeltaTests : IDisposable
{
    private readonly String _testDir;

    public SecondaryIndexDeltaTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"SecIdxDeltaTests_{Guid.NewGuid():N}");
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

    private SqlEngine CreateEngine(String? path = null)
    {
        var dir = path ?? _testDir;

        return new SqlEngine(dir, new DbOptions { Path = dir, WalMode = WalMode.None });
    }

    private String GetIdxPath(String tableName, String indexName, String? path = null) =>
        Path.Combine(path ?? _testDir, $"{tableName}_{indexName}.idx");

    private String GetDeltaPath(String tableName, String indexName, String? path = null) =>
        Path.Combine(path ?? _testDir, $"{tableName}_{indexName}.idxl");

    private static String[] QueryIds(SqlEngine engine, String sql) =>
        engine.Execute(sql).Rows.Select(r => Convert.ToString(r[0])!).OrderBy(e => e, StringComparer.Ordinal).ToArray();

    /// <summary>全表扫描后按列值过滤，作为索引查询的正确性基准</summary>
    private static String[] ScanIds(SqlEngine engine, String tableName, String value) =>
        engine.Execute($"SELECT id, name FROM {tableName}").Rows
            .Where(r => String.Equals(Convert.ToString(r[1]), value, StringComparison.OrdinalIgnoreCase))
            .Select(r => Convert.ToString(r[0])!)
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToArray();

    private static void CopyDirectory(String source, String destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }

    /// <summary>删掉二级索引哈希文件，强制走"基线 + 增量回放"这条兜底路径</summary>
    private static void DeleteSecondaryHashFiles(String directory)
    {
        foreach (var file in Directory.GetFiles(directory, "*.hidx")) File.Delete(file);
    }

    [Fact(DisplayName = "二级索引提交只追加增量，不重写基线")]
    public void Commit_AppendsDeltaWithoutRewritingBaseline()
    {
        using var engine = CreateEngine();

        engine.Execute("CREATE TABLE t1 (id INT PRIMARY KEY, name VARCHAR(50))");
        engine.Execute("INSERT INTO t1 (id, name) VALUES (1, 'a'), (2, 'b'), (3, 'c')");
        engine.Execute("CREATE INDEX idx_t1_name ON t1 (name)");

        var idxFile = GetIdxPath("t1", "idx_t1_name");
        var deltaFile = GetDeltaPath("t1", "idx_t1_name");
        Assert.True(File.Exists(idxFile), "建索引提交后应写入基线 .idx");

        // 逐条 INSERT（每行一个事务），基线长度不应再随提交次数增长
        var baselineLength = new FileInfo(idxFile).Length;
        for (var i = 4; i <= 13; i++)
        {
            engine.Execute($"INSERT INTO t1 (id, name) VALUES ({i}, 'n{i}')");
            Assert.Equal(baselineLength, new FileInfo(idxFile).Length);
        }

        Assert.True(File.Exists(deltaFile), "逐条插入后应产生增量文件 .idxl");
        Assert.True(new FileInfo(deltaFile).Length > FileHeader.HeaderSize, "增量文件应包含记录体");
    }

    [Fact(DisplayName = "写入更新删除后重启，索引查询与全表扫描一致")]
    public void Reload_AfterUpdateAndDelete_IndexMatchesFullScan()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t2 (id INT PRIMARY KEY, name VARCHAR(50))");
            engine.Execute("CREATE INDEX idx_t2_name ON t2 (name)");
            for (var i = 1; i <= 20; i++)
                engine.Execute($"INSERT INTO t2 (id, name) VALUES ({i}, 'name{i}')");

            // 基线之后的变更全部落在增量：更新 5 行、删除 5 行
            engine.Execute("UPDATE t2 SET name = 'shared' WHERE id <= 5");
            engine.Execute("DELETE FROM t2 WHERE id > 15");
        }

        using var reopened = CreateEngine();

        Assert.Equal("INDEX LOOKUP", Convert.ToString(reopened.Execute("EXPLAIN SELECT * FROM t2 WHERE name = 'shared'").Rows[0][1])!);

        var indexIds = QueryIds(reopened, "SELECT id FROM t2 WHERE name = 'shared'");
        Assert.Equal(ScanIds(reopened, "t2", "shared"), indexIds);
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, indexIds);

        // 更新前的旧值不应再被索引命中，被删除的行也应消失
        Assert.Empty(QueryIds(reopened, "SELECT id FROM t2 WHERE name = 'name1'"));
        Assert.Empty(QueryIds(reopened, "SELECT id FROM t2 WHERE name = 'name16'"));

        var keptIds = QueryIds(reopened, "SELECT id FROM t2 WHERE name = 'name10'");
        Assert.Equal(ScanIds(reopened, "t2", "name10"), keptIds);
        Assert.Equal(new[] { "10" }, keptIds);
    }

    [Fact(DisplayName = "关闭表时压缩增量：基线含全部条目且增量文件被清理")]
    public void Dispose_CompactsDeltaIntoBaseline()
    {
        var idxFile = GetIdxPath("t3", "idx_t3_name");
        var deltaFile = GetDeltaPath("t3", "idx_t3_name");
        Int64 baselineBeforeClose;

        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t3 (id INT PRIMARY KEY, name VARCHAR(50))");
            engine.Execute("CREATE INDEX idx_t3_name ON t3 (name)");
            baselineBeforeClose = new FileInfo(idxFile).Length;

            for (var i = 1; i <= 30; i++)
                engine.Execute($"INSERT INTO t3 (id, name) VALUES ({i}, 'v{i}')");

            Assert.True(File.Exists(deltaFile), "写入后应有增量文件");
            Assert.Equal(baselineBeforeClose, new FileInfo(idxFile).Length);
        }

        Assert.True(File.Exists(idxFile));
        Assert.False(File.Exists(deltaFile), "关闭后增量文件应被清理");
        Assert.True(new FileInfo(idxFile).Length > baselineBeforeClose, "关闭时应把增量合并进基线");

        using var reopened = CreateEngine();
        Assert.Equal("INDEX LOOKUP", Convert.ToString(reopened.Execute("EXPLAIN SELECT * FROM t3 WHERE name = 'v30'").Rows[0][1])!);
        Assert.Equal(new[] { "30" }, QueryIds(reopened, "SELECT id FROM t3 WHERE name = 'v30'"));
        Assert.Equal(30, Convert.ToInt32(reopened.Execute("SELECT COUNT(*) FROM t3").GetScalar()));
    }

    [Fact(DisplayName = "增量文件尾部截断后重启回退全表重建")]
    public void TruncatedDelta_FallsBackToRebuild()
    {
        var snapshotDir = Path.Combine(Path.GetTempPath(), $"SecIdxDeltaSnap_{Guid.NewGuid():N}");
        try
        {
            using (var engine = CreateEngine())
            {
                engine.Execute("CREATE TABLE t4 (id INT PRIMARY KEY, name VARCHAR(50))");
                engine.Execute("INSERT INTO t4 (id, name) VALUES (1, 'a'), (2, 'b'), (3, 'c')");
                engine.Execute("CREATE INDEX idx_t4_name ON t4 (name)");

                // 基线只含前 3 行，其余 5 行只存在增量里
                for (var i = 4; i <= 8; i++)
                    engine.Execute($"INSERT INTO t4 (id, name) VALUES ({i}, 'k{i}')");

                // 复制目录得到崩溃后的磁盘快照
                CopyDirectory(_testDir, snapshotDir);
            }

            var deltaFile = GetDeltaPath("t4", "idx_t4_name", snapshotDir);
            Assert.True(File.Exists(deltaFile), "快照中应保留增量文件");

            // 哈希文件丢失时才需要基线 + 增量，损毁的增量必须让位给全表重建
            DeleteSecondaryHashFiles(snapshotDir);

            var length = new FileInfo(deltaFile).Length;
            using (var fs = new FileStream(deltaFile, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(length - 5);
            }

            using var reopened = new SqlEngine(snapshotDir, new DbOptions { Path = snapshotDir, WalMode = WalMode.None });

            // 增量中的行必须能查到，否则说明没有回退到全表重建
            var ids = QueryIds(reopened, "SELECT id FROM t4 WHERE name = 'k7'");
            Assert.Equal(new[] { "7" }, ids);
            Assert.False(File.Exists(deltaFile), "增量损坏后应被丢弃");
        }
        finally
        {
            if (Directory.Exists(snapshotDir))
            {
                try { Directory.Delete(snapshotDir, recursive: true); }
                catch { }
            }
        }
    }

    [Fact(DisplayName = "TRUNCATE 后重启不会从旧索引文件回放出旧行")]
    public void Truncate_ThenReopen_NoStaleIndexRows()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t5 (id INT PRIMARY KEY, name VARCHAR(50))");
            engine.Execute("CREATE INDEX idx_t5_name ON t5 (name)");
            for (var i = 1; i <= 10; i++)
                engine.Execute($"INSERT INTO t5 (id, name) VALUES ({i}, 'old{i}')");

            engine.Execute("TRUNCATE TABLE t5");
            Assert.Equal(0, Convert.ToInt32(engine.Execute("SELECT COUNT(*) FROM t5").GetScalar()));

            engine.Execute("INSERT INTO t5 (id, name) VALUES (100, 'fresh')");
        }

        using var reopened = CreateEngine();
        Assert.Equal(new[] { "100" }, QueryIds(reopened, "SELECT id FROM t5 WHERE name = 'fresh'"));
        Assert.Empty(QueryIds(reopened, "SELECT id FROM t5 WHERE name = 'old3'"));
        Assert.Equal(1, Convert.ToInt32(reopened.Execute("SELECT COUNT(*) FROM t5").GetScalar()));
    }

    [Fact(DisplayName = "表改名后索引文件随迁且重启查询正确")]
    public void RenameTable_IndexSurvivesReopen()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t6 (id INT PRIMARY KEY, name VARCHAR(50))");
            engine.Execute("CREATE INDEX idx_t6_name ON t6 (name)");
            for (var i = 1; i <= 5; i++)
                engine.Execute($"INSERT INTO t6 (id, name) VALUES ({i}, 'mv{i}')");

            engine.Execute("RENAME TABLE t6 TO t6_renamed");
        }

        Assert.True(File.Exists(GetIdxPath("t6_renamed", "idx_t6_name")), "索引文件应随表改名搬迁");
        Assert.False(File.Exists(GetIdxPath("t6", "idx_t6_name")), "旧表名的索引文件应已移除");

        using var reopened = CreateEngine();
        Assert.Equal(new[] { "3" }, QueryIds(reopened, "SELECT id FROM t6_renamed WHERE name = 'mv3'"));
    }

    [Fact(DisplayName = "有效增量走回放而非重建，结果与全表扫描一致")]
    public void ValidDelta_ReplaysWithoutRebuild()
    {
        var snapshotDir = Path.Combine(Path.GetTempPath(), $"SecIdxDeltaReplay_{Guid.NewGuid():N}");
        try
        {
            using (var engine = CreateEngine())
            {
                engine.Execute("CREATE TABLE t7 (id INT PRIMARY KEY, name VARCHAR(50))");
                engine.Execute("INSERT INTO t7 (id, name) VALUES (1, 'a'), (2, 'b')");
                engine.Execute("CREATE INDEX idx_t7_name ON t7 (name)");

                // 基线之后只写增量：多行插入（一次提交多条记录）、更新、删除各一批
                engine.Execute("INSERT INTO t7 (id, name) VALUES (3, 'x3'), (4, 'x4'), (5, 'x5'), (6, 'x6')");
                for (var i = 7; i <= 10; i++)
                    engine.Execute($"INSERT INTO t7 (id, name) VALUES ({i}, 'x{i}')");
                engine.Execute("UPDATE t7 SET name = 'moved' WHERE id <= 3");
                engine.Execute("DELETE FROM t7 WHERE id = 4");

                CopyDirectory(_testDir, snapshotDir);
            }

            var deltaFile = GetDeltaPath("t7", "idx_t7_name", snapshotDir);
            Assert.True(File.Exists(deltaFile), "快照中应保留增量文件");

            // 哈希文件仍在时直接复用、不读增量；删掉后才是"基线 + 增量回放"路径
            DeleteSecondaryHashFiles(snapshotDir);

            using var reopened = new SqlEngine(snapshotDir, new DbOptions { Path = snapshotDir, WalMode = WalMode.None });

            // 增量完整时不应删除增量文件（删除即代表走了全表重建）
            Assert.True(File.Exists(deltaFile), "增量完整时应回放而非重建");

            var indexIds = QueryIds(reopened, "SELECT id FROM t7 WHERE name = 'x9'");
            Assert.Equal(ScanIds(reopened, "t7", "x9"), indexIds);
            Assert.Equal(new[] { "9" }, indexIds);
            Assert.Empty(QueryIds(reopened, "SELECT id FROM t7 WHERE name = 'x4'"));
            Assert.Equal(new[] { "1", "2", "3" }, QueryIds(reopened, "SELECT id FROM t7 WHERE name = 'moved'"));
            Assert.Equal(ScanIds(reopened, "t7", "moved"), QueryIds(reopened, "SELECT id FROM t7 WHERE name = 'moved'"));
        }
        finally
        {
            if (Directory.Exists(snapshotDir))
            {
                try { Directory.Delete(snapshotDir, recursive: true); }
                catch { }
            }
        }
    }

    [Fact(DisplayName = "逐条插入 5000 行性能冒烟（增量提交不再全量重写索引）")]
    public void SingleRowInsert_PerformanceSmoke()
    {
        using var engine = CreateEngine();

        engine.Execute("CREATE TABLE perf (id INT PRIMARY KEY, name VARCHAR(50), amount INT)");
        engine.Execute("CREATE INDEX idx_perf_name ON perf (name)");

        var sw = Stopwatch.StartNew();
        for (var i = 1; i <= 5000; i++)
            engine.Execute($"INSERT INTO perf (id, name, amount) VALUES ({i}, 'name{i}', {i})");
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"5000 行单条插入耗时 {sw.Elapsed.TotalSeconds:F1} 秒，超过 20 秒阈值");
        Assert.Equal(5000, Convert.ToInt32(engine.Execute("SELECT COUNT(*) FROM perf").GetScalar()));
        Assert.Equal(new[] { "4999" }, QueryIds(engine, "SELECT id FROM perf WHERE name = 'name4999'"));
    }
}
