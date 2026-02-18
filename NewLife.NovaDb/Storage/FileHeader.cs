namespace NewLife.NovaDb.Storage;

/// <summary>文件头结构（每个 .data/.idx/.wal 文件的开头，固定 32 字节）</summary>
/// <remarks>
/// 文件头布局（32 字节）：
/// - 0-3: Magic Number (0x4E4F5641 "NOVA")
/// - 4-5: Version (当前版本 1)
/// - 6: FileType (1=Data, 2=Index, 3=Wal)
/// - 7: Reserved
/// - 8-11: PageSize (页大小，字节)
/// - 12-19: CreatedAt (创建时间，UTC Ticks)
/// - 20-23: OptionsHash (配置哈希)
/// - 24-31: Reserved (预留扩展)
/// </remarks>
public class FileHeader
{
    /// <summary>魔数标识（固定 "NOVA" 0x4E4F5641）</summary>
    public const UInt32 MagicNumber = 0x4E4F5641;

    /// <summary>文件头固定大小（32 字节）</summary>
    public const Int32 HeaderSize = 32;

    /// <summary>文件格式版本号</summary>
    public UInt16 Version { get; set; } = 1;

    /// <summary>文件类型（Data/Index/Wal）</summary>
    public FileType FileType { get; set; }

    /// <summary>页大小（字节）</summary>
    public UInt32 PageSize { get; set; }

    /// <summary>创建时间（UTC Ticks）</summary>
    public Int64 CreatedAt { get; set; }

    /// <summary>配置哈希（用于验证配置一致性）</summary>
    public UInt32 OptionsHash { get; set; }

    /// <summary>序列化为字节数组（固定 32 字节）</summary>
    /// <returns>32 字节的文件头数据</returns>
    public Byte[] ToBytes()
    {
        var buffer = new Byte[HeaderSize];
        var offset = 0;

        // Magic Number (4 bytes)
        Buffer.BlockCopy(BitConverter.GetBytes(MagicNumber), 0, buffer, offset, 4);
        offset += 4;

        // Version (2 bytes)
        Buffer.BlockCopy(BitConverter.GetBytes(Version), 0, buffer, offset, 2);
        offset += 2;

        // FileType (1 byte)
        buffer[offset++] = (Byte)FileType;

        // Reserved (1 byte)
        offset += 1;

        // PageSize (4 bytes)
        Buffer.BlockCopy(BitConverter.GetBytes(PageSize), 0, buffer, offset, 4);
        offset += 4;

        // CreatedAt (8 bytes)
        Buffer.BlockCopy(BitConverter.GetBytes(CreatedAt), 0, buffer, offset, 8);
        offset += 8;

        // OptionsHash (4 bytes)
        Buffer.BlockCopy(BitConverter.GetBytes(OptionsHash), 0, buffer, offset, 4);
        offset += 4;

        // Reserved (8 bytes) - 自动为 0

        return buffer;
    }

    /// <summary>从字节数组反序列化</summary>
    /// <param name="buffer">包含文件头数据的字节数组（至少 32 字节）</param>
    /// <returns>反序列化的文件头对象</returns>
    /// <exception cref="ArgumentNullException">buffer 为 null</exception>
    /// <exception cref="ArgumentException">buffer 长度不足 32 字节</exception>
    /// <exception cref="Core.NovaException">魔数验证失败或文件类型无效</exception>
    public static FileHeader FromBytes(Byte[] buffer)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));

        if (buffer.Length < HeaderSize)
            throw new ArgumentException($"Buffer too short for FileHeader, expected {HeaderSize} bytes, got {buffer.Length}", nameof(buffer));

        var offset = 0;

        // Magic Number 验证
        var magic = BitConverter.ToUInt32(buffer, offset);
        offset += 4;
        if (magic != MagicNumber)
            throw new Core.NovaException(Core.ErrorCode.FileCorrupted, $"Invalid magic number: 0x{magic:X8}, expected 0x{MagicNumber:X8}");

        // Version
        var version = BitConverter.ToUInt16(buffer, offset);
        offset += 2;

        // FileType
        var fileTypeByte = buffer[offset++];
        if (!Enum.IsDefined(typeof(FileType), fileTypeByte))
            throw new Core.NovaException(Core.ErrorCode.FileCorrupted, $"Invalid file type: {fileTypeByte}");

        var fileType = (FileType)fileTypeByte;

        // Reserved
        offset += 1;

        // PageSize 验证
        var pageSize = BitConverter.ToUInt32(buffer, offset);
        offset += 4;
        if (pageSize == 0 || pageSize > 64 * 1024)
            throw new Core.NovaException(Core.ErrorCode.FileCorrupted, $"Invalid page size: {pageSize}, must be between 1 and 65536");

        // CreatedAt
        var createdAt = BitConverter.ToInt64(buffer, offset);
        offset += 8;

        // OptionsHash
        var optionsHash = BitConverter.ToUInt32(buffer, offset);

        return new FileHeader
        {
            Version = version,
            FileType = fileType,
            PageSize = pageSize,
            CreatedAt = createdAt,
            OptionsHash = optionsHash
        };
    }
}

/// <summary>
/// 文件类型枚举
/// </summary>
public enum FileType : Byte
{
    /// <summary>
    /// 数据文件 (.data)
    /// </summary>
    Data = 1,

    /// <summary>
    /// 索引文件 (.idx)
    /// </summary>
    Index = 2,

    /// <summary>
    /// WAL 日志文件 (.wal)
    /// </summary>
    Wal = 3
}
