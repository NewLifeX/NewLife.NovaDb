using NewLife.Buffers;
using NewLife.Data;
using NewLife.Security;

namespace NewLife.NovaDb.Storage;

/// <summary>文件头结构（每个 .data/.idx/.wal 文件的开头，固定 32 字节）</summary>
/// <remarks>
/// 文件头布局（32 字节）：
/// - 0-3: Magic Number (0x4E4F5641 "NOVA")
/// - 4: Version (当前版本 1)
/// - 5: FileType (1=Data, 2=Index, 3=Wal, 4=Binlog)
/// - 6: PageSizeShift (页大小位移，实际页大小 = 1 &lt;&lt; shift)
/// - 7: Flags (特性标志：bit0=加密, bit1=压缩, bit2=只读)
/// - 8-15: CreateTime (创建时间，UTC 毫秒)
/// - 16-19: Checksum (CRC32 校验和，覆盖 bytes[0-15])
/// - 20-31: Reserved (预留扩展)
/// </remarks>
public class FileHeader
{
    /// <summary>魔数标识（固定 "NOVA" 0x4E4F5641）</summary>
    public const UInt32 MagicNumber = 0x4E4F5641;

    /// <summary>文件头固定大小（32 字节）</summary>
    public const Int32 HeaderSize = 32;

    /// <summary>文件格式版本号</summary>
    public Byte Version { get; set; } = 1;

    /// <summary>文件类型（Data/Index/Wal）</summary>
    public FileType FileType { get; set; }

    /// <summary>页大小（字节）</summary>
    public UInt32 PageSize { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreateTime { get; set; }

    /// <summary>特性标志。bit0=加密, bit1=压缩, bit2=只读，其余位预留</summary>
    public FileFlags Flags { get; set; }

    /// <summary>文件头校验和（CRC32，覆盖 bytes[0-15]）</summary>
    public UInt32 Checksum { get; set; }

    /// <summary>序列化为数据包（固定 32 字节），使用后需 Dispose 归还到对象池</summary>
    /// <returns>包含 32 字节文件头数据的数据包</returns>
    public IOwnerPacket ToPacket()
    {
        var pk = new OwnerPacket(HeaderSize);
        var writer = new SpanWriter(pk);

        // Magic Number (4 bytes)
        writer.Write(MagicNumber);

        // Version (1 byte)
        writer.WriteByte(Version);

        // FileType (1 byte)
        writer.WriteByte((Byte)FileType);

        // PageSizeShift (1 byte)
        writer.WriteByte(GetPageSizeShift(PageSize));

        // Flags (1 byte)
        writer.WriteByte((Byte)Flags);

        // CreateTime (8 bytes, UTC 毫秒)
        writer.Write(CreateTime.ToUniversalTime().ToLong());

        // 计算 Checksum（CRC32 of bytes[0-15]）
        var span = pk.GetSpan();
        var crc = Crc32.Compute(span[..16]);

        // Checksum (4 bytes)
        writer.Write(crc);

        // Reserved (12 bytes) - 预留扩展
        writer.FillZero(12);

        return pk;
    }

    /// <summary>从数据包反序列化</summary>
    /// <param name="data">包含文件头数据的数据包（至少 32 字节）</param>
    /// <returns>反序列化的文件头对象</returns>
    /// <exception cref="ArgumentNullException">data 为 null</exception>
    /// <exception cref="ArgumentException">data 长度不足 16 字节</exception>
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
        var version = reader.ReadByte();

        // FileType
        var fileTypeByte = reader.ReadByte();
        if (!Enum.IsDefined(typeof(FileType), fileTypeByte))
            throw new Core.NovaException(Core.ErrorCode.FileCorrupted, $"Invalid file type: {fileTypeByte}");

        var fileType = (FileType)fileTypeByte;

        // PageSizeShift 验证（最大 2^24 = 16MB）
        var pageSizeShift = reader.ReadByte();
        if (pageSizeShift > 24)
            throw new Core.NovaException(Core.ErrorCode.FileCorrupted, $"Invalid page size shift: {pageSizeShift}, must be 0-24");

        var pageSize = 1u << pageSizeShift;

        // Flags
        var flags = (FileFlags)reader.ReadByte();

        // CreateTime
        var createTimeMs = reader.ReadInt64();

        // Checksum 验证
        var storedChecksum = reader.ReadUInt32();
        var span = data.GetSpan();
        var computedChecksum = Crc32.Compute(span[..16]);
        if (storedChecksum != computedChecksum)
            throw new Core.NovaException(Core.ErrorCode.ChecksumFailed, $"FileHeader checksum mismatch: stored=0x{storedChecksum:X8}, computed=0x{computedChecksum:X8}");

        return new FileHeader
        {
            Version = version,
            FileType = fileType,
            PageSize = pageSize,
            Flags = flags,
            CreateTime = createTimeMs.ToDateTime().ToLocalTime(),
            Checksum = storedChecksum
        };
    }

    /// <summary>计算页大小的位移值（页大小必须为 2 的幂次）</summary>
    /// <param name="pageSize">页大小（字节）</param>
    /// <returns>位移值，即 1 &lt;&lt; shift = pageSize</returns>
    private static Byte GetPageSizeShift(UInt32 pageSize)
    {
        if (pageSize == 0 || (pageSize & (pageSize - 1)) != 0)
            throw new ArgumentException($"PageSize must be a power of 2, got {pageSize}", nameof(pageSize));

        Byte shift = 0;
        var v = pageSize;
        while (v > 1) { v >>= 1; shift++; }
        return shift;
    }
}

/// <summary>文件类型枚举</summary>
public enum FileType : Byte
{
    /// <summary>数据文件 (.data)</summary>
    Data = 1,

    /// <summary>索引文件 (.idx)</summary>
    Index = 2,

    /// <summary>WAL 日志文件 (.wal)</summary>
    Wal = 3,

    /// <summary>Binlog 逻辑日志文件（主从同步/增量备份）</summary>
    Binlog = 4
}

/// <summary>文件特性标志</summary>
[Flags]
public enum FileFlags : Byte
{
    /// <summary>无特殊标志</summary>
    None = 0,

    /// <summary>文件已加密</summary>
    Encrypted = 1,

    /// <summary>文件已压缩</summary>
    Compressed = 2,

    /// <summary>文件只读</summary>
    ReadOnly = 4
}
