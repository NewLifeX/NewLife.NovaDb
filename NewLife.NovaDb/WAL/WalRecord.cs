using NewLife.Buffers;
using NewLife.Data;

namespace NewLife.NovaDb.WAL;

/// <summary>
/// WAL 记录类型
/// </summary>
public enum WalRecordType : Byte
{
    /// <summary>
    /// 开始事务
    /// </summary>
    BeginTx = 1,

    /// <summary>
    /// 更新页
    /// </summary>
    UpdatePage = 2,

    /// <summary>
    /// 提交事务
    /// </summary>
    CommitTx = 3,

    /// <summary>
    /// 回滚事务
    /// </summary>
    AbortTx = 4,

    /// <summary>
    /// 检查点
    /// </summary>
    Checkpoint = 5
}

/// <summary>
/// WAL 记录
/// </summary>
public class WalRecord
{
    /// <summary>头部固定大小（37 字节）</summary>
    public const Int32 RecordHeaderSize = 37;

    /// <summary>
    /// 日志序列号（LSN）
    /// </summary>
    public UInt64 Lsn { get; set; }

    /// <summary>
    /// 事务 ID
    /// </summary>
    public UInt64 TxId { get; set; }

    /// <summary>
    /// 记录类型
    /// </summary>
    public WalRecordType RecordType { get; set; }

    /// <summary>
    /// 页 ID（仅用于 UpdatePage）
    /// </summary>
    public UInt64 PageId { get; set; }

    /// <summary>
    /// 数据（页数据或其他）
    /// </summary>
    public Byte[] Data { get; set; } = [];

    /// <summary>
    /// 时间戳
    /// </summary>
    public Int64 Timestamp { get; set; }

    /// <summary>序列化为数据包，使用后需 Dispose 归还到对象池</summary>
    /// <returns>包含 WAL 记录数据的数据包</returns>
    public IOwnerPacket ToPacket()
    {
        // 头部：LSN(8) + TxId(8) + RecordType(1) + PageId(8) + DataLength(4) + Timestamp(8) = 37 bytes
        var pk = new OwnerPacket(RecordHeaderSize + Data.Length);
        var writer = new SpanWriter(pk);

        // LSN
        writer.Write(Lsn);

        // TxId
        writer.Write(TxId);

        // RecordType
        writer.WriteByte((Byte)RecordType);

        // PageId
        writer.Write(PageId);

        // DataLength
        writer.Write(Data.Length);

        // Timestamp
        writer.Write(Timestamp);

        // Data
        if (Data.Length > 0)
            writer.Write(Data);

        return pk;
    }

    /// <summary>从数据包反序列化</summary>
    /// <param name="data">包含 WAL 记录数据的数据包</param>
    /// <returns>反序列化的 WAL 记录</returns>
    public static WalRecord Read(IPacket data)
    {
        if (data.Length < RecordHeaderSize)
            throw new ArgumentException("Buffer too short for WalRecord");

        var reader = new SpanReader(data);

        // LSN
        var lsn = reader.ReadUInt64();

        // TxId
        var txId = reader.ReadUInt64();

        // RecordType
        var recordType = (WalRecordType)reader.ReadByte();

        // PageId
        var pageId = reader.ReadUInt64();

        // DataLength
        var dataLength = reader.ReadInt32();

        // Timestamp
        var timestamp = reader.ReadInt64();

        // Data
        var recordData = new Byte[dataLength];
        if (dataLength > 0)
        {
            reader.ReadBytes(dataLength).CopyTo(recordData);
        }

        return new WalRecord
        {
            Lsn = lsn,
            TxId = txId,
            RecordType = recordType,
            PageId = pageId,
            Data = recordData,
            Timestamp = timestamp
        };
    }
}
