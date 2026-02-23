namespace NewLife.NovaDb.Engine;

/// <summary>物化视图定义，记录视图名称、源查询及刷新策略</summary>
/// <remarks>
/// 对应模块 X01（增量物化视图）、X02（定时修正），
/// 物化视图将 SELECT 聚合结果缓存到独立表中，减少实时查询开销。
/// </remarks>
public class MaterializedView
{
    /// <summary>视图名称</summary>
    public String Name { get; set; } = String.Empty;

    /// <summary>源 SQL 查询（SELECT ... FROM ... GROUP BY ...）</summary>
    public String Query { get; set; } = String.Empty;

    /// <summary>结果缓存：列名列表</summary>
    public List<String> ColumnNames { get; set; } = [];

    /// <summary>结果缓存：行数据</summary>
    public List<Object?[]> Rows { get; set; } = [];

    /// <summary>最后刷新时间</summary>
    public DateTime LastRefreshTime { get; set; }

    /// <summary>刷新间隔（秒），0 表示仅手动刷新</summary>
    public Int32 RefreshIntervalSeconds { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>刷新次数</summary>
    public Int64 RefreshCount { get; set; }

    /// <summary>最后刷新耗时（毫秒）</summary>
    public Int64 LastRefreshMs { get; set; }

    /// <summary>是否需要刷新（基于定时策略）</summary>
    /// <returns>需要刷新返回 true</returns>
    public Boolean NeedsRefresh()
    {
        if (RefreshIntervalSeconds <= 0) return false;
        if (LastRefreshTime == default) return true;
        return (DateTime.UtcNow - LastRefreshTime).TotalSeconds >= RefreshIntervalSeconds;
    }
}
