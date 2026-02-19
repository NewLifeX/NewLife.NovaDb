using System;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using Xunit;

namespace XUnitTest.Storage;

public class FileHeaderTests
{
    [Fact]
    public void TestSerializeDeserialize()
    {
        var header = new FileHeader
        {
            Version = 1,
            FileType = FileType.Data,
            PageSize = 4096,
            CreatedAt = DateTime.UtcNow.Ticks,
            OptionsHash = 12345
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        Assert.Equal(FileHeader.HeaderSize, bytes.Length);
        Assert.Equal(32, bytes.Length);

        var deserialized = FileHeader.Read(new ArrayPacket(bytes));

        Assert.Equal(header.Version, deserialized.Version);
        Assert.Equal(header.FileType, deserialized.FileType);
        Assert.Equal(header.PageSize, deserialized.PageSize);
        Assert.Equal(header.CreatedAt, deserialized.CreatedAt);
        Assert.Equal(header.OptionsHash, deserialized.OptionsHash);
    }

    [Fact]
    public void TestMagicNumber()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 4096
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        var magic = BitConverter.ToUInt32(bytes, 0);

        Assert.Equal(FileHeader.MagicNumber, magic);
        Assert.Equal(0x4E4F5641u, magic); // "NOVA"
    }

    [Fact]
    public void TestInvalidMagicNumber()
    {
        var bytes = new Byte[32];
        BitConverter.GetBytes(0xDEADBEEFu).CopyTo(bytes, 0);

        var ex = Assert.Throws<NovaException>(() => FileHeader.Read(new ArrayPacket(bytes)));
        Assert.Equal(ErrorCode.FileCorrupted, ex.Code);
        Assert.Contains("Invalid magic number", ex.Message);
    }

    [Fact]
    public void TestNullBuffer()
    {
        Assert.Throws<ArgumentNullException>(() => FileHeader.Read(null!));
    }

    [Fact]
    public void TestBufferTooShort()
    {
        var bytes = new Byte[31]; // 少于 32 字节
        var ex = Assert.Throws<ArgumentException>(() => FileHeader.Read(new ArrayPacket(bytes)));
        Assert.Contains("too short", ex.Message);
    }

    [Fact]
    public void TestInvalidFileType()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 4096
        };
        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();

        // 设置无效的文件类型（0 或 > 3）
        bytes[6] = 99;

        var ex = Assert.Throws<NovaException>(() => FileHeader.Read(new ArrayPacket(bytes)));
        Assert.Equal(ErrorCode.FileCorrupted, ex.Code);
        Assert.Contains("Invalid file type", ex.Message);
    }

    [Fact]
    public void TestInvalidPageSizeZero()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 4096
        };
        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();

        // 设置 PageSize 为 0
        BitConverter.GetBytes(0u).CopyTo(bytes, 8);

        var ex = Assert.Throws<NovaException>(() => FileHeader.Read(new ArrayPacket(bytes)));
        Assert.Equal(ErrorCode.FileCorrupted, ex.Code);
        Assert.Contains("Invalid page size", ex.Message);
    }

    [Fact]
    public void TestInvalidPageSizeTooLarge()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 4096
        };
        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();

        // 设置 PageSize > 64KB
        BitConverter.GetBytes(128u * 1024).CopyTo(bytes, 8);

        var ex = Assert.Throws<NovaException>(() => FileHeader.Read(new ArrayPacket(bytes)));
        Assert.Equal(ErrorCode.FileCorrupted, ex.Code);
        Assert.Contains("Invalid page size", ex.Message);
    }

    [Fact]
    public void TestDifferentFileTypes()
    {
        foreach (FileType type in Enum.GetValues(typeof(FileType)))
        {
            var header = new FileHeader
            {
                FileType = type,
                PageSize = 4096
            };

            using var pk = header.ToPacket();
            var bytes = pk.GetSpan().ToArray();
            var deserialized = FileHeader.Read(new ArrayPacket(bytes));

            Assert.Equal(type, deserialized.FileType);
        }
    }

    [Fact]
    public void TestBoundaryPageSizes()
    {
        // 最小有效 PageSize
        var header1 = new FileHeader { FileType = FileType.Data, PageSize = 1 };
        using var pk1 = header1.ToPacket();
        var bytes1 = pk1.GetSpan().ToArray();
        var deserialized1 = FileHeader.Read(new ArrayPacket(bytes1));
        Assert.Equal(1u, deserialized1.PageSize);

        // 最大有效 PageSize (64KB)
        var header2 = new FileHeader { FileType = FileType.Data, PageSize = 64 * 1024 };
        using var pk2 = header2.ToPacket();
        var bytes2 = pk2.GetSpan().ToArray();
        var deserialized2 = FileHeader.Read(new ArrayPacket(bytes2));
        Assert.Equal(64u * 1024, deserialized2.PageSize);
    }

    [Fact]
    public void TestVersionPersistence()
    {
        var header = new FileHeader
        {
            Version = 42,
            FileType = FileType.Index,
            PageSize = 8192
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        var deserialized = FileHeader.Read(new ArrayPacket(bytes));

        Assert.Equal(42, deserialized.Version);
    }

    [Fact]
    public void TestCreatedAtPersistence()
    {
        var now = DateTime.UtcNow.Ticks;
        var header = new FileHeader
        {
            FileType = FileType.Wal,
            PageSize = 4096,
            CreatedAt = now
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        var deserialized = FileHeader.Read(new ArrayPacket(bytes));

        Assert.Equal(now, deserialized.CreatedAt);
    }

    [Fact]
    public void TestOptionsHashPersistence()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 4096,
            OptionsHash = 0xABCD1234
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        var deserialized = FileHeader.Read(new ArrayPacket(bytes));

        Assert.Equal(0xABCD1234u, deserialized.OptionsHash);
    }

    [Fact]
    public void TestReservedBytesAreZero()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 4096
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();

        // Reserved byte at offset 7
        Assert.Equal(0, bytes[7]);

        // Reserved 8 bytes at offset 24-31
        for (var i = 24; i < 32; i++)
        {
            Assert.Equal(0, bytes[i]);
        }
    }
}
