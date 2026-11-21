using System.Diagnostics;
using System.Globalization;

namespace NewLife.QueryBench;

/// <summary>基准运行器</summary>
public sealed class BenchRunner
{
    private readonly BenchOptions _options;
    private readonly List<CsvRecord> _records = [];
    private readonly List<String> _failedDatabases = [];

    /// <summary>实例化</summary>
    /// <param name="options"></param>
    public BenchRunner(BenchOptions options) => _options = options;

    /// <summary>运行全部规模与表的基准</summary>
    /// <returns></returns>
    public async Task RunAsync()
    {
        BenchLog.Write($"仓库根目录：{_options.RepositoryRoot}");
        BenchLog.Write($"CSV 文件：{_options.CsvFile}");
        BenchLog.Write($"规模：[{String.Join(",", _options.Scales)}] 数据库：[{String.Join(",", _options.Databases)}] 表：[{String.Join(",", _options.Tables)}]");

        _records.AddRange(CsvStore.Load(_options.CsvFile));
        var names = _options.Databases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (!_options.LoadOnly) await RunBaselinesAsync(names);

        foreach (var scale in _options.Scales)
        {
            var dataset = DatasetInfo.Create(scale);
            BenchLog.Write($"=== 规模 {scale}：{dataset.Rows} 行，user_id 1..{dataset.MaxUserId} ===");

            var targets = OpenTargets(names, scale);
            if (targets.Count == 0)
            {
                BenchLog.Write($"[{scale}] 没有可用的数据库连接，跳过该规模");
                continue;
            }

            try
            {
                foreach (var table in _options.Tables)
                    await RunTableAsync(dataset, table, targets);
            }
            finally
            {
                foreach (var target in targets) target.Dispose();
            }
        }

        WriteSummary();
    }

    private async Task RunBaselinesAsync(List<String> names)
    {
        var query = new BenchQuery { Id = "SELECT 1", SqlTemplate = "SELECT 1" };

        foreach (var name in names)
        {
            var existing = _records.Where(e => e.Scale == "baseline" && e.Db == name && e.Table == "-" && e.Query == query.Id).ToList();
            if (!_options.Force && existing.Count > 0 && existing.All(e => e.IsOk))
            {
                BenchLog.Write($"[baseline/{name}] 已存在，跳过");
                continue;
            }

            BenchTarget? target = null;
            try
            {
                target = BenchTargetFactory.Create(name);
                target.Open(_options, "baseline");
                var record = await MeasureAsync("baseline", "-", target, query);
                _records.Add(record);
                CsvStore.Append(_options.CsvFile, [record]);
                BenchLog.Write($"[baseline/{name}] {FormatRecord(record)}");
            }
            catch (Exception ex)
            {
                _failedDatabases.Add($"{name}: {BenchLog.Summarize(ex)}");
                BenchLog.Write($"[baseline/{name}] 连接失败：{BenchLog.Summarize(ex)}");
            }
            finally
            {
                target?.Dispose();
            }
        }
    }

    private List<BenchTarget> OpenTargets(List<String> names, String scale)
    {
        var targets = new List<BenchTarget>();
        foreach (var name in names)
        {
            try
            {
                var target = BenchTargetFactory.Create(name);
                target.Open(_options, scale);
                targets.Add(target);
            }
            catch (Exception ex)
            {
                _failedDatabases.Add($"{name}: {BenchLog.Summarize(ex)}");
                BenchLog.Write($"[{scale}/{name}] 连接失败：{BenchLog.Summarize(ex)}");
            }
        }

        return targets;
    }

    private async Task RunTableAsync(DatasetInfo dataset, String table, List<BenchTarget> targets)
    {
        var queries = QueryCatalog.Build(dataset, _options.IncludeJoin);
        var newRecords = new List<CsvRecord>();

        foreach (var target in targets)
        {
            if (!_options.LoadOnly && !_options.Force && IsComboComplete(dataset.Scale, target.Name, table, queries))
            {
                BenchLog.Write($"[{dataset.Scale}/{target.Name}/{table}] CSV 已存在且状态一致，跳过（--force 可强制重跑）");
                continue;
            }

            var started = Stopwatch.StartNew();
            try
            {
                target.Recreate(table, _options.IncludeJoin);

                BenchLog.Write($"[{dataset.Scale}/{target.Name}/{table}] 装载 {dataset.Rows} 行 ...");
                target.Load(table, dataset.Rows, _options.IncludeJoin);
                var seconds = started.Elapsed.TotalSeconds;
                target.Analyze(table, _options.IncludeJoin);
                BenchLog.Write($"[{dataset.Scale}/{target.Name}/{table}] 装载完成 {seconds:0.###}s，{dataset.Rows / Math.Max(seconds, 0.001):0} 行/s，已统计");

                if (_options.LoadOnly) continue;

                foreach (var query in queries)
                {
                    var record = await MeasureAsync(dataset.Scale, table, target, query);
                    newRecords.Add(record);
                    BenchLog.Write($"[{dataset.Scale}/{target.Name}/{table}] {FormatRecord(record)}");
                }
            }
            catch (Exception ex)
            {
                BenchLog.Write($"[{dataset.Scale}/{target.Name}/{table}] 执行失败：{BenchLog.Summarize(ex)}");
                if (!_options.LoadOnly)
                {
                    foreach (var query in queries)
                    {
                        if (newRecords.Any(e => e.Scale == dataset.Scale && e.Db == target.Name && e.Table == table && e.Query == query.Id)) continue;

                        newRecords.Add(CreateFailureRecord(dataset.Scale, target.Name, table, query.Id, ex));
                    }
                }
            }
            finally
            {
                if (!_options.Keep && !_options.LoadOnly)
                {
                    try
                    {
                        target.Drop(table);
                        if (_options.IncludeJoin) target.Drop("t_user");
                    }
                    catch (Exception ex)
                    {
                        BenchLog.Write($"[{dataset.Scale}/{target.Name}/{table}] 清理失败：{BenchLog.Summarize(ex)}");
                    }
                }
            }
        }

        ApplyMismatch(dataset.Scale, table, queries, newRecords);
        CsvStore.Append(_options.CsvFile, newRecords);
        _records.AddRange(newRecords);
    }

    private async Task<CsvRecord> MeasureAsync(String scale, String table, BenchTarget target, BenchQuery query)
    {
        var sql = query.GetSql(table);

        try
        {
            for (var i = 0; i < _options.Warmup; i++)
                await target.ExecuteAsync(sql, query.Parameters, _options.TimeoutSeconds, null);
        }
        catch (Exception ex)
        {
            return CreateFailureRecord(scale, target.Name, table, query.Id, ex);
        }

        var times = new List<Double>(Math.Min(_options.Iterations, 1024));
        var hash = "";
        Int64 rows = 0;
        var total = Stopwatch.StartNew();

        try
        {
            for (var i = 0; i < _options.Iterations; i++)
            {
                var hasher = i == 0 ? new ResultHasher(query.Ordered) : null;
                var iteration = Stopwatch.StartNew();
                var count = await target.ExecuteAsync(sql, query.Parameters, _options.TimeoutSeconds, hasher);
                iteration.Stop();

                times.Add(iteration.Elapsed.TotalMilliseconds);
                if (i == 0)
                {
                    rows = count;
                    hash = hasher!.Finish();
                }

                if (total.Elapsed.TotalSeconds >= _options.BudgetSeconds) break;
            }
        }
        catch (Exception ex)
        {
            var status = BenchLog.IsTimeout(ex) ? "TIMEOUT" : "ERROR";
            return BuildRecord(scale, target.Name, table, query.Id, rows, times, hash, status, BenchLog.Summarize(ex));
        }

        return BuildRecord(scale, target.Name, table, query.Id, rows, times, hash, "OK", "");
    }

    private static CsvRecord BuildRecord(String scale, String db, String table, String query, Int64 rows, List<Double> times, String hash, String status, String detail)
    {
        var (p50, p95, average, ops) = Summarize(times);
        return new CsvRecord
        {
            Scale = scale,
            Db = db,
            Table = table,
            Query = query,
            Rows = rows,
            Iterations = times.Count,
            P50 = p50,
            P95 = p95,
            Average = average,
            Ops = ops,
            Hash = hash,
            Status = status,
            Detail = detail,
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };
    }

    private static CsvRecord CreateFailureRecord(String scale, String db, String table, String query, Exception exception)
    {
        var status = BenchLog.IsTimeout(exception) ? "TIMEOUT" : "ERROR";
        return BuildRecord(scale, db, table, query, 0, [], "", status, BenchLog.Summarize(exception));
    }

    private static (Double p50, Double p95, Double average, Double ops) Summarize(List<Double> times)
    {
        if (times.Count == 0) return (0, 0, 0, 0);

        var sorted = times.ToArray();
        Array.Sort(sorted);

        var average = times.Average();
        return (Percentile(sorted, 50), Percentile(sorted, 95), average, average > 0 ? 1000.0 / average : 0);
    }

    private static Double Percentile(Double[] sorted, Double percentile)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];

        var rank = percentile / 100.0 * (sorted.Length - 1);
        var low = (Int32)Math.Floor(rank);
        var high = (Int32)Math.Ceiling(rank);
        return low == high ? sorted[low] : sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }

    private Boolean IsComboComplete(String scale, String db, String table, List<BenchQuery> queries)
    {
        var rows = _records.Where(e => e.Scale == scale && e.Db == db && e.Table == table).ToList();
        if (rows.Count == 0) return false;

        var latest = rows
            .GroupBy(e => e.Query, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(e => e.Key, e => e.Last(), StringComparer.OrdinalIgnoreCase);

        return queries.All(e => latest.TryGetValue(e.Id, out var record) && record.IsOk);
    }

    private void ApplyMismatch(String scale, String table, List<BenchQuery> queries, List<CsvRecord> newRecords)
    {
        foreach (var query in queries)
        {
            var rows = _records.Concat(newRecords)
                .Where(e => e.Scale == scale && e.Table == table && e.Query == query.Id && !String.IsNullOrEmpty(e.Hash))
                .ToList();
            if (rows.Count == 0) continue;

            // 每个库只取最近一次记录，避免历史批次影响本轮一致性判定
            var latest = rows.GroupBy(e => e.Db, StringComparer.OrdinalIgnoreCase).Select(e => e.Last()).ToList();
            var distinct = latest.Select(e => e.Hash).Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count <= 1) continue;

            var detail = String.Join(";", latest.Select(e => $"{e.Db}={e.Hash}"));
            foreach (var record in newRecords.Where(e => e.Query == query.Id))
            {
                record.Status = "MISMATCH";
                record.Detail = detail;
            }

            BenchLog.Write($"[{scale}/{table}/{query.Id}] 结果哈希不一致：{detail}");
        }
    }

    private static String FormatRecord(CsvRecord record)
    {
        var basic = $"{record.Query} rows={record.Rows} p50={record.P50:0.###}ms p95={record.P95:0.###}ms ops={record.Ops:0} hash={record.Hash} {record.Status}";
        return String.IsNullOrEmpty(record.Detail) ? basic : $"{basic} ({record.Detail})";
    }

    private void WriteSummary()
    {
        if (_failedDatabases.Count > 0)
        {
            BenchLog.Write("--- 连接失败 ---");
            foreach (var item in _failedDatabases.Distinct()) BenchLog.Write($"  {item}");
        }

        var recent = _records.Where(e => e.Timestamp.Length > 0).ToList();
        var mismatch = recent.Count(e => e.Status == "MISMATCH");
        var timeout = recent.Count(e => e.Status == "TIMEOUT");
        var error = recent.Count(e => e.Status == "ERROR");

        BenchLog.Write($"--- 汇总：记录 {recent.Count} 条，MISMATCH {mismatch}，TIMEOUT {timeout}，ERROR {error} ---");
    }
}
