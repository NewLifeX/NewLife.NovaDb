namespace NewLife.NovaDb.Core;

/// <summary>
/// NovaDb 异常基类
/// </summary>
public class NovaDbException : Exception
{
    /// <summary>
    /// 错误码
    /// </summary>
    public ErrorCode Code { get; }

    public NovaDbException(ErrorCode code, String message) : base(message)
    {
        Code = code;
    }

    public NovaDbException(ErrorCode code, String message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }
}

/// <summary>
/// 错误码枚举
/// </summary>
public enum ErrorCode
{
    /// <summary>
    /// 未知错误
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// 文件损坏
    /// </summary>
    FileCorrupted = 1000,

    /// <summary>
    /// 校验失败
    /// </summary>
    ChecksumFailed = 1001,

    /// <summary>
    /// 文件格式不兼容
    /// </summary>
    IncompatibleFileFormat = 1002,

    /// <summary>
    /// 解析失败
    /// </summary>
    ParseFailed = 2000,

    /// <summary>
    /// SQL 语法错误
    /// </summary>
    SyntaxError = 2001,

    /// <summary>
    /// 事务冲突
    /// </summary>
    TransactionConflict = 3000,

    /// <summary>
    /// 死锁检测
    /// </summary>
    Deadlock = 3001,

    /// <summary>
    /// 表已存在
    /// </summary>
    TableExists = 4000,

    /// <summary>
    /// 表不存在
    /// </summary>
    TableNotFound = 4001,

    /// <summary>
    /// 主键冲突
    /// </summary>
    PrimaryKeyConflict = 4002,

    /// <summary>
    /// 约束违反
    /// </summary>
    ConstraintViolation = 4003,

    /// <summary>
    /// 不支持的操作
    /// </summary>
    NotSupported = 5000,

    /// <summary>
    /// 无效参数
    /// </summary>
    InvalidArgument = 5001,

    /// <summary>
    /// I/O 错误
    /// </summary>
    IoError = 6000,

    /// <summary>
    /// 磁盘空间不足
    /// </summary>
    DiskFull = 6001
}
