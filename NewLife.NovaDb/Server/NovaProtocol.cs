using NewLife.Buffers;
using NewLife.Data;

namespace NewLife.NovaDb.Server;

/// <summary>请求类型</summary>
public enum RequestType : Byte
{
    /// <summary>握手</summary>
    Handshake = 1,

    /// <summary>执行 SQL</summary>
    Execute = 2,

    /// <summary>查询</summary>
    Query = 3,

    /// <summary>获取结果</summary>
    Fetch = 4,

    /// <summary>关闭</summary>
    Close = 5,

    /// <summary>心跳</summary>
    Ping = 6,

    /// <summary>开始事务</summary>
    BeginTx = 7,

    /// <summary>提交事务</summary>
    CommitTx = 8,

    /// <summary>回滚事务</summary>
    RollbackTx = 9
}

/// <summary>响应状态码</summary>
public enum ResponseStatus : Byte
{
    /// <summary>成功</summary>
    Ok = 0,

    /// <summary>错误</summary>
    Error = 1,

    /// <summary>数据行</summary>
    Row = 2,

    /// <summary>数据结束</summary>
    Done = 3
}

/// <summary>协议消息头（固定 16 字节）</summary>
public class ProtocolHeader
{
    /// <summary>协议魔数（0x4E56 = "NV"）</summary>
    public const UInt16 Magic = 0x4E56;

    /// <summary>协议版本</summary>
    public Byte Version { get; set; } = 1;

    /// <summary>请求类型</summary>
    public RequestType RequestType { get; set; }

    /// <summary>序列号（用于请求/响应匹配）</summary>
    public UInt32 SequenceId { get; set; }

    /// <summary>负载长度</summary>
    public Int32 PayloadLength { get; set; }

    /// <summary>响应状态码</summary>
    public ResponseStatus Status { get; set; }

    /// <summary>头部大小（字节）</summary>
    public const Int32 HeaderSize = 16;

    /// <summary>最大负载长度（100MB）</summary>
    public const Int32 MaxPayloadLength = 100 * 1024 * 1024;

    /// <summary>序列化为数据包，使用后需 Dispose 归还到对象池</summary>
    /// <returns>包含 16 字节头部数据的数据包</returns>
    public IOwnerPacket ToPacket()
    {
        var pk = new OwnerPacket(HeaderSize);
        var writer = new SpanWriter(pk) { IsLittleEndian = false };

        // 2B: Magic
        writer.Write(Magic);

        // 1B: Version
        writer.WriteByte(Version);

        // 1B: RequestType
        writer.WriteByte((Byte)RequestType);

        // 4B: SequenceId (big-endian)
        writer.Write(SequenceId);

        // 4B: PayloadLength (big-endian)
        writer.Write(PayloadLength);

        // 1B: Status
        writer.WriteByte((Byte)Status);

        // 3B: Reserved
        writer.FillZero(3);

        return pk;
    }

    /// <summary>从数据包反序列化</summary>
    /// <param name="data">至少 16 字节的数据包</param>
    /// <returns>协议头实例</returns>
    public static ProtocolHeader Read(IPacket data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (data.Length < HeaderSize)
            throw new ArgumentException($"Buffer must be at least {HeaderSize} bytes", nameof(data));

        var reader = new SpanReader(data) { IsLittleEndian = false };

        var magic = reader.ReadUInt16();
        if (magic != Magic)
            throw new InvalidOperationException($"Invalid magic number: 0x{magic:X4}, expected 0x{Magic:X4}");

        var header = new ProtocolHeader
        {
            Version = reader.ReadByte(),
            RequestType = (RequestType)reader.ReadByte(),
            SequenceId = reader.ReadUInt32(),
            PayloadLength = reader.ReadInt32(),
            Status = (ResponseStatus)reader.ReadByte()
        };

        if (header.PayloadLength < 0 || header.PayloadLength > MaxPayloadLength)
            throw new InvalidOperationException($"Payload length {header.PayloadLength} exceeds maximum {MaxPayloadLength}");

        return header;
    }
}
