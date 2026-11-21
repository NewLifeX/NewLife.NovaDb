namespace NewLife.NovaDb.Core;

/// <summary>NovaDb 数据库配置选项</summary>
public class DbOptions
{
    /// <summary>数据库路径（文件夹即数据库）</summary>
    public String Path { get; set; } = String.Empty;

    /// <summary>WAL 模式：FULL(同步)/NORMAL(异步1s)/NONE(全异步)</summary>
    public WalMode WalMode { get; set; } = WalMode.Normal;

    /// <summary>页大小（字节），默认 4KB</summary>
    public Int32 PageSize { get; set; } = 4096;

    /// <summary>热数据窗口（秒），默认 600 秒（10 分钟）</summary>
    public Int32 HotWindowSeconds { get; set; } = 600;

    /// <summary>冷数据淘汰阈值（秒），默认 1800 秒（30 分钟）</summary>
    public Int32 ColdEvictionSeconds { get; set; } = 1800;

    /// <summary>分片大小阈值（字节），默认 1GB</summary>
    public Int64 ShardSizeThreshold { get; set; } = 1024L * 1024 * 1024;

    /// <summary>分片行数阈值，默认 1000 万行</summary>
    public Int64 ShardRowThreshold { get; set; } = 10_000_000;

    /// <summary>是否启用校验和验证</summary>
    public Boolean EnableChecksum { get; set; } = true;

    /// <summary>页缓存大小（页数），默认 1024 页</summary>
    public Int32 PageCacheSize { get; set; } = 1024;

    /// <summary>索引刷盘间隔（毫秒），默认 1000 毫秒；0 表示每次提交都刷盘</summary>
    /// <remarks>
    /// 索引是行日志的派生数据，崩溃时可按行日志重建，因此无需每次提交都强制刷盘。
    /// 间隔越大写入吞吐越高，崩溃后需要重建的尾部越长。
    /// </remarks>
    public Int32 IndexFlushIntervalMs { get; set; } = 1000;

    /// <summary>表文件巡检间隔（毫秒），默认 200 毫秒；0 表示每次操作都检查</summary>
    /// <remarks>运维在运行期删除或移走表的索引/数据文件后，引擎按此间隔感知并降级</remarks>
    public Int32 FileWatchIntervalMs { get; set; } = 200;

    /// <summary>时序分片粒度（小时），默认 1 小时</summary>
    public Int32 FluxPartitionHours { get; set; } = 1;

    /// <summary>时序数据默认 TTL（秒），0 表示永不过期</summary>
    public Int64 FluxDefaultTtlSeconds { get; set; } = 0;

    /// <summary>KV 默认 TTL（未显式指定时使用）</summary>
    public TimeSpan DefaultKvTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>KV 自动压缩比率。当文件写入记录数 ÷ 存活键数 超过此比率时自动触发 Compact，0 表示禁用自动压缩。默认 5.0</summary>
    public Double KvCompactRatio { get; set; } = 5.0;

    /// <summary>是否只读模式。只读模式下禁止所有写操作（DDL/DML），可跳过 WAL 初始化、关闭脏页刷盘，获得更高读取性能并避免多进程写冲突</summary>
    public Boolean ReadOnly { get; set; }
}

/// <summary>WAL 持久化模式</summary>
public enum WalMode
{
    /// <summary>不使用 WAL（最高吞吐，崩溃可能丢失数据）</summary>
    None,

    /// <summary>异步 WAL，每秒刷盘（默认，平衡性能和安全）</summary>
    Normal,

    /// <summary>同步 WAL，每次提交立即刷盘（最强数据安全）</summary>
    Full
}
