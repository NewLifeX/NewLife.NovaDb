// NewLife.NovaDb NativeAOT compatibility smoke test.
// Runs a fixed set of scenarios against the library and reports PASS/FAIL per scenario.
// Same binary is run under JIT (dotnet run) and NativeAOT (publish -r <rid>) to compare.
//
// 说明：AOT 能力验收冒烟件，由 .github/workflows/aot.yml 的 aot（win-x64/linux-x64/linux-arm64）
// 与 loongarch64（QEMU）作业自动执行（非 0 退出码即失败）。
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NewLife;
using NewLife.Caching;
using NewLife.Data;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Engine.Flux;
using NewLife.NovaDb.Server;
using NewLife.NovaDb.Sql;
using NewLife.NovaDb.Storage;
using NewLife.NovaDb.WAL;

try { Console.OutputEncoding = Encoding.UTF8; } catch { }

Console.WriteLine("=== NewLife.NovaDb AOT smoke ===");
Console.WriteLine($"framework={RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"IsDynamicCodeSupported={RuntimeFeature.IsDynamicCodeSupported} IsDynamicCodeCompiled={RuntimeFeature.IsDynamicCodeCompiled}");
Console.WriteLine($"STJ reflection enabled={System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault}");

var results = new List<(String Name, Boolean Ok, String Error)>();
void Run(String name, Action action)
{
    try
    {
        action();
        results.Add((name, true, ""));
        Console.WriteLine($"[PASS] {name}");
    }
    catch (Exception ex)
    {
        results.Add((name, false, $"{ex.GetType().Name}: {ex.Message}"));
        Console.WriteLine($"[FAIL] {name} -> {ex.GetType().Name}: {ex.Message}");
    }
}

var temp = Path.Combine(Path.GetTempPath(), "nova-aot-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(temp);
Console.WriteLine($"[diag] temp={temp}");

// 复杂值类型经 JSON 反射编解码：NativeAOT 需在消费方显式保留类型成员（NewLife.Caching 接口签名无裁剪注解）
[DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties, typeof(SmokeUser))]
static void PreserveSmokeUser() { }
PreserveSmokeUser();

Run("基础类型转换 Utility", () =>
{
    if ("123".ToInt() != 123) throw new Exception("ToInt");
    if ("3.14".ToDouble() != 3.14) throw new Exception("ToDouble");
    if ("true".ToBoolean() != true) throw new Exception("ToBoolean");
    if ("2026-10-16".ToDateTime().Year != 2026) throw new Exception("ToDateTime");
});

Run("嵌入式 ADO.NET DDL/DML/分页查询", () =>
{
    var dir = Path.Combine(temp, "rel");
    using var conn = new NovaConnection { ConnectionString = $"Data Source={dir};WalMode=Full" };
    conn.Open();
    using var cmd = conn.CreateCommand();

    cmd.CommandText = "CREATE TABLE smoke_users (id INT PRIMARY KEY, name VARCHAR(64), age INT)";
    cmd.ExecuteNonQuery();

    cmd.CommandText = "INSERT INTO smoke_users VALUES (1,'Alice',30),(2,'Bob',25),(3,'Cara',41),(4,'Dora',35),(5,'Eve',28)";
    if (cmd.ExecuteNonQuery() != 5) throw new Exception("INSERT 行数不为 5");

    cmd.CommandText = "SELECT name FROM smoke_users WHERE age >= 28 ORDER BY age DESC LIMIT 2 OFFSET 1";
    using (var reader = cmd.ExecuteReader())
    {
        if (!reader.Read()) throw new Exception("分页查询无结果");
        if (reader.GetString(0) != "Dora") throw new Exception($"分页首行为 {reader.GetString(0)}，应为 Dora");
    }

    // 架构表（ADO.NET GetSchemaTable）在 AOT 下可用，DataType 列为运行时类型元数据
    cmd.CommandText = "SELECT id, name FROM smoke_users";
    using (var reader = cmd.ExecuteReader())
    {
        var schema = reader.GetSchemaTable() ?? throw new Exception("架构表为 null");
        if (schema.Rows.Count != 2) throw new Exception($"架构表行数为 {schema.Rows.Count}");
        if (schema.Rows[0][System.Data.Common.SchemaTableColumn.ColumnName] as String != "id") throw new Exception("架构表首列名不符");
    }

    cmd.CommandText = "SELECT COUNT(*) FROM smoke_users";
    if (Convert.ToInt32(cmd.ExecuteScalar()) != 5) throw new Exception("COUNT 不为 5");

    cmd.CommandText = "UPDATE smoke_users SET age = age + 1 WHERE id = 1";
    if (cmd.ExecuteNonQuery() != 1) throw new Exception("UPDATE 行数不为 1");

    cmd.CommandText = "DELETE FROM smoke_users WHERE id = 5";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "SELECT COUNT(*) FROM smoke_users";
    if (Convert.ToInt32(cmd.ExecuteScalar()) != 4) throw new Exception("DELETE 后 COUNT 不为 4");
});

Run("SQL 聚合、函数与 JOIN", () =>
{
    var dir = Path.Combine(temp, "sqlx");
    using var conn = new NovaConnection { ConnectionString = $"Data Source={dir}" };
    conn.Open();
    using var cmd = conn.CreateCommand();

    cmd.CommandText = "CREATE TABLE t_ord (id INT PRIMARY KEY, cat VARCHAR(32), amount DOUBLE)";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "INSERT INTO t_ord VALUES (1,'a',10.5),(2,'a',20.0),(3,'b',5.25),(4,'b',7.75)";
    cmd.ExecuteNonQuery();

    cmd.CommandText = "SELECT cat, COUNT(*), SUM(amount) FROM t_ord GROUP BY cat ORDER BY cat";
    using (var reader = cmd.ExecuteReader())
    {
        if (!reader.Read()) throw new Exception("GROUP BY 无结果");
        if (Convert.ToInt32(reader.GetValue(1)) != 2) throw new Exception("COUNT 不为 2");
    }

    cmd.CommandText = "SELECT UPPER(cat) FROM t_ord WHERE amount > 8 ORDER BY amount LIMIT 1";
    using (var reader = cmd.ExecuteReader())
    {
        if (!reader.Read()) throw new Exception("标量函数无结果");
        if (reader.GetString(0) != "A") throw new Exception($"UPPER 结果为 {reader.GetString(0)}");
    }

    cmd.CommandText = "CREATE TABLE t_cat (cat VARCHAR(32) PRIMARY KEY, title VARCHAR(64))";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "INSERT INTO t_cat VALUES ('a','Alpha'),('b','Beta')";
    cmd.ExecuteNonQuery();

    cmd.CommandText = "SELECT o.id, c.title FROM t_ord o JOIN t_cat c ON o.cat = c.cat WHERE o.id = 3";
    using (var reader = cmd.ExecuteReader())
    {
        if (!reader.Read()) throw new Exception("JOIN 无结果");
        if (reader.GetString(1) != "Beta") throw new Exception($"JOIN 结果为 {reader.GetString(1)}");
    }
});

Run("事务提交与回滚", () =>
{
    var dir = Path.Combine(temp, "tx");
    using var conn = new NovaConnection { ConnectionString = $"Data Source={dir}" };
    conn.Open();
    using var cmd = conn.CreateCommand();

    cmd.CommandText = "CREATE TABLE t_tx (id INT PRIMARY KEY, v INT)";
    cmd.ExecuteNonQuery();

    cmd.CommandText = "BEGIN";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "INSERT INTO t_tx VALUES (1, 100)";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "ROLLBACK";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "SELECT COUNT(*) FROM t_tx";
    if (Convert.ToInt32(cmd.ExecuteScalar()) != 0) throw new Exception("回滚后应为 0 行");

    cmd.CommandText = "BEGIN";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "INSERT INTO t_tx VALUES (2, 200)";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "COMMIT";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "SELECT COUNT(*) FROM t_tx";
    if (Convert.ToInt32(cmd.ExecuteScalar()) != 1) throw new Exception("提交后应为 1 行");
});

Run("系统表、EXPLAIN 与 Binlog 命令", () =>
{
    var dir = Path.Combine(temp, "sys");
    using var conn = new NovaConnection { ConnectionString = $"Data Source={dir};WalMode=Full" };
    conn.Open();
    using var cmd = conn.CreateCommand();

    cmd.CommandText = "CREATE TABLE t_sys (id INT PRIMARY KEY, val VARCHAR(32))";
    cmd.ExecuteNonQuery();

    // 启用 Binlog 并产生变更，验证管理命令
    var engine = conn.SqlEngine!;
    engine.Binlog = new BinlogWriter(Path.Combine(dir, "binlog"), "smokedb");
    cmd.CommandText = "INSERT INTO t_sys VALUES (1, 'x')";
    cmd.ExecuteNonQuery();

    cmd.CommandText = "SHOW BINLOG STATUS";
    using (var reader = cmd.ExecuteReader())
    {
        if (!reader.Read()) throw new Exception("SHOW BINLOG STATUS 无结果");
        var file = Convert.ToString(reader.GetValue(0));
        if (String.IsNullOrEmpty(file)) throw new Exception("Binlog 文件名为空");
    }

    // 系统表
    cmd.CommandText = "SELECT * FROM _sys.tables";
    using (var reader = cmd.ExecuteReader())
    {
        if (!reader.Read()) throw new Exception("_sys.tables 无结果");
    }

    // EXPLAIN
    cmd.CommandText = "EXPLAIN SELECT * FROM t_sys WHERE id = 1";
    using (var reader = cmd.ExecuteReader())
    {
        if (!reader.Read()) throw new Exception("EXPLAIN 无结果");
    }
});

Run("WAL 重放（关闭后重开可见）", () =>
{
    var dir = Path.Combine(temp, "recover");
    using (var conn = new NovaConnection { ConnectionString = $"Data Source={dir};WalMode=Full" })
    {
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE t_rec (id INT PRIMARY KEY, v VARCHAR(16))";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO t_rec VALUES (1,'a'),(2,'b')";
        cmd.ExecuteNonQuery();
    }

    using (var conn = new NovaConnection { ConnectionString = $"Data Source={dir};WalMode=Full" })
    {
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM t_rec";
        if (Convert.ToInt32(cmd.ExecuteScalar()) != 2) throw new Exception("重开后行数不为 2");
    }
});

Run("KV 缓存 NovaCache（ICache）", () =>
{
    var dir = Path.Combine(temp, "kv");
    var cache = NovaClientFactory.Instance.GetCache($"Data Source={dir}", "smoke_kv");
    cache.Set("k1", "v1");
    cache.Set("k2", 42);
    if (cache.Get<String>("k1") != "v1") throw new Exception("Get k1 失败");
    if (cache.Get<Int32>("k2") != 42) throw new Exception("Get k2 失败");

    var n = cache.Increment("counter", 3);
    if (n != 3) throw new Exception($"Increment 结果为 {n}");

    if (cache.Remove("k1") != 1) throw new Exception("Remove 失败");
    if (cache.Get<String>("k1") != null) throw new Exception("Remove 后仍可读取");
});

Run("KV 复杂对象往返（JSON 编解码）", () =>
{
    var dir = Path.Combine(temp, "kvobj");
    var cache = NovaClientFactory.Instance.GetCache($"Data Source={dir}", "smoke_obj");
    cache.Set("user", new SmokeUser { Name = "Alice", Age = 30, Tags = ["a", "b"] });

    var user = cache.Get<SmokeUser>("user");
    if (user == null) throw new Exception("复杂对象读取为 null");
    if (user.Name != "Alice" || user.Age != 30) throw new Exception($"对象字段不一致: {user.Name}/{user.Age}");
    if (user.Tags == null || user.Tags.Length != 2) throw new Exception("数组字段往返失败");
});

Run("MQ 队列 NovaQueue（IProducerConsumer）", () =>
{
    var dir = Path.Combine(temp, "mq");
    var queue = NovaClientFactory.Instance.GetQueue<String>($"Data Source={dir}", "smoke_topic", "smoke_group");
    queue.Add("m1", "m2", "m3");

    var got = queue.Take(10).ToList();
    if (got.Count != 3) throw new Exception($"Take 数量为 {got.Count}");
    if (got[0] != "m1") throw new Exception($"首条消息为 {got[0]}");
});

Run("时序列式封存、滚动聚合与窗口分组", () =>
{
    var dir = Path.Combine(temp, "fluxwin");
    var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).Ticks;
    var minute = TimeSpan.FromMinutes(1).Ticks;

    using var flux = new FluxEngine(Path.Combine(dir, "flux"), new DbOptions { FluxPartitionHours = 24 });
    for (var i = 0; i < 120; i++)
    {
        flux.Append(new FluxEntry
        {
            Timestamp = start + i * minute,
            Fields = new Dictionary<String, Object?> { ["value"] = (Double)(i + 1) },
        });
    }

    // 列式封存与压缩率统计
    if (flux.SealPartitions(sealAll: true) != 1) throw new Exception("封存分区数量不为 1");
    var stats = flux.GetCompressionStats();
    if (stats.EntryCount != 120 || stats.BlockBytes <= 0 || stats.RowBytes <= 0) throw new Exception("压缩统计异常");

    using var sql = new SqlEngine(Path.Combine(dir, "sql"), new DbOptions { WalMode = WalMode.None }) { FluxProvider = () => flux };
    var report = sql.Execute("SELECT * FROM _sys.flux_compression");
    if (report.Rows.Count == 0) throw new Exception("压缩率报告为空");

    // 滚动聚合回填与查询自动路由
    using var manager = new ContinuousQueryManager(flux, sql);
    manager.RegisterRollup("smoke_rollup", "sensor", "value", [minute, TimeSpan.FromHours(1).Ticks], "rollup_sensor", start);
    manager.Backfill("smoke_rollup", start, start + 2 * TimeSpan.FromHours(1).Ticks);
    if (manager.ResolveTargetTable("sensor", "value", TimeSpan.FromHours(1).Ticks) == null) throw new Exception("聚合查询未命中预聚合表");

    flux.Clear();
    var avg = manager.QueryAggregate("sensor", start, start + 2 * TimeSpan.FromHours(1).Ticks, TimeSpan.FromHours(1).Ticks, "value", "avg");
    if (avg.Count != 2 || Math.Abs(avg[0].Value - 30.5) > 0.0001) throw new Exception($"自动路由结果异常: {avg.Count}");

    // 会话/状态窗口分组
    sql.Execute("CREATE TABLE states (id INT PRIMARY KEY, ts DATETIME, status VARCHAR)");
    sql.Execute("INSERT INTO states VALUES (1, '2026-01-01 08:00:00', 'idle')");
    sql.Execute("INSERT INTO states VALUES (2, '2026-01-01 08:10:00', 'idle')");
    sql.Execute("INSERT INTO states VALUES (3, '2026-01-01 09:00:00', 'busy')");
    sql.Execute("INSERT INTO states VALUES (4, '2026-01-01 09:05:00', 'idle')");

    var sessions = sql.Execute("SELECT SESSION_WINDOW(ts, 300) AS session, COUNT(*) FROM states GROUP BY SESSION_WINDOW(ts, 300)");
    if (sessions.Rows.Count != 3) throw new Exception($"会话窗口数量为 {sessions.Rows.Count}");

    var stateWindows = sql.Execute("SELECT STATE_WINDOW(status) AS win, COUNT(*) FROM states GROUP BY STATE_WINDOW(status)");
    if (stateWindows.Rows.Count != 3) throw new Exception($"状态窗口数量为 {stateWindows.Rows.Count}");
});

Run("向量存储 Top-K 检索", () =>
{
    var dir = Path.Combine(temp, "vec");
    var store = NovaClientFactory.Instance.GetVectorStore($"Data Source={dir}");
    var col = store.GetCollection("smoke");
    col.UpsertAsync(new VectorRecord { Id = "a", Vector = [1f, 0f, 0f] }).GetAwaiter().GetResult();
    col.UpsertAsync(new VectorRecord { Id = "b", Vector = [0f, 1f, 0f] }).GetAwaiter().GetResult();
    col.UpsertAsync(new VectorRecord { Id = "c", Vector = [0.9f, 0.1f, 0f] }).GetAwaiter().GetResult();

    var result = col.SearchAsync([1f, 0f, 0f], top: 2).GetAwaiter().GetResult();
    if (result.Count != 2) throw new Exception($"Top-2 结果为 {result.Count} 条");
    if (result[0].Record.Id != "a") throw new Exception($"最近邻为 {result[0].Record.Id}");
});

Run("全文检索 BM25", () =>
{
    var idx = new FullTextIndex("smoke_idx", ["title"]);
    idx.Add(1, "NewLife database engine");
    idx.Add(2, "another document about nothing");

    var result = idx.Search("database");
    if (result.Count == 0) throw new Exception("检索无命中");
});

Run("Geo 坐标转换与 geohash", () =>
{
    var (lat, lon) = GeoHelper.Wgs84ToGcj02(39.908823, 116.397470);
    if (lat <= 0 || lon <= 0) throw new Exception("坐标系转换结果异常");

    var hash = GeoHelper.EncodeGeohash(39.908823, 116.397470, 6);
    if (String.IsNullOrEmpty(hash) || hash.Length != 6) throw new Exception($"geohash 异常: {hash}");
});

Run("备份、校验与恢复", () =>
{
    var dir = Path.Combine(temp, "bk_src");
    using (var conn = new NovaConnection { ConnectionString = $"Data Source={dir}" })
    {
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE t_bk (id INT PRIMARY KEY, v INT)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO t_bk VALUES (1, 1)";
        cmd.ExecuteNonQuery();
    }

    var bak = Path.Combine(temp, "bk_out");
    var result = BackupManager.Backup(dir, bak);
    if (String.IsNullOrEmpty(result.BackupPath)) throw new Exception("BackupPath 为空");
    if (!BackupManager.VerifyBackup(bak)) throw new Exception("备份校验失败");

    var restored = Path.Combine(temp, "bk_restore");
    BackupManager.Restore(bak, restored);

    using var conn2 = new NovaConnection { ConnectionString = $"Data Source={restored}" };
    conn2.Open();
    using var cmd2 = conn2.CreateCommand();
    cmd2.CommandText = "SELECT COUNT(*) FROM t_bk";
    if (Convert.ToInt32(cmd2.ExecuteScalar()) != 1) throw new Exception("恢复后数据不一致");
});

Run("网络模式 NovaServer + NovaClient RPC", () =>
{
    var dir = Path.Combine(temp, "srv");
    using var server = new NovaServer(0) { DbPath = dir };
    server.Start();
    if (server.Port <= 0) throw new Exception("服务器端口无效");

    using (var conn = new NovaConnection { ConnectionString = $"Server=127.0.0.1;Port={server.Port}" })
    {
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE net_t (id INT PRIMARY KEY, v VARCHAR(32))";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO net_t VALUES (1, 'net')";
        if (cmd.ExecuteNonQuery() != 1) throw new Exception("网络 INSERT 失败");

        cmd.CommandText = "SELECT v FROM net_t WHERE id = 1";
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != "net") throw new Exception("网络 SELECT 失败");
        }

        cmd.CommandText = "SHOW DATABASES";
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.FieldCount != 1) throw new Exception($"网络 SHOW DATABASES 列数为 {reader.FieldCount}");
        }
    }

    using (var client = new NovaClient($"tcp://127.0.0.1:{server.Port}"))
    {
        client.Open();
        var pong = client.PingAsync().GetAwaiter().GetResult();
        if (pong.IsNullOrEmpty()) throw new Exception("Ping 失败");
        client.Close();
    }

    server.Stop();
});

var failed = 0;
foreach (var r in results)
{
    if (!r.Ok) failed++;
}

Console.WriteLine($"=== 结果：{results.Count - failed}/{results.Count} 通过 ===");
if (failed > 0)
{
    foreach (var r in results)
    {
        if (!r.Ok) Console.WriteLine($"  FAIL {r.Name}: {r.Error}");
    }
    return 1;
}

try { Directory.Delete(temp, true); } catch { }
return 0;

/// <summary>冒烟用复杂值类型</summary>
class SmokeUser
{
    public String Name { get; set; } = String.Empty;
    public Int32 Age { get; set; }
    public String[]? Tags { get; set; }
}
