using NewLife.Log;

namespace NewLife.NovaDb.Core;

/// <summary>慢查询日志，记录执行时间超过阈值的 SQL 语句</summary>
/// <remarks>
/// 支持可配置的慢查询阈值，自动记录 SQL 文本、执行耗时和影响行数。
/// 最近的慢查询记录保留在内存环形缓冲区中，可通过 <see cref="RecentEntries"/> 访问。
/// </remarks>
public class SlowQueryLog
{
    #region 属性
    /// <summary>慢查询阈值（毫秒），执行时间超过此值将被记录。默认 100ms</summary>
    public Int32 ThresholdMs { get; set; } = 100;

    /// <summary>是否启用慢查询日志。默认 true</summary>
    public Boolean Enabled { get; set; } = true;

    /// <summary>内存中保留的最近慢查询记录数。默认 100</summary>
    public Int32 MaxEntries { get; set; } = 100;

    /// <summary>慢查询总数</summary>
    public Int64 TotalCount => _totalCount;

    /// <summary>最近的慢查询记录</summary>
    public IReadOnlyList<SlowQueryEntry> RecentEntries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList().AsReadOnly();
            }
        }
    }
    #endregion

    #region 字段
    private Int64 _totalCount;
    private readonly LinkedList<SlowQueryEntry> _entries = new();
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif
    #endregion

    #region 日志
    /// <summary>日志接口</summary>
    public ILog Log { get; set; } = XTrace.Log;
    #endregion

    #region 方法
    /// <summary>记录一次 SQL 执行，如果超过阈值则记录到慢查询日志</summary>
    /// <param name="sql">SQL 文本</param>
    /// <param name="elapsedMs">执行耗时（毫秒）</param>
    /// <param name="affectedRows">影响行数</param>
    /// <returns>是否为慢查询</returns>
    public Boolean Record(String sql, Int64 elapsedMs, Int32 affectedRows = 0)
    {
        if (!Enabled || elapsedMs < ThresholdMs) return false;
        if (String.IsNullOrEmpty(sql)) return false;

        Interlocked.Increment(ref _totalCount);

        var entry = new SlowQueryEntry
        {
            Sql = sql,
            ElapsedMs = elapsedMs,
            AffectedRows = affectedRows,
            Time = DateTime.Now
        };

        lock (_lock)
        {
            _entries.AddLast(entry);

            // 环形缓冲区，超过上限移除最旧记录
            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveFirst();
            }
        }

        // 输出到日志
        Log?.Warn("[SlowQuery] {0}ms | rows={1} | {2}",
            elapsedMs,
            affectedRows,
            sql.Length > 200 ? sql.Substring(0, 200) + "..." : sql);

        return true;
    }

    /// <summary>清空所有慢查询记录</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
    #endregion
}

/// <summary>慢查询记录条目</summary>
public class SlowQueryEntry
{
    /// <summary>SQL 文本</summary>
    public String Sql { get; set; } = String.Empty;

    /// <summary>执行耗时（毫秒）</summary>
    public Int64 ElapsedMs { get; set; }

    /// <summary>影响行数</summary>
    public Int32 AffectedRows { get; set; }

    /// <summary>记录时间</summary>
    public DateTime Time { get; set; }

    /// <summary>输出文本表示</summary>
    public override String ToString() => $"[{Time:HH:mm:ss.fff}] {ElapsedMs}ms rows={AffectedRows} {Sql}";
}
