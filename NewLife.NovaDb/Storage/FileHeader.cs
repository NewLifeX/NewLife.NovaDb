using NewLife.Buffers;
using NewLife.Data;

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

    /// <summary>序列化为数据包（固定 32 字节），使用后需 Dispose 归还到对象池</summary>
    /// <returns>包含 32 字节文件头数据的数据包</returns>
    public IOwnerPacket ToPacket()
    {
        var pk = new OwnerPacket(HeaderSize);
        var writer = new SpanWriter(pk);

        // Magic Number (4 bytes)
        writer.Write(MagicNumber);

        // Version (2 bytes)
        writer.Write(Version);

        // FileType (1 byte)
        writer.WriteByte((Byte)FileType);

        // Reserved (1 byte)
        writer.WriteByte(0);

        // PageSize (4 bytes)
        writer.Write(PageSize);

        // CreatedAt (8 bytes)
        writer.Write(CreatedAt);

        // OptionsHash (4 bytes)
        writer.Write(OptionsHash);

        // Reserved (8 bytes) - 显式填零
        writer.FillZero(8);

        return pk;
    }

    /// <summary>从数据包反序列化</summary>
    /// <param name="data">包含文件头数据的数据包（至少 32 字节）</param>
    /// <returns>反序列化的文件头对象</returns>
    /// <exception cref="ArgumentNullException">data 为 null</exception>
    /// <exception cref="ArgumentException">data 长度不足 32 字节</exception>
    /// <exception cref="Core.NovaException">魔数验证失败或文件类型无效</exception>
    public static FileHeader Read(IPacket data)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        if (data.Length < HeaderSize)
            throw new ArgumentException($"Buffer too short for FileHeader, expected {HeaderSize} bytes, got {data.Length}", nameof(data));

        var reader = new SpanReader(data);

        // Magic Number 验证
        var magic = reader.ReadUInt32();
        if (magic != MagicNumber)
            throw new Core.NovaException(Core.ErrorCode.FileCorrupted, $"Invalid magic number: 0x{magic:X8}, expected 0x{MagicNumber:X8}");

        // Version
        var version = reader.ReadUInt16();

        // FileType
        var fileTypeByte = reader.ReadByte();
        if (!Enum.IsDefined(typeof(FileType), fileTypeByte))
            throw new Core.NovaException(Core.ErrorCode.FileCorrupted, $"Invalid file type: {fileTypeByte}");

        var fileType = (FileType)fileTypeByte;

        // Reserved
        reader.Advance(1);

        // PageSize 验证
        var pageSize = reader.ReadUInt32();
        if (pageSize == 0 || pageSize > 64 * 1024)
            throw new Core.NovaException(Core.ErrorCode.FileCorrupted, $"Invalid page size: {pageSize}, must be between 1 and 65536");

        // CreatedAt
        var createdAt = reader.ReadInt64();

        // OptionsHash
        var optionsHash = reader.ReadUInt32();

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
