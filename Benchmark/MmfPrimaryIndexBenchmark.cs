using BenchmarkDotNet.Attributes;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;

namespace Benchmark;

/// <summary>主键索引（MMF 哈希 + 磁盘有序段）基准测试</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class MmfPrimaryIndexBenchmark
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
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaBench_MmfIdx_{RowCount}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbPath);
        _options = new DbOptions { Path = _dbPath, WalMode = WalMode.None };

        _engine = new SqlEngine(_dbPath, _options);
        _engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, name VARCHAR(30), score INT)");
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

    [Benchmark(Description = "等值点查（走 .hidx）")]
    public Object? PointLookup()
    {
        var id = Random.Shared.Next(1, RowCount + 1);

        return _engine.Execute($"SELECT name FROM t WHERE id = {id}").GetScalar();
    }

    [Benchmark(Description = "主键范围扫描（走 .oidx）")]
    public Int32 RangeScan()
    {
        var from = Random.Shared.Next(1, RowCount / 2);

        return _engine.Execute($"SELECT id FROM t WHERE id >= {from} AND id < {from} + 1000").Rows.Count;
    }

    [Benchmark(Description = "插入单行")]
    public void Insert()
    {
        var id = Interlocked.Increment(ref _counter);
        _engine.Execute($"INSERT INTO t (id, name, score) VALUES ({id}, 'name{id}', {id % 100})");
    }
}
