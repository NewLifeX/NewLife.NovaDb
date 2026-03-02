using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Engine.Flux;

/// <summary>Append-only 时序分区存储引擎</summary>
public partial class FluxEngine : IDisposable
{
    private readonly String _basePath;
    private readonly DbOptions _options;
    private readonly SortedDictionary<String, List<FluxEntry>> _partitions = new(StringComparer.Ordinal);
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif
    private Boolean _disposed;

    /// <summary>创建 FluxEngine 实例</summary>
    /// <param name="basePath">数据存储基础路径</param>
    /// <param name="options">数据库选项</param>
    public FluxEngine(String basePath, DbOptions options)
    {
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (!Directory.Exists(_basePath))
            Directory.CreateDirectory(_basePath);

        // 打开时序日志并恢复数据
        OpenFluxLog();
    }

    /// <summary>追加单条时序条目</summary>
    /// <param name="entry">时序条目</param>
    public void Append(FluxEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));

        lock (_lock)
        {
            var key = GetPartitionKey(entry.Timestamp);
            if (!_partitions.TryGetValue(key, out var list))
            {
                list = [];
                _partitions[key] = list;
            }

            // 同毫秒自增序列号
            var mid = MessageId.Auto(list, entry.Timestamp);
            entry.SequenceId = mid.Sequence;
            list.Add(entry);

            // 持久化
            PersistFluxAppend(entry);
        }
    }

    /// <summary>批量追加时序条目</summary>
    /// <param name="entries">时序条目集合</param>
    public void AppendBatch(IEnumerable<FluxEntry> entries)
    {
        if (entries == null) throw new ArgumentNullException(nameof(entries));

        lock (_lock)
        {
            foreach (var entry in entries)
            {
                var key = GetPartitionKey(entry.Timestamp);
                if (!_partitions.TryGetValue(key, out var list))
                {
                    list = [];
                    _partitions[key] = list;
                }

                var mid = MessageId.Auto(list, entry.Timestamp);
                entry.SequenceId = mid.Sequence;
                list.Add(entry);

                // 持久化
                PersistFluxAppend(entry);
            }
        }
    }

    /// <summary>按时间范围查询条目</summary>
    /// <param name="startTicks">起始时间（Ticks）</param>
    /// <param name="endTicks">结束时间（Ticks）</param>
    /// <returns>符合条件的条目列表</returns>
    public List<FluxEntry> QueryRange(Int64 startTicks, Int64 endTicks)
    {
        var result = new List<FluxEntry>();

        lock (_lock)
        {
            foreach (var kvp in _partitions)
            {
                foreach (var entry in kvp.Value)
                {
                    if (entry.Timestamp >= startTicks && entry.Timestamp <= endTicks)
                        result.Add(entry);
                }
            }
        }

        return result;
    }

    /// <summary>根据时间戳计算分区键，格式为 yyyyMMddHH</summary>
    /// <param name="ticks">UTC 时间戳（Ticks）</param>
    /// <returns>分区键字符串</returns>
    public String GetPartitionKey(Int64 ticks)
    {
        var dt = new DateTime(ticks, DateTimeKind.Utc);
        var hours = _options.FluxPartitionHours;
        if (hours <= 0) hours = 1;

        // 按分片粒度对齐小时
        var alignedHour = dt.Hour / hours * hours;
        var aligned = new DateTime(dt.Year, dt.Month, dt.Day, alignedHour, 0, 0, DateTimeKind.Utc);
        return aligned.ToString("yyyyMMddHH");
    }

    /// <summary>删除过期分区</summary>
    /// <param name="ttlSeconds">TTL（秒）</param>
    /// <returns>删除的分区数量</returns>
    public Int32 DeleteExpiredPartitions(Int64 ttlSeconds)
    {
        if (ttlSeconds <= 0) return 0;

        var cutoff = DateTime.UtcNow.AddSeconds(-ttlSeconds);
        var cutoffKey = cutoff.ToString("yyyyMMddHH");
        var toRemove = new List<String>();

        lock (_lock)
        {
            foreach (var key in _partitions.Keys)
            {
                if (String.Compare(key, cutoffKey, StringComparison.Ordinal) < 0)
                    toRemove.Add(key);
            }

            foreach (var key in toRemove)
            {
                _partitions.Remove(key);
            }
        }

        // 持久化清理记录
        if (toRemove.Count > 0)
            PersistFluxPurge(cutoffKey);

        return toRemove.Count;
    }

    /// <summary>获取分区数量</summary>
    /// <returns>分区数量</returns>
    public Int32 GetPartitionCount()
    {
        lock (_lock)
        {
            return _partitions.Count;
        }
    }

    /// <summary>获取总条目数</summary>
    /// <returns>条目总数</returns>
    public Int64 GetEntryCount()
    {
        lock (_lock)
        {
            var count = 0L;
            foreach (var list in _partitions.Values)
            {
                count += list.Count;
            }
            return count;
        }
    }

    /// <summary>获取所有条目（按时间戳排序）</summary>
    /// <returns>所有条目列表</returns>
    internal List<FluxEntry> GetAllEntries()
    {
        var result = new List<FluxEntry>();

        lock (_lock)
        {
            foreach (var list in _partitions.Values)
            {
                result.AddRange(list);
            }
        }

        result.Sort((a, b) =>
        {
            var cmp = a.Timestamp.CompareTo(b.Timestamp);
            if (cmp != 0) return cmp;
            return a.SequenceId.CompareTo(b.SequenceId);
        });

        return result;
    }

    /// <summary>清空所有分区数据</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _partitions.Clear();
            TruncateFluxLog();
        }
    }

    /// <summary>对指定时间范围的数据执行降采样聚合</summary>
    /// <param name="startTicks">起始时间 Ticks</param>
    /// <param name="endTicks">结束时间 Ticks</param>
    /// <param name="bucketTicks">分桶大小 Ticks（如 1 小时 = TimeSpan.FromHours(1).Ticks）</param>
    /// <param name="fieldName">聚合字段名</param>
    /// <param name="aggregation">聚合方式：avg/sum/min/max/count</param>
    /// <returns>降采样结果列表，每个结果包含桶起始时间和聚合值</returns>
    public List<DownsampleResult> Downsample(Int64 startTicks, Int64 endTicks, Int64 bucketTicks, String fieldName, String aggregation)
    {
        if (bucketTicks <= 0) throw new ArgumentException("Bucket size must be positive", nameof(bucketTicks));
        if (fieldName == null) throw new ArgumentNullException(nameof(fieldName));

        var entries = QueryRange(startTicks, endTicks);
        var buckets = new SortedDictionary<Int64, List<Double>>();

        // 按桶分组
        foreach (var entry in entries)
        {
            if (!entry.Fields.TryGetValue(fieldName, out var val) || val == null) continue;

            var bucketStart = (entry.Timestamp - startTicks) / bucketTicks * bucketTicks + startTicks;
            if (!buckets.TryGetValue(bucketStart, out var list))
            {
                list = [];
                buckets[bucketStart] = list;
            }

            list.Add(Convert.ToDouble(val));
        }

        // 执行聚合
        var agg = aggregation.ToLower();
        var results = new List<DownsampleResult>();

        foreach (var kvp in buckets)
        {
            var values = kvp.Value;
            Double aggValue = agg switch
            {
                "avg" or "average" => values.Sum() / values.Count,
                "sum" => values.Sum(),
                "min" => values.Min(),
                "max" => values.Max(),
                "count" => values.Count,
                _ => throw new NovaException(ErrorCode.InvalidArgument, $"Unknown aggregation: {aggregation}")
            };

            results.Add(new DownsampleResult(kvp.Key, aggValue));
        }

        return results;
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;
        CloseFluxLog();
        _partitions.Clear();
        _disposed = true;
    }
}
