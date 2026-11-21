using System.Globalization;
using System.Text;

namespace NewLife.QueryBench;

/// <summary>CSV 记录。逐行追加写，支持中断续跑</summary>
public sealed class CsvRecord
{
    /// <summary>规模</summary>
    public String Scale { get; set; } = "";

    /// <summary>数据库</summary>
    public String Db { get; set; } = "";

    /// <summary>表</summary>
    public String Table { get; set; } = "";

    /// <summary>查询编号</summary>
    public String Query { get; set; } = "";

    /// <summary>结果行数</summary>
    public Int64 Rows { get; set; }

    /// <summary>完成迭代次数</summary>
    public Int32 Iterations { get; set; }

    /// <summary>p50（毫秒）</summary>
    public Double P50 { get; set; }

    /// <summary>p95（毫秒）</summary>
    public Double P95 { get; set; }

    /// <summary>平均（毫秒）</summary>
    public Double Average { get; set; }

    /// <summary>每秒操作数</summary>
    public Double Ops { get; set; }

    /// <summary>结果集哈希</summary>
    public String Hash { get; set; } = "";

    /// <summary>状态：OK/TIMEOUT/ERROR/MISMATCH</summary>
    public String Status { get; set; } = "";

    /// <summary>附加说明</summary>
    public String Detail { get; set; } = "";

    /// <summary>记录时间</summary>
    public String Timestamp { get; set; } = "";

    /// <summary>是否成功</summary>
    public Boolean IsOk => Status == "OK";
}

/// <summary>CSV 读写</summary>
public static class CsvStore
{
    private static readonly String[] _headers =
    [
        "scale", "db", "table", "query", "rows", "iterations",
        "p50_ms", "p95_ms", "avg_ms", "ops", "hash", "status", "detail", "timestamp",
    ];

    /// <summary>读取 CSV。文件不存在时返回空列表</summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public static List<CsvRecord> Load(String path)
    {
        var list = new List<CsvRecord>();
        if (!File.Exists(path)) return list;

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length <= 1) return list;

        for (var i = 1; i < lines.Length; i++)
        {
            if (String.IsNullOrWhiteSpace(lines[i])) continue;

            var fields = ParseLine(lines[i]);
            if (fields.Count < _headers.Length) continue;

            list.Add(new CsvRecord
            {
                Scale = fields[0],
                Db = fields[1],
                Table = fields[2],
                Query = fields[3],
                Rows = ParseInt64(fields[4]),
                Iterations = (Int32)ParseInt64(fields[5]),
                P50 = ParseDouble(fields[6]),
                P95 = ParseDouble(fields[7]),
                Average = ParseDouble(fields[8]),
                Ops = ParseDouble(fields[9]),
                Hash = fields[10],
                Status = fields[11],
                Detail = fields[12],
                Timestamp = fields[13],
            });
        }

        return list;
    }

    /// <summary>追加记录。不存在时先写表头</summary>
    /// <param name="path"></param>
    /// <param name="records"></param>
    public static void Append(String path, IReadOnlyList<CsvRecord> records)
    {
        if (records.Count == 0) return;

        var dir = Path.GetDirectoryName(path);
        if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var exists = File.Exists(path) && new FileInfo(path).Length > 0;
        using var writer = new StreamWriter(path, true, new UTF8Encoding(false));
        if (!exists) writer.WriteLine(String.Join(",", _headers));

        foreach (var record in records)
        {
            writer.WriteLine(String.Join(",", [
                Escape(record.Scale),
                Escape(record.Db),
                Escape(record.Table),
                Escape(record.Query),
                record.Rows.ToString(CultureInfo.InvariantCulture),
                record.Iterations.ToString(CultureInfo.InvariantCulture),
                record.P50.ToString("0.###", CultureInfo.InvariantCulture),
                record.P95.ToString("0.###", CultureInfo.InvariantCulture),
                record.Average.ToString("0.###", CultureInfo.InvariantCulture),
                record.Ops.ToString("0.###", CultureInfo.InvariantCulture),
                Escape(record.Hash),
                Escape(record.Status),
                Escape(record.Detail),
                Escape(record.Timestamp),
            ]));
        }
    }

    private static String Escape(String text)
    {
        if (text.IndexOfAny([',', '"', '\r', '\n']) < 0) return text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static List<String> ParseLine(String line)
    {
        var fields = new List<String>();
        var builder = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        builder.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    builder.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == ',')
            {
                fields.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(ch);
            }
        }

        fields.Add(builder.ToString());
        return fields;
    }

    private static Int64 ParseInt64(String text) => Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static Double ParseDouble(String text) => Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
