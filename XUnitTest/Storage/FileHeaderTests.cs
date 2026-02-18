using System;
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

        var bytes = header.ToBytes();
        Assert.Equal(32, bytes.Length);

        var deserialized = FileHeader.FromBytes(bytes);

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

        var bytes = header.ToBytes();
        var magic = BitConverter.ToUInt32(bytes, 0);

        Assert.Equal(FileHeader.MagicNumber, magic);
        Assert.Equal(0x4E4F5641u, magic); // "NOVA"
    }

    [Fact]
    public void TestInvalidMagicNumber()
    {
        var bytes = new byte[32];
        BitConverter.GetBytes(0xDEADBEEFu).CopyTo(bytes, 0);

        Assert.Throws<NewLife.NovaDb.Core.NovaDbException>(() => FileHeader.FromBytes(bytes));
    }
}
