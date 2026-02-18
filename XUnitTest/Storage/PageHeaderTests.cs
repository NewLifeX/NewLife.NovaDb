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
        Assert.Equal(32, bytes.Length);

        var deserialized = PageHeader.FromBytes(bytes);

        Assert.Equal(header.PageId, deserialized.PageId);
        Assert.Equal(header.PageType, deserialized.PageType);
        Assert.Equal(header.Lsn, deserialized.Lsn);
        Assert.Equal(header.Checksum, deserialized.Checksum);
        Assert.Equal(header.DataLength, deserialized.DataLength);
    }
}
