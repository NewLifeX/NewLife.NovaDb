using System;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using Xunit;

namespace XUnitTest.Storage;

public class PageHeaderTests
{
    [Fact(DisplayName = "测试页头序列化与反序列化")]
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

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        Assert.Equal(PageHeader.HeaderSize, bytes.Length);
        Assert.Equal(32, bytes.Length);

        var deserialized = PageHeader.Read(new ArrayPacket(bytes));

        Assert.Equal(header.PageId, deserialized.PageId);
        Assert.Equal(header.PageType, deserialized.PageType);
        Assert.Equal(header.Flags, deserialized.Flags);
        Assert.Equal(header.Lsn, deserialized.Lsn);
        Assert.Equal(header.Checksum, deserialized.Checksum);
        Assert.Equal(header.DataLength, deserialized.DataLength);
    }

    [Fact(DisplayName = "测试空缓冲区读取抛出异常")]
    public void TestNullBuffer()
    {
        Assert.Throws<ArgumentNullException>(() => PageHeader.Read(null!));
    }

    [Fact(DisplayName = "测试缓冲区过短抛出异常")]
    public void TestBufferTooShort()
    {
        var bytes = new Byte[31]; // 少于 32 字节
        var ex = Assert.Throws<ArgumentException>(() => PageHeader.Read(new ArrayPacket(bytes)));
        Assert.Contains("too short", ex.Message);
    }

    [Fact(DisplayName = "测试无效页类型抛出异常")]
    public void TestInvalidPageType()
    {
        var header = new PageHeader
        {
            PageId = 1,
            PageType = PageType.Data,
            DataLength = 100
        };
        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();

        // 设置无效的页类型（> 4）
        bytes[8] = 99;

        var ex = Assert.Throws<NovaException>(() => PageHeader.Read(new ArrayPacket(bytes)));
        Assert.Equal(ErrorCode.FileCorrupted, ex.Code);
        Assert.Contains("Invalid page type", ex.Message);
    }

    [Fact(DisplayName = "测试所有页类型序列化往返")]
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

            using var pk = header.ToPacket();
            var bytes = pk.GetSpan().ToArray();
            var deserialized = PageHeader.Read(new ArrayPacket(bytes));

            Assert.Equal(type, deserialized.PageType);
        }
    }

    [Fact(DisplayName = "测试最大页 ID 序列化往返")]
    public void TestLargePageId()
    {
        var header = new PageHeader
        {
            PageId = UInt64.MaxValue,
            PageType = PageType.Index,
            DataLength = 512
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        var deserialized = PageHeader.Read(new ArrayPacket(bytes));

        Assert.Equal(UInt64.MaxValue, deserialized.PageId);
    }

    [Fact(DisplayName = "测试最大 LSN 序列化往返")]
    public void TestLargeLsn()
    {
        var header = new PageHeader
        {
            PageId = 1,
            PageType = PageType.Data,
            Lsn = UInt64.MaxValue,
            DataLength = 256
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        var deserialized = PageHeader.Read(new ArrayPacket(bytes));

        Assert.Equal(UInt64.MaxValue, deserialized.Lsn);
    }

    [Fact(DisplayName = "测试最大校验和序列化往返")]
    public void TestLargeChecksum()
    {
        var header = new PageHeader
        {
            PageId = 1,
            PageType = PageType.Data,
            Checksum = UInt32.MaxValue,
            DataLength = 128
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        var deserialized = PageHeader.Read(new ArrayPacket(bytes));

        Assert.Equal(UInt32.MaxValue, deserialized.Checksum);
    }

    [Fact(DisplayName = "测试最大数据长度序列化往返")]
    public void TestLargeDataLength()
    {
        var header = new PageHeader
        {
            PageId = 1,
            PageType = PageType.Data,
            DataLength = UInt32.MaxValue
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        var deserialized = PageHeader.Read(new ArrayPacket(bytes));

        Assert.Equal(UInt32.MaxValue, deserialized.DataLength);
    }

    [Fact(DisplayName = "测试零值页头序列化往返")]
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

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        var deserialized = PageHeader.Read(new ArrayPacket(bytes));

        Assert.Equal(0UL, deserialized.PageId);
        Assert.Equal(PageType.Empty, deserialized.PageType);
        Assert.Equal(0UL, deserialized.Lsn);
        Assert.Equal(0u, deserialized.Checksum);
        Assert.Equal(0u, deserialized.DataLength);
    }

    [Fact(DisplayName = "测试保留字节为零")]
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

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();

        // Reserved 2 bytes at offset 10-11
        Assert.Equal(0, bytes[10]);
        Assert.Equal(0, bytes[11]);

        // Reserved 4 bytes at offset 24-27
        for (var i = 24; i < 28; i++)
        {
            Assert.Equal(0, bytes[i]);
        }
    }

    [Fact(DisplayName = "测试随机数据序列化往返")]
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

            using var pk = header.ToPacket();
            var bytes = pk.GetSpan().ToArray();
            var deserialized = PageHeader.Read(new ArrayPacket(bytes));

            Assert.Equal(header.PageId, deserialized.PageId);
            Assert.Equal(header.PageType, deserialized.PageType);
            Assert.Equal(header.Flags, deserialized.Flags);
            Assert.Equal(header.Lsn, deserialized.Lsn);
            Assert.Equal(header.Checksum, deserialized.Checksum);
            Assert.Equal(header.DataLength, deserialized.DataLength);
        }
    }

    [Fact(DisplayName = "测试元数据页类型序列化")]
    public void TestMetadataPageType()
    {
        var header = new PageHeader
        {
            PageId = 999,
            PageType = PageType.Metadata,
            Lsn = 777,
            DataLength = 333
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        var deserialized = PageHeader.Read(new ArrayPacket(bytes));

        Assert.Equal(PageType.Metadata, deserialized.PageType);
    }

    [Fact(DisplayName = "测试页标志持久化")]
    public void TestFlagsPersistence()
    {
        var header = new PageHeader
        {
            PageId = 1,
            PageType = PageType.Data,
            Flags = PageFlags.Encrypted | PageFlags.Compressed,
            DataLength = 100
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();

        // Flags 在 offset 9
        Assert.Equal((Byte)(PageFlags.Encrypted | PageFlags.Compressed), bytes[9]);

        var deserialized = PageHeader.Read(new ArrayPacket(bytes));
        Assert.Equal(PageFlags.Encrypted | PageFlags.Compressed, deserialized.Flags);
    }
}
