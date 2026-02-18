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

    /// <summary>序列化为字节数组</summary>
    /// <returns>16 字节的头部数据</returns>
    public Byte[] ToBytes()
    {
        var buffer = new Byte[HeaderSize];

        // 2B: Magic
        buffer[0] = (Byte)(Magic >> 8);
        buffer[1] = (Byte)(Magic & 0xFF);

        // 1B: Version
        buffer[2] = Version;

        // 1B: RequestType
        buffer[3] = (Byte)RequestType;

        // 4B: SequenceId (big-endian)
        buffer[4] = (Byte)(SequenceId >> 24);
        buffer[5] = (Byte)(SequenceId >> 16);
        buffer[6] = (Byte)(SequenceId >> 8);
        buffer[7] = (Byte)(SequenceId & 0xFF);

        // 4B: PayloadLength (big-endian)
        buffer[8] = (Byte)(PayloadLength >> 24);
        buffer[9] = (Byte)(PayloadLength >> 16);
        buffer[10] = (Byte)(PayloadLength >> 8);
        buffer[11] = (Byte)(PayloadLength & 0xFF);

        // 1B: Status
        buffer[12] = (Byte)Status;

        // 3B: Reserved (already zeroed)

        return buffer;
    }

    /// <summary>从字节数组反序列化</summary>
    /// <param name="buffer">至少 16 字节的数据</param>
    /// <returns>协议头实例</returns>
    public static ProtocolHeader FromBytes(Byte[] buffer)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (buffer.Length < HeaderSize)
            throw new ArgumentException($"Buffer must be at least {HeaderSize} bytes", nameof(buffer));

        var magic = (UInt16)((buffer[0] << 8) | buffer[1]);
        if (magic != Magic)
            throw new InvalidOperationException($"Invalid magic number: 0x{magic:X4}, expected 0x{Magic:X4}");

        var header = new ProtocolHeader
        {
            Version = buffer[2],
            RequestType = (RequestType)buffer[3],
            SequenceId = (UInt32)((buffer[4] << 24) | (buffer[5] << 16) | (buffer[6] << 8) | buffer[7]),
            PayloadLength = (buffer[8] << 24) | (buffer[9] << 16) | (buffer[10] << 8) | buffer[11],
            Status = (ResponseStatus)buffer[12]
        };

        if (header.PayloadLength < 0 || header.PayloadLength > MaxPayloadLength)
            throw new InvalidOperationException($"Payload length {header.PayloadLength} exceeds maximum {MaxPayloadLength}");

        return header;
    }
}
