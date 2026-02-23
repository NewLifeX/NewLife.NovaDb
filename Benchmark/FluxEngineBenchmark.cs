using BenchmarkDotNet.Attributes;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;

namespace Benchmark;

/// <summary>时序引擎基准测试</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class FluxEngineBenchmark
{
    private String _basePath = null!;
    private FluxEngine _engine = null!;
    private Int64 _baseTicks;

    [GlobalSetup]
    public void Setup()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"NovaBench_Flux_{Guid.NewGuid():N}");
        _engine = new FluxEngine(_basePath, new DbOptions());
        _baseTicks = DateTime.UtcNow.Ticks;

        // 预置查询数据
        for (var i = 0; i < 1000; i++)
        {
            _engine.Append(new FluxEntry
            {
                Timestamp = _baseTicks + i * TimeSpan.TicksPerSecond,
                Fields = new Dictionary<String, Object?> { ["temperature"] = 20.0 + i % 30, ["humidity"] = 40.0 + i % 50 },
                Tags = new Dictionary<String, String> { ["device"] = $"sensor_{i % 10}" }
            });
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _engine.Dispose();
        try { Directory.Delete(_basePath, true); } catch { }
    }

    [Benchmark(Description = "Append 单条写入")]
    public void AppendSingle()
    {
        _engine.Append(new FluxEntry
        {
            Timestamp = _baseTicks + Random.Shared.Next(0, 3600) * TimeSpan.TicksPerSecond,
            Fields = new Dictionary<String, Object?> { ["temperature"] = 25.5, ["humidity"] = 60.0 },
            Tags = new Dictionary<String, String> { ["device"] = "sensor_0" }
        });
    }

    [Benchmark(Description = "AppendBatch 批量写入(100条)")]
    public void AppendBatch()
    {
        var entries = new List<FluxEntry>(100);
        var ts = _baseTicks;
        for (var i = 0; i < 100; i++)
        {
            entries.Add(new FluxEntry
            {
                Timestamp = ts + i * TimeSpan.TicksPerMillisecond,
                Fields = new Dictionary<String, Object?> { ["temperature"] = 20.0 + i, ["humidity"] = 50.0 },
                Tags = new Dictionary<String, String> { ["device"] = "batch_sensor" }
            });
        }
        _engine.AppendBatch(entries);
    }

    [Benchmark(Description = "QueryRange 时间范围查询")]
    public List<FluxEntry> QueryRange()
    {
        var start = _baseTicks;
        var end = _baseTicks + 100 * TimeSpan.TicksPerSecond;
        return _engine.QueryRange(start, end);
    }
}
