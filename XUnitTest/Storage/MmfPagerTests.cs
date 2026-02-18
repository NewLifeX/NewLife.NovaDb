using System;
using System.IO;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using Xunit;

namespace XUnitTest.Storage;

public class MmfPagerTests : IDisposable
{
    private readonly string _testFile;

    public MmfPagerTests()
    {
        _testFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.data");
    }

    public void Dispose()
    {
        if (File.Exists(_testFile))
        {
            File.Delete(_testFile);
        }
    }

    [Fact]
    public void TestCreateAndOpenPager()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 4096,
            CreatedAt = DateTime.UtcNow.Ticks
        };

        using var pager = new MmfPager(_testFile, 4096);
        pager.Open(header);

        Assert.Equal(_testFile, pager.FilePath);
        Assert.Equal(4096, pager.PageSize);
    }

    [Fact]
    public void TestWriteAndReadPage()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 4096,
            CreatedAt = DateTime.UtcNow.Ticks
        };

        using var pager = new MmfPager(_testFile, 4096, enableChecksum: false);
        pager.Open(header);

        // 创建测试页
        var pageData = new byte[4096];
        var pageHeader = new PageHeader
        {
            PageId = 0,
            PageType = PageType.Data,
            Lsn = 1,
            DataLength = 100
        };

        var headerBytes = pageHeader.ToBytes();
        Buffer.BlockCopy(headerBytes, 0, pageData, 0, 32);

        // 写入一些测试数据
        for (int i = 32; i < 132; i++)
        {
            pageData[i] = (byte)(i % 256);
        }

        // 写入页
        pager.WritePage(0, pageData);

        // 读取页
        var readData = pager.ReadPage(0);

        // 验证数据
        Assert.Equal(pageData.Length, readData.Length);
        for (int i = 32; i < 132; i++)
        {
            Assert.Equal(pageData[i], readData[i]);
        }
    }

    [Fact]
    public void TestPageCount()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 4096,
            CreatedAt = DateTime.UtcNow.Ticks
        };

        using var pager = new MmfPager(_testFile, 4096, enableChecksum: false);
        pager.Open(header);

        Assert.Equal(0UL, pager.PageCount);

        var pageData = new byte[4096];
        pager.WritePage(0, pageData);
        Assert.Equal(1UL, pager.PageCount);

        pager.WritePage(1, pageData);
        Assert.Equal(2UL, pager.PageCount);
    }
}
