using System;
using System.IO;
using System.Linq;
using NewLife.NovaDb.WAL;
using Xunit;

namespace XUnitTest.WAL;

/// <summary>Binlog 写入与读取回放测试（L04）</summary>
public class BinlogWriterTests : IDisposable
{
    private readonly String _baseDir;
    private readonly String _dbPath;

    public BinlogWriterTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), $"BinlogTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "binlog");
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDir))
        {
            try { Directory.Delete(_baseDir, true); }
            catch { }
        }
    }

    [Fact(DisplayName = "写入事件后读取回放一致")]
    public void WriteThenReadBack()
    {
        using (var writer = new BinlogWriter(_dbPath, "testdb"))
        {
            writer.Write(BinlogEventType.Insert, "INSERT INTO users VALUES (1, 'Alice')", 1);
            writer.Write(BinlogEventType.Update, "UPDATE users SET age = 30 WHERE id = 1", 1);
            writer.Write(BinlogEventType.Delete, "DELETE FROM users WHERE id = 1", 1);
        }

        using var reader = new BinlogWriter(_dbPath, "testdb");
        var events = reader.ReadEvents(1);

        Assert.Equal(3, events.Count);
        Assert.Equal(BinlogEventType.Insert, events[0].EventType);
        Assert.Equal("INSERT INTO users VALUES (1, 'Alice')", events[0].Sql);
        Assert.Equal(1, events[0].AffectedRows);
        Assert.Equal("testdb", events[0].Database);
        Assert.True(events[0].Timestamp > 0);

        Assert.Equal(BinlogEventType.Update, events[1].EventType);
        Assert.Equal("UPDATE users SET age = 30 WHERE id = 1", events[1].Sql);

        Assert.Equal(BinlogEventType.Delete, events[2].EventType);
        Assert.Equal("DELETE FROM users WHERE id = 1", events[2].Sql);
    }

    [Fact(DisplayName = "事件序号按写入顺序递增")]
    public void EventPositions_Increment()
    {
        using (var writer = new BinlogWriter(_dbPath, "testdb"))
        {
            writer.Write(BinlogEventType.Insert, "INSERT INTO t VALUES (1)", 1);
            writer.Write(BinlogEventType.Insert, "INSERT INTO t VALUES (2)", 1);
        }

        using var reader = new BinlogWriter(_dbPath, "testdb");
        var events = reader.ReadEvents(1);
        Assert.Equal(0, events[0].Position);
        Assert.Equal(1, events[1].Position);
    }

    [Fact(DisplayName = "Rotate 轮转后多文件各自回放")]
    public void Rotate_MultipleFiles()
    {
        using (var writer = new BinlogWriter(_dbPath, "testdb"))
        {
            writer.Write(BinlogEventType.Insert, "INSERT INTO t VALUES (1)", 1);
            writer.Rotate();
            writer.Write(BinlogEventType.Update, "UPDATE t SET x = 2 WHERE id = 1", 1);
            writer.Rotate();
            writer.Write(BinlogEventType.Delete, "DELETE FROM t WHERE id = 1", 1);
        }

        using var reader = new BinlogWriter(_dbPath, "testdb");
        var file1 = reader.ReadEvents(1);
        var file2 = reader.ReadEvents(2);
        var file3 = reader.ReadEvents(3);

        // 每个文件含写入事件 + 轮转标记
        Assert.Contains(file1, e => e.EventType == BinlogEventType.Insert);
        Assert.Contains(file2, e => e.EventType == BinlogEventType.Update);
        Assert.Contains(file3, e => e.EventType == BinlogEventType.Delete);

        // 轮转标记写入
        Assert.Contains(file1, e => e.EventType == BinlogEventType.Rotate);
        Assert.Contains(file2, e => e.EventType == BinlogEventType.Rotate);
    }

    [Fact(DisplayName = "ListFiles 列出全部 Binlog 文件")]
    public void ListFiles_ReturnsAll()
    {
        using (var writer = new BinlogWriter(_dbPath, "testdb"))
        {
            writer.Write(BinlogEventType.Insert, "INSERT INTO t VALUES (1)", 1);
            writer.Rotate();
            writer.Write(BinlogEventType.Insert, "INSERT INTO t VALUES (2)", 1);
        }

        using var reader = new BinlogWriter(_dbPath, "testdb");
        var files = reader.ListFiles();
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.True(f.Size > 0));
    }

    [Fact(DisplayName = "Purge 清理旧文件保留新文件")]
    public void Purge_RemovesOldFiles()
    {
        using (var writer = new BinlogWriter(_dbPath, "testdb"))
        {
            writer.Write(BinlogEventType.Insert, "INSERT INTO t VALUES (1)", 1);
            writer.Rotate();
            writer.Write(BinlogEventType.Insert, "INSERT INTO t VALUES (2)", 1);
            writer.Rotate();
            writer.Write(BinlogEventType.Insert, "INSERT INTO t VALUES (3)", 1);
        }

        Int32 purged;
        using (var w = new BinlogWriter(_dbPath, "testdb"))
        {
            purged = w.Purge(2);
        }

        Assert.True(purged >= 1);
        using var reader = new BinlogWriter(_dbPath, "testdb");
        var files = reader.ListFiles();
        // Purge(2) 删除 index < 2 的文件，保留 index >= 2 的文件（文件 2、3）
        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(files, f => f.FileName == "binlog.000001");
        Assert.Contains("binlog.000002", files[0].FileName);
    }

    [Fact(DisplayName = "不存在的文件读取返回空")]
    public void ReadEvents_NonexistentFile_Empty()
    {
        using var reader = new BinlogWriter(_dbPath, "testdb");
        var events = reader.ReadEvents(99);
        Assert.Empty(events);
    }
}
