namespace NewLife.NovaDb.Storage;

/// <summary>数据库信息（系统库中的数据库记录）</summary>
public class DatabaseInfo
{
    /// <summary>数据库名称（即目录名）</summary>
    public String Name { get; set; } = String.Empty;

    /// <summary>数据库完整路径</summary>
    public String Path { get; set; } = String.Empty;

    /// <summary>数据库状态</summary>
    public DatabaseStatus Status { get; set; }

    /// <summary>是否为外部数据库（不在默认目录内，由用户手动注册）</summary>
    public Boolean IsExternal { get; set; }

    /// <summary>文件头版本号</summary>
    public UInt16 Version { get; set; }

    /// <summary>页大小（字节）</summary>
    public UInt32 PageSize { get; set; }

    /// <summary>创建时间（来自 FileHeader）</summary>
    public DateTime CreateTime { get; set; }

    /// <summary>最后一次扫描发现的时间（UTC Ticks）</summary>
    public Int64 LastSeenAt { get; set; }
}
