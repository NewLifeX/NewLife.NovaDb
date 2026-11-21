using System;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

#nullable enable

namespace XUnitTest.Sql;

/// <summary>主键索引落盘（.hidx 哈希 + .oidx 有序段）的行为测试</summary>
public class PrimaryIndexStorageTests : IDisposable
{
    private readonly String _testDir;

    public PrimaryIndexStorageTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"PrimaryIndex_{Guid.NewGuid():N}");
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

    private SqlEngine CreateEngine() => new(_testDir, new DbOptions { Path = _testDir, WalMode = WalMode.None });

    private static String[] QueryIds(SqlEngine engine, String sql) =>
        engine.Execute(sql).Rows.Select(r => Convert.ToString(r[0])!).ToArray();

    private void ForceRebuild()
    {
        foreach (var pattern in new[] { "*.hidx", "*.oidx" })
        {
            foreach (var file in Directory.GetFiles(_testDir, pattern)) File.Delete(file);
        }
    }

    [Fact(DisplayName = "重开与强制重建后，点查/范围/排序结果一致")]
    public void ReopenAndRebuild()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30))");
            for (var i = 1; i <= 50; i++) engine.Execute($"INSERT INTO t (id, name) VALUES ({i}, 'n{i}')");
            engine.Execute("UPDATE t SET name = 'u' WHERE id <= 5");
            engine.Execute("DELETE FROM t WHERE id > 40");
        }

        using (var reopened = CreateEngine())
        {
            Assert.Equal("u", Convert.ToString(reopened.Execute("SELECT name FROM t WHERE id = 3").GetScalar()));
            Assert.Equal(new[] { "11", "12", "13" }, QueryIds(reopened, "SELECT id FROM t WHERE id >= 11 AND id <= 13"));
            Assert.Equal(new[] { "40", "39", "38" }, QueryIds(reopened, "SELECT id FROM t ORDER BY id DESC LIMIT 3"));
            Assert.Equal("40", Convert.ToString(reopened.Execute("SELECT COUNT(*) FROM t").GetScalar()));
        }

        ForceRebuild();

        using (var rebuilt = CreateEngine())
        {
            Assert.Equal("u", Convert.ToString(rebuilt.Execute("SELECT name FROM t WHERE id = 3").GetScalar()));
            Assert.Equal("40", Convert.ToString(rebuilt.Execute("SELECT COUNT(*) FROM t").GetScalar()));
            Assert.Equal(new[] { "40", "39", "38" }, QueryIds(rebuilt, "SELECT id FROM t ORDER BY id DESC LIMIT 3"));
        }
    }

    [Fact(DisplayName = "只有索引缺失时按行日志重建，数据不受影响")]
    public void RebuildKeepsLatestVersion()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30))");
            engine.Execute("INSERT INTO t (id, name) VALUES (1, 'a')");
            engine.Execute("UPDATE t SET name = 'b' WHERE id = 1");
        }

        ForceRebuild();

        using var rebuilt = CreateEngine();
        Assert.Equal("b", Convert.ToString(rebuilt.Execute("SELECT name FROM t WHERE id = 1").GetScalar()));
        Assert.Single(rebuilt.Execute("SELECT id FROM t").Rows);
    }

    [Fact(DisplayName = "事务内未提交写入对自身可见，提交后重开仍可见")]
    public void PendingWriteVisibility()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30))");
            engine.Execute("INSERT INTO t (id, name) VALUES (1, 'a')");
            engine.Execute("UPDATE t SET name = 'b' WHERE id = 1");
            Assert.Equal("b", Convert.ToString(engine.Execute("SELECT name FROM t WHERE id = 1").GetScalar()));
            Assert.Single(engine.Execute("SELECT id FROM t").Rows);
        }

        using var reopened = CreateEngine();
        Assert.Equal("b", Convert.ToString(reopened.Execute("SELECT name FROM t WHERE id = 1").GetScalar()));
    }

    [Fact(DisplayName = "清空表后重开为空")]
    public void TruncateThenReopen()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30))");
            engine.Execute("INSERT INTO t (id, name) VALUES (1, 'a'), (2, 'b')");
            engine.Execute("TRUNCATE TABLE t");
        }

        using var reopened = CreateEngine();
        Assert.Empty(reopened.Execute("SELECT id FROM t").Rows);
        Assert.Equal("0", Convert.ToString(reopened.Execute("SELECT COUNT(*) FROM t").GetScalar()));
    }

    [Fact(DisplayName = "修改普通列后主键索引仍可用")]
    public void AlterNormalColumn()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30))");
            engine.Execute("INSERT INTO t (id, name) VALUES (1, 'a'), (2, 'b')");
            engine.Execute("ALTER TABLE t MODIFY name VARCHAR(60)");
            Assert.Equal("a", Convert.ToString(engine.Execute("SELECT name FROM t WHERE id = 1").GetScalar()));
            Assert.Equal(new[] { "1", "2" }, QueryIds(engine, "SELECT id FROM t ORDER BY id"));
        }

        using var reopened = CreateEngine();
        Assert.Equal("b", Convert.ToString(reopened.Execute("SELECT name FROM t WHERE id = 2").GetScalar()));
    }

    [Fact(DisplayName = "主键索引存活计数在增删改、清空与重开后保持准确")]
    public void LiveCountStaysAccurate()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30))");
            for (var i = 1; i <= 100; i++) engine.Execute($"INSERT INTO t (id, name) VALUES ({i}, 'n{i}')");
            Assert.Equal("100", Convert.ToString(engine.Execute("SELECT COUNT(*) FROM t").GetScalar()));

            engine.Execute("DELETE FROM t WHERE id <= 40");
            Assert.Equal("60", Convert.ToString(engine.Execute("SELECT COUNT(*) FROM t").GetScalar()));

            engine.Execute("UPDATE t SET name = 'u' WHERE id <= 60");
            Assert.Equal("60", Convert.ToString(engine.Execute("SELECT COUNT(*) FROM t").GetScalar()));

            engine.Execute("DELETE FROM t WHERE id > 55");
            Assert.Equal("15", Convert.ToString(engine.Execute("SELECT COUNT(*) FROM t").GetScalar()));

            engine.Execute("TRUNCATE TABLE t");
            Assert.Equal("0", Convert.ToString(engine.Execute("SELECT COUNT(*) FROM t").GetScalar()));

            engine.Execute("INSERT INTO t (id, name) VALUES (1, 'a'), (2, 'b')");
            Assert.Equal("2", Convert.ToString(engine.Execute("SELECT COUNT(*) FROM t").GetScalar()));
        }

        using (var reopened = CreateEngine())
        {
            Assert.Equal("2", Convert.ToString(reopened.Execute("SELECT COUNT(*) FROM t").GetScalar()));
            reopened.Execute("DELETE FROM t");
            Assert.Equal("0", Convert.ToString(reopened.Execute("SELECT COUNT(*) FROM t").GetScalar()));
        }

        // 索引文件被删除后重建，计数仍准确
        ForceRebuild();

        using var rebuilt = CreateEngine();
        Assert.Equal("0", Convert.ToString(rebuilt.Execute("SELECT COUNT(*) FROM t").GetScalar()));
    }

    [Fact(DisplayName = "索引文件尾部被截断后重开自动重建")]
    public void CorruptedIndexRebuilds()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30))");
            for (var i = 1; i <= 20; i++) engine.Execute($"INSERT INTO t (id, name) VALUES ({i}, 'n{i}')");
        }

        foreach (var file in Directory.GetFiles(_testDir, "*.hidx").Concat(Directory.GetFiles(_testDir, "*.oidx")))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(Math.Min(stream.Length, 512));
        }

        using var reopened = CreateEngine();
        Assert.Equal("20", Convert.ToString(reopened.Execute("SELECT COUNT(*) FROM t").GetScalar()));
        Assert.Equal("n7", Convert.ToString(reopened.Execute("SELECT name FROM t WHERE id = 7").GetScalar()));
    }
}
