using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

namespace Benchmark;

/// <summary>自定义配置，使用 InProcess 工具链避免 Deterministic 构建冲突</summary>
public class AntiViralConfig : ManualConfig
{
    public AntiViralConfig()
    {
        AddJob(Job.Default
            .WithToolchain(InProcessNoEmitToolchain.Instance)
            .WithWarmupCount(3)
            .WithIterationCount(5));
        AddColumn(StatisticColumn.OperationsPerSecond);
    }
}
