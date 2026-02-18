namespace NewLife.NovaDb.Storage;

/// <summary>
/// 页头结构（每个页的开头）
/// </summary>
public class PageHeader
{
    /// <summary>
    /// 页 ID
    /// </summary>
    public UInt64 PageId { get; set; }

    /// <summary>
    /// 页类型
    /// </summary>
    public PageType PageType { get; set; }

    /// <summary>
    /// 日志序列号（LSN）
    /// </summary>
    public UInt64 Lsn { get; set; }

    /// <summary>
    /// 校验和（CRC32）
    /// </summary>
    public UInt32 Checksum { get; set; }

    /// <summary>
    /// 页内有效数据长度
    /// </summary>
    public UInt32 DataLength { get; set; }

    /// <summary>
    /// 序列化为字节数组（固定 32 字节）
    /// </summary>
    public Byte[] ToBytes()
    {
        var buffer = new Byte[32];
        var offset = 0;

        // PageId (8 bytes)
        Buffer.BlockCopy(BitConverter.GetBytes(PageId), 0, buffer, offset, 8);
        offset += 8;

        // PageType (1 byte)
        buffer[offset++] = (Byte)PageType;

        // Reserved (3 bytes)
        offset += 3;

        // Lsn (8 bytes)
        Buffer.BlockCopy(BitConverter.GetBytes(Lsn), 0, buffer, offset, 8);
        offset += 8;

        // Checksum (4 bytes)
        Buffer.BlockCopy(BitConverter.GetBytes(Checksum), 0, buffer, offset, 4);
        offset += 4;

        // DataLength (4 bytes)
        Buffer.BlockCopy(BitConverter.GetBytes(DataLength), 0, buffer, offset, 4);
        offset += 4;

        // Reserved (4 bytes)
        // offset += 4;

        return buffer;
    }

    /// <summary>
    /// 从字节数组反序列化
    /// </summary>
    public static PageHeader FromBytes(Byte[] buffer)
    {
        if (buffer.Length < 32)
        {
            throw new ArgumentException("Buffer too short for PageHeader");
        }

        var offset = 0;

        // PageId
        var pageId = BitConverter.ToUInt64(buffer, offset);
        offset += 8;

        // PageType
        var pageType = (PageType)buffer[offset++];

        // Reserved
        offset += 3;

        // Lsn
        var lsn = BitConverter.ToUInt64(buffer, offset);
        offset += 8;

        // Checksum
        var checksum = BitConverter.ToUInt32(buffer, offset);
        offset += 4;

        // DataLength
        var dataLength = BitConverter.ToUInt32(buffer, offset);

        return new PageHeader
        {
            PageId = pageId,
            PageType = pageType,
            Lsn = lsn,
            Checksum = checksum,
            DataLength = dataLength
        };
    }
}

/// <summary>
/// 页类型枚举
/// </summary>
public enum PageType : byte
{
    /// <summary>
    /// 空白页
    /// </summary>
    Empty = 0,

    /// <summary>
    /// 数据页
    /// </summary>
    Data = 1,

    /// <summary>
    /// 索引页
    /// </summary>
    Index = 2,

    /// <summary>
    /// 目录页（稀疏索引）
    /// </summary>
    Directory = 3,

    /// <summary>
    /// 元数据页
    /// </summary>
    Metadata = 4
}
