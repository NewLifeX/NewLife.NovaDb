using System;
using NewLife.NovaDb.Core;
using Xunit;

namespace XUnitTest.Core;

/// <summary>DbOptions 配置选项测试</summary>
public class DbOptionsTests
{
    [Fact]
    public void TestDefaultValues()
    {
        var options = new DbOptions();

        Assert.Equal(String.Empty, options.Path);
        Assert.Equal(WalMode.Normal, options.WalMode);
        Assert.Equal(4096, options.PageSize);
        Assert.Equal(600, options.HotWindowSeconds);
        Assert.Equal(1800, options.ColdEvictionSeconds);
        Assert.Equal(1024L * 1024 * 1024, options.ShardSizeThreshold);
        Assert.Equal(10_000_000, options.ShardRowThreshold);
        Assert.True(options.EnableChecksum);
        Assert.Equal(1024, options.PageCacheSize);
        Assert.Equal(1, options.FluxPartitionHours);
        Assert.Equal(0, options.FluxDefaultTtlSeconds);
    }

    [Fact]
    public void TestSetPath()
    {
        var options = new DbOptions
        {
            Path = "/data/nova"
        };

        Assert.Equal("/data/nova", options.Path);
    }

    [Fact]
    public void TestSetWalMode()
    {
        var options = new DbOptions
        {
            WalMode = WalMode.Full
        };

        Assert.Equal(WalMode.Full, options.WalMode);
    }

    [Fact]
    public void TestSetPageSize()
    {
        var options = new DbOptions
        {
            PageSize = 8192
        };

        Assert.Equal(8192, options.PageSize);
    }

    [Fact]
    public void TestSetHotWindowSeconds()
    {
        var options = new DbOptions
        {
            HotWindowSeconds = 1200
        };

        Assert.Equal(1200, options.HotWindowSeconds);
    }

    [Fact]
    public void TestSetColdEvictionSeconds()
    {
        var options = new DbOptions
        {
            ColdEvictionSeconds = 3600
        };

        Assert.Equal(3600, options.ColdEvictionSeconds);
    }

    [Fact]
    public void TestSetShardThresholds()
    {
        var options = new DbOptions
        {
            ShardSizeThreshold = 2L * 1024 * 1024 * 1024,
            ShardRowThreshold = 20_000_000
        };

        Assert.Equal(2L * 1024 * 1024 * 1024, options.ShardSizeThreshold);
        Assert.Equal(20_000_000, options.ShardRowThreshold);
    }

    [Fact]
    public void TestSetEnableChecksum()
    {
        var options = new DbOptions
        {
            EnableChecksum = false
        };

        Assert.False(options.EnableChecksum);
    }

    [Fact]
    public void TestSetPageCacheSize()
    {
        var options = new DbOptions
        {
            PageCacheSize = 2048
        };

        Assert.Equal(2048, options.PageCacheSize);
    }

    [Fact]
    public void TestSetFluxOptions()
    {
        var options = new DbOptions
        {
            FluxPartitionHours = 24,
            FluxDefaultTtlSeconds = 86400
        };

        Assert.Equal(24, options.FluxPartitionHours);
        Assert.Equal(86400, options.FluxDefaultTtlSeconds);
    }
}

/// <summary>WalMode 枚举测试</summary>
public class WalModeTests
{
    [Fact]
    public void TestWalModeValues()
    {
        Assert.Equal(0, (Int32)WalMode.None);
        Assert.Equal(1, (Int32)WalMode.Normal);
        Assert.Equal(2, (Int32)WalMode.Full);
    }

    [Fact]
    public void TestWalModeName()
    {
        Assert.Equal("None", WalMode.None.ToString());
        Assert.Equal("Normal", WalMode.Normal.ToString());
        Assert.Equal("Full", WalMode.Full.ToString());
    }
}
