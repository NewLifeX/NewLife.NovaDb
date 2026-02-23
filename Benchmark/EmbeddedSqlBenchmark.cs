using BenchmarkDotNet.Attributes;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;

namespace Benchmark;

/// <summary>嵌入模式 SQL 引擎基准测试</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class EmbeddedSqlBenchmark
{
    private String _dbPath = null!;
    private SqlEngine _engine = null!;
    private Int32 _counter;

    [Params(WalMode.None, WalMode.Normal)]
    public WalMode WalMode { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaBench_Sql_{WalMode}_{Guid.NewGuid():N}");
        _engine = new SqlEngine(_dbPath, new DbOptions { Path = _dbPath, WalMode = WalMode });
        _engine.Execute("CREATE TABLE bench (id INT PRIMARY KEY, name VARCHAR, age INT, score DOUBLE)");

        // 预置查询数据
        for (var i = 1; i <= 1000; i++)
            _engine.Execute($"INSERT INTO bench VALUES ({i}, 'user{i}', {20 + i % 50}, {60.0 + i % 40})");

        _counter = 2000;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _engine.Dispose();
        try { Directory.Delete(_dbPath, true); } catch { }
    }

    [Benchmark(Description = "Insert 单行插入")]
    public void Insert()
    {
        var id = Interlocked.Increment(ref _counter);
        _engine.Execute($"INSERT INTO bench VALUES ({id}, 'bench{id}', 25, 88.5)");
    }

    [Benchmark(Description = "Select 主键查询")]
    public void SelectByPK()
    {
        _engine.Execute("SELECT * FROM bench WHERE id = 500");
    }

    [Benchmark(Description = "Select 范围查询")]
    public void SelectRange()
    {
        _engine.Execute("SELECT * FROM bench WHERE age > 30 AND age < 50");
    }

    [Benchmark(Description = "Update 单行更新")]
    public void Update()
    {
        _engine.Execute("UPDATE bench SET score = 99.9 WHERE id = 100");
    }

    [Benchmark(Description = "Delete 单行删除 + 重插")]
    public void DeleteAndReinsert()
    {
        var id = Interlocked.Increment(ref _counter);
        _engine.Execute($"INSERT INTO bench VALUES ({id}, 'tmp', 20, 70.0)");
        _engine.Execute($"DELETE FROM bench WHERE id = {id}");
    }

    [Benchmark(Description = "聚合查询 COUNT/AVG")]
    public void Aggregate()
    {
        _engine.Execute("SELECT COUNT(*), AVG(score) FROM bench WHERE age >= 25");
    }
}
