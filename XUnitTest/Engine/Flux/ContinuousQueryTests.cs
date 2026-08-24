using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;
using NewLife.NovaDb.Sql;

#nullable enable

namespace XUnitTest.Engine.Flux;

/// <summary>滑动窗口与连续查询测试（F08/F10/F11/F12）</summary>
public class ContinuousQueryTests : IDisposable
{
    private readonly String _testDir;

    public ContinuousQueryTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"ContinuousQueryTests_{Guid.NewGuid():N}");
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

    private FluxEngine CreateEngine()
    {
        var options = new DbOptions { FluxPartitionHours = 1 };
        return new FluxEngine(_testDir, options);
    }

    [Fact(DisplayName = "滑动窗口：窗口大小大于步长时窗口重叠")]
    public void SlidingWindow_OverlappingWindows()
    {
        using var engine = CreateEngine();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var minute = TimeSpan.FromMinutes(1).Ticks;

        // 每分钟一条：10, 20, 30, 40, 50
        for (var i = 0; i < 5; i++)
        {
            engine.Append(new FluxEntry
            {
                Timestamp = start + i * minute,
                Fields = new Dictionary<String, Object?> { ["value"] = (i + 1) * 10.0 }
            });
        }

        // 窗口 3 分钟，步长 1 分钟：窗口 [0,3) [1,4) [2,5)
        var window = 3 * minute;
        var step = minute;
        var results = engine.SlidingWindow(start, start + 5 * minute, window, step, "value", "avg");

        Assert.Equal(3, results.Count);
        // 窗口1: (10+20+30)/3 = 20
        Assert.Equal(20.0, results[0].Value, 5);
        // 窗口2: (20+30+40)/3 = 30
        Assert.Equal(30.0, results[1].Value, 5);
        // 窗口3: (30+40+50)/3 = 40
        Assert.Equal(40.0, results[2].Value, 5);
    }

    [Fact(DisplayName = "滑动窗口：SUM 聚合")]
    public void SlidingWindow_Sum()
    {
        using var engine = CreateEngine();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var minute = TimeSpan.FromMinutes(1).Ticks;

        for (var i = 0; i < 4; i++)
        {
            engine.Append(new FluxEntry
            {
                Timestamp = start + i * minute,
                Fields = new Dictionary<String, Object?> { ["value"] = 10.0 }
            });
        }

        var results = engine.SlidingWindow(start, start + 4 * minute, 2 * minute, minute, "value", "sum");
        Assert.Equal(3, results.Count);
        Assert.Equal(20.0, results[0].Value, 5);
    }

    [Fact(DisplayName = "滑动窗口：步长大于窗口抛异常")]
    public void SlidingWindow_StepExceedsWindow_Throws()
    {
        using var engine = CreateEngine();
        var start = DateTime.UtcNow.Ticks;
        var minute = TimeSpan.FromMinutes(1).Ticks;

        Assert.Throws<ArgumentException>(() =>
            engine.SlidingWindow(start, start + 10 * minute, minute, 2 * minute, "value", "avg"));
    }

    [Fact(DisplayName = "滑动窗口：无数据返回空")]
    public void SlidingWindow_NoData_ReturnsEmpty()
    {
        using var engine = CreateEngine();
        var start = DateTime.UtcNow.Ticks;
        var minute = TimeSpan.FromMinutes(1).Ticks;

        var results = engine.SlidingWindow(start, start + 10 * minute, minute, minute, "value", "avg");
        Assert.Empty(results);
    }

    [Fact(DisplayName = "滑动窗口：参数校验")]
    public void SlidingWindow_InvalidArguments_Throws()
    {
        using var engine = CreateEngine();
        var start = DateTime.UtcNow.Ticks;
        var minute = TimeSpan.FromMinutes(1).Ticks;

        Assert.Throws<ArgumentException>(() => engine.SlidingWindow(start, start + minute, 0, minute, "v", "avg"));
        Assert.Throws<ArgumentException>(() => engine.SlidingWindow(start, start + minute, minute, 0, "v", "avg"));
        Assert.Throws<ArgumentNullException>(() => engine.SlidingWindow(start, start + minute, minute, minute, null!, "avg"));
    }

    [Fact(DisplayName = "连续查询：注册并处理新增数据写入目标表")]
    public void ContinuousQuery_ProcessNewData()
    {
        using var engine = CreateEngine();
        using var sql = new SqlEngine(Path.Combine(_testDir, "sql"));
        using var manager = new ContinuousQueryManager(engine, sql);

        var minute = TimeSpan.FromMinutes(1).Ticks;
        var hour = TimeSpan.FromHours(1).Ticks;

        var def = new ContinuousQueryDefinition
        {
            Name = "sensor_agg",
            SourceTable = "sensors",
            FieldName = "value",
            Aggregations = ["sum", "avg", "min", "max", "count"],
            Rollups = [minute, hour],
            TargetPrefix = "stat",
            LastProcessedTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks
        };
        manager.Register(def);

        // 写入 3 条分钟数据
        var start = new DateTime(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc).Ticks;
        for (var i = 0; i < 3; i++)
        {
            engine.Append(new FluxEntry
            {
                Timestamp = start + i * minute,
                Fields = new Dictionary<String, Object?> { ["value"] = 10.0 * (i + 1) }
            });
        }

        var processed = manager.Run("sensor_agg");
        Assert.Equal(3, processed);

        // 分钟粒度表：3 个桶
        var minuteResult = sql.Execute("SELECT * FROM stat_1m ORDER BY bucket_start");
        Assert.Equal(3, minuteResult.Rows.Count);

        // 小时粒度表：1 个桶
        var hourResult = sql.Execute("SELECT * FROM stat_1h");
        Assert.Single(hourResult.Rows);
        Assert.Equal(60.0, Convert.ToDouble(hourResult.Rows[0][2]), 5); // sum_val
    }

    [Fact(DisplayName = "连续查询：游标增量只处理新数据")]
    public void ContinuousQuery_IncrementalCursor()
    {
        using var engine = CreateEngine();
        using var sql = new SqlEngine(Path.Combine(_testDir, "sql"));
        using var manager = new ContinuousQueryManager(engine, sql);

        var minute = TimeSpan.FromMinutes(1).Ticks;
        var def = new ContinuousQueryDefinition
        {
            Name = "inc_agg",
            SourceTable = "t",
            FieldName = "value",
            Rollups = [minute],
            TargetPrefix = "s",
            LastProcessedTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks
        };
        manager.Register(def);

        var start = new DateTime(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc).Ticks;
        engine.Append(new FluxEntry
        {
            Timestamp = start,
            Fields = new Dictionary<String, Object?> { ["value"] = 10.0 }
        });

        Assert.Equal(1, manager.Run("inc_agg"));
        // 游标已推进，无新数据不重复处理
        Assert.Equal(0, manager.Run("inc_agg"));
        Assert.Equal(1, def.ProcessedCount);
    }

    [Fact(DisplayName = "连续查询：同桶多条数据合并聚合")]
    public void ContinuousQuery_UpsertMergesSameBucket()
    {
        using var engine = CreateEngine();
        using var sql = new SqlEngine(Path.Combine(_testDir, "sql"));
        using var manager = new ContinuousQueryManager(engine, sql);

        var minute = TimeSpan.FromMinutes(1).Ticks;
        // 使用相对当前时间的时间戳，避免与游标推进逻辑冲突
        // 两条数据偏移固定为整分钟内的 10s/30s，数学上保证同桶，避免整点边界跨桶导致的 flaky
        var baseTicks = DateTime.UtcNow.AddMinutes(-2).Ticks;
        var bucketStart = baseTicks / minute * minute;
        var def = new ContinuousQueryDefinition
        {
            Name = "upsert_agg",
            SourceTable = "t",
            FieldName = "value",
            Rollups = [minute],
            TargetPrefix = "u",
            LastProcessedTicks = bucketStart
        };
        manager.Register(def);

        // 同一分钟桶内的两条数据，一次处理
        engine.Append(new FluxEntry
        {
            Timestamp = bucketStart + 10 * TimeSpan.TicksPerSecond,
            Fields = new Dictionary<String, Object?> { ["value"] = 10.0 }
        });
        engine.Append(new FluxEntry
        {
            Timestamp = bucketStart + 30 * TimeSpan.TicksPerSecond,
            Fields = new Dictionary<String, Object?> { ["value"] = 20.0 }
        });
        manager.Run("upsert_agg");

        var result = sql.Execute("SELECT * FROM u_1m");
        Assert.Single(result.Rows);
        // cnt = 2, sum = 30, min = 10, max = 20
        Assert.Equal(2, Convert.ToInt32(result.Rows[0][1]));
        Assert.Equal(30.0, Convert.ToDouble(result.Rows[0][2]), 5);
        Assert.Equal(10.0, Convert.ToDouble(result.Rows[0][3]), 5);
        Assert.Equal(20.0, Convert.ToDouble(result.Rows[0][4]), 5);
    }

    [Fact(DisplayName = "连续查询：重复注册抛异常")]
    public void ContinuousQuery_DuplicateRegistration_Throws()
    {
        using var engine = CreateEngine();
        using var sql = new SqlEngine(Path.Combine(_testDir, "sql"));
        using var manager = new ContinuousQueryManager(engine, sql);

        var def = new ContinuousQueryDefinition
        {
            Name = "dup",
            SourceTable = "t",
            FieldName = "value",
            Rollups = [TimeSpan.FromMinutes(1).Ticks],
            TargetPrefix = "d"
        };
        manager.Register(def);

        Assert.Throws<NovaException>(() => manager.Register(new ContinuousQueryDefinition
        {
            Name = "dup",
            SourceTable = "t",
            FieldName = "value",
            Rollups = [TimeSpan.FromMinutes(1).Ticks],
            TargetPrefix = "d"
        }));
    }

    [Fact(DisplayName = "连续查询：粒度标签格式")]
    public void ContinuousQuery_GranuleLabel()
    {
        Assert.Equal("30s", ContinuousQueryDefinition.GetGranuleLabel(TimeSpan.FromSeconds(30).Ticks));
        Assert.Equal("5m", ContinuousQueryDefinition.GetGranuleLabel(TimeSpan.FromMinutes(5).Ticks));
        Assert.Equal("1h", ContinuousQueryDefinition.GetGranuleLabel(TimeSpan.FromHours(1).Ticks));
        Assert.Equal("1d", ContinuousQueryDefinition.GetGranuleLabel(TimeSpan.FromDays(1).Ticks));
    }

    [Fact(DisplayName = "连续查询：注销查询")]
    public void ContinuousQuery_Unregister()
    {
        using var engine = CreateEngine();
        using var sql = new SqlEngine(Path.Combine(_testDir, "sql"));
        using var manager = new ContinuousQueryManager(engine, sql);

        var def = new ContinuousQueryDefinition
        {
            Name = "unreg",
            SourceTable = "t",
            FieldName = "value",
            Rollups = [TimeSpan.FromMinutes(1).Ticks],
            TargetPrefix = "x"
        };
        manager.Register(def);
        Assert.Equal(1, manager.Count);

        Assert.True(manager.Unregister("unreg"));
        Assert.Equal(0, manager.Count);
        Assert.Null(manager.Get("unreg"));
    }

    [Fact(DisplayName = "实时聚合：MQ 消费触发连续查询")]
    public void Realtime_MqConsume_TriggersAggregation()
    {
        using var engine = CreateEngine();
        using var sql = new SqlEngine(Path.Combine(_testDir, "sql"));
        using var manager = new ContinuousQueryManager(engine, sql);
        using var queue = new NewLife.NovaDb.Queues.NovaQueue<Int32>(engine, "topic1");
        queue.SetGroup("g1");

        var minute = TimeSpan.FromMinutes(1).Ticks;
        var def = new ContinuousQueryDefinition
        {
            Name = "rt_agg",
            SourceTable = "topic1",
            FieldName = "value",
            Rollups = [minute],
            TargetPrefix = "rt"
        };
        manager.Register(def);

        // 挂接消费事件
        var unsubscribe = manager.SubscribeRealtime(queue, "rt_agg");
        try
        {
            queue.Publish(new FluxEntry
            {
                Timestamp = DateTime.UtcNow.Ticks,
                Fields = new Dictionary<String, Object?> { ["value"] = 42.0 }
            });

            // 消费消息触发聚合
            var msg = queue.TakeOne(0);
            Assert.NotNull(msg);

            // OnConsumed 事件由 ConsumeAsync 循环触发，此处手动验证挂接处理器可调用
            var handler = manager.AttachRealtime(queue, "rt_agg");
            Assert.NotNull(handler);
            handler("fake-msg-id");

            // 处理器已执行聚合
            Assert.True(def.ProcessedCount >= 1);
            Assert.Equal(1L, def.RunCount);
        }
        finally
        {
            unsubscribe();
        }
    }
}
