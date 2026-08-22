using System;
using System.IO;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using Xunit;

namespace XUnitTest.Storage;

public class MmfPagerTests : IDisposable
{
    private readonly String _testFile;

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

    #region 构造函数
    [Fact(DisplayName = "构造函数路径为 null 时抛异常")]
    public void TestConstructorNullPath()
    {
        Assert.Throws<ArgumentNullException>(() => new MmfPager(null!, 4096));
    }

    [Fact(DisplayName = "构造函数非法页大小抛异常")]
    public void TestConstructorInvalidPageSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MmfPager(_testFile, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MmfPager(_testFile, -1));
    }
    #endregion

    #region Open
    [Fact(DisplayName = "创建并打开分页器")]
    public void TestCreateAndOpenPager()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 4096,
            CreateTime = DateTime.Now
        };

        using var pager = new MmfPager(_testFile, 4096);
        pager.Open(header);

        Assert.Equal(_testFile, pager.FilePath);
        Assert.Equal(4096, pager.PageSize);
    }

    [Fact(DisplayName = "打开页大小不匹配的现有文件抛异常")]
    public void TestOpenExistingFileWithMismatchedPageSize()
    {
        // 创建文件
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };
        using (var pager1 = new MmfPager(_testFile, 4096))
        {
            pager1.Open(header);
        }

        // 用不同 PageSize 打开
        using var pager2 = new MmfPager(_testFile, 8192);
        var ex = Assert.Throws<NovaException>(() => pager2.Open());
        Assert.Equal(ErrorCode.IncompatibleFileFormat, ex.Code);
        Assert.Contains("Page size mismatch", ex.Message);
    }

    [Fact(DisplayName = "释放后打开抛异常")]
    public void TestOpenAfterDispose()
    {
        var pager = new MmfPager(_testFile, 4096);
        pager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pager.Open());
    }
    #endregion

    #region ReadPage / WritePage
    [Fact(DisplayName = "写入并读取页面数据一致")]
    public void TestWriteAndReadPage()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 4096,
            CreateTime = DateTime.Now
        };

        using var pager = new MmfPager(_testFile, 4096, enableChecksum: false);
        pager.Open(header);

        // 构建测试页
        var pageData = new Byte[4096];
        var pageHeader = new PageHeader
        {
            PageId = 0,
            PageType = PageType.Data,
            Lsn = 1,
            DataLength = 100
        };

        using var phPk = pageHeader.ToPacket();
        var headerBytes = phPk.GetSpan().ToArray();
        Buffer.BlockCopy(headerBytes, 0, pageData, 0, PageHeader.HeaderSize);

        for (var i = 32; i < 132; i++)
        {
            pageData[i] = (Byte)(i % 256);
        }

        pager.WritePage(0, pageData);

        var readData = pager.ReadPage(0);

        Assert.Equal(pageData.Length, readData.Length);
        for (var i = 32; i < 132; i++)
        {
            Assert.Equal(pageData[i], readData[i]);
        }
    }

    [Fact(DisplayName = "写入多个页面互不影响")]
    public void TestWriteMultiplePages()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };

        using var pager = new MmfPager(_testFile, 4096, enableChecksum: false);
        pager.Open(header);

        // 写入 3 个页面
        for (UInt64 i = 0; i < 3; i++)
        {
            var data = new Byte[4096];
            data[32] = (Byte)(i + 100); // 每页标记不同数据
            pager.WritePage(i, data);
        }

        // 验证每个页面数据独立
        for (UInt64 i = 0; i < 3; i++)
        {
            var data = pager.ReadPage(i);
            Assert.Equal((Byte)(i + 100), data[32]);
        }
    }

    [Fact(DisplayName = "读取越界页面抛异常")]
    public void TestReadPageOutOfRange()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };

        using var pager = new MmfPager(_testFile, 4096, enableChecksum: false);
        pager.Open(header);

        // 没有写任何页，读取 pageId=0 应越界
        Assert.Throws<ArgumentOutOfRangeException>(() => pager.ReadPage(0));
    }

    [Fact(DisplayName = "未打开时读取页面抛异常")]
    public void TestReadPageNotOpened()
    {
        using var pager = new MmfPager(_testFile, 4096);
        Assert.Throws<InvalidOperationException>(() => pager.ReadPage(0));
    }

    [Fact(DisplayName = "未打开时写入页面抛异常")]
    public void TestWritePageNotOpened()
    {
        using var pager = new MmfPager(_testFile, 4096);
        var data = new Byte[4096];
        Assert.Throws<InvalidOperationException>(() => pager.WritePage(0, data));
    }

    [Fact(DisplayName = "写入空数据抛异常")]
    public void TestWritePageNullData()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };
        using var pager = new MmfPager(_testFile, 4096);
        pager.Open(header);

        Assert.Throws<ArgumentNullException>(() => pager.WritePage(0, null!));
    }

    [Fact(DisplayName = "写入错误大小数据抛异常")]
    public void TestWritePageWrongSize()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };
        using var pager = new MmfPager(_testFile, 4096);
        pager.Open(header);

        Assert.Throws<ArgumentException>(() => pager.WritePage(0, new Byte[1024])); // 太小
        Assert.Throws<ArgumentException>(() => pager.WritePage(0, new Byte[8192])); // 太大
    }

    [Fact(DisplayName = "释放后读取页面抛异常")]
    public void TestReadAfterDispose()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };
        var pager = new MmfPager(_testFile, 4096);
        pager.Open(header);
        pager.WritePage(0, new Byte[4096]);
        pager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pager.ReadPage(0));
    }

    [Fact(DisplayName = "释放后写入页面抛异常")]
    public void TestWriteAfterDispose()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };
        var pager = new MmfPager(_testFile, 4096);
        pager.Open(header);
        pager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pager.WritePage(0, new Byte[4096]));
    }
    #endregion

    #region Checksum
    [Fact(DisplayName = "带校验和写入与读取页面")]
    public void TestWriteAndReadWithChecksum()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };

        using var pager = new MmfPager(_testFile, 4096, enableChecksum: true);
        pager.Open(header);

        var pageData = new Byte[4096];
        var pageHeader = new PageHeader
        {
            PageId = 0,
            PageType = PageType.Data,
            Lsn = 1,
            DataLength = 50
        };

        using var phPk = pageHeader.ToPacket();
        var phBytes = phPk.GetSpan().ToArray();
        Buffer.BlockCopy(phBytes, 0, pageData, 0, PageHeader.HeaderSize);
        for (var i = 32; i < 82; i++)
        {
            pageData[i] = (Byte)(i * 3);
        }

        // WritePage 会自动计算校验和
        pager.WritePage(0, pageData);

        // ReadPage 会自动验证校验和
        var readData = pager.ReadPage(0);
        Assert.Equal(4096, readData.Length);
    }

    [Fact(DisplayName = "页面数据损坏时校验和检测报错")]
    public void TestChecksumCorruption()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };

        // 先写入数据
        using (var pager = new MmfPager(_testFile, 4096, enableChecksum: true))
        {
            pager.Open(header);

            var pageData = new Byte[4096];
            var pageHeader = new PageHeader
            {
                PageId = 0,
                PageType = PageType.Data,
                Lsn = 1,
                DataLength = 10
            };

            using var phPk = pageHeader.ToPacket();
            var phBytes = phPk.GetSpan().ToArray();
            Buffer.BlockCopy(phBytes, 0, pageData, 0, PageHeader.HeaderSize);
            pager.WritePage(0, pageData);
        }

        // 篡改文件中的页数据
        using (var fs = File.Open(_testFile, FileMode.Open, FileAccess.ReadWrite))
        {
            // 在 FileHeader(32B) + PageHeader(32B) 后篡改一个字节
            fs.Seek(FileHeader.HeaderSize + PageHeader.HeaderSize, SeekOrigin.Begin);
            fs.WriteByte(0xFF);
        }

        // 重新打开并读取应触发校验和错误
        using var pager2 = new MmfPager(_testFile, 4096, enableChecksum: true);
        pager2.Open();

        var ex = Assert.Throws<NovaException>(() => pager2.ReadPage(0));
        Assert.Equal(ErrorCode.ChecksumFailed, ex.Code);
        Assert.Contains("Checksum mismatch", ex.Message);
    }
    #endregion

    #region PageCount / Flush
    [Fact(DisplayName = "页面数量统计正确")]
    public void TestPageCount()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 4096,
            CreateTime = DateTime.Now
        };

        using var pager = new MmfPager(_testFile, 4096, enableChecksum: false);
        pager.Open(header);

        Assert.Equal(0UL, pager.PageCount);

        pager.WritePage(0, new Byte[4096]);
        Assert.Equal(1UL, pager.PageCount);

        pager.WritePage(1, new Byte[4096]);
        Assert.Equal(2UL, pager.PageCount);
    }

    [Fact(DisplayName = "未打开时页面数量为零")]
    public void TestPageCountBeforeOpen()
    {
        using var pager = new MmfPager(_testFile, 4096);
        Assert.Equal(0UL, pager.PageCount); // 未打开返回 0
    }

    [Fact(DisplayName = "刷新页面不抛异常")]
    public void TestFlush()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };
        using var pager = new MmfPager(_testFile, 4096, enableChecksum: false);
        pager.Open(header);

        pager.WritePage(0, new Byte[4096]);
        pager.Flush(); // 不应抛异常
    }

    [Fact(DisplayName = "未打开时刷新不抛异常")]
    public void TestFlushBeforeOpen()
    {
        using var pager = new MmfPager(_testFile, 4096);
        pager.Flush(); // 未打开时不应抛异常
    }
    #endregion

    #region DoubleDispose
    [Fact(DisplayName = "重复释放不抛异常")]
    public void TestDoubleDispose()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };
        var pager = new MmfPager(_testFile, 4096);
        pager.Open(header);

        pager.Dispose();
        pager.Dispose(); // 第二次释放不应抛异常
    }
    #endregion

    #region 跨实例持久化
    [Fact(DisplayName = "数据跨实例持久化")]
    public void TestDataPersistsAcrossInstances()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };

        // 第一个实例写数据
        using (var pager1 = new MmfPager(_testFile, 4096, enableChecksum: false))
        {
            pager1.Open(header);
            var data = new Byte[4096];
            data[32] = 42;
            data[100] = 99;
            pager1.WritePage(0, data);
        }

        // 第二个实例读数据
        using (var pager2 = new MmfPager(_testFile, 4096, enableChecksum: false))
        {
            pager2.Open();
            var data = pager2.ReadPage(0);
            Assert.Equal(42, data[32]);
            Assert.Equal(99, data[100]);
        }
    }
    #endregion
}
