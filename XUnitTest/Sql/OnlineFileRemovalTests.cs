using System;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

#nullable enable

namespace XUnitTest.Sql;

/// <summary>运行期删除/移走表文件的降级行为测试</summary>
/// <remarks>
/// 模拟运维操作：Linux 直接删除，Windows 上存在映射视图时删除会被拒，改用改名移走。
/// 巡检间隔设为 0，使每次操作都会触发文件检查，保证测试确定。
/// </remarks>
public class OnlineFileRemovalTests : IDisposable
{
    private readonly String _testDir;

    public OnlineFileRemovalTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"OnlineRemoval_{Guid.NewGuid():N}");
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

    private SqlEngine CreateEngine() => new(_testDir, new DbOptions { Path = _testDir, WalMode = WalMode.None, FileWatchIntervalMs = 0 });

    private static String LocalPath(String dir, String fileName) => Path.Combine(dir, fileName);

    /// <summary>模拟运维删除单个文件：删不掉就改名移走（Windows 有映射视图时不能删除）</summary>
    private static void RemoveOrMove(String path)
    {
        try
        {
            File.Delete(path);
            return;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var moved = path + ".removed";
        if (File.Exists(moved)) File.Delete(moved);

        File.Move(path, moved);
    }

    private static String[] QueryIds(SqlEngine engine, String sql) =>
        engine.Execute(sql).Rows.Select(r => Convert.ToString(r[0])!).ToArray();

    [Fact(DisplayName = "移走二级索引文件后查询回退全表扫描，重开自动重建")]
    public void SecondaryIndexRemovedFallsBackToFullScan()
    {
        var indexPath = LocalPath(_testDir, "t_idx_score.hidx");

        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, score INT, name VARCHAR(30))");
            engine.Execute("CREATE INDEX idx_score ON t (score)");
            for (var i = 1; i <= 200; i++)
                engine.Execute($"INSERT INTO t (id, score, name) VALUES ({i}, {i % 20}, 'n{i}')");

            var plan = engine.Execute("EXPLAIN SELECT id FROM t WHERE score = 3");
            Assert.Contains("idx_score", Convert.ToString(plan.Rows[0][3])!);
            Assert.True(File.Exists(indexPath));

            RemoveOrMove(indexPath);
            Assert.False(File.Exists(indexPath));

            Assert.Equal(10, engine.Execute("SELECT id FROM t WHERE score = 3").Rows.Count);
            Assert.Equal("200", Convert.ToString(engine.Execute("SELECT COUNT(*) FROM t").GetScalar()));

            var degraded = engine.Execute("EXPLAIN SELECT id FROM t WHERE score = 3");
            Assert.Equal("FULL SCAN", Convert.ToString(degraded.Rows[0][1]));
            Assert.DoesNotContain("idx_score", Convert.ToString(degraded.Rows[0][3])!);

            // 降级后仍可写入，索引不再维护但数据正确
            engine.Execute("INSERT INTO t (id, score, name) VALUES (201, 3, 'n201')");
            Assert.Equal(11, engine.Execute("SELECT id FROM t WHERE score = 3").Rows.Count);
        }

        using var reopened = CreateEngine();
        Assert.True(File.Exists(indexPath));
        Assert.Equal(11, reopened.Execute("SELECT id FROM t WHERE score = 3").Rows.Count);
        Assert.Equal("201", Convert.ToString(reopened.Execute("SELECT COUNT(*) FROM t").GetScalar()));
    }

    [Fact(DisplayName = "移走主键哈希索引后按行日志自动重建")]
    public void PrimaryHashIndexRemovedIsRebuilt()
    {
        var hashPath = LocalPath(_testDir, "t.hidx");

        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30))");
        for (var i = 1; i <= 50; i++) engine.Execute($"INSERT INTO t (id, name) VALUES ({i}, 'n{i}')");

        RemoveOrMove(hashPath);
        Assert.False(File.Exists(hashPath));

        Assert.Equal("n3", Convert.ToString(engine.Execute("SELECT name FROM t WHERE id = 3").GetScalar()));
        Assert.True(File.Exists(hashPath), "主键索引应在访问时重建");
        Assert.Equal("50", Convert.ToString(engine.Execute("SELECT COUNT(*) FROM t").GetScalar()));
        Assert.Equal(new[] { "11", "12", "13" }, QueryIds(engine, "SELECT id FROM t WHERE id >= 11 AND id <= 13"));
    }

    [Fact(DisplayName = "移走主键有序索引后范围与排序仍正确")]
    public void PrimaryOrderedIndexRemovedIsRebuilt()
    {
        var orderedPath = LocalPath(_testDir, "t.oidx");

        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30))");
        for (var i = 1; i <= 30; i++) engine.Execute($"INSERT INTO t (id, name) VALUES ({i}, 'n{i}')");

        RemoveOrMove(orderedPath);
        Assert.False(File.Exists(orderedPath));

        Assert.Equal(new[] { "30", "29" }, QueryIds(engine, "SELECT id FROM t ORDER BY id DESC LIMIT 2"));
        Assert.True(File.Exists(orderedPath), "主键有序索引应在访问时重建");
        Assert.Equal(new[] { "28", "29", "30" }, QueryIds(engine, "SELECT id FROM t WHERE id >= 28 ORDER BY id"));
    }

    [Fact(DisplayName = "移走行数据文件后表按空表继续服务")]
    public void RowLogRemovedResetsTableToEmpty()
    {
        var rowLogPath = LocalPath(_testDir, "t.rlog");

        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30))");
            engine.Execute("INSERT INTO t (id, name) VALUES (1, 'a'), (2, 'b')");

            RemoveOrMove(rowLogPath);
            Assert.False(File.Exists(rowLogPath));

            Assert.Empty(engine.Execute("SELECT id FROM t").Rows);
            Assert.Equal("0", Convert.ToString(engine.Execute("SELECT COUNT(*) FROM t").GetScalar()));

            // 空表可继续写入，数据文件被重建
            engine.Execute("INSERT INTO t (id, name) VALUES (7, 'g')");
            Assert.True(File.Exists(rowLogPath));
            Assert.Equal("7", Convert.ToString(engine.Execute("SELECT id FROM t").GetScalar()));
        }

        using var reopened = CreateEngine();
        Assert.Equal("7", Convert.ToString(reopened.Execute("SELECT id FROM t").GetScalar()));
        Assert.Equal("1", Convert.ToString(reopened.Execute("SELECT COUNT(*) FROM t").GetScalar()));
    }

    [Fact(DisplayName = "行数据文件缺失时二级索引一起重置，重开后不会残留旧条目")]
    public void RowLogRemovedAlsoResetsSecondaryIndexes()
    {
        var rowLogPath = LocalPath(_testDir, "t.rlog");

        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, score INT)");
            engine.Execute("CREATE INDEX idx_score ON t (score)");
            for (var i = 1; i <= 20; i++) engine.Execute($"INSERT INTO t (id, score) VALUES ({i}, {i % 5})");
            Assert.Equal(4, engine.Execute("SELECT id FROM t WHERE score = 1").Rows.Count);

            RemoveOrMove(rowLogPath);

            Assert.Equal("0", Convert.ToString(engine.Execute("SELECT COUNT(*) FROM t").GetScalar()));
            Assert.Empty(engine.Execute("SELECT id FROM t WHERE score = 1").Rows);
        }

        using var reopened = CreateEngine();
        Assert.Equal("0", Convert.ToString(reopened.Execute("SELECT COUNT(*) FROM t").GetScalar()));
        Assert.Empty(reopened.Execute("SELECT id FROM t WHERE score = 1").Rows);
    }

    [Fact(DisplayName = "唯一索引文件缺失后降级为不校验唯一性")]
    public void UniqueIndexRemovedSkipsUniquenessCheck()
    {
        var indexPath = LocalPath(_testDir, "u_uq_email.hidx");

        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE u (id INT PRIMARY KEY, email VARCHAR(50))");
        engine.Execute("CREATE UNIQUE INDEX uq_email ON u (email)");
        engine.Execute("INSERT INTO u (id, email) VALUES (1, 'a@x')");

        RemoveOrMove(indexPath);

        // 降级后不抛唯一约束异常
        engine.Execute("INSERT INTO u (id, email) VALUES (2, 'a@x')");
        Assert.Equal("2", Convert.ToString(engine.Execute("SELECT COUNT(*) FROM u").GetScalar()));
    }

    [Fact(DisplayName = "_sys.table_files 反映缺失与降级状态")]
    public void SystemTableReportsFileState()
    {
        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, score INT)");
        engine.Execute("CREATE INDEX idx_score ON t (score)");
        engine.Execute("INSERT INTO t (id, score) VALUES (1, 1)");

        var before = engine.Execute("SELECT * FROM _sys.table_files WHERE table_name = 't'");
        Assert.NotEmpty(before.Rows);
        Assert.Contains(before.Rows, r => Convert.ToString(r[2]) == "data" && Convert.ToBoolean(r[3]));

        RemoveOrMove(LocalPath(_testDir, "t_idx_score.hidx"));

        var after = engine.Execute("SELECT * FROM _sys.table_files WHERE table_name = 't'");
        Assert.Contains(after.Rows, r => Convert.ToString(r[2]) == "secondary_index" && !Convert.ToBoolean(r[3]) && Convert.ToBoolean(r[5]));
    }

    [Fact(DisplayName = "正常删除数据行不会触发文件降级")]
    public void NormalDeletesDoNotTriggerDegradation()
    {
        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30))");
        engine.Execute("INSERT INTO t (id, name) VALUES (1, 'a'), (2, 'b'), (3, 'c')");
        engine.Execute("DELETE FROM t WHERE id = 2");
        engine.Execute("TRUNCATE TABLE t");
        engine.Execute("INSERT INTO t (id, name) VALUES (9, 'z')");

        Assert.Equal("9", Convert.ToString(engine.Execute("SELECT id FROM t").GetScalar()));
        Assert.True(File.Exists(LocalPath(_testDir, "t.rlog")));
        Assert.True(File.Exists(LocalPath(_testDir, "t.hidx")));
    }
}
