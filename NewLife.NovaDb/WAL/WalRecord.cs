using NewLife.Buffers;
using NewLife.Serialization;

namespace NewLife.NovaDb.WAL;

/// <summary>WAL 记录类型</summary>
public enum WalRecordType : Byte
{
    /// <summary>开始事务</summary>
    BeginTx = 1,

    /// <summary>更新页</summary>
    UpdatePage = 2,

    /// <summary>提交事务</summary>
    CommitTx = 3,

    /// <summary>回滚事务</summary>
    AbortTx = 4,

    /// <summary>检查点</summary>
    Checkpoint = 5
}

/// <summary>WAL 记录头，仅描述元信息，不携带负载数据以减少内存分配</summary>
/// <remarks>
/// 头部布局（37 字节）：
/// - 0-7: LSN (日志序列号)
/// - 8-15: TxId (事务 ID)
/// - 16: RecordType (记录类型)
/// - 17-24: PageId (页 ID)
/// - 25-28: DataLength (负载长度)
/// - 29-36: Timestamp (时间戳)
/// </remarks>
public class WalRecord : ISpanSerializable
{
    /// <summary>头部固定大小（37 字节）</summary>
    public const Int32 HeaderSize = 37;

    /// <summary>日志序列号（LSN）</summary>
    public UInt64 Lsn { get; set; }

    /// <summary>事务 ID</summary>
    public UInt64 TxId { get; set; }

    /// <summary>记录类型</summary>
    public WalRecordType RecordType { get; set; }

    /// <summary>页 ID（仅用于 UpdatePage）</summary>
    public UInt64 PageId { get; set; }

    /// <summary>负载数据长度（数据紧跟头部之后存储，头部本身不持有负载）</summary>
    public Int32 DataLength { get; set; }

    /// <summary>时间戳</summary>
    public Int64 Timestamp { get; set; }

    /// <summary>将头部成员序列化写入 SpanWriter</summary>
    /// <param name="writer">Span 写入器</param>
    public void Write(ref SpanWriter writer)
    {
        writer.Write(Lsn);
        writer.Write(TxId);
        writer.WriteByte((Byte)RecordType);
        writer.Write(PageId);
        writer.Write(DataLength);
        writer.Write(Timestamp);
    }

    /// <summary>从 SpanReader 反序列化读取头部成员</summary>
    /// <param name="reader">Span 读取器</param>
    public void Read(ref SpanReader reader)
    {
        Lsn = reader.ReadUInt64();
        TxId = reader.ReadUInt64();
        RecordType = (WalRecordType)reader.ReadByte();
        PageId = reader.ReadUInt64();
        DataLength = reader.ReadInt32();
        Timestamp = reader.ReadInt64();
    }
}
