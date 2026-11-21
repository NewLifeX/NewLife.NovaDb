using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;

namespace NewLife.NovaDb.Sql;

partial class SqlEngine
{
    #region 统计信息

    /// <summary>统计缓存：表名 → 统计信息</summary>
    private readonly Dictionary<String, TableStatistics> _statistics = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>统计缓存锁（独立于元数据锁，避免与已有的元数据锁嵌套死锁）</summary>
    private readonly Object _statsLock = new();

    /// <summary>执行 ANALYZE [TABLE] name，未指定表名时分析全库</summary>
    /// <param name="stmt">ANALYZE 语句</param>
    /// <returns>受影响表数写入 AffectedRows</returns>
    private SqlResult ExecuteAnalyze(AnalyzeStatement stmt)
    {
        // 分析期间禁止 DDL 改动表结构，DML 与查询不受影响
        using var rl = _metaLock.AcquireRead();

        var names = new List<String>();
        var tables = new List<NovaTable>();
        if (!String.IsNullOrEmpty(stmt.TableName))
        {
            if (!_tables.TryGetValue(stmt.TableName!, out var table))
                throw new NovaException(ErrorCode.TableNotFound, "Table '{0}' not found", stmt.TableName);

            names.Add(stmt.TableName!);
            tables.Add(table);
        }
        else
        {
            foreach (var kvp in _tables)
            {
                names.Add(kvp.Key);
                tables.Add(kvp.Value);
            }
        }

        for (var i = 0; i < tables.Count; i++)
        {
            var stats = tables[i].BuildStatistics();

            lock (_statsLock)
            {
                _statistics.TryGetValue(names[i], out var previous);
                stats.StatVersion = (previous?.StatVersion ?? 0) + 1;
                _statistics[names[i]] = stats;
            }
        }

        return new SqlResult { AffectedRows = tables.Count };
    }

    /// <summary>获取有效的表统计信息；统计缺失、过期或表定义变化时返回 null</summary>
    /// <param name="tableName">表名</param>
    /// <param name="table">表实例，为空时直接返回 null</param>
    /// <returns>统计信息或 null，调用方需回退原有的粗估规则</returns>
    private TableStatistics? GetTableStatistics(String tableName, NovaTable? table)
    {
        if (table == null) return null;

        TableStatistics? stats;
        lock (_statsLock)
        {
            if (!_statistics.TryGetValue(tableName, out stats)) return null;
        }

        if (stats.IsStale(table))
        {
            lock (_statsLock)
            {
                _statistics.Remove(tableName);
            }

            return null;
        }

        return stats;
    }

    /// <summary>获取缓存的统计信息（不校验是否过期），供 _sys.stats 与 information_schema 展示</summary>
    /// <param name="tableName">表名</param>
    /// <returns>统计信息或 null</returns>
    private TableStatistics? GetCachedTableStatistics(String tableName)
    {
        lock (_statsLock)
        {
            _statistics.TryGetValue(tableName, out var stats);
            return stats;
        }
    }

    #endregion
}
