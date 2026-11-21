using BenchmarkDotNet.Attributes;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;

namespace Benchmark;

/// <summary>二级索引（MMF 哈希字典 + 惰性有序层）基准测试</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class MmfSecondaryIndexBenchmark
{
    private String _dbPath = null!;
    private SqlEngine _engine = null!;
    private DbOptions _options = null!;
    private Int32 _counter;

    [Params(20000)]
    public Int32 RowCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaBench_MmfSecIdx_{RowCount}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbPath);
        _options = new DbOptions { Path = _dbPath, WalMode = WalMode.None };

        _engine = new SqlEngine(_dbPath, _options);
        _engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30), score INT NOT NULL)");
        _engine.Execute("CREATE INDEX idx_score ON t (score)");
        for (var i = 1; i <= RowCount; i++)
            _engine.Execute($"INSERT INTO t (id, name, score) VALUES ({i}, 'name{i}', {i % 100})");

        _counter = RowCount;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _engine?.Dispose();
        try { Directory.Delete(_dbPath, true); } catch { }
    }

    [Benchmark(Description = "二级等值查询（走 .hidx）")]
    public Int32 IndexLookup()
    {
        var score = Random.Shared.Next(0, 100);

        return _engine.Execute($"SELECT id FROM t WHERE score = {score}").Rows.Count;
    }

    [Benchmark(Description = "二级等值计数（index-only）")]
    public Object? IndexOnlyCount() => _engine.Execute("SELECT COUNT(*) FROM t WHERE score = 42").GetScalar();

    [Benchmark(Description = "按索引顺序取前 100 行（首次调用构建有序层）")]
    public Int32 OrderedScan() => _engine.Execute("SELECT id FROM t ORDER BY score LIMIT 100").Rows.Count;

    [Benchmark(Description = "按索引顺序分页（单列 + OFFSET）")]
    public Int32 OrderedPage()
    {
        var skip = Random.Shared.Next(0, 200);

        return _engine.Execute($"SELECT id FROM t ORDER BY score LIMIT 14 OFFSET {skip}").Rows.Count;
    }

    [Benchmark(Description = "带二级索引插入单行")]
    public void Insert()
    {
        var id = Interlocked.Increment(ref _counter);
        _engine.Execute($"INSERT INTO t (id, name, score) VALUES ({id}, 'name{id}', {id % 100})");
    }
}
