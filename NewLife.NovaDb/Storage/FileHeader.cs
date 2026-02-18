namespace NewLife.NovaDb.Storage;

/// <summary>
/// 文件头结构（每个 .data/.idx/.wal 文件的开头）
/// </summary>
public class FileHeader
{
    /// <summary>
    /// 魔数标识（固定 "NOVA" 0x4E4F5641）
    /// </summary>
    public const UInt32 MagicNumber = 0x4E4F5641;

    /// <summary>
    /// 文件格式版本号
    /// </summary>
    public UInt16 Version { get; set; } = 1;

    /// <summary>
    /// 文件类型（Data/Index/Wal）
    /// </summary>
    public FileType FileType { get; set; }

    /// <summary>
    /// 页大小（字节）
    /// </summary>
    public UInt32 PageSize { get; set; }

    /// <summary>
    /// 创建时间（UTC Ticks）
    /// </summary>
    public Int64 CreatedAt { get; set; }

    /// <summary>
    /// 配置哈希（用于验证配置一致性）
    /// </summary>
    public UInt32 OptionsHash { get; set; }

    /// <summary>
    /// 序列化为字节数组（固定 32 字节）
    /// </summary>
    public Byte[] ToBytes()
    {
        var buffer = new Byte[32];
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

        // Reserved (8 bytes)
        // offset += 8;

        return buffer;
    }

    /// <summary>
    /// 从字节数组反序列化
    /// </summary>
    public static FileHeader FromBytes(Byte[] buffer)
    {
        if (buffer.Length < 32)
        {
            throw new ArgumentException("Buffer too short for FileHeader");
        }

        var offset = 0;

        // Magic Number
        var magic = BitConverter.ToUInt32(buffer, offset);
        offset += 4;
        if (magic != MagicNumber)
        {
            throw new Core.NovaDbException(Core.ErrorCode.FileCorrupted,
                $"Invalid magic number: 0x{magic:X8}");
        }

        // Version
        var version = BitConverter.ToUInt16(buffer, offset);
        offset += 2;

        // FileType
        var fileType = (FileType)buffer[offset++];

        // Reserved
        offset += 1;

        // PageSize
        var pageSize = BitConverter.ToUInt32(buffer, offset);
        offset += 4;

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
public enum FileType : byte
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
