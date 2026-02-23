using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;

#nullable enable

namespace XUnitTest.Engine.Flux;

/// <summary>FluxEngine 单元测试</summary>
public class FluxEngineTests : IDisposable
{
    private readonly String _testDir;

    public FluxEngineTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"FluxEngineTests_{Guid.NewGuid():N}");
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

    private FluxEngine CreateEngine(Int32 partitionHours = 1)
    {
        var options = new DbOptions { FluxPartitionHours = partitionHours };
        return new FluxEngine(_testDir, options);
    }

    [Fact(DisplayName = "测试追加条目")]
    public void TestAppend()
    {
        using var engine = CreateEngine();
        var now = DateTime.UtcNow.Ticks;

        var entry = new FluxEntry
        {
            Timestamp = now,
            Fields = new Dictionary<String, Object?> { ["temperature"] = 25.5 },
            Tags = new Dictionary<String, String> { ["sensor"] = "A1" }
        };

        engine.Append(entry);

        Assert.Equal(1, engine.GetEntryCount());
        Assert.Equal(0, entry.SequenceId);
    }

    [Fact(DisplayName = "测试同毫秒自增序列号")]
    public void TestSameTimestampAutoIncrement()
    {
        using var engine = CreateEngine();
        var now = DateTime.UtcNow.Ticks;

        for (var i = 0; i < 5; i++)
        {
            engine.Append(new FluxEntry
            {
                Timestamp = now,
                Fields = new Dictionary<String, Object?> { ["value"] = i }
            });
        }

        Assert.Equal(5, engine.GetEntryCount());

        var entries = engine.QueryRange(now, now);
        Assert.Equal(5, entries.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(i, entries[i].SequenceId);
        }
    }

    [Fact(DisplayName = "测试批量追加")]
    public void TestAppendBatch()
    {
        using var engine = CreateEngine();
        var baseTime = DateTime.UtcNow.Ticks;

        var entries = new List<FluxEntry>();
        for (var i = 0; i < 10; i++)
        {
            entries.Add(new FluxEntry
            {
                Timestamp = baseTime + i * TimeSpan.TicksPerSecond,
                Fields = new Dictionary<String, Object?> { ["value"] = i }
            });
        }

        engine.AppendBatch(entries);

        Assert.Equal(10, engine.GetEntryCount());
    }

    [Fact(DisplayName = "测试时间范围查询")]
    public void TestQueryRange()
    {
        using var engine = CreateEngine();
        var baseTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        for (var i = 0; i < 10; i++)
        {
            engine.Append(new FluxEntry
            {
                Timestamp = baseTime + i * TimeSpan.TicksPerMinute,
                Fields = new Dictionary<String, Object?> { ["value"] = i }
            });
        }

        // 查询前 5 条
        var start = baseTime;
        var end = baseTime + 4 * TimeSpan.TicksPerMinute;
        var result = engine.QueryRange(start, end);
        Assert.Equal(5, result.Count);
    }

    [Fact(DisplayName = "测试分区管理")]
    public void TestPartitionManagement()
    {
        using var engine = CreateEngine(partitionHours: 1);

        // 写入不同小时的数据
        var hour1 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var hour2 = new DateTime(2025, 1, 1, 1, 0, 0, DateTimeKind.Utc).Ticks;
        var hour3 = new DateTime(2025, 1, 1, 2, 0, 0, DateTimeKind.Utc).Ticks;

        engine.Append(new FluxEntry { Timestamp = hour1, Fields = new Dictionary<String, Object?> { ["v"] = 1 } });
        engine.Append(new FluxEntry { Timestamp = hour2, Fields = new Dictionary<String, Object?> { ["v"] = 2 } });
        engine.Append(new FluxEntry { Timestamp = hour3, Fields = new Dictionary<String, Object?> { ["v"] = 3 } });

        Assert.Equal(3, engine.GetPartitionCount());
    }

    [Fact(DisplayName = "测试删除过期分区")]
    public void TestDeleteExpiredPartitions()
    {
        using var engine = CreateEngine(partitionHours: 1);

        // 写入过去的数据
        var oldTime = DateTime.UtcNow.AddHours(-10).Ticks;
        var recentTime = DateTime.UtcNow.Ticks;

        engine.Append(new FluxEntry { Timestamp = oldTime, Fields = new Dictionary<String, Object?> { ["v"] = 1 } });
        engine.Append(new FluxEntry { Timestamp = recentTime, Fields = new Dictionary<String, Object?> { ["v"] = 2 } });

        Assert.Equal(2, engine.GetPartitionCount());

        // 删除超过 1 小时的分区
        var deleted = engine.DeleteExpiredPartitions(3600);
        Assert.True(deleted >= 1);
        Assert.True(engine.GetPartitionCount() >= 1);
    }

    [Fact(DisplayName = "测试条目计数")]
    public void TestEntryCount()
    {
        using var engine = CreateEngine();
        Assert.Equal(0, engine.GetEntryCount());

        var now = DateTime.UtcNow.Ticks;
        for (var i = 0; i < 100; i++)
        {
            engine.Append(new FluxEntry
            {
                Timestamp = now + i,
                Fields = new Dictionary<String, Object?> { ["v"] = i }
            });
        }

        Assert.Equal(100, engine.GetEntryCount());
    }

    [Fact(DisplayName = "测试清空数据")]
    public void TestClear()
    {
        using var engine = CreateEngine();
        var now = DateTime.UtcNow.Ticks;

        engine.Append(new FluxEntry { Timestamp = now, Fields = new Dictionary<String, Object?> { ["v"] = 1 } });
        Assert.Equal(1, engine.GetEntryCount());

        engine.Clear();
        Assert.Equal(0, engine.GetEntryCount());
        Assert.Equal(0, engine.GetPartitionCount());
    }

    [Fact(DisplayName = "测试分区键格式")]
    public void TestPartitionKeyFormat()
    {
        using var engine = CreateEngine(partitionHours: 1);

        var dt = new DateTime(2025, 7, 15, 14, 30, 0, DateTimeKind.Utc);
        var key = engine.GetPartitionKey(dt.Ticks);

        Assert.Equal("2025071514", key);
    }

    [Fact(DisplayName = "测试多小时分片粒度")]
    public void TestMultiHourPartition()
    {
        using var engine = CreateEngine(partitionHours: 4);

        var dt1 = new DateTime(2025, 7, 15, 1, 0, 0, DateTimeKind.Utc);
        var dt2 = new DateTime(2025, 7, 15, 3, 0, 0, DateTimeKind.Utc);
        var dt3 = new DateTime(2025, 7, 15, 5, 0, 0, DateTimeKind.Utc);

        // 0-3 小时应对齐到 0 小时
        Assert.Equal(engine.GetPartitionKey(dt1.Ticks), engine.GetPartitionKey(dt2.Ticks));
        // 5 小时应对齐到 4 小时
        Assert.NotEqual(engine.GetPartitionKey(dt1.Ticks), engine.GetPartitionKey(dt3.Ticks));
    }
}
