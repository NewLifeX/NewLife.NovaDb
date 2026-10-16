using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;
using Xunit;

#nullable enable

namespace XUnitTest.Engine.Flux;

/// <summary>Flux 列式块存储测试（F13/F19）</summary>
public class FluxColumnarTests : IDisposable
{
    private readonly String _testDir;

    public FluxColumnarTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"FluxColumnarTests_{Guid.NewGuid():N}");
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

    private FluxEngine CreateEngine() => new(_testDir, new DbOptions { FluxPartitionHours = 1 });

    private static FluxEntry CreateEntry(Int64 ticks, Int32 seq)
    {
        return new FluxEntry
        {
            Timestamp = ticks,
            SequenceId = seq,
            Fields = new Dictionary<String, Object?>
            {
                ["value"] = 20.5 + seq,
                ["count"] = seq,
                ["online"] = seq % 2 == 0,
                ["name"] = "device-" + (seq % 3),
            },
            Tags = new Dictionary<String, String> { ["sensor"] = "A1" },
        };
    }

    [Fact(DisplayName = "列式块编解码往返保持一致")]
    public void BlockCodec_RoundTrip()
    {
        var baseTicks = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).Ticks;
        var entries = new List<FluxEntry>();
        for (var i = 0; i < 200; i++)
        {
            entries.Add(CreateEntry(baseTicks + i * TimeSpan.TicksPerSecond, i));
        }

        // 混合类型与空值列
        entries[0].Fields["mixed"] = 42;
        entries[1].Fields["mixed"] = "text";
        entries[2].Fields["mixed"] = null;
        entries[3].Fields["blob"] = new Byte[] { 1, 2, 3 };

        var payload = FluxBlockCodec.Encode("2026010110", entries, out var stats);
        var decoded = FluxBlockCodec.Decode(payload, out var partitionKey);

        Assert.Equal("2026010110", partitionKey);
        Assert.Equal(entries.Count, decoded.Count);
        Assert.True(stats.ColumnCount > 0);
        Assert.True(stats.StoredPayloadBytes > 0);

        for (var i = 0; i < entries.Count; i++)
        {
            Assert.Equal(entries[i].Timestamp, decoded[i].Timestamp);
            Assert.Equal(entries[i].SequenceId, decoded[i].SequenceId);
            Assert.Equal(20.5 + i, (Double)decoded[i].Fields["value"]!);
            Assert.Equal(i, (Int32)decoded[i].Fields["count"]!);
            Assert.Equal(i % 2 == 0, (Boolean)decoded[i].Fields["online"]!);
            Assert.Equal("device-" + (i % 3), decoded[i].Fields["name"]);
            Assert.Equal("A1", decoded[i].Tags["sensor"]);
        }

        Assert.Equal(42, decoded[0].Fields["mixed"]);
        Assert.Equal("text", decoded[1].Fields["mixed"]);
        Assert.Null(decoded[2].Fields["mixed"]);
        Assert.True(decoded[2].Fields.ContainsKey("mixed"));
        Assert.Equal(new Byte[] { 1, 2, 3 }, decoded[3].Fields["blob"]);
    }

    [Fact(DisplayName = "空块编解码返回空列表")]
    public void BlockCodec_EmptyEntries()
    {
        var payload = FluxBlockCodec.Encode("2026010110", [], out var stats);
        var decoded = FluxBlockCodec.Decode(payload, out var partitionKey);

        Assert.Empty(decoded);
        Assert.Equal("2026010110", partitionKey);
        Assert.Equal(0, stats.EntryCount);
    }

    [Fact(DisplayName = "封存后数据可查且行式日志被清空")]
    public void Seal_KeepsDataQueryable()
    {
        using var engine = CreateEngine();
        var baseTicks = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).Ticks;
        for (var i = 0; i < 100; i++)
        {
            engine.Append(CreateEntry(baseTicks + i * TimeSpan.TicksPerSecond, i));
        }

        var sealedCount = engine.SealPartitions(sealAll: true);

        Assert.Equal(1, sealedCount);
        Assert.Equal(100, engine.GetEntryCount());
        Assert.Equal(100, engine.QueryRange(baseTicks, baseTicks + TimeSpan.TicksPerHour).Count);
        Assert.True(File.Exists(Path.Combine(_testDir, "flux.col")));

        // 行式日志被重写为只含文件头
        var logLength = new FileInfo(Path.Combine(_testDir, "flux.rlog")).Length;
        Assert.Equal(32, logLength);

        var stats = engine.GetCompressionStats();
        Assert.Equal(1, stats.BlockCount);
        Assert.Equal(100, stats.EntryCount);
        Assert.True(stats.RowBytes > 0);
        Assert.True(stats.BlockBytes > 0);
    }

    [Fact(DisplayName = "重启后从列式块恢复数据")]
    public void Seal_RestartRestoresFromBlocks()
    {
        var baseTicks = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).Ticks;
        using (var engine = CreateEngine())
        {
            for (var i = 0; i < 300; i++)
            {
                engine.Append(CreateEntry(baseTicks + i * TimeSpan.TicksPerSecond, i));
            }
            engine.SealPartitions(sealAll: true);
        }

        using var reopened = CreateEngine();
        Assert.Equal(300, reopened.GetEntryCount());

        var entries = reopened.QueryRange(baseTicks, baseTicks + TimeSpan.TicksPerHour);
        Assert.Equal(300, entries.Count);
        Assert.Equal(20.5, (Double)entries[0].Fields["value"]!);
        Assert.Equal(20.5 + 299, (Double)entries[299].Fields["value"]!);

        var stats = reopened.GetCompressionStats();
        Assert.Equal(1, stats.BlockCount);
        Assert.Equal(300, stats.EntryCount);
        Assert.True(stats.ColumnCount > 0);
    }

    [Fact(DisplayName = "旧格式行式日志目录无列式文件时照常读取")]
    public void LegacyLogOnly_StillReadable()
    {
        var baseTicks = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).Ticks;
        using (var engine = CreateEngine())
        {
            for (var i = 0; i < 50; i++)
            {
                engine.Append(CreateEntry(baseTicks + i * TimeSpan.TicksPerSecond, i));
            }
        }

        Assert.False(File.Exists(Path.Combine(_testDir, "flux.col")));

        using var reopened = CreateEngine();
        Assert.Equal(50, reopened.GetEntryCount());
        Assert.Equal(0, reopened.GetCompressionStats().BlockCount);
    }

    [Fact(DisplayName = "时间片滚动时自动封存已完成分区")]
    public void Append_RolloverAutoSeals()
    {
        using var engine = CreateEngine();
        var firstHour = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).Ticks;

        for (var i = 0; i < 20; i++)
        {
            engine.Append(CreateEntry(firstHour + i * TimeSpan.TicksPerSecond, i));
        }
        Assert.Equal(0, engine.GetCompressionStats().BlockCount);

        // 写入下一个时间片的数据，触发上一分区封存
        engine.Append(CreateEntry(firstHour + TimeSpan.TicksPerHour, 0));

        var stats = engine.GetCompressionStats();
        Assert.Equal(1, stats.BlockCount);
        Assert.Equal(20, stats.EntryCount);
        Assert.Equal(21, engine.GetEntryCount());
        Assert.Equal(2, engine.GetPartitionCount());
    }

    [Fact(DisplayName = "封存后迟到数据不丢失且重启后不重复")]
    public void Seal_LateAppendKeepsData()
    {
        var baseTicks = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).Ticks;
        using (var engine = CreateEngine())
        {
            for (var i = 0; i < 30; i++)
            {
                engine.Append(CreateEntry(baseTicks + i * TimeSpan.TicksPerSecond, i));
            }
            engine.SealPartitions(sealAll: true);

            // 迟到数据回写已封存分区
            engine.Append(CreateEntry(baseTicks + 30 * TimeSpan.TicksPerSecond, 30));
            Assert.Equal(31, engine.GetEntryCount());

            engine.SealPartitions(sealAll: true);
            Assert.Equal(31, engine.GetEntryCount());
        }

        using var reopened = CreateEngine();
        Assert.Equal(31, reopened.GetEntryCount());

        var entries = reopened.QueryRange(baseTicks, baseTicks + TimeSpan.TicksPerHour);
        var distinct = entries.Select(e => e.GetMessageId()).Distinct().Count();
        Assert.Equal(entries.Count, distinct);
    }

    [Fact(DisplayName = "TTL 清理回收列式块空间")]
    public void DeleteExpired_RewritesColumnar()
    {
        using var engine = CreateEngine();
        var oldTicks = DateTime.UtcNow.AddHours(-3).Ticks;
        var newTicks = DateTime.UtcNow.AddHours(-1).Ticks;

        for (var i = 0; i < 10; i++)
        {
            engine.Append(CreateEntry(oldTicks + i * TimeSpan.TicksPerSecond, i));
            engine.Append(CreateEntry(newTicks + i * TimeSpan.TicksPerSecond, i));
        }

        Assert.Equal(2, engine.SealPartitions(sealAll: true));
        Assert.Equal(20, engine.GetEntryCount());

        var removed = engine.DeleteExpiredPartitions(2 * 3600L);

        Assert.Equal(1, removed);
        Assert.Equal(10, engine.GetEntryCount());

        var stats = engine.GetCompressionStats();
        Assert.Equal(1, stats.BlockCount);
        Assert.Equal(10, stats.EntryCount);
    }

    [Fact(DisplayName = "重复字符串列启用 LZ4 压缩且压缩率大于一")]
    public void Compression_ReportsRatio()
    {
        using var engine = CreateEngine();
        var baseTicks = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).Ticks;

        for (var i = 0; i < 2000; i++)
        {
            var entry = CreateEntry(baseTicks + i * TimeSpan.TicksPerSecond, i);
            entry.Fields["message"] = "设备采集数据正常上报，状态稳定，温度与湿度均在阈值范围内";
            engine.Append(entry);
        }

        engine.SealPartitions(sealAll: true);
        var stats = engine.GetCompressionStats();

        Assert.Equal(2000, stats.EntryCount);
        Assert.True(stats.CompressedColumnCount > 0, "存在重复字符串列时应启用 LZ4");
        Assert.True(stats.CompressionRatio > 1, $"压缩率 {stats.CompressionRatio:F2} 应大于 1");
        Assert.True(stats.SavedRatio > 0);
    }

    [Fact(DisplayName = "清空数据同时清理列式块与统计")]
    public void Clear_ResetsColumnar()
    {
        using var engine = CreateEngine();
        var baseTicks = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).Ticks;
        for (var i = 0; i < 20; i++)
        {
            engine.Append(CreateEntry(baseTicks + i * TimeSpan.TicksPerSecond, i));
        }
        engine.SealPartitions(sealAll: true);

        engine.Clear();

        Assert.Equal(0, engine.GetEntryCount());
        var stats = engine.GetCompressionStats();
        Assert.Equal(0, stats.BlockCount);
        Assert.Equal(0, stats.EntryCount);
    }

    [Fact(DisplayName = "封存分区保留消息标识与字段类型")]
    public void Seal_PreservesMessageIdAndTypes()
    {
        var baseTicks = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).Ticks;
        var entry = CreateEntry(baseTicks, 0);
        entry.Fields["big"] = 9_000_000_000L;
        entry.Fields["stamp"] = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        using (var engine = CreateEngine())
        {
            engine.Append(entry);
            engine.SealPartitions(sealAll: true);
        }

        using var reopened = CreateEngine();
        var restored = reopened.QueryRange(baseTicks, baseTicks + TimeSpan.TicksPerSecond);
        var single = Assert.Single(restored);

        Assert.Equal(entry.GetMessageId(), single.GetMessageId());
        Assert.Equal(9_000_000_000L, single.Fields["big"]);

        // 非基础类型与 AOF 行式记录一致，统一归一化为字符串
        Assert.IsType<String>(single.Fields["stamp"]);
        Assert.Equal(entry.Fields["stamp"]!.ToString(), single.Fields["stamp"]!.ToString());
    }
}
