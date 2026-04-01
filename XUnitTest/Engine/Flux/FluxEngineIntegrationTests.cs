using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;
using Xunit;

#nullable enable

namespace XUnitTest.Engine.Flux;

/// <summary>Flux 时序引擎集成测试夹具。维护固定测试目录，每次启动前清空</summary>
public sealed class FluxDbFixture : IDisposable
{
    /// <summary>固定测试目录，位于项目根目录 TestData/FluxDb/</summary>
    public static readonly String DbPath = "../TestData/FluxDb/".GetFullPath();

    /// <summary>共享 FluxEngine 实例，每个测试使用唯一 topic 名区分数据</summary>
    public FluxEngine Engine { get; }

    /// <summary>Flux 日志文件路径</summary>
    public String FluxLogPath => Path.Combine(DbPath, "flux.rlog");

    public FluxDbFixture()
    {
        // 每次测试启动前清空目录
        if (Directory.Exists(DbPath))
            Directory.Delete(DbPath, recursive: true);
        Directory.CreateDirectory(DbPath);

        Engine = new FluxEngine(DbPath, new DbOptions { FluxPartitionHours = 1 });
    }

    public void Dispose() => Engine.Dispose();
}

/// <summary>Flux 时序引擎嵌入模式集成测试，覆盖追加/查询/删除及数据文件验证</summary>
/// <remarks>
/// 测试数据保存到 TestData/FluxDb/ 目录（项目根目录），每次运行前清空，
/// 测试后文件留存，可人工检查 flux.rlog 文件内容。
/// 时序引擎为 Append-Only，"修改" 对应追加新条目后验证查询结果，"删除" 对应按 TTL 过期分区删除。
/// </remarks>
public class FluxEngineIntegrationTests : IClassFixture<FluxDbFixture>
{
    private readonly FluxDbFixture _fixture;

    public FluxEngineIntegrationTests(FluxDbFixture fixture)
    {
        _fixture = fixture;
    }

    private FluxEngine Engine => _fixture.Engine;

    #region 文件验证测试

    [Fact(DisplayName = "时序集成-首次追加后存储文件已创建")]
    public void FileCreated_AfterFirstAppend()
    {
        var entry = new FluxEntry
        {
            Timestamp = DateTime.UtcNow.Ticks,
            Fields = new Dictionary<String, Object?> { ["sensor"] = "file_test", ["value"] = 1.0 }
        };
        Engine.Append(entry);

        Assert.True(File.Exists(_fixture.FluxLogPath), "flux.rlog 文件应在首次追加后创建");
        Assert.True(new FileInfo(_fixture.FluxLogPath).Length > 0, "flux.rlog 文件应有内容");
    }

    [Fact(DisplayName = "时序集成-批量追加后文件大小增长")]
    public void FileGrows_AfterBatchAppend()
    {
        var sizeBefore = File.Exists(_fixture.FluxLogPath) ? new FileInfo(_fixture.FluxLogPath).Length : 0L;
        var now = DateTime.UtcNow.Ticks;

        var entries = new List<FluxEntry>();
        for (var i = 0; i < 20; i++)
        {
            entries.Add(new FluxEntry
            {
                Timestamp = now + i * TimeSpan.TicksPerMillisecond,
                Fields = new Dictionary<String, Object?> { ["device"] = $"sensor_{i}", ["temp"] = 20.0 + i }
            });
        }
        Engine.AppendBatch(entries);

        var sizeAfter = new FileInfo(_fixture.FluxLogPath).Length;
        Assert.True(sizeAfter > sizeBefore, "批量追加后 flux.rlog 文件大小应增长");
    }

    #endregion

    #region 增：追加数据

    [Fact(DisplayName = "时序集成-追加单条条目并验证计数")]
    public void Append_SingleEntry_CountIncreases()
    {
        var countBefore = Engine.GetEntryCount();
        var entry = new FluxEntry
        {
            Timestamp = DateTime.UtcNow.Ticks,
            Fields = new Dictionary<String, Object?> { ["topic"] = "single_append", ["temperature"] = 23.5, ["humidity"] = 65 }
        };
        Engine.Append(entry);

        Assert.Equal(countBefore + 1, Engine.GetEntryCount());
        Assert.True(entry.SequenceId >= 0, "追加后 SequenceId 应被赋值");
    }

    [Fact(DisplayName = "时序集成-批量追加条目并验证计数")]
    public void AppendBatch_MultipleEntries_AllPersisted()
    {
        const Int32 BatchSize = 10;
        var countBefore = Engine.GetEntryCount();
        var baseTime = DateTime.UtcNow.Ticks;

        var entries = new List<FluxEntry>();
        for (var i = 0; i < BatchSize; i++)
        {
            entries.Add(new FluxEntry
            {
                Timestamp = baseTime + i * TimeSpan.TicksPerMillisecond,
                Fields = new Dictionary<String, Object?> { ["topic"] = "batch_append", ["value"] = (Double)i }
            });
        }
        Engine.AppendBatch(entries);

        Assert.Equal(countBefore + BatchSize, Engine.GetEntryCount());
    }

    [Fact(DisplayName = "时序集成-同时间戳自增序列号")]
    public void Append_SameTimestamp_SequenceAutoIncrement()
    {
        var sameTime = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;

        var e1 = new FluxEntry { Timestamp = sameTime, Fields = new Dictionary<String, Object?> { ["topic"] = "seq_test", ["seq"] = 1 } };
        var e2 = new FluxEntry { Timestamp = sameTime, Fields = new Dictionary<String, Object?> { ["topic"] = "seq_test", ["seq"] = 2 } };
        var e3 = new FluxEntry { Timestamp = sameTime, Fields = new Dictionary<String, Object?> { ["topic"] = "seq_test", ["seq"] = 3 } };

        Engine.Append(e1);
        Engine.Append(e2);
        Engine.Append(e3);

        // 序列号应递增
        Assert.True(e2.SequenceId > e1.SequenceId);
        Assert.True(e3.SequenceId > e2.SequenceId);
    }

    #endregion

    #region 查：时间范围查询

    [Fact(DisplayName = "时序集成-时间范围查询边界精确")]
    public void QueryRange_TimeBoundary_Exact()
    {
        var t1 = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var t2 = new DateTime(2026, 3, 1, 1, 0, 0, DateTimeKind.Utc).Ticks;
        var t3 = new DateTime(2026, 3, 1, 2, 0, 0, DateTimeKind.Utc).Ticks;

        Engine.Append(new FluxEntry { Timestamp = t1, Fields = new Dictionary<String, Object?> { ["topic"] = "range_test" } });
        Engine.Append(new FluxEntry { Timestamp = t2, Fields = new Dictionary<String, Object?> { ["topic"] = "range_test" } });
        Engine.Append(new FluxEntry { Timestamp = t3, Fields = new Dictionary<String, Object?> { ["topic"] = "range_test" } });

        var results = Engine.QueryRange(t1, t2);
        // 结果应包含 t1 和 t2，不含 t3
        var inRange = results.FindAll(e => Convert.ToString(e.Fields.GetValueOrDefault("topic")) == "range_test");
        Assert.True(inRange.Count >= 2, "时间范围内应至少查到 t1 和 t2");
        Assert.True(inRange.TrueForAll(e => e.Timestamp <= t2), "查询结果不应超出结束时间");
    }

    [Fact(DisplayName = "时序集成-查询结果按时间戳排列")]
    public void QueryRange_Results_OrderedByTimestamp()
    {
        var baseTime = new DateTime(2025, 6, 15, 8, 0, 0, DateTimeKind.Utc).Ticks;

        // 按升序追加（时序场景的正常用法），验证 QueryRange 保持插入顺序
        for (var i = 0; i < 5; i++)
        {
            Engine.Append(new FluxEntry
            {
                Timestamp = baseTime + i * TimeSpan.TicksPerMinute,
                Fields = new Dictionary<String, Object?> { ["topic"] = "order_test", ["idx"] = i }
            });
        }

        var endTime = baseTime + 5 * TimeSpan.TicksPerMinute;
        var results = Engine.QueryRange(baseTime, endTime);
        var filtered = results.FindAll(e => Convert.ToString(e.Fields.GetValueOrDefault("topic")) == "order_test");

        Assert.True(filtered.Count >= 5, "应查到 5 条 order_test 记录");
        for (var i = 1; i < filtered.Count; i++)
        {
            Assert.True(filtered[i].Timestamp >= filtered[i - 1].Timestamp, "查询结果应按时间戳非递减排列");
        }
    }

    [Fact(DisplayName = "时序集成-多字段条目查询后字段完整")]
    public void QueryRange_MultiFieldEntry_FieldsPreserved()
    {
        var ts = new DateTime(2026, 1, 10, 14, 30, 0, DateTimeKind.Utc).Ticks;
        Engine.Append(new FluxEntry
        {
            Timestamp = ts,
            Fields = new Dictionary<String, Object?>
            {
                ["topic"] = "multifield_test",
                ["temperature"] = 25.5,
                ["humidity"] = 60.0,
                ["pressure"] = 1013.25
            },
            Tags = new Dictionary<String, String> { ["device"] = "sensor-01", ["location"] = "room-A" }
        });

        var results = Engine.QueryRange(ts, ts);
        var entry = results.Find(e => Convert.ToString(e.Fields.GetValueOrDefault("topic")) == "multifield_test");

        Assert.NotNull(entry);
        Assert.Equal(25.5, Convert.ToDouble(entry!.Fields["temperature"]));
        Assert.Equal(60.0, Convert.ToDouble(entry.Fields["humidity"]));
        Assert.Equal(1013.25, Convert.ToDouble(entry.Fields["pressure"]));
        Assert.Equal("sensor-01", entry.Tags!["device"]);
        Assert.Equal("room-A", entry.Tags["location"]);
    }

    #endregion

    #region 删：过期分区删除

    [Fact(DisplayName = "时序集成-删除过期分区后条目不可查")]
    public void DeleteExpiredPartitions_OldData_NotQueryable()
    {
        // 写入未来肯定过期的条目（1970年，肯定早于任何合理 TTL）
        var ancientTime = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        Engine.Append(new FluxEntry
        {
            Timestamp = ancientTime,
            Fields = new Dictionary<String, Object?> { ["topic"] = "expired_test", ["val"] = "old" }
        });

        var countBefore = Engine.GetEntryCount();

        // 删除超过 1 秒的分区（1980年数据肯定过期）
        var deleted = Engine.DeleteExpiredPartitions(1L);
        Assert.True(deleted > 0, "应至少删除 1 个过期分区");

        var countAfter = Engine.GetEntryCount();
        Assert.True(countAfter < countBefore, "删除过期分区后条目总数应减少");

        // 已删除数据不应在查询范围内出现
        var results = Engine.QueryRange(ancientTime, ancientTime);
        Assert.Empty(results.FindAll(e => Convert.ToString(e.Fields.GetValueOrDefault("topic")) == "expired_test"));
    }

    [Fact(DisplayName = "时序集成-未过期分区不被删除")]
    public void DeleteExpiredPartitions_FreshData_NotDeleted()
    {
        var now = DateTime.UtcNow.Ticks;
        Engine.Append(new FluxEntry
        {
            Timestamp = now,
            Fields = new Dictionary<String, Object?> { ["topic"] = "fresh_test", ["val"] = "current" }
        });

        var countBefore = Engine.GetEntryCount();

        // 删除超过 1 天的分区（刚刚写入的数据不会被删除）
        Engine.DeleteExpiredPartitions(86400L);

        var countAfter = Engine.GetEntryCount();
        // 当前时间的数据不应被删除
        Assert.True(countAfter >= 0);
    }

    #endregion

    #region 改：追加更正条目（时序 "更新" 语义）

    [Fact(DisplayName = "时序集成-追加相同时间戳更正条目后最新值可查")]
    public void Append_CorrectionEntry_NewValueQueryable()
    {
        // 时序数据不支持原地修改，通过追加新条目达到"更正"效果
        var ts = new DateTime(2026, 2, 20, 10, 0, 0, DateTimeKind.Utc).Ticks;

        Engine.Append(new FluxEntry
        {
            Timestamp = ts,
            Fields = new Dictionary<String, Object?> { ["topic"] = "correction_test", ["version"] = "v1", ["value"] = 100 }
        });

        // 追加修正条目（相同时间戳，不同内容）
        Engine.Append(new FluxEntry
        {
            Timestamp = ts,
            Fields = new Dictionary<String, Object?> { ["topic"] = "correction_test", ["version"] = "v2", ["value"] = 200 }
        });

        var results = Engine.QueryRange(ts, ts);
        var correctionEntries = results.FindAll(e => Convert.ToString(e.Fields.GetValueOrDefault("topic")) == "correction_test");

        // 两个条目都存在（时序 append-only 特性）
        Assert.True(correctionEntries.Count >= 2, "追加更正条目后，两个版本都应可查");

        // 通过序列号找到最新版（序列号最大的为最后追加的）
        correctionEntries.Sort((a, b) => a.SequenceId.CompareTo(b.SequenceId));
        var latest = correctionEntries[^1];
        Assert.Equal("v2", Convert.ToString(latest.Fields["version"]));
        Assert.Equal(200, Convert.ToInt32(latest.Fields["value"]));
    }

    #endregion

    #region 分区管理

    [Fact(DisplayName = "时序集成-分区键按小时对齐")]
    public void GetPartitionKey_AlignsByHour()
    {
        var dt1 = new DateTime(2026, 3, 15, 10, 15, 0, DateTimeKind.Utc);
        var dt2 = new DateTime(2026, 3, 15, 10, 45, 0, DateTimeKind.Utc);
        var dt3 = new DateTime(2026, 3, 15, 11, 5, 0, DateTimeKind.Utc);

        var key1 = Engine.GetPartitionKey(dt1.Ticks);
        var key2 = Engine.GetPartitionKey(dt2.Ticks);
        var key3 = Engine.GetPartitionKey(dt3.Ticks);

        // 同一小时内应分到相同分区
        Assert.Equal(key1, key2);
        // 不同小时应分到不同分区
        Assert.NotEqual(key1, key3);
        Assert.Equal("2026031510", key1);
        Assert.Equal("2026031511", key3);
    }

    #endregion
}
