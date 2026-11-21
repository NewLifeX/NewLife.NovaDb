using System.Globalization;

namespace NewLife.QueryBench;

/// <summary>基准命令行选项</summary>
public sealed class BenchOptions
{
    /// <summary>规模与行数对照</summary>
    public static readonly Dictionary<String, Int64> ScaleRows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tiny"] = 100,
        ["small"] = 10_000,
        ["medium"] = 1_000_000,
        ["large"] = 20_000_000,
    };

    /// <summary>全部表名</summary>
    public static readonly String[] AllTables = ["t_noidx", "t_singleidx", "t_composite"];

    /// <summary>全部数据库名</summary>
    public static readonly String[] AllDatabases = ["nova", "sqlite", "mysql"];

    /// <summary>数据库列表</summary>
    public List<String> Databases { get; } = ["nova", "sqlite", "mysql"];

    /// <summary>规模列表</summary>
    public List<String> Scales { get; } = ["tiny", "small"];

    /// <summary>表列表</summary>
    public List<String> Tables { get; } = [.. AllTables];

    /// <summary>预热次数</summary>
    public Int32 Warmup { get; set; } = 3;

    /// <summary>单查询最大迭代次数</summary>
    public Int32 Iterations { get; set; } = 200;

    /// <summary>单查询累计测量预算（秒）</summary>
    public Double BudgetSeconds { get; set; } = 30;

    /// <summary>单条查询超时（秒）</summary>
    public Int32 TimeoutSeconds { get; set; } = 30;

    /// <summary>CSV 输出目录</summary>
    public String OutDir { get; set; } = "Doc/性能/data";

    /// <summary>数据库文件根目录（Bin 已忽略）</summary>
    public String DataDir { get; set; } = "Bin/Benchmark/QueryBenchData";

    /// <summary>报告输出文件</summary>
    public String ReportFile { get; set; } = "Doc/性能/查询性能对比测试报告.md";

    /// <summary>MySQL 连接配置文件</summary>
    public String MySqlFile { get; set; } = "Bin/Benchmark/Config/mysql.config";

    /// <summary>MySQL 连接串（优先级最高）</summary>
    public String? MySqlConnectionString { get; set; }

    /// <summary>保留数据（不清理）</summary>
    public Boolean Keep { get; set; }

    /// <summary>忽略已完成组合，强制重跑</summary>
    public Boolean Force { get; set; }

    /// <summary>只装载数据，不执行查询</summary>
    public Boolean LoadOnly { get; set; }

    /// <summary>运行结束后生成报告</summary>
    public Boolean Report { get; set; }

    /// <summary>只生成报告，不跑基准</summary>
    public Boolean ReportOnly { get; set; }

    /// <summary>启用 Q11 两表连接</summary>
    public Boolean IncludeJoin { get; set; }

    /// <summary>显示帮助</summary>
    public Boolean Help { get; set; }

    /// <summary>仓库根目录</summary>
    public String RepositoryRoot { get; private set; } = FindRepositoryRoot();

    /// <summary>CSV 文件路径</summary>
    public String CsvFile => Path.Combine(ResolveOutputPath(OutDir), "query_bench.csv");

    /// <summary>数据库文件根目录绝对路径</summary>
    public String DataRoot => ResolveOutputPath(DataDir);

    /// <summary>查找仓库根目录</summary>
    /// <returns></returns>
    private static String FindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!String.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "NewLife.NovaDb.sln"))) return dir;

            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>解析输入路径。相对路径先按当前目录，再按仓库根目录</summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public String ResolveInputPath(String path)
    {
        if (Path.IsPathRooted(path)) return path;

        var current = Path.GetFullPath(path);
        if (File.Exists(current) || Directory.Exists(current)) return current;

        return Path.GetFullPath(Path.Combine(RepositoryRoot, path));
    }

    /// <summary>解析输出路径。相对路径固定相对仓库根目录，避免受启动目录影响</summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public String ResolveOutputPath(String path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(RepositoryRoot, path));

    /// <summary>解析命令行</summary>
    /// <param name="args"></param>
    /// <param name="options"></param>
    /// <param name="error"></param>
    /// <returns></returns>
    public static Boolean TryParse(String[] args, out BenchOptions options, out String? error)
    {
        options = new BenchOptions();
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            String Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"选项 {arg} 缺少取值");
                return args[++i];
            }

            try
            {
                switch (arg.ToLowerInvariant())
                {
                    case "--db":
                        options.Databases.Clear();
                        options.Databases.AddRange(SplitList(Next(), "数据库"));
                        break;
                    case "--scale":
                        options.Scales.Clear();
                        options.Scales.AddRange(SplitList(Next(), "规模"));
                        break;
                    case "--table":
                        options.Tables.Clear();
                        var tables = SplitList(Next(), "表");
                        foreach (var item in tables)
                        {
                            if (String.Equals(item, "all", StringComparison.OrdinalIgnoreCase)) options.Tables.AddRange(AllTables);
                            else options.Tables.Add(item);
                        }
                        break;
                    case "--warmup":
                        options.Warmup = ParseInt(Next(), arg);
                        break;
                    case "--iterations":
                        options.Iterations = ParseInt(Next(), arg);
                        break;
                    case "--budget":
                        options.BudgetSeconds = ParseDouble(Next(), arg);
                        break;
                    case "--timeout":
                        options.TimeoutSeconds = ParseInt(Next(), arg);
                        break;
                    case "--out":
                        options.OutDir = Next();
                        break;
                    case "--data-dir":
                        options.DataDir = Next();
                        break;
                    case "--report-out":
                        options.ReportFile = Next();
                        break;
                    case "--mysql":
                        options.MySqlConnectionString = Next();
                        break;
                    case "--mysql-file":
                        options.MySqlFile = Next();
                        break;
                    case "--keep":
                        options.Keep = true;
                        break;
                    case "--force":
                        options.Force = true;
                        break;
                    case "--load-only":
                        options.LoadOnly = true;
                        break;
                    case "--report":
                        options.Report = true;
                        break;
                    case "--report-only":
                        options.ReportOnly = true;
                        options.Report = true;
                        break;
                    case "--include-join":
                        options.IncludeJoin = true;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        options.Help = true;
                        break;
                    default:
                        throw new ArgumentException($"未知选项：{arg}");
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        foreach (var db in options.Databases.ToArray())
        {
            var canonical = AllDatabases.FirstOrDefault(e => String.Equals(e, db, StringComparison.OrdinalIgnoreCase));
            if (canonical == null)
            {
                error = $"不支持的数据库：{db}（可选 {String.Join(",", AllDatabases)}）";
                return false;
            }

            options.Databases[options.Databases.IndexOf(db)] = canonical;
        }

        foreach (var scale in options.Scales.ToArray())
        {
            var canonical = ScaleRows.Keys.FirstOrDefault(e => String.Equals(e, scale, StringComparison.OrdinalIgnoreCase));
            if (canonical == null)
            {
                error = $"不支持的规模：{scale}（可选 {String.Join(",", ScaleRows.Keys)}）";
                return false;
            }

            options.Scales[options.Scales.IndexOf(scale)] = canonical;
        }

        foreach (var table in options.Tables.ToArray())
        {
            var canonical = AllTables.FirstOrDefault(e => String.Equals(e, table, StringComparison.OrdinalIgnoreCase));
            if (canonical == null)
            {
                error = $"不支持的表：{table}（可选 {String.Join(",", AllTables)}）";
                return false;
            }

            options.Tables[options.Tables.IndexOf(table)] = canonical;
        }

        if (options.Warmup < 0) error = "--warmup 不能小于 0";
        else if (options.Iterations < 1) error = "--iterations 至少为 1";
        else if (options.BudgetSeconds <= 0) error = "--budget 必须大于 0";
        else if (options.TimeoutSeconds <= 0) error = "--timeout 必须大于 0";
        else if (options.Databases.Count == 0) error = "--db 不能为空";
        else if (options.Scales.Count == 0) error = "--scale 不能为空";
        else if (options.Tables.Count == 0) error = "--table 不能为空";

        return error == null;
    }

    /// <summary>打印用法</summary>
    public static void PrintUsage()
    {
        Console.WriteLine("""
            三库查询性能对比基准（NovaDb / SQLite / MySQL）

            用法：
              QueryBench [选项]

            选项：
              --db nova,sqlite,mysql        参与对比的数据库（默认全部）
              --scale tiny,small            数据规模：tiny=100 small=10000 medium=1000000 large=20000000
              --table all                   表：t_noidx,t_singleidx,t_composite,all
              --warmup 3                    每个查询的预热次数
              --iterations 200              每个查询的最大迭代次数
              --budget 30                   每个查询的累计测量预算（秒），与 iterations 先到者止
              --timeout 30                  单条查询超时（秒）
              --out Doc/性能/data           CSV 输出目录
              --data-dir <dir>              数据库文件目录（默认 Bin/Benchmark/QueryBenchData）
              --report-out <file>           报告输出文件（默认 Doc/性能/查询性能对比测试报告.md）
              --mysql <connectionString>    MySQL 连接串，优先级最高
              --mysql-file <file>           MySQL 连接配置文件（默认 Bin/Benchmark/Config/mysql.config）
              --keep                        保留数据（默认每个组合跑完即清理）
              --force                       忽略 CSV 中已完成的组合，强制重跑
              --load-only                   只装载数据，不执行查询，且不清理
              --report                      运行结束后生成 Markdown 报告
              --report-only                 只根据已有 CSV 生成报告
              --include-join                追加 Q11 两表连接查询
              --help                        显示帮助

            环境变量：
              QUERYBENCH_MYSQL              MySQL 连接串，优先级低于 --mysql、高于配置文件
            """);
    }

    private static String[] SplitList(String text, String name)
    {
        var items = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length == 0) throw new ArgumentException($"{name}列表不能为空");
        return items;
    }

    private static Int32 ParseInt(String text, String name)
    {
        if (!Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"{name} 需要整数，实际为 {text}");
        return value;
    }

    private static Double ParseDouble(String text, String name)
    {
        if (!Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"{name} 需要数字，实际为 {text}");
        return value;
    }
}
