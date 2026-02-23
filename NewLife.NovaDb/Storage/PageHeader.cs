using NewLife.Buffers;
using NewLife.Data;

namespace NewLife.NovaDb.Storage;

/// <summary>页头结构（每个页的开头，固定 32 字节）</summary>
/// <remarks>
/// 页头布局（32 字节）：
/// - 0-7: PageId (页 ID)
/// - 8: PageType (0=Empty, 1=Data, 2=Index, 3=Directory, 4=Metadata)
/// - 9: Flags (页级标志：bit0=已加密, bit1=已压缩, bit2=脏页)
/// - 10-11: Reserved (预留)
/// - 12-19: LSN (日志序列号)
/// - 20-23: DataLength (页内有效数据长度)
/// - 24-27: Reserved (预留扩展)
/// - 28-31: Checksum (CRC32 校验和)
/// </remarks>
public class PageHeader
{
    /// <summary>页头固定大小（32 字节）</summary>
    public const Int32 HeaderSize = 32;

    /// <summary>页 ID</summary>
    public UInt64 PageId { get; set; }

    /// <summary>页类型</summary>
    public PageType PageType { get; set; }

    /// <summary>页级标志。bit0=已加密, bit1=已压缩, bit2=脏页，其余位预留</summary>
    public PageFlags Flags { get; set; }

    /// <summary>日志序列号（LSN）</summary>
    public UInt64 Lsn { get; set; }

    /// <summary>校验和（CRC32）</summary>
    public UInt32 Checksum { get; set; }

    /// <summary>页内有效数据长度</summary>
    public UInt32 DataLength { get; set; }

    /// <summary>序列化为数据包（固定 32 字节），使用后需 Dispose 归还到对象池</summary>
    /// <returns>包含 32 字节页头数据的数据包</returns>
    public IOwnerPacket ToPacket()
    {
        var pk = new OwnerPacket(HeaderSize);
        var writer = new SpanWriter(pk);

        // PageId (8 bytes)
        writer.Write(PageId);

        // PageType (1 byte)
        writer.WriteByte((Byte)PageType);

        // Flags (1 byte)
        writer.WriteByte((Byte)Flags);

        // Reserved (2 bytes)
        writer.FillZero(2);

        // Lsn (8 bytes)
        writer.Write(Lsn);

        // DataLength (4 bytes)
        writer.Write(DataLength);

        // Reserved (4 bytes) - 预留扩展
        writer.FillZero(4);

        // Checksum (4 bytes)
        writer.Write(Checksum);

        return pk;
    }

    /// <summary>从数据包反序列化</summary>
    /// <param name="data">包含页头数据的数据包（至少 32 字节）</param>
    /// <returns>反序列化的页头对象</returns>
    /// <exception cref="ArgumentNullException">data 为 null</exception>
    /// <exception cref="ArgumentException">data 长度不足 32 字节</exception>
    /// <exception cref="Core.NovaException">页类型无效</exception>
    public static PageHeader Read(IPacket data)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        if (data.Length < HeaderSize)
            throw new ArgumentException($"Buffer too short for PageHeader, expected {HeaderSize} bytes, got {data.Length}", nameof(data));

        var reader = new SpanReader(data);

        // PageId
        var pageId = reader.ReadUInt64();

        // PageType 验证
        var pageTypeByte = reader.ReadByte();
        if (!Enum.IsDefined(typeof(PageType), pageTypeByte))
            throw new Core.NovaException(Core.ErrorCode.FileCorrupted, $"Invalid page type: {pageTypeByte}");

        var pageType = (PageType)pageTypeByte;

        // Flags
        var flags = (PageFlags)reader.ReadByte();

        // Reserved
        reader.Advance(2);

        // Lsn
        var lsn = reader.ReadUInt64();

        // DataLength
        var dataLength = reader.ReadUInt32();

        // Reserved
        reader.Advance(4);

        // Checksum
        var checksum = reader.ReadUInt32();

        return new PageHeader
        {
            PageId = pageId,
            PageType = pageType,
            Flags = flags,
            Lsn = lsn,
            Checksum = checksum,
            DataLength = dataLength
        };
    }
}

/// <summary>页类型枚举</summary>
public enum PageType : Byte
{
    /// <summary>空白页</summary>
    Empty = 0,

    /// <summary>数据页</summary>
    Data = 1,

    /// <summary>索引页</summary>
    Index = 2,

    /// <summary>目录页（稀疏索引）</summary>
    Directory = 3,

    /// <summary>元数据页</summary>
    Metadata = 4
}

/// <summary>页级特性标志</summary>
[Flags]
public enum PageFlags : Byte
{
    /// <summary>无特殊标志</summary>
    None = 0,

    /// <summary>页已加密</summary>
    Encrypted = 1,

    /// <summary>页已压缩</summary>
    Compressed = 2,

    /// <summary>脏页（已修改未刷盘）</summary>
    Dirty = 4
}
