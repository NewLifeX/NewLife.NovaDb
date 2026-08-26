using NewLife.NovaDb.Core;
using Xunit;

namespace XUnitTest.Core;

/// <summary>慢查询日志测试（O02）</summary>
public class SlowQueryLogTests
{
    [Fact(DisplayName = "低于阈值不记录")]
    public void UnderThreshold_NotRecorded()
    {
        var log = new SlowQueryLog { ThresholdMs = 100 };
        var result = log.Record("SELECT 1", 99);

        Assert.False(result);
        Assert.Equal(0, log.TotalCount);
        Assert.Empty(log.RecentEntries);
    }

    [Fact(DisplayName = "等于阈值记录")]
    public void AtThreshold_Recorded()
    {
        var log = new SlowQueryLog { ThresholdMs = 100 };
        var result = log.Record("SELECT 1", 100);

        Assert.True(result);
        Assert.Equal(1, log.TotalCount);
    }

    [Fact(DisplayName = "超过阈值记录 SQL/耗时/行数")]
    public void OverThreshold_RecordedWithDetails()
    {
        var log = new SlowQueryLog { ThresholdMs = 100 };
        var result = log.Record("SELECT * FROM users", 250, 5);

        Assert.True(result);
        Assert.Equal(1, log.TotalCount);

        var entry = Assert.Single(log.RecentEntries);
        Assert.Equal("SELECT * FROM users", entry.Sql);
        Assert.Equal(250, entry.ElapsedMs);
        Assert.Equal(5, entry.AffectedRows);
        Assert.NotEqual(default, entry.Time);
    }

    [Fact(DisplayName = "未启用不记录")]
    public void Disabled_NotRecorded()
    {
        var log = new SlowQueryLog { Enabled = false, ThresholdMs = 1 };
        var result = log.Record("SELECT 1", 1000);

        Assert.False(result);
        Assert.Equal(0, log.TotalCount);
        Assert.Empty(log.RecentEntries);
    }

    [Fact(DisplayName = "空 SQL 不记录")]
    public void EmptySql_NotRecorded()
    {
        var log = new SlowQueryLog { ThresholdMs = 1 };
        var result = log.Record("", 1000);

        Assert.False(result);
        Assert.Equal(0, log.TotalCount);
    }

    [Fact(DisplayName = "环形缓冲区超过上限移除最旧")]
    public void RingBuffer_Overflow_RemovesOldest()
    {
        var log = new SlowQueryLog { ThresholdMs = 1, MaxEntries = 3 };

        log.Record("SELECT 1", 10);
        log.Record("SELECT 2", 10);
        log.Record("SELECT 3", 10);
        log.Record("SELECT 4", 10);
        log.Record("SELECT 5", 10);

        // 总数累加，但内存仅保留最近 3 条
        Assert.Equal(5, log.TotalCount);
        Assert.Equal(3, log.RecentEntries.Count);
        Assert.Equal("SELECT 3", log.RecentEntries[0].Sql);
        Assert.Equal("SELECT 5", log.RecentEntries[2].Sql);
    }

    [Fact(DisplayName = "Clear 清空记录但保留总数")]
    public void Clear_EmptiesEntries()
    {
        var log = new SlowQueryLog { ThresholdMs = 1 };
        log.Record("SELECT 1", 10);

        log.Clear();

        Assert.Empty(log.RecentEntries);
        Assert.Equal(1, log.TotalCount);
    }

    [Fact(DisplayName = "阈值可配置")]
    public void Threshold_Configurable()
    {
        var log = new SlowQueryLog { ThresholdMs = 500 };

        Assert.False(log.Record("SELECT 1", 499));
        Assert.True(log.Record("SELECT 1", 500));
        Assert.Equal(1, log.TotalCount);
    }

    [Fact(DisplayName = "多记录按时间顺序追加")]
    public void MultipleRecords_Ordered()
    {
        var log = new SlowQueryLog { ThresholdMs = 1 };

        log.Record("SELECT 1", 10);
        log.Record("SELECT 2", 10);

        Assert.Equal(2, log.RecentEntries.Count);
        Assert.Equal("SELECT 1", log.RecentEntries[0].Sql);
        Assert.Equal("SELECT 2", log.RecentEntries[1].Sql);
    }

    [Fact(DisplayName = "ToString 包含耗时与 SQL")]
    public void ToString_ContainsDetails()
    {
        var log = new SlowQueryLog { ThresholdMs = 1 };
        log.Record("SELECT * FROM big_table", 1234, 7);

        var text = log.RecentEntries[0].ToString();
        Assert.Contains("1234ms", text);
        Assert.Contains("rows=7", text);
        Assert.Contains("SELECT * FROM big_table", text);
    }
}
