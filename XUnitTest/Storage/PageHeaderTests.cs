using System;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using Xunit;

namespace XUnitTest.Storage;

public class PageHeaderTests
{
    [Fact]
    public void TestSerializeDeserialize()
    {
        var header = new PageHeader
        {
            PageId = 100,
            PageType = PageType.Data,
            Lsn = 12345,
            Checksum = 0xABCDEF,
            DataLength = 2048
        };

        var bytes = header.ToBytes();
        Assert.Equal(PageHeader.HeaderSize, bytes.Length);
        Assert.Equal(32, bytes.Length);

        var deserialized = PageHeader.FromBytes(bytes);

        Assert.Equal(header.PageId, deserialized.PageId);
        Assert.Equal(header.PageType, deserialized.PageType);
        Assert.Equal(header.Lsn, deserialized.Lsn);
        Assert.Equal(header.Checksum, deserialized.Checksum);
        Assert.Equal(header.DataLength, deserialized.DataLength);
    }

    [Fact]
    public void TestNullBuffer()
    {
        Assert.Throws<ArgumentNullException>(() => PageHeader.FromBytes(null!));
    }

    [Fact]
    public void TestBufferTooShort()
    {
        var bytes = new Byte[31]; // 少于 32 字节
        var ex = Assert.Throws<ArgumentException>(() => PageHeader.FromBytes(bytes));
        Assert.Contains("too short", ex.Message);
    }

    [Fact]
    public void TestInvalidPageType()
    {
        var header = new PageHeader
        {
            PageId = 1,
            PageType = PageType.Data,
            DataLength = 100
        };
        var bytes = header.ToBytes();

        // 设置无效的页类型（> 4）
        bytes[8] = 99;

        var ex = Assert.Throws<NovaException>(() => PageHeader.FromBytes(bytes));
        Assert.Equal(ErrorCode.FileCorrupted, ex.Code);
        Assert.Contains("Invalid page type", ex.Message);
    }

    [Fact]
    public void TestAllPageTypes()
    {
        foreach (PageType type in Enum.GetValues(typeof(PageType)))
        {
            var header = new PageHeader
            {
                PageId = 42,
                PageType = type,
                DataLength = 1024
            };

            var bytes = header.ToBytes();
            var deserialized = PageHeader.FromBytes(bytes);

            Assert.Equal(type, deserialized.PageType);
        }
    }

    [Fact]
    public void TestLargePageId()
    {
        var header = new PageHeader
        {
            PageId = UInt64.MaxValue,
            PageType = PageType.Index,
            DataLength = 512
        };

        var bytes = header.ToBytes();
        var deserialized = PageHeader.FromBytes(bytes);

        Assert.Equal(UInt64.MaxValue, deserialized.PageId);
    }

    [Fact]
    public void TestLargeLsn()
    {
        var header = new PageHeader
        {
            PageId = 1,
            PageType = PageType.Data,
            Lsn = UInt64.MaxValue,
            DataLength = 256
        };

        var bytes = header.ToBytes();
        var deserialized = PageHeader.FromBytes(bytes);

        Assert.Equal(UInt64.MaxValue, deserialized.Lsn);
    }

    [Fact]
    public void TestLargeChecksum()
    {
        var header = new PageHeader
        {
            PageId = 1,
            PageType = PageType.Data,
            Checksum = UInt32.MaxValue,
            DataLength = 128
        };

        var bytes = header.ToBytes();
        var deserialized = PageHeader.FromBytes(bytes);

        Assert.Equal(UInt32.MaxValue, deserialized.Checksum);
    }

    [Fact]
    public void TestLargeDataLength()
    {
        var header = new PageHeader
        {
            PageId = 1,
            PageType = PageType.Data,
            DataLength = UInt32.MaxValue
        };

        var bytes = header.ToBytes();
        var deserialized = PageHeader.FromBytes(bytes);

        Assert.Equal(UInt32.MaxValue, deserialized.DataLength);
    }

    [Fact]
    public void TestZeroValues()
    {
        var header = new PageHeader
        {
            PageId = 0,
            PageType = PageType.Empty,
            Lsn = 0,
            Checksum = 0,
            DataLength = 0
        };

        var bytes = header.ToBytes();
        var deserialized = PageHeader.FromBytes(bytes);

        Assert.Equal(0UL, deserialized.PageId);
        Assert.Equal(PageType.Empty, deserialized.PageType);
        Assert.Equal(0UL, deserialized.Lsn);
        Assert.Equal(0u, deserialized.Checksum);
        Assert.Equal(0u, deserialized.DataLength);
    }

    [Fact]
    public void TestReservedBytesAreZero()
    {
        var header = new PageHeader
        {
            PageId = 123,
            PageType = PageType.Directory,
            Lsn = 456,
            Checksum = 789,
            DataLength = 1000
        };

        var bytes = header.ToBytes();

        // Reserved 3 bytes at offset 9-11
        Assert.Equal(0, bytes[9]);
        Assert.Equal(0, bytes[10]);
        Assert.Equal(0, bytes[11]);

        // Reserved 4 bytes at offset 28-31
        for (var i = 28; i < 32; i++)
        {
            Assert.Equal(0, bytes[i]);
        }
    }

    [Fact]
    public void TestRoundTripWithRandomData()
    {
        var random = new Random(42);

        for (var i = 0; i < 100; i++)
        {
            var header = new PageHeader
            {
                PageId = (UInt64)random.Next() * (UInt64)random.Next(),
                PageType = (PageType)(random.Next(0, 5)),
                Lsn = (UInt64)random.Next() * (UInt64)random.Next(),
                Checksum = (UInt32)random.Next(),
                DataLength = (UInt32)random.Next()
            };

            var bytes = header.ToBytes();
            var deserialized = PageHeader.FromBytes(bytes);

            Assert.Equal(header.PageId, deserialized.PageId);
            Assert.Equal(header.PageType, deserialized.PageType);
            Assert.Equal(header.Lsn, deserialized.Lsn);
            Assert.Equal(header.Checksum, deserialized.Checksum);
            Assert.Equal(header.DataLength, deserialized.DataLength);
        }
    }

    [Fact]
    public void TestMetadataPageType()
    {
        var header = new PageHeader
        {
            PageId = 999,
            PageType = PageType.Metadata,
            Lsn = 777,
            DataLength = 333
        };

        var bytes = header.ToBytes();
        var deserialized = PageHeader.FromBytes(bytes);

        Assert.Equal(PageType.Metadata, deserialized.PageType);
    }
}
