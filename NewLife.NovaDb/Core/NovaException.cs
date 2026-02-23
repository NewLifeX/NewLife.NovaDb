namespace NewLife.NovaDb.Core;

/// <summary>NovaDb 异常基类</summary>
public class NovaException : Exception
{
    /// <summary>错误码</summary>
    public ErrorCode Code { get; }

    /// <summary>实例化</summary>
    /// <param name="code">错误码</param>
    /// <param name="message">错误消息</param>
    public NovaException(ErrorCode code, String message) : base(message)
    {
        Code = code;
    }

    /// <summary>实例化</summary>
    /// <param name="code">错误码</param>
    /// <param name="message">错误消息</param>
    /// <param name="innerException">内部异常</param>
    public NovaException(ErrorCode code, String message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }
}

/// <summary>错误码枚举</summary>
public enum ErrorCode
{
    /// <summary>未知错误</summary>
    Unknown = 0,

    /// <summary>文件损坏</summary>
    FileCorrupted = 1000,

    /// <summary>校验失败</summary>
    ChecksumFailed = 1001,

    /// <summary>文件格式不兼容</summary>
    IncompatibleFileFormat = 1002,

    /// <summary>解析失败</summary>
    ParseFailed = 2000,

    /// <summary>SQL 语法错误</summary>
    SyntaxError = 2001,

    /// <summary>事务冲突</summary>
    TransactionConflict = 3000,

    /// <summary>死锁检测</summary>
    Deadlock = 3001,

    /// <summary>事务错误</summary>
    TransactionError = 3002,

    /// <summary>表已存在</summary>
    TableExists = 4000,

    /// <summary>表不存在</summary>
    TableNotFound = 4001,

    /// <summary>主键冲突</summary>
    PrimaryKeyConflict = 4002,

    /// <summary>约束违反</summary>
    ConstraintViolation = 4003,

    /// <summary>唯一约束违反</summary>
    UniqueConstraintViolation = 4009,

    /// <summary>分片未找到</summary>
    ShardNotFound = 4004,

    /// <summary>分片数量超限</summary>
    ShardLimitExceeded = 4005,

    /// <summary>流不存在</summary>
    StreamNotFound = 4006,

    /// <summary>消费组不存在</summary>
    ConsumerGroupNotFound = 4007,

    /// <summary>消息已过期</summary>
    MessageExpired = 4008,

    /// <summary>键不存在</summary>
    KeyNotFound = 4009,

    /// <summary>键已过期</summary>
    KeyExpired = 4010,

    /// <summary>不支持的操作</summary>
    NotSupported = 5000,

    /// <summary>无效参数</summary>
    InvalidArgument = 5001,

    /// <summary>只读模式下禁止写操作</summary>
    ReadOnlyViolation = 5002,

    /// <summary>操作超时</summary>
    Timeout = 5003,

    /// <summary>I/O 错误</summary>
    IoError = 6000,

    /// <summary>磁盘空间不足</summary>
    DiskFull = 6001,

    /// <summary>文件被其他进程锁定</summary>
    FileLocked = 6002,

    /// <summary>连接失败</summary>
    ConnectionFailed = 7000,

    /// <summary>认证失败</summary>
    AuthenticationFailed = 7001,

    /// <summary>协议错误</summary>
    ProtocolError = 7002,

    /// <summary>会话过期</summary>
    SessionExpired = 7003,

    /// <summary>复制错误</summary>
    ReplicationError = 8000,

    /// <summary>节点不存在</summary>
    NodeNotFound = 8001,

    /// <summary>非主节点</summary>
    NotMaster = 8002,

    /// <summary>复制延迟过大</summary>
    ReplicationLag = 8003,

    /// <summary>数据库已存在</summary>
    DatabaseExists = 9000,

    /// <summary>数据库不存在</summary>
    DatabaseNotFound = 9001
}
