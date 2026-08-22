using NewLife.NovaDb.Core;
using NewLife.NovaDb.Queues;
using NewLife.NovaDb.Sql;

namespace NewLife.NovaDb.Engine.Flux;

/// <summary>连续查询管理器（对应 F10 连续查询、F11 实时聚合引擎、F12 多粒度汇总）</summary>
/// <remarks>
/// 对标 TDengine TSMA：对 Flux 时序表的新增数据自动执行聚合，把结果写入目标统计表。
/// 支持多粒度链（Rollups），一次写入同时维护 1分钟/15分钟/1小时/1天 多级统计表，
/// 目标表由引擎自动创建（UPSERT 语义），向应用层隐藏统计表，解决手工建表与 DDL 权限问题。
/// </remarks>
public class ContinuousQueryManager : IDisposable
{
    private readonly FluxEngine _flux;
    private readonly SqlEngine _sql;
    private readonly Dictionary<String, ContinuousQueryDefinition> _queries = new(StringComparer.OrdinalIgnoreCase);
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif
    private Timer? _scheduler;
    private Boolean _disposed;

    /// <summary>查询数量</summary>
    public Int32 Count
    {
        get
        {
            lock (_lock) return _queries.Count;
        }
    }

    /// <summary>创建连续查询管理器</summary>
    /// <param name="flux">Flux 时序引擎（源数据）</param>
    /// <param name="sql">SQL 引擎（目标统计表）</param>
    public ContinuousQueryManager(FluxEngine flux, SqlEngine sql)
    {
        _flux = flux ?? throw new ArgumentNullException(nameof(flux));
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
    }

    /// <summary>注册连续查询</summary>
    /// <remarks>游标初始化为当前最大时间戳，只处理注册之后的新增数据</remarks>
    /// <param name="definition">查询定义</param>
    /// <returns>注册后的查询定义</returns>
    public ContinuousQueryDefinition Register(ContinuousQueryDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        if (String.IsNullOrEmpty(definition.Name)) throw new ArgumentNullException(nameof(definition.Name));
        if (String.IsNullOrEmpty(definition.SourceTable)) throw new ArgumentNullException(nameof(definition.SourceTable));
        if (definition.Rollups.Count == 0) throw new ArgumentException("At least one rollup granule is required", nameof(definition));
        if (String.IsNullOrEmpty(definition.TargetPrefix)) throw new ArgumentNullException(nameof(definition.TargetPrefix));

        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ContinuousQueryManager));
            if (_queries.ContainsKey(definition.Name))
                throw new NovaException(ErrorCode.InvalidArgument, $"Continuous query '{definition.Name}' already exists");

            // 游标初始化为当前最大时间戳（注册前已有数据不重算，由应用决定是否回填）
            var now = DateTime.UtcNow.Ticks;
            definition.LastProcessedTicks = definition.LastProcessedTicks > 0 ? definition.LastProcessedTicks : now;

            _queries[definition.Name] = definition;

            // 启动调度器
            _scheduler ??= new Timer(SchedulerCallback, null, 60_000, 60_000);

            return definition;
        }
    }

    /// <summary>注销连续查询</summary>
    /// <param name="name">查询名称</param>
    /// <returns>是否成功</returns>
    public Boolean Unregister(String name)
    {
        lock (_lock)
        {
            return _queries.Remove(name);
        }
    }

    /// <summary>获取连续查询定义</summary>
    /// <param name="name">查询名称</param>
    /// <returns>查询定义，不存在返回 null</returns>
    public ContinuousQueryDefinition? Get(String name)
    {
        lock (_lock)
        {
            return _queries.TryGetValue(name, out var def) ? def : null;
        }
    }

    /// <summary>执行全部连续查询</summary>
    /// <returns>处理的数据条数</returns>
    public Int64 RunAll()
    {
        var total = 0L;
        List<ContinuousQueryDefinition> defs;
        lock (_lock)
        {
            defs = _queries.Values.ToList();
        }

        foreach (var def in defs)
        {
            try
            {
                total += ProcessNewData(def);
            }
            catch
            {
                // 单个查询失败不影响其他查询
            }
        }

        return total;
    }

    /// <summary>执行指定连续查询：处理游标之后的新增数据</summary>
    /// <param name="name">查询名称</param>
    /// <returns>处理的数据条数</returns>
    public Int64 Run(String name)
    {
        ContinuousQueryDefinition def;
        lock (_lock)
        {
            if (!_queries.TryGetValue(name, out var q)) return 0;
            def = q;
        }

        return ProcessNewData(def);
    }

    /// <summary>挂接消息队列消费事件，消费消息后自动聚合（F11 实时聚合引擎）</summary>
    /// <remarks>消息被消费确认后触发一次聚合，实现"MQ 消费时自动聚合，批量更新统计表"</remarks>
    /// <typeparam name="T">消息类型</typeparam>
    /// <param name="queue">消息队列</param>
    /// <param name="name">连续查询名称</param>
    /// <returns>挂接的处理器（用于取消挂接）</returns>
    public Action<String> AttachRealtime<T>(NovaQueue<T> queue, String name)
    {
        if (queue == null) throw new ArgumentNullException(nameof(queue));

        return msgId => Run(name);
    }

    /// <summary>挂接消息队列消费事件并订阅（F11 实时聚合引擎）</summary>
    /// <remarks>返回取消订阅的动作。消息被消费确认后自动触发指定连续查询的聚合</remarks>
    /// <typeparam name="T">消息类型</typeparam>
    /// <param name="queue">消息队列</param>
    /// <param name="name">连续查询名称</param>
    /// <returns>取消订阅的动作</returns>
    public Action SubscribeRealtime<T>(NovaQueue<T> queue, String name)
    {
        if (queue == null) throw new ArgumentNullException(nameof(queue));

        var handler = AttachRealtime(queue, name);
        queue.OnConsumed += handler;
        return () => queue.OnConsumed -= handler;
    }

    /// <summary>处理新增数据：从游标位置扫描源表，按多粒度聚合后 UPSERT 到目标表</summary>
    /// <param name="def">查询定义</param>
    /// <returns>处理的数据条数</returns>
    private Int64 ProcessNewData(ContinuousQueryDefinition def)
    {
        var now = DateTime.UtcNow.Ticks;
        var cursor = def.LastProcessedTicks;
        if (cursor >= now) return 0;

        // 查询新增数据
        var entries = _flux.QueryRange(cursor, now);
        if (entries.Count == 0)
        {
            def.LastProcessedTicks = now;
            return 0;
        }

        // 按粒度分组聚合：粒度 → 桶起始 → 聚合累计器
        var buckets = new Dictionary<Int64, Dictionary<Int64, BucketAccumulator>>();
        foreach (var granule in def.Rollups)
        {
            buckets[granule] = [];
        }

        foreach (var entry in entries)
        {
            if (!entry.Fields.TryGetValue(def.FieldName, out var val) || val == null) continue;

            var v = Convert.ToDouble(val);
            foreach (var granule in def.Rollups)
            {
                var bucketStart = entry.Timestamp / granule * granule;
                var granuleBuckets = buckets[granule];
                if (!granuleBuckets.TryGetValue(bucketStart, out var acc))
                {
                    acc = new BucketAccumulator();
                    granuleBuckets[bucketStart] = acc;
                }
                acc.Add(v);
            }
        }

        // UPSERT 聚合结果到各粒度目标表
        foreach (var granule in def.Rollups)
        {
            var target = def.GetTargetTable(granule);
            EnsureTargetTable(target);

            foreach (var kvp in buckets[granule])
            {
                var acc = kvp.Value;
                UpsertBucket(def, target, kvp.Key, acc);
            }
        }

        def.ProcessedCount += entries.Count;
        def.RunCount++;
        def.LastRunTime = DateTime.UtcNow;
        def.LastProcessedTicks = now;

        return entries.Count;
    }

    /// <summary>确保目标统计表存在（引擎自动建表，应用无感知）</summary>
    /// <param name="target">目标表名</param>
    private void EnsureTargetTable(String target)
    {
        var exists = _sql.TableNames.Contains(target);
        if (!exists)
        {
            _sql.Execute($"CREATE TABLE `{target}` (" +
                "bucket_start DATETIME PRIMARY KEY, " +
                "cnt INT, " +
                "sum_val DOUBLE, " +
                "min_val DOUBLE, " +
                "max_val DOUBLE)");
        }
    }

    /// <summary>UPSERT 单桶聚合结果到目标表（查询-合并-写入）</summary>
    /// <param name="def">查询定义</param>
    /// <param name="target">目标表名</param>
    /// <param name="bucketStart">桶起始 Ticks</param>
    /// <param name="acc">聚合累计器</param>
    private void UpsertBucket(ContinuousQueryDefinition def, String target, Int64 bucketStart, BucketAccumulator acc)
    {
        var dt = new DateTime(bucketStart, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss");

        // 查询现有桶
        var existing = _sql.Execute($"SELECT cnt, sum_val, min_val, max_val FROM `{target}` WHERE bucket_start = '{dt}'");
        if (existing.Rows.Count == 0)
        {
            _sql.Execute($"INSERT INTO `{target}` (bucket_start, cnt, sum_val, min_val, max_val) " +
                $"VALUES ('{dt}', {acc.Count}, {acc.Sum}, {acc.Min}, {acc.Max})");
        }
        else
        {
            var row = existing.Rows[0];
            var cnt = Convert.ToInt64(row[0]) + acc.Count;
            var sum = Convert.ToDouble(row[1]) + acc.Sum;
            var min = Math.Min(Convert.ToDouble(row[2]), acc.Min);
            var max = Math.Max(Convert.ToDouble(row[3]), acc.Max);
            _sql.Execute($"UPDATE `{target}` SET cnt = {cnt}, sum_val = {sum}, min_val = {min}, max_val = {max} " +
                $"WHERE bucket_start = '{dt}'");
        }
    }

    /// <summary>调度器回调：周期执行全部查询</summary>
    private void SchedulerCallback(Object? state)
    {
        try
        {
            RunAll();
        }
        catch
        {
            // 定时执行异常不中断调度器
        }
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            _scheduler?.Dispose();
            _scheduler = null;
            _queries.Clear();
        }
    }

    /// <summary>桶聚合累计器</summary>
    private sealed class BucketAccumulator
    {
        /// <summary>计数</summary>
        public Int64 Count { get; private set; }

        /// <summary>求和</summary>
        public Double Sum { get; private set; }

        /// <summary>最小值</summary>
        public Double Min { get; private set; } = Double.MaxValue;

        /// <summary>最大值</summary>
        public Double Max { get; private set; } = Double.MinValue;

        /// <summary>累计一个值</summary>
        /// <param name="value">数值</param>
        public void Add(Double value)
        {
            Count++;
            Sum += value;
            if (value < Min) Min = value;
            if (value > Max) Max = value;
        }
    }
}
