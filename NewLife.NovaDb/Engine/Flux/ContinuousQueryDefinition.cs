using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Engine.Flux;

/// <summary>连续查询定义（对应 F10 连续查询、F12 多粒度汇总）</summary>
/// <remarks>
/// 连续查询对 Flux 时序表的新增数据自动执行聚合，把结果写入目标统计表。
/// 通过多粒度链（Rollups）一次计算 1分钟→15分钟→1小时→1天 多粒度汇总，
/// 向应用层隐藏统计表，避免手工建表与 DDL 权限问题。
/// </remarks>
public class ContinuousQueryDefinition
{
    /// <summary>查询名称</summary>
    public String Name { get; set; } = String.Empty;

    /// <summary>源时序表名（Flux 引擎表）</summary>
    public String SourceTable { get; set; } = String.Empty;

    /// <summary>聚合函数列表：count/sum/avg/min/max/first/last</summary>
    public List<String> Aggregations { get; set; } = [];

    /// <summary>聚合字段名</summary>
    public String FieldName { get; set; } = String.Empty;

    /// <summary>多粒度链（Ticks），如 1分钟/15分钟/1小时/1天。每个粒度输出一张统计表</summary>
    public List<Int64> Rollups { get; set; } = [];

    /// <summary>目标统计表前缀。实际表名为 {Prefix}_{粒度标签}</summary>
    public String TargetPrefix { get; set; } = String.Empty;

    /// <summary>调度间隔（秒），默认 60 秒</summary>
    public Int32 ScheduleIntervalSeconds { get; set; } = 60;

    /// <summary>增量游标：上次处理到的数据时间（Ticks）。下次只处理游标之后的新数据</summary>
    public Int64 LastProcessedTicks { get; set; }

    /// <summary>已处理数据条数</summary>
    public Int64 ProcessedCount { get; set; }

    /// <summary>执行次数</summary>
    public Int64 RunCount { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>上次执行时间</summary>
    public DateTime LastRunTime { get; set; }

    /// <summary>获取粒度标签（用于目标表命名），如 5m/1h/1d</summary>
    /// <param name="ticks">粒度 Ticks</param>
    /// <returns>粒度标签</returns>
    public static String GetGranuleLabel(Int64 ticks)
    {
        var ts = TimeSpan.FromTicks(ticks);
        if (ts.TotalSeconds < 60) return $"{(Int32)ts.TotalSeconds}s";
        if (ts.TotalMinutes < 60) return $"{(Int32)ts.TotalMinutes}m";
        if (ts.TotalHours < 24) return $"{(Int32)ts.TotalHours}h";
        return $"{(Int32)ts.TotalDays}d";
    }

    /// <summary>获取目标统计表名</summary>
    /// <param name="ticks">粒度 Ticks</param>
    /// <returns>目标表名</returns>
    public String GetTargetTable(Int64 ticks) => $"{TargetPrefix}_{GetGranuleLabel(ticks)}";
}
