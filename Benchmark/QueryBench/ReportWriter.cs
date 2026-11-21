using System.Globalization;
using System.Text;

namespace NewLife.QueryBench;

/// <summary>从 CSV 生成 Markdown 对比报告</summary>
public static class ReportWriter
{
    /// <summary>生成报告并返回输出路径</summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public static String Write(BenchOptions options)
    {
        var records = CsvStore.Load(options.CsvFile);
        var output = options.ResolveOutputPath(options.ReportFile);
        var dir = Path.GetDirectoryName(output);
        if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var databases = records.Select(e => e.Db).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(e => Array.FindIndex(BenchOptions.AllDatabases, d => String.Equals(d, e, StringComparison.OrdinalIgnoreCase))).ToArray();
        var current = Latest(records);

        var builder = new StringBuilder();
        builder.AppendLine("# 查询性能对比测试报告");
        builder.AppendLine();
        builder.AppendLine($"> 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}（本机）");
        builder.AppendLine($"> 数据来源：`{RelativePath(options.RepositoryRoot, options.CsvFile)}`");
        builder.AppendLine("> 对比对象：NewLife.NovaDb（嵌入模式）/ SQLite / MySQL，同一份确定性数据与同一套参数化 SQL。");
        builder.AppendLine();
        builder.AppendLine("## 数据集");
        builder.AppendLine();
        builder.AppendLine("| 规模 | 行数 | 说明 |");
        builder.AppendLine("|---|---:|---|");
        foreach (var scale in BenchOptions.ScaleRows)
        {
            if (!records.Any(e => e.Scale == scale.Key)) continue;
            builder.AppendLine($"| {scale.Key} | {scale.Value} | {DescribeScale(scale.Key)} |");
        }
        builder.AppendLine();
        builder.AppendLine("> 数据由固定种子 20261121 按行号生成，时间锚点 2026-11-21；`created_at` 在 365 天内唯一，保证 `ORDER BY created_at` 跨库可比。");
        builder.AppendLine("> SQLite 索引名带表名后缀（SQLite 索引名在库内全局唯一），逻辑与其它两库一致。");
        builder.AppendLine();

        WriteBaseline(builder, current, databases);
        WriteCoverage(builder, current, databases);

        foreach (var scale in BenchOptions.ScaleRows.Keys)
        {
            if (!records.Any(e => String.Equals(e.Scale, scale, StringComparison.OrdinalIgnoreCase))) continue;

            builder.AppendLine($"## {scale}（{BenchOptions.ScaleRows[scale].ToString("N0", CultureInfo.InvariantCulture)} 行）");
            builder.AppendLine();

            foreach (var table in BenchOptions.AllTables)
            {
                if (!records.Any(e => String.Equals(e.Scale, scale, StringComparison.OrdinalIgnoreCase) && e.Table == table)) continue;

                builder.AppendLine($"### {table}");
                builder.AppendLine();
                builder.AppendLine($"| 查询 | 行数 | {String.Join(" | ", databases.Select(e => $"{Display(e)} p50/p95 (ms)"))} | 状态 | 哈希 |");
                builder.Append("|---|---:|");
                foreach (var _ in databases) builder.Append("---:|");
                builder.AppendLine("---|---|");

                foreach (var query in QueryIds)
                {
                    var items = databases.Select(db => Find(current, scale, table, db, query)).ToArray();
                    if (items.All(e => e == null)) continue;

                    var rowCount = items.Where(e => e != null).Select(e => e!.Rows).DefaultIfEmpty(0).Max();
                    var cells = items.Select(e => FormatCell(e)).ToArray();
                    var status = FormatStatus(items);
                    var hash = FormatHash(items);

                    builder.AppendLine($"| {query} | {rowCount} | {String.Join(" | ", cells)} | {status} | {hash} |");
                }

                builder.AppendLine();
            }
        }

        builder.AppendLine("## 说明");
        builder.AppendLine();
        builder.AppendLine("- p50/p95 为客户端实测耗时（含驱动与结果遍历），已扣除前 3 次预热；单查询按 `--budget` 秒或 `--iterations` 次先到者止。");
        builder.AppendLine("- 哈希为结果集内容哈希（FNV-1a）。带 ORDER BY 的查询按行序哈希，其余查询按行哈希的和/异或聚合，跨库按集合比较。");
        builder.AppendLine("- `TIMEOUT` 表示单次执行超过 `--timeout`；`MISMATCH` 表示同一查询在不同库之间结果不一致；`ERROR` 表示执行异常。");
        builder.AppendLine("- 客户端基线 `SELECT 1` 反映各驱动的最小往返开销，用于解释小结果集查询的差异。");

        File.WriteAllText(output, builder.ToString(), new UTF8Encoding(false));
        BenchLog.Write($"报告已生成：{output}");

        return output;
    }

    private static readonly String[] QueryIds = ["Q1", "Q2", "Q3", "Q4", "Q5", "Q6", "Q7", "Q8", "Q9", "Q10", "Q11"];

    private static List<CsvRecord> Latest(List<CsvRecord> records)
    {
        var map = new Dictionary<String, CsvRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var key = $"{record.Scale}\u0001{record.Db}\u0001{record.Table}\u0001{record.Query}";
            map[key] = record;
        }
        return [.. map.Values];
    }

    private static CsvRecord? Find(List<CsvRecord> records, String scale, String table, String db, String query)
        => records.FirstOrDefault(e => String.Equals(e.Scale, scale, StringComparison.OrdinalIgnoreCase)
            && e.Table == table
            && String.Equals(e.Db, db, StringComparison.OrdinalIgnoreCase)
            && e.Query == query);

    private static String FormatCell(CsvRecord? record)
    {
        if (record == null) return "-";
        if (record.Status == "TIMEOUT") return "TIMEOUT";
        if (record.Status == "ERROR") return "ERROR";
        if (record.Status == "MISMATCH") return "MISMATCH";
        if (record.Iterations == 0) return "-";

        return $"{record.P50.ToString("0.###", CultureInfo.InvariantCulture)} / {record.P95.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    private static String FormatStatus(CsvRecord?[] records)
    {
        if (records.All(e => e == null)) return "-";
        if (records.Any(e => e?.Status == "MISMATCH")) return "MISMATCH";
        if (records.Any(e => e?.Status == "TIMEOUT")) return "TIMEOUT";
        if (records.Any(e => e?.Status == "ERROR")) return "ERROR";
        if (records.Any(e => e == null)) return "PARTIAL";
        return "OK";
    }

    private static String FormatHash(CsvRecord?[] records)
    {
        var hashes = records.Where(e => e != null && !String.IsNullOrEmpty(e.Hash)).Select(e => e!.Hash).Distinct(StringComparer.Ordinal).ToArray();
        return hashes.Length switch
        {
            0 => "-",
            1 => hashes[0],
            _ => "不一致",
        };
    }

    private static void WriteBaseline(StringBuilder builder, List<CsvRecord> records, String[] databases)
    {
        var baseline = records.Where(e => e.Scale == "baseline").ToList();
        if (baseline.Count == 0) return;

        builder.AppendLine("## 客户端基线（SELECT 1）");
        builder.AppendLine();
        builder.AppendLine("| 库 | p50 (ms) | p95 (ms) | avg (ms) | ops/s | 状态 |");
        builder.AppendLine("|---|---:|---:|---:|---:|---|");

        foreach (var db in databases)
        {
            var record = baseline.FirstOrDefault(e => String.Equals(e.Db, db, StringComparison.OrdinalIgnoreCase));
            if (record == null) continue;

            if (record.IsOk)
                builder.AppendLine($"| {Display(db)} | {record.P50:0.###} | {record.P95:0.###} | {record.Average:0.###} | {record.Ops:0} | OK |");
            else
                builder.AppendLine($"| {Display(db)} | - | - | - | - | {record.Status} |");
        }

        builder.AppendLine();
    }

    private static void WriteCoverage(StringBuilder builder, List<CsvRecord> records, String[] databases)
    {
        if (databases.Length < 2) return;

        var mismatch = records.Count(e => e.Status == "MISMATCH");
        builder.AppendLine("## 结果一致性");
        builder.AppendLine();
        builder.AppendLine(mismatch == 0
            ? "当前 CSV 内所有已完成的同查询结果哈希一致。"
            : $"当前 CSV 内共有 {mismatch} 条 MISMATCH 记录，详见各表“状态”列。");
        builder.AppendLine();
    }

    private static String Display(String db) => db.ToLowerInvariant() switch
    {
        "nova" => "NovaDb",
        "sqlite" => "SQLite",
        "mysql" => "MySQL",
        _ => db,
    };

    private static String DescribeScale(String scale) => scale switch
    {
        "tiny" => "冒烟",
        "small" => "小数据量",
        "medium" => "中数据量",
        "large" => "大数据量",
        _ => "",
    };

    private static String RelativePath(String root, String path)
    {
        try
        {
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }
        catch
        {
            return path;
        }
    }
}
