namespace NewLife.NovaDb.Engine;

/// <summary>分片元数据</summary>
public class ShardInfo
{
    /// <summary>分片 ID</summary>
    public Int32 ShardId { get; set; }

    /// <summary>最小键（包含）</summary>
    public Object? MinKey { get; set; }

    /// <summary>最大键（包含）</summary>
    public Object? MaxKey { get; set; }

    /// <summary>当前行数</summary>
    public Int64 RowCount { get; set; }

    /// <summary>当前大小（字节）</summary>
    public Int64 SizeBytes { get; set; }

    /// <summary>数据文件路径</summary>
    public String DataFilePath { get; set; } = String.Empty;

    /// <summary>是否只读（归档分片）</summary>
    public Boolean IsReadOnly { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
