using System;
using NewLife.NovaDb.Core;
using Xunit;

namespace XUnitTest.Core;

/// <summary>ServerDbOptions 网络模式配置选项测试</summary>
public class ServerDbOptionsTests
{
    [Fact(DisplayName = "网络模式默认值")]
    public void TestDefaultValues()
    {
        var options = new ServerDbOptions();

        // 继承自 DbOptions 的共用默认值
        Assert.Equal(String.Empty, options.Path);
        Assert.Equal(WalMode.Normal, options.WalMode);
        Assert.Equal(4096, options.PageSize);

        // 网络模式专属默认值
        Assert.True(options.EnableBinlog);
        Assert.Equal(256L * 1024 * 1024, options.BinlogMaxFileSize);
        Assert.Equal(0, options.BinlogRetentionDays);
        Assert.Equal(0, options.BinlogMaxTotalSize);
    }

    [Fact(DisplayName = "网络模式继承自DbOptions")]
    public void TestInheritsFromDbOptions()
    {
        var options = new ServerDbOptions();

        Assert.IsAssignableFrom<DbOptions>(options);
    }

    [Fact(DisplayName = "设置Binlog文件大小")]
    public void TestSetBinlogMaxFileSize()
    {
        var options = new ServerDbOptions
        {
            BinlogMaxFileSize = 512L * 1024 * 1024
        };

        Assert.Equal(512L * 1024 * 1024, options.BinlogMaxFileSize);
    }

    [Fact(DisplayName = "设置Binlog保留天数")]
    public void TestSetBinlogRetentionDays()
    {
        var options = new ServerDbOptions
        {
            BinlogRetentionDays = 30
        };

        Assert.Equal(30, options.BinlogRetentionDays);
    }

    [Fact(DisplayName = "设置Binlog总大小上限")]
    public void TestSetBinlogMaxTotalSize()
    {
        var options = new ServerDbOptions
        {
            BinlogMaxTotalSize = 10L * 1024 * 1024 * 1024
        };

        Assert.Equal(10L * 1024 * 1024 * 1024, options.BinlogMaxTotalSize);
    }

    [Fact(DisplayName = "禁用Binlog")]
    public void TestDisableBinlog()
    {
        var options = new ServerDbOptions
        {
            EnableBinlog = false
        };

        Assert.False(options.EnableBinlog);
    }

    [Fact(DisplayName = "ServerDbOptions可作为DbOptions传递")]
    public void TestPolymorphism()
    {
        DbOptions options = new ServerDbOptions
        {
            Path = "/data/nova",
            WalMode = WalMode.Full,
            EnableBinlog = true,
            BinlogRetentionDays = 7
        };

        Assert.Equal("/data/nova", options.Path);
        Assert.Equal(WalMode.Full, options.WalMode);

        var serverOptions = Assert.IsType<ServerDbOptions>(options);
        Assert.True(serverOptions.EnableBinlog);
        Assert.Equal(7, serverOptions.BinlogRetentionDays);
    }
}
