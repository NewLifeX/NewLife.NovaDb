namespace NewLife.NovaDb.Storage;

/// <summary>页头结构（每个页的开头，固定 32 字节）</summary>
/// <remarks>
/// 页头布局（32 字节）：
/// - 0-7: PageId (页 ID)
/// - 8: PageType (0=Empty, 1=Data, 2=Index, 3=Directory, 4=Metadata)
/// - 9-11: Reserved (预留)
/// - 12-19: LSN (日志序列号)
/// - 20-23: Checksum (CRC32 校验和)
/// - 24-27: DataLength (页内有效数据长度)
/// - 28-31: Reserved (预留扩展)
/// </remarks>
public class PageHeader
{
    /// <summary>页头固定大小（32 字节）</summary>
    public const Int32 HeaderSize = 32;

    /// <summary>页 ID</summary>
    public UInt64 PageId { get; set; }

    /// <summary>页类型</summary>
    public PageType PageType { get; set; }

    /// <summary>日志序列号（LSN）</summary>
    public UInt64 Lsn { get; set; }

    /// <summary>校验和（CRC32）</summary>
    public UInt32 Checksum { get; set; }

    /// <summary>页内有效数据长度</summary>
    public UInt32 DataLength { get; set; }

    /// <summary>序列化为字节数组（固定 32 字节）</summary>
    /// <returns>32 字节的页头数据</returns>
    public Byte[] ToBytes()
    {
        var buffer = new Byte[HeaderSize];
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

        // Reserved (4 bytes) - 自动为 0

        return buffer;
    }

    /// <summary>从字节数组反序列化</summary>
    /// <param name="buffer">包含页头数据的字节数组（至少 32 字节）</param>
    /// <returns>反序列化的页头对象</returns>
    /// <exception cref="ArgumentNullException">buffer 为 null</exception>
    /// <exception cref="ArgumentException">buffer 长度不足 32 字节</exception>
    /// <exception cref="Core.NovaException">页类型无效</exception>
    public static PageHeader FromBytes(Byte[] buffer)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));

        if (buffer.Length < HeaderSize)
            throw new ArgumentException($"Buffer too short for PageHeader, expected {HeaderSize} bytes, got {buffer.Length}", nameof(buffer));

        var offset = 0;

        // PageId
        var pageId = BitConverter.ToUInt64(buffer, offset);
        offset += 8;

        // PageType 验证
        var pageTypeByte = buffer[offset++];
        if (!Enum.IsDefined(typeof(PageType), pageTypeByte))
            throw new Core.NovaException(Core.ErrorCode.FileCorrupted, $"Invalid page type: {pageTypeByte}");

        var pageType = (PageType)pageTypeByte;

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
public enum PageType : Byte
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
