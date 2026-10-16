using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;
using NewLife.NovaDb.Sql;
using Xunit;

#nullable enable

namespace XUnitTest.Engine.Flux;

/// <summary>滚动聚合与聚合查询自动路由测试（F16）</summary>
public class RollupRouteTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _sql;
    private readonly FluxEngine _flux;

    private static readonly Int64 Minute = TimeSpan.FromMinutes(1).Ticks;
    private static readonly Int64 Hour = TimeSpan.FromHours(1).Ticks;
    private static readonly Int64 Start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).Ticks;

    public RollupRouteTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"RollupRouteTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        _sql = new SqlEngine(Path.Combine(_testDir, "sql"), new DbOptions { WalMode = WalMode.None });
        _flux = new FluxEngine(Path.Combine(_testDir, "flux"), new DbOptions { FluxPartitionHours = 24 });
    }

    public void Dispose()
    {
        _flux.Dispose();
        _sql.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); }
            catch { }
        }
    }

    /// <summary>写入两小时、每分钟一条的源数据，取值 1~120</summary>
    private void FillSourceData(String field = "value")
    {
        for (var i = 0; i < 120; i++)
        {
            _flux.Append(new FluxEntry
            {
                Timestamp = Start + i * Minute,
                Fields = new Dictionary<String, Object?> { [field] = (Double)(i + 1) },
            });
        }
    }

    private ContinuousQueryManager CreateManager() => new(_flux, _sql);

    [Fact(DisplayName = "滚动聚合注册后可解析目标统计表")]
    public void RegisterRollup_ResolvesTargetTable()
    {
        using var manager = CreateManager();
        FillSourceData();
        var def = manager.RegisterRollup("sensor_rollup", "sensor", "value", [Minute, Hour], "rollup_sensor", Start);

        Assert.Equal(2, def.Rollups.Count);
        Assert.Equal("rollup_sensor_1m", def.GetTargetTable(Minute));
        Assert.Equal("rollup_sensor_1h", def.GetTargetTable(Hour));

        // 目标表尚未产生数据时命中失败
        Assert.Null(manager.ResolveTargetTable("sensor", "value", 5 * Minute));

        manager.Backfill("sensor_rollup", Start, Start + 2 * Hour);

        // 回填后按最大可整除粒度命中
        Assert.Equal("rollup_sensor_1h", manager.ResolveTargetTable("sensor", "value", Hour));
        Assert.Equal("rollup_sensor_1h", manager.ResolveTargetTable("sensor", "value", 2 * Hour));
        Assert.Equal("rollup_sensor_1m", manager.ResolveTargetTable("sensor", "value", 5 * Minute));
        Assert.Null(manager.ResolveTargetTable("sensor", "other", Hour));
        Assert.Null(manager.ResolveTargetTable("unknown", "value", Hour));
    }

    [Fact(DisplayName = "回填历史数据生成多粒度预聚合表")]
    public void Backfill_BuildsRollupTables()
    {
        using var manager = CreateManager();
        FillSourceData();
        manager.RegisterRollup("sensor_rollup", "sensor", "value", [Minute, Hour], "rollup_sensor", Start);

        var processed = manager.Backfill("sensor_rollup", Start, Start + 2 * Hour);

        Assert.Equal(120, processed);

        // 1 分钟粒度：每分钟一条，cnt 均为 1
        var minuteRows = _sql.Execute("SELECT COUNT(*) FROM rollup_sensor_1m").Rows;
        Assert.Equal(120L, Convert.ToInt64(minuteRows[0][0]));

        // 1 小时粒度：两个桶，累计 cnt/sum/min/max
        var hourRows = _sql.Execute("SELECT cnt, sum_val, min_val, max_val FROM rollup_sensor_1h ORDER BY bucket_start").Rows;
        Assert.Equal(2, hourRows.Count);
        Assert.Equal(60L, Convert.ToInt64(hourRows[0][0]));
        Assert.Equal(1830.0, Convert.ToDouble(hourRows[0][1]), 5);
        Assert.Equal(1.0, Convert.ToDouble(hourRows[0][2]), 5);
        Assert.Equal(60.0, Convert.ToDouble(hourRows[0][3]), 5);
        Assert.Equal(60L, Convert.ToInt64(hourRows[1][0]));
        Assert.Equal(5430.0, Convert.ToDouble(hourRows[1][1]), 5);
        Assert.Equal(61.0, Convert.ToDouble(hourRows[1][2]), 5);
        Assert.Equal(120.0, Convert.ToDouble(hourRows[1][3]), 5);
    }

    [Fact(DisplayName = "聚合查询命中预聚合表后不依赖源数据")]
    public void QueryAggregate_RoutesToRollup()
    {
        using var manager = CreateManager();
        FillSourceData();
        manager.RegisterRollup("sensor_rollup", "sensor", "value", [Minute, Hour], "rollup_sensor", Start);
        manager.Backfill("sensor_rollup", Start, Start + 2 * Hour);

        // 清空源数据，命中路由的查询仍应返回结果
        _flux.Clear();

        var avg = manager.QueryAggregate("sensor", Start, Start + 2 * Hour, Hour, "value", "avg");
        Assert.Equal(2, avg.Count);
        Assert.Equal(Start, avg[0].BucketStartTicks);
        Assert.Equal(30.5, avg[0].Value, 5);
        Assert.Equal(90.5, avg[1].Value, 5);

        // 回退路径在源数据清空后无结果，证明上面确实走了预聚合表
        var missBucket = TimeSpan.FromSeconds(90).Ticks;
        Assert.Empty(manager.QueryAggregate("sensor", Start, Start + 2 * Hour, missBucket, "value", "avg"));
    }

    [Fact(DisplayName = "查询分桶大于预聚合粒度时二次合并")]
    public void QueryAggregate_MergesBuckets()
    {
        using var manager = CreateManager();
        FillSourceData();
        manager.RegisterRollup("sensor_rollup", "sensor", "value", [Minute, Hour], "rollup_sensor", Start);
        manager.Backfill("sensor_rollup", Start, Start + 2 * Hour);
        _flux.Clear();

        var avg = manager.QueryAggregate("sensor", Start, Start + 2 * Hour, 2 * Hour, "value", "avg");
        var sum = manager.QueryAggregate("sensor", Start, Start + 2 * Hour, 2 * Hour, "value", "sum");
        var count = manager.QueryAggregate("sensor", Start, Start + 2 * Hour, 2 * Hour, "value", "count");
        var min = manager.QueryAggregate("sensor", Start, Start + 2 * Hour, 2 * Hour, "value", "min");
        var max = manager.QueryAggregate("sensor", Start, Start + 2 * Hour, 2 * Hour, "value", "max");

        Assert.Equal(60.5, avg[0].Value, 5);
        Assert.Equal(7260.0, sum[0].Value, 5);
        Assert.Equal(120.0, count[0].Value, 5);
        Assert.Equal(1.0, min[0].Value, 5);
        Assert.Equal(120.0, max[0].Value, 5);
    }

    [Fact(DisplayName = "未命中粒度或字段时回退原始扫描")]
    public void QueryAggregate_FallsBackToRawScan()
    {
        using var manager = CreateManager();
        FillSourceData();
        manager.RegisterRollup("sensor_rollup", "sensor", "value", [Hour], "rollup_sensor", Start);
        manager.Backfill("sensor_rollup", Start, Start + 2 * Hour);

        // 15 分钟分桶无法由 1 小时预聚合还原，回退原始扫描
        var routed = manager.QueryAggregate("sensor", Start, Start + 2 * Hour, 15 * Minute, "value", "avg");
        var raw = _flux.Downsample(Start, Start + 2 * Hour, 15 * Minute, "value", "avg");

        Assert.Equal(raw.Count, routed.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            Assert.Equal(raw[i].BucketStartTicks, routed[i].BucketStartTicks);
            Assert.Equal(raw[i].Value, routed[i].Value, 5);
        }

        // 未注册字段同样回退
        var other = manager.QueryAggregate("sensor", Start, Start + 2 * Hour, Hour, "unknown", "avg");
        Assert.Empty(other);
    }

    [Fact(DisplayName = "注销滚动聚合后路由失效")]
    public void Unregister_RemovesRoute()
    {
        using var manager = CreateManager();
        FillSourceData();
        manager.RegisterRollup("sensor_rollup", "sensor", "value", [Hour], "rollup_sensor", Start);
        manager.Backfill("sensor_rollup", Start, Start + 2 * Hour);

        Assert.NotNull(manager.ResolveTargetTable("sensor", "value", Hour));
        Assert.True(manager.Unregister("sensor_rollup"));
        Assert.Null(manager.ResolveTargetTable("sensor", "value", Hour));
    }

    [Fact(DisplayName = "实时链路统计不受回填影响")]
    public void Backfill_KeepsRealtimeCursor()
    {
        using var manager = CreateManager();
        FillSourceData();
        var def = manager.RegisterRollup("sensor_rollup", "sensor", "value", [Hour], "rollup_sensor", Start);

        var cursor = def.LastProcessedTicks;
        manager.Backfill("sensor_rollup", Start, Start + 2 * Hour);

        Assert.Equal(cursor, def.LastProcessedTicks);
        Assert.Equal(120, def.ProcessedCount);
    }
}
