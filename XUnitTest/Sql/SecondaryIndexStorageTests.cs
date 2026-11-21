using System;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Sql;
using NewLife.NovaDb.Storage;
using Xunit;

#nullable enable

namespace XUnitTest.Sql;

/// <summary>二级索引落盘（.hidx 哈希字典 + 惰性有序表）的行为测试</summary>
public class SecondaryIndexStorageTests : IDisposable
{
    private readonly String _testDir;

    public SecondaryIndexStorageTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"SecIdxStorage_{Guid.NewGuid():N}");
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

    private String HidxPath(String tableName, String indexName) => Path.Combine(_testDir, $"{tableName}_{indexName}.hidx");

    private String[] QueryIds(SqlEngine engine, String sql) =>
        engine.Execute(sql).Rows.Select(r => Convert.ToString(r[0])!).OrderBy(e => e, StringComparer.Ordinal).ToArray();

    private static String Plan(SqlEngine engine, String sql) => Convert.ToString(engine.Execute("EXPLAIN " + sql).Rows[0][1])!;

    private void DeleteBaselineFiles(String tableName, String indexName)
    {
        foreach (var extension in new[] { ".idx", ".idxl" })
        {
            var path = Path.Combine(_testDir, $"{tableName}_{indexName}{extension}");
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact(DisplayName = "重开后二级索引就地复用哈希文件，等值与排序结果一致")]
    public void ReopenUsesHashFile()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30), score INT)");
            engine.Execute("CREATE INDEX idx_name ON t (name)");
            for (var i = 1; i <= 60; i++) engine.Execute($"INSERT INTO t (id, name, score) VALUES ({i}, 'n{i}', {i % 7})");
            engine.Execute("UPDATE t SET name = 'shared' WHERE id <= 5");
            engine.Execute("UPDATE t SET name = 'n5' WHERE id = 6");
            engine.Execute("DELETE FROM t WHERE id > 50");

            // 有序查询先把内存跳表建起来，随后的写入要同步维护
            Assert.Equal(50, engine.Execute("SELECT id FROM t ORDER BY name").Rows.Count);
        }

        Assert.True(File.Exists(HidxPath("t", "idx_name")));
        Assert.True(new FileInfo(HidxPath("t", "idx_name")).Length > 0);

        // 删掉旧基线后仍能查询，说明二级索引只剩哈希文件在支撑
        DeleteBaselineFiles("t", "idx_name");

        using (var reopened = CreateEngine())
        {
            // 6 号被原地改名，5 号并入 shared
            Assert.Equal("INDEX LOOKUP", Plan(reopened, "SELECT id FROM t WHERE name = 'n5'"));
            Assert.Equal(new[] { "6" }, QueryIds(reopened, "SELECT id FROM t WHERE name = 'n5'"));
            Assert.Equal(new[] { "1", "2", "3", "4", "5" }, QueryIds(reopened, "SELECT id FROM t WHERE name = 'shared'"));
            Assert.Equal("5", Convert.ToString(reopened.Execute("SELECT COUNT(*) FROM t WHERE name = 'shared'").GetScalar()));
            Assert.Empty(reopened.Execute("SELECT id FROM t WHERE name = 'name7'").Rows);

            // 有序表按需从哈希文件重建
            Assert.Equal("50", Convert.ToString(reopened.Execute("SELECT COUNT(*) FROM t").GetScalar()));
            Assert.Equal(new[] { "10", "11", "12", "13", "14" }, QueryIds(reopened, "SELECT id FROM t ORDER BY name LIMIT 5"));
            Assert.Equal(new[] { "43", "44" }, QueryIds(reopened, "SELECT id FROM t WHERE name >= 'n43' AND name < 'n45'"));
        }
    }

    [Fact(DisplayName = "联合索引重开后从前缀查询可用，走哈希文件而不是全表重建")]
    public void CompositeIndexReopen()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, a INT, b INT)");
            engine.Execute("CREATE INDEX idx_ab ON t (a, b)");
            for (var i = 1; i <= 30; i++) engine.Execute($"INSERT INTO t (id, a, b) VALUES ({i}, {i % 3}, {i})");
        }

        Assert.True(File.Exists(HidxPath("t", "idx_ab")));
        DeleteBaselineFiles("t", "idx_ab");

        using var reopened = CreateEngine();
        Assert.Equal(new[] { "6", "9" }, QueryIds(reopened, "SELECT id FROM t WHERE a = 0 AND b > 3 AND b < 10 ORDER BY id"));
        Assert.Equal(new[] { "1", "4", "7" }, QueryIds(reopened, "SELECT id FROM t WHERE a = 1 AND b > 0 AND b < 8 ORDER BY id"));
        Assert.Equal("10", Convert.ToString(reopened.Execute("SELECT COUNT(*) FROM t WHERE a = 2").GetScalar()));
    }

    [Fact(DisplayName = "哈希索引文件损坏时回退全表重建，数据不受影响")]
    public void CorruptedHashFileFallsBackToRebuild()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30))");
            engine.Execute("CREATE INDEX idx_name ON t (name)");
            for (var i = 1; i <= 20; i++) engine.Execute($"INSERT INTO t (id, name) VALUES ({i}, 'n{i}')");
        }

        var path = HidxPath("t", "idx_name");
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(FileHeader.HeaderSize / 2);
        }

        using var reopened = CreateEngine();
        Assert.Equal(new[] { "7" }, QueryIds(reopened, "SELECT id FROM t WHERE name = 'n7'"));
        Assert.Equal("20", Convert.ToString(reopened.Execute("SELECT COUNT(*) FROM t").GetScalar()));
    }

    [Fact(DisplayName = "唯一索引重开后仍拦截重复值")]
    public void UniqueIndexStillEnforcedAfterReopen()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, code VARCHAR(20))");
            engine.Execute("CREATE UNIQUE INDEX uk_code ON t (code)");
            engine.Execute("INSERT INTO t (id, code) VALUES (1, 'a'), (2, 'b')");
        }

        using var reopened = CreateEngine();
        Assert.Throws<NovaException>(() => reopened.Execute("INSERT INTO t (id, code) VALUES (3, 'a')"));

        // 允许插入新值，也允许把不同行改成自己的值
        reopened.Execute("INSERT INTO t (id, code) VALUES (3, 'c')");
        Assert.Equal("3", Convert.ToString(reopened.Execute("SELECT COUNT(*) FROM t").GetScalar()));
    }

    [Fact(DisplayName = "清空表后重建索引文件，重开无残留条目")]
    public void TruncateDropsHashEntries()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30))");
            engine.Execute("CREATE INDEX idx_name ON t (name)");
            for (var i = 1; i <= 10; i++) engine.Execute($"INSERT INTO t (id, name) VALUES ({i}, 'n{i}')");
            engine.Execute("TRUNCATE TABLE t");
        }

        using var reopened = CreateEngine();
        Assert.Empty(reopened.Execute("SELECT id FROM t WHERE name = 'n1'").Rows);
        Assert.Equal("0", Convert.ToString(reopened.Execute("SELECT COUNT(*) FROM t").GetScalar()));

        reopened.Execute("INSERT INTO t (id, name) VALUES (11, 'n1')");
        Assert.Equal(new[] { "11" }, QueryIds(reopened, "SELECT id FROM t WHERE name = 'n1'"));
    }

    [Fact(DisplayName = "只读模式复用哈希文件，不重写索引")]
    public void ReadOnlyReusesHashFile()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30))");
            engine.Execute("CREATE INDEX idx_name ON t (name)");
            for (var i = 1; i <= 10; i++) engine.Execute($"INSERT INTO t (id, name) VALUES ({i}, 'n{i}')");
        }

        var path = HidxPath("t", "idx_name");
        var length = new FileInfo(path).Length;

        using (var ro = new SqlEngine(_testDir, new DbOptions { Path = _testDir, WalMode = WalMode.None, ReadOnly = true }))
        {
            Assert.Equal(new[] { "3" }, QueryIds(ro, "SELECT id FROM t WHERE name = 'n3'"));
            Assert.Equal("10", Convert.ToString(ro.Execute("SELECT COUNT(*) FROM t").GetScalar()));
        }

        Assert.Equal(length, new FileInfo(path).Length);
    }

    [Fact(DisplayName = "索引列改类型后按新编码重建，查询仍命中")]
    public void TypeChangeRebuildsIndex()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, score INT)");
            engine.Execute("CREATE INDEX idx_score ON t (score)");
            for (var i = 1; i <= 9; i++) engine.Execute($"INSERT INTO t (id, score) VALUES ({i}, {i % 3})");

            engine.Execute("ALTER TABLE t MODIFY COLUMN score LONG");

            Assert.Equal("INDEX LOOKUP", Plan(engine, "SELECT id FROM t WHERE score = 0"));
            Assert.Equal(new[] { "3", "6", "9" }, QueryIds(engine, "SELECT id FROM t WHERE score = 0"));
            Assert.Equal("3", Convert.ToString(engine.Execute("SELECT COUNT(*) FROM t WHERE score = 1").GetScalar()));
        }

        using var reopened = CreateEngine();
        Assert.Equal(new[] { "1", "4", "7" }, QueryIds(reopened, "SELECT id FROM t WHERE score = 1"));
    }

    [Fact(DisplayName = "二级索引组合层：等值读哈希，有序表按需构建")]
    public void OrderedLayerIsLazy()
    {
        var file = Path.Combine(_testDir, "lazy.hidx");
        var codec = new DefaultDataCodec();
        var indexDef = new IndexDefinition("idx_lazy", ["name"]);

        using var index = NovaSecondaryIndex.Open(file, indexDef, codec, [DataType.String], DataType.Int32);
        index.Add("b", 2);
        index.Add("a", 1);
        index.Add("c", 3);

        Assert.False(index.IsOrderedLoaded);

        // 等值、计数只读哈希文件
        Assert.Equal(new Object[] { 2 }, index.TryGet("b")!);
        Assert.Equal(1, index.CountKey("a"));
        Assert.Null(index.TryGet("missing"));
        Assert.False(index.IsOrderedLoaded);

        // 范围/排序才触发跳表构建
        var ordered = index.EnsureOrdered();
        Assert.True(index.IsOrderedLoaded);
        Assert.Equal(new[] { "a", "b", "c" }, ordered.GetAll().Select(e => Convert.ToString(e.Key.Value)).ToArray());

        // 之后的新增与删除同步维护有序表
        index.Add("d", 4);
        index.Remove("b", 2);
        Assert.Equal(new[] { "a", "c", "d" }, ordered.GetAll().Select(e => Convert.ToString(e.Key.Value)).ToArray());
    }

    [Fact(DisplayName = "二级索引重开后哈希文件恢复全部条目")]
    public void HashFileRoundTrips()
    {
        var file = Path.Combine(_testDir, "roundtrip.hidx");
        var codec = new DefaultDataCodec();
        var indexDef = new IndexDefinition("idx_rt", ["name"]);

        using (var index = NovaSecondaryIndex.Open(file, indexDef, codec, [DataType.String], DataType.Int32))
        {
            index.Add("x", 1);
            index.Add("x", 2);
            index.Add("y", 3);
            index.Commit(1024);
        }

        using var reopened = NovaSecondaryIndex.Open(file, indexDef, codec, [DataType.String], DataType.Int32);
        Assert.Equal(2, reopened.KeyCount);
        Assert.Equal(1024, reopened.AppliedOffset);
        Assert.Equal(new Object[] { 1, 2 }, reopened.TryGet("x")!);
        Assert.Equal(new Object[] { 3 }, reopened.TryGet("y")!);
        Assert.False(reopened.IsOrderedLoaded);
    }
}
