using System;
using System.IO;
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
    [Fact]
    public void TestConstructorNullPath()
    {
        Assert.Throws<ArgumentNullException>(() => new MmfPager(null!, 4096));
    }

    [Fact]
    public void TestConstructorInvalidPageSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MmfPager(_testFile, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MmfPager(_testFile, -1));
    }
    #endregion

    #region Open
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

    [Fact]
    public void TestOpenAfterDispose()
    {
        var pager = new MmfPager(_testFile, 4096);
        pager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pager.Open());
    }
    #endregion

    #region ReadPage / WritePage
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

        // 构建测试页
        var pageData = new Byte[4096];
        var pageHeader = new PageHeader
        {
            PageId = 0,
            PageType = PageType.Data,
            Lsn = 1,
            DataLength = 100
        };

        var headerBytes = pageHeader.ToBytes();
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

    [Fact]
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

    [Fact]
    public void TestReadPageOutOfRange()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };

        using var pager = new MmfPager(_testFile, 4096, enableChecksum: false);
        pager.Open(header);

        // 没有写任何页，读取 pageId=0 应越界
        Assert.Throws<ArgumentOutOfRangeException>(() => pager.ReadPage(0));
    }

    [Fact]
    public void TestReadPageNotOpened()
    {
        using var pager = new MmfPager(_testFile, 4096);
        Assert.Throws<InvalidOperationException>(() => pager.ReadPage(0));
    }

    [Fact]
    public void TestWritePageNotOpened()
    {
        using var pager = new MmfPager(_testFile, 4096);
        var data = new Byte[4096];
        Assert.Throws<InvalidOperationException>(() => pager.WritePage(0, data));
    }

    [Fact]
    public void TestWritePageNullData()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };
        using var pager = new MmfPager(_testFile, 4096);
        pager.Open(header);

        Assert.Throws<ArgumentNullException>(() => pager.WritePage(0, null!));
    }

    [Fact]
    public void TestWritePageWrongSize()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };
        using var pager = new MmfPager(_testFile, 4096);
        pager.Open(header);

        Assert.Throws<ArgumentException>(() => pager.WritePage(0, new Byte[1024])); // 太小
        Assert.Throws<ArgumentException>(() => pager.WritePage(0, new Byte[8192])); // 太大
    }

    [Fact]
    public void TestReadAfterDispose()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };
        var pager = new MmfPager(_testFile, 4096);
        pager.Open(header);
        pager.WritePage(0, new Byte[4096]);
        pager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pager.ReadPage(0));
    }

    [Fact]
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
    [Fact]
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

        Buffer.BlockCopy(pageHeader.ToBytes(), 0, pageData, 0, PageHeader.HeaderSize);
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

    [Fact]
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

            Buffer.BlockCopy(pageHeader.ToBytes(), 0, pageData, 0, PageHeader.HeaderSize);
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

        pager.WritePage(0, new Byte[4096]);
        Assert.Equal(1UL, pager.PageCount);

        pager.WritePage(1, new Byte[4096]);
        Assert.Equal(2UL, pager.PageCount);
    }

    [Fact]
    public void TestPageCountBeforeOpen()
    {
        using var pager = new MmfPager(_testFile, 4096);
        Assert.Equal(0UL, pager.PageCount); // 未打开返回 0
    }

    [Fact]
    public void TestFlush()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };
        using var pager = new MmfPager(_testFile, 4096, enableChecksum: false);
        pager.Open(header);

        pager.WritePage(0, new Byte[4096]);
        pager.Flush(); // 不应抛异常
    }

    [Fact]
    public void TestFlushBeforeOpen()
    {
        using var pager = new MmfPager(_testFile, 4096);
        pager.Flush(); // 未打开时不应抛异常
    }
    #endregion

    #region DoubleDispose
    [Fact]
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
    [Fact]
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
