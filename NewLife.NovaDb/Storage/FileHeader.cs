using NewLife.Buffers;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.Security;

namespace NewLife.NovaDb.Storage;

/// <summary>文件头结构（每个 .data/.idx/.wal 文件的开头，固定 32 字节）</summary>
/// <remarks>
/// 文件头布局（32 字节）：
/// - 0-3: Magic Number (0x41564F4E，小端写入磁盘即 "NOVA" 4E 4F 56 41)
/// - 4: Version (当前版本 1)
/// - 5: FileType (1=Data, 2=Index, 3=Wal, 4=Binlog, 5=Metadata)
/// - 6: PageSizeShift (页大小位移，实际页大小 = 1 &lt;&lt; shift)
/// - 7: Flags (特性标志：bit0=加密, bit1=压缩, bit2=只读)
/// - 8-15: CreateTime (创建时间，UTC 毫秒)
/// - 16-27: Reserved (预留扩展)
/// - 28-31: Checksum (CRC32 校验和，覆盖 bytes[0-27])
/// </remarks>
public class FileHeader
{
    /// <summary>魔数标识（小端写入磁盘 = "NOVA" 0x4E 0x4F 0x56 0x41）</summary>
    public const UInt32 MagicNumber = 0x41564F4E;

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

    /// <summary>文件头校验和（CRC32，覆盖 bytes[0-27]）</summary>
    public UInt32 Checksum { get; set; }

    /// <summary>将文件头序列化到指定的 Span（必须至少 32 字节）</summary>
    /// <param name="span">目标 Span，至少 32 字节</param>
    /// <exception cref="ArgumentException">span 长度小于 32 字节</exception>
    public void Write(Span<Byte> span)
    {
        if (span.Length < HeaderSize)
            throw new ArgumentException($"Span too short for FileHeader, expected {HeaderSize} bytes, got {span.Length}", nameof(span));

        var writer = new SpanWriter(span);

        // Magic Number (4 bytes) - 小端写入，MagicNumber=0x41564F4E，磁盘字节 = 4E 4F 56 41 = "NOVA"
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

        // Reserved (12 bytes) - 预留扩展
        writer.FillZero(12);

        // 计算 Checksum（CRC32 of bytes[0-27]）
        var crc = Crc32.Compute(span[..28]);

        // Checksum (4 bytes)
        writer.Write(crc);
    }

    /// <summary>序列化为数据包（固定 32 字节），使用后需 Dispose 归还到对象池</summary>
    /// <returns>包含 32 字节文件头数据的数据包</returns>
    public IOwnerPacket ToPacket()
    {
        var pk = new OwnerPacket(HeaderSize);
        Write(pk.GetSpan());
        return pk;
    }

    /// <summary>从 Span 反序列化文件头</summary>
    /// <param name="span">包含文件头数据的 Span（至少 32 字节）</param>
    /// <returns>反序列化的文件头对象</returns>
    /// <exception cref="ArgumentException">span 长度不足 32 字节</exception>
    /// <exception cref="NovaException">魔数验证失败、文件类型无效或校验和验证失败</exception>
    public static FileHeader Read(ReadOnlySpan<Byte> span)
    {
        if (span.Length < HeaderSize)
            throw new ArgumentException($"Buffer too short for FileHeader, expected {HeaderSize} bytes, got {span.Length}", nameof(span));

        var reader = new SpanReader(span);

        // Magic Number 验证 - 小端读取，MagicNumber=0x41564F4E，对应磁盘 "NOVA"
        var magic = reader.ReadUInt32();
        if (magic != MagicNumber)
            throw new NovaException(ErrorCode.FileCorrupted, $"Invalid magic number: 0x{magic:X8}, expected 0x{MagicNumber:X8}");

        // Version
        var version = reader.ReadByte();

        // FileType
        var fileTypeByte = reader.ReadByte();
        if (!Enum.IsDefined(typeof(FileType), fileTypeByte))
            throw new NovaException(ErrorCode.FileCorrupted, $"Invalid file type: {fileTypeByte}");

        var fileType = (FileType)fileTypeByte;

        // PageSizeShift 验证（最大 2^24 = 16MB）
        var pageSizeShift = reader.ReadByte();
        if (pageSizeShift > 24)
            throw new NovaException(ErrorCode.FileCorrupted, $"Invalid page size shift: {pageSizeShift}, must be 0-24");

        var pageSize = 1u << pageSizeShift;

        // Flags
        var flags = (FileFlags)reader.ReadByte();

        // CreateTime
        var createTimeMs = reader.ReadInt64();

        // Reserved (12 bytes)
        reader.Advance(12);

        // Checksum 验证
        var storedChecksum = reader.ReadUInt32();
        var computedChecksum = Crc32.Compute(span[..28]);
        if (storedChecksum != computedChecksum)
            throw new NovaException(ErrorCode.ChecksumFailed, $"FileHeader checksum mismatch: stored=0x{storedChecksum:X8}, computed=0x{computedChecksum:X8}");

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

    /// <summary>从数据包反序列化</summary>
    /// <param name="data">包含文件头数据的数据包（至少 32 字节）</param>
    /// <returns>反序列化的文件头对象</returns>
    /// <exception cref="ArgumentNullException">data 为 null</exception>
    /// <exception cref="ArgumentException">data 长度不足 32 字节</exception>
    /// <exception cref="NovaException">魔数验证失败或文件类型无效</exception>
    public static FileHeader Read(IPacket data)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        return Read(data.GetSpan());
    }

    /// <summary>从文件路径直接读取文件头（32 字节）</summary>
    /// <param name="fileName">文件路径</param>
    /// <returns>反序列化的文件头对象</returns>
    /// <exception cref="ArgumentNullException">fileName 为 null</exception>
    /// <exception cref="NovaException">文件过短、不是 NOVA 文件、文件类型无效或校验和验证失败</exception>
    public static FileHeader Read(String fileName)
    {
        if (fileName == null) throw new ArgumentNullException(nameof(fileName));

        using var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < HeaderSize)
            throw new NovaException(ErrorCode.FileCorrupted, $"文件过短，无法读取文件头（期望 {HeaderSize} 字节，实际 {fs.Length} 字节）: {fileName}");

        var buf = new Byte[HeaderSize];
        if (fs.Read(buf, 0, HeaderSize) < HeaderSize)
            throw new NovaException(ErrorCode.FileCorrupted, $"读取文件头失败，数据不足: {fileName}");

        return Read(buf);
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
    Binlog = 4,

    /// <summary>数据库元数据文件 (nova.db)</summary>
    Metadata = 5,

    /// <summary>KV 数据文件 (.kvd)</summary>
    KvData = 6,
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
