using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;
using NewLife.NovaDb.Server;
using NewLife.NovaDb.Sql;
using Xunit;

#nullable enable

namespace XUnitTest.Engine.Flux;

/// <summary>Flux 压缩率监控与报告测试（F19）</summary>
public class FluxCompressionReportTests : IDisposable
{
    private readonly String _testDir;

    public FluxCompressionReportTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"FluxCompressionReportTests_{Guid.NewGuid():N}");
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

    private static FluxEntry CreateEntry(Int64 ticks, Int32 seq)
    {
        return new FluxEntry
        {
            Timestamp = ticks,
            Fields = new Dictionary<String, Object?>
            {
                ["value"] = 20.5 + seq,
                ["status"] = seq % 2 == 0 ? "ok" : "warning",
                ["payload"] = "设备采集数据正常上报，温度与湿度均在阈值范围内，无需干预处理",
            },
            Tags = new Dictionary<String, String> { ["sensor"] = "A1" },
        };
    }

    private FluxEngine CreateFluxEngine()
    {
        var flux = new FluxEngine(Path.Combine(_testDir, "flux"), new DbOptions { FluxPartitionHours = 1 });
        var baseTicks = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).Ticks;
        for (var i = 0; i < 500; i++)
        {
            flux.Append(CreateEntry(baseTicks + i * TimeSpan.TicksPerSecond, i));
        }
        flux.SealPartitions(sealAll: true);

        return flux;
    }

    [Fact(DisplayName = "系统表返回列式压缩率报告")]
    public void SysFluxCompression_ReportsStats()
    {
        using var flux = CreateFluxEngine();
        var engine = new SqlEngine(Path.Combine(_testDir, "sql"), new DbOptions()) { FluxProvider = () => flux };

        var result = engine.Execute("SELECT * FROM _sys.flux_compression");
        var rows = result.Rows.ToDictionary(r => (String)r[0]!, r => (String)r[1]!);

        Assert.Equal("1", rows["block_count"]);
        Assert.Equal("500", rows["entry_count"]);
        Assert.True(Int32.Parse(rows["column_count"]) > 0);
        Assert.True(Int32.Parse(rows["compressed_column_count"]) > 0);
        Assert.True(Int64.Parse(rows["row_bytes"]) > Int64.Parse(rows["block_bytes"]));
        Assert.True(Double.Parse(rows["compression_ratio"]) > 1);
        Assert.True(Double.Parse(rows["saved_ratio"]) > 0);
    }

    [Fact(DisplayName = "系统表支持按指标名过滤")]
    public void SysFluxCompression_FilterByMetric()
    {
        using var flux = CreateFluxEngine();
        var engine = new SqlEngine(Path.Combine(_testDir, "sql"), new DbOptions()) { FluxProvider = () => flux };

        var result = engine.Execute("SELECT * FROM _sys.flux_compression WHERE metric = 'block_count'");

        var row = Assert.Single(result.Rows);
        Assert.Equal("block_count", row[0]);
        Assert.Equal("1", row[1]);
    }

    [Fact(DisplayName = "未启用消息队列时返回空结果")]
    public void SysFluxCompression_WithoutFlux()
    {
        var engine = new SqlEngine(Path.Combine(_testDir, "sql"), new DbOptions());

        var result = engine.Execute("SELECT * FROM _sys.flux_compression");

        Assert.Empty(result.Rows);
        Assert.Equal(["metric", "value"], result.ColumnNames!);
    }

    [Fact(DisplayName = "压缩统计随封存累积更新")]
    public void CompressionStats_Accumulate()
    {
        using var flux = CreateFluxEngine();

        var first = flux.GetCompressionStats();
        Assert.Equal(500, first.EntryCount);

        var baseTicks = new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc).Ticks;
        for (var i = 0; i < 200; i++)
        {
            flux.Append(CreateEntry(baseTicks + i * TimeSpan.TicksPerSecond, i));
        }
        flux.SealPartitions(sealAll: true);

        var second = flux.GetCompressionStats();
        Assert.Equal(2, second.BlockCount);
        Assert.Equal(700, second.EntryCount);
        Assert.True(second.RowBytes > first.RowBytes);
    }

    [Fact(DisplayName = "服务器引擎自动接入 Flux 压缩统计提供者")]
    public void ServerEngine_WiresFluxProvider()
    {
        using var server = new NovaServer(0) { DbPath = _testDir };
        server.Start();

        var engine = server.GetOrCreateEngine(null);
        Assert.NotNull(engine);
        Assert.NotNull(engine.FluxProvider);
        Assert.Same(server.FluxEngine, engine.FluxProvider!());
    }

    [Fact(DisplayName = "嵌入模式连接查询压缩报告不抛异常")]
    public void EmbeddedConnection_QueriesCompressionReport()
    {
        var csb = new NovaConnectionStringBuilder { DataSource = Path.Combine(_testDir, "embedded") };
        using var connection = new NovaConnection(csb.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM _sys.flux_compression";
        using var reader = command.ExecuteReader();

        // 未使用消息队列时无数据行，但列结构可用
        Assert.Equal(2, reader.FieldCount);
        Assert.False(reader.Read());
    }
}
