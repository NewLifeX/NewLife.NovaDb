namespace NewLife.NovaDb.WAL;

/// <summary>
/// WAL 记录类型
/// </summary>
public enum WalRecordType : byte
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
    public Byte[] Data { get; set; } = Array.Empty<Byte>();

    /// <summary>
    /// 时间戳
    /// </summary>
    public Int64 Timestamp { get; set; }

    /// <summary>
    /// 序列化为字节数组
    /// </summary>
    public Byte[] ToBytes()
    {
        // 头部：LSN(8) + TxId(8) + RecordType(1) + PageId(8) + DataLength(4) + Timestamp(8) = 37 bytes
        var headerSize = 37;
        var buffer = new Byte[headerSize + Data.Length];
        var offset = 0;

        // LSN
        Buffer.BlockCopy(BitConverter.GetBytes(Lsn), 0, buffer, offset, 8);
        offset += 8;

        // TxId
        Buffer.BlockCopy(BitConverter.GetBytes(TxId), 0, buffer, offset, 8);
        offset += 8;

        // RecordType
        buffer[offset++] = (Byte)RecordType;

        // PageId
        Buffer.BlockCopy(BitConverter.GetBytes(PageId), 0, buffer, offset, 8);
        offset += 8;

        // DataLength
        Buffer.BlockCopy(BitConverter.GetBytes(Data.Length), 0, buffer, offset, 4);
        offset += 4;

        // Timestamp
        Buffer.BlockCopy(BitConverter.GetBytes(Timestamp), 0, buffer, offset, 8);
        offset += 8;

        // Data
        if (Data.Length > 0)
        {
            Buffer.BlockCopy(Data, 0, buffer, offset, Data.Length);
        }

        return buffer;
    }

    /// <summary>
    /// 从字节数组反序列化
    /// </summary>
    public static WalRecord FromBytes(Byte[] buffer)
    {
        if (buffer.Length < 37)
        {
            throw new ArgumentException("Buffer too short for WalRecord");
        }

        var offset = 0;

        // LSN
        var lsn = BitConverter.ToUInt64(buffer, offset);
        offset += 8;

        // TxId
        var txId = BitConverter.ToUInt64(buffer, offset);
        offset += 8;

        // RecordType
        var recordType = (WalRecordType)buffer[offset++];

        // PageId
        var pageId = BitConverter.ToUInt64(buffer, offset);
        offset += 8;

        // DataLength
        var dataLength = BitConverter.ToInt32(buffer, offset);
        offset += 4;

        // Timestamp
        var timestamp = BitConverter.ToInt64(buffer, offset);
        offset += 8;

        // Data
        var data = new Byte[dataLength];
        if (dataLength > 0)
        {
            Buffer.BlockCopy(buffer, offset, data, 0, dataLength);
        }

        return new WalRecord
        {
            Lsn = lsn,
            TxId = txId,
            RecordType = recordType,
            PageId = pageId,
            Data = data,
            Timestamp = timestamp
        };
    }
}
