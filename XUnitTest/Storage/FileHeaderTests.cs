using System;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using NewLife.Security;
using Xunit;

namespace XUnitTest.Storage;

public class FileHeaderTests
{
    /// <summary>篡改字节后重新计算 Checksum，使校验和仍有效以测试其它验证逻辑</summary>
    private static void RecomputeChecksum(Byte[] bytes)
    {
        var crc = Crc32.Compute(bytes.AsSpan(0, 16));
        BitConverter.GetBytes(crc).CopyTo(bytes, 16);
    }

    [Fact]
    public void TestSerializeDeserialize()
    {
        var header = new FileHeader
        {
            Version = 1,
            FileType = FileType.Data,
            PageSize = 4096,
            CreateTime = DateTime.Now,
            Flags = FileFlags.None
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        Assert.Equal(FileHeader.HeaderSize, bytes.Length);
        Assert.Equal(32, bytes.Length);

        var deserialized = FileHeader.Read(new ArrayPacket(bytes));

        Assert.Equal(header.Version, deserialized.Version);
        Assert.Equal(header.FileType, deserialized.FileType);
        Assert.Equal(header.PageSize, deserialized.PageSize);
        Assert.Equal(header.Flags, deserialized.Flags);
        Assert.True(deserialized.Checksum != 0);
        // UTC 毫秒序列化会丢失亚毫秒精度，比较到秒级
        Assert.True(Math.Abs((header.CreateTime - deserialized.CreateTime).TotalSeconds) < 1);
    }

    [Fact]
    public void TestFlagsPersistence()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 4096,
            Flags = FileFlags.Encrypted | FileFlags.Compressed
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();

        // Flags 在 offset 7
        Assert.Equal((Byte)(FileFlags.Encrypted | FileFlags.Compressed), bytes[7]);

        var deserialized = FileHeader.Read(new ArrayPacket(bytes));
        Assert.Equal(FileFlags.Encrypted | FileFlags.Compressed, deserialized.Flags);
    }

    [Fact]
    public void TestChecksumVerification()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 4096
        };
        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();

        // 篡改 Checksum（offset 16-19）
        bytes[16] ^= 0xFF;

        var ex = Assert.Throws<NovaException>(() => FileHeader.Read(new ArrayPacket(bytes)));
        Assert.Equal(ErrorCode.ChecksumFailed, ex.Code);
        Assert.Contains("checksum mismatch", ex.Message);
    }

    [Fact]
    public void TestChecksumDetectsDataCorruption()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 4096,
            CreateTime = DateTime.Now
        };
        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();

        // 篡改 CreateTime 区域（offset 8-15），但不更新 Checksum
        bytes[10] ^= 0xFF;

        var ex = Assert.Throws<NovaException>(() => FileHeader.Read(new ArrayPacket(bytes)));
        Assert.Equal(ErrorCode.ChecksumFailed, ex.Code);
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
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };
        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();

        // 篡改魔数并重新计算 Checksum
        BitConverter.GetBytes(0xDEADBEEFu).CopyTo(bytes, 0);
        RecomputeChecksum(bytes);

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
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };
        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();

        // 篡改 FileType（offset 5）并重新计算 Checksum
        bytes[5] = 99;
        RecomputeChecksum(bytes);

        var ex = Assert.Throws<NovaException>(() => FileHeader.Read(new ArrayPacket(bytes)));
        Assert.Equal(ErrorCode.FileCorrupted, ex.Code);
        Assert.Contains("Invalid file type", ex.Message);
    }

    [Fact]
    public void TestInvalidPageSizeShiftTooLarge()
    {
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };
        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();

        // 篡改 PageSizeShift（offset 6）并重新计算 Checksum
        bytes[6] = 25;
        RecomputeChecksum(bytes);

        var ex = Assert.Throws<NovaException>(() => FileHeader.Read(new ArrayPacket(bytes)));
        Assert.Equal(ErrorCode.FileCorrupted, ex.Code);
        Assert.Contains("Invalid page size shift", ex.Message);
    }

    [Fact]
    public void TestNonPowerOf2PageSizeThrows()
    {
        var header = new FileHeader
        {
            FileType = FileType.Data,
            PageSize = 3000 // 非 2 的幂次
        };

        Assert.Throws<ArgumentException>(() => header.ToPacket());
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
        // 最小有效 PageSize (2^0 = 1)
        var header1 = new FileHeader { FileType = FileType.Data, PageSize = 1 };
        using var pk1 = header1.ToPacket();
        var deserialized1 = FileHeader.Read(new ArrayPacket(pk1.GetSpan().ToArray()));
        Assert.Equal(1u, deserialized1.PageSize);

        // 最大有效 PageSize (2^24 = 16MB)
        var header2 = new FileHeader { FileType = FileType.Data, PageSize = 16 * 1024 * 1024 };
        using var pk2 = header2.ToPacket();
        var deserialized2 = FileHeader.Read(new ArrayPacket(pk2.GetSpan().ToArray()));
        Assert.Equal(16u * 1024 * 1024, deserialized2.PageSize);
    }

    [Fact]
    public void TestCommonPageSizes()
    {
        // 验证常见页大小的往返
        UInt32[] sizes = [512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 128 * 1024, 256 * 1024, 1024 * 1024];
        foreach (var size in sizes)
        {
            var header = new FileHeader { FileType = FileType.Data, PageSize = size };
            using var pk = header.ToPacket();
            var deserialized = FileHeader.Read(new ArrayPacket(pk.GetSpan().ToArray()));
            Assert.Equal(size, deserialized.PageSize);
        }
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
    public void TestCreateTimePersistence()
    {
        var now = DateTime.Now;
        var header = new FileHeader
        {
            FileType = FileType.Wal,
            PageSize = 4096,
            CreateTime = now
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        var deserialized = FileHeader.Read(new ArrayPacket(bytes));

        // UTC 毫秒序列化会丢失亚毫秒精度，比较到秒级
        Assert.True(Math.Abs((now - deserialized.CreateTime).TotalSeconds) < 1);
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

        // Reserved 12 bytes at offset 20-31
        for (var i = 20; i < 32; i++)
        {
            Assert.Equal(0, bytes[i]);
        }
    }

    [Fact]
    public void TestPageSizeShiftEncoding()
    {
        // 4096 = 2^12，offset 6 应为 12
        var header = new FileHeader { FileType = FileType.Data, PageSize = 4096 };
        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();

        Assert.Equal(12, bytes[6]);
    }

    [Fact]
    public void TestBinlogFileType()
    {
        var header = new FileHeader
        {
            FileType = FileType.Binlog,
            PageSize = 4096
        };

        using var pk = header.ToPacket();
        var deserialized = FileHeader.Read(new ArrayPacket(pk.GetSpan().ToArray()));

        Assert.Equal(FileType.Binlog, deserialized.FileType);
    }
}
