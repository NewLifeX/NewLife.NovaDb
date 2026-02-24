using BenchmarkDotNet.Attributes;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.KV;

namespace Benchmark;

/// <summary>KV 引擎基准测试</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class KvStoreBenchmark
{
    private String _storePath = null!;
    private KvStore _store = null!;
    private Int32 _counter;
    private Byte[] _value = null!;

    [Params(64, 1024)]
    public Int32 ValueSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _storePath = Path.Combine(Path.GetTempPath(), $"NovaBench_Kv_{ValueSize}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storePath);
        var kvFile = Path.Combine(_storePath, "bench.kvd");
        _store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        _value = new Byte[ValueSize];
        Random.Shared.NextBytes(_value);

        // 预置数据
        for (var i = 0; i < 1000; i++)
            _store.Set($"key:{i}", _value);

        _counter = 2000;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store?.Dispose();
        try { Directory.Delete(_storePath, true); } catch { }
    }

    [Benchmark(Description = "Set 写入")]
    public void Set()
    {
        var id = Interlocked.Increment(ref _counter);
        _store.Set($"key:{id}", _value);
    }

    [Benchmark(Description = "Get 读取")]
    public IOwnerPacket? Get()
    {
        return _store.Get("key:500");
    }

    [Benchmark(Description = "Set+Get 读写混合")]
    public IOwnerPacket? SetThenGet()
    {
        var id = Interlocked.Increment(ref _counter);
        var key = $"key:{id}";
        _store.Set(key, _value);
        return _store.Get(key);
    }

    [Benchmark(Description = "Delete 删除")]
    public void Delete()
    {
        var id = Interlocked.Increment(ref _counter);
        _store.Set($"del:{id}", _value);
        _store.Delete($"del:{id}");
    }

    [Benchmark(Description = "Exists 存在检查")]
    public Boolean Exists()
    {
        return _store.Exists("key:500");
    }

    [Benchmark(Description = "Inc 原子递增")]
    public Int64 Inc()
    {
        return _store.Inc("counter:bench", 1);
    }

    [Benchmark(Description = "IncDouble 浮点递增")]
    public Double IncDouble()
    {
        return _store.IncDouble("dcounter:bench", 1.5);
    }

    [Benchmark(Description = "Search 模式搜索")]
    public Int32 Search()
    {
        var count = 0;
        foreach (var _ in _store.Search("key:1*", 0, 10))
            count++;
        return count;
    }

    [Benchmark(Description = "SetAll 批量写入(10)")]
    public void SetAll()
    {
        var id = Interlocked.Increment(ref _counter);
        var dict = new Dictionary<String, Byte[]?>(10);
        for (var i = 0; i < 10; i++)
            dict[$"batch:{id}:{i}"] = _value;
        _store.SetAll(dict);
    }

    [Benchmark(Description = "GetAll 批量读取(10)")]
    public IDictionary<String, IOwnerPacket?> GetAll()
    {
        var keys = new List<String>(10);
        for (var i = 0; i < 10; i++)
            keys.Add($"key:{i}");
        return _store.GetAll(keys);
    }

    [Benchmark(Description = "SetExpiration TTL设置")]
    public Boolean SetExpiration()
    {
        return _store.SetExpiration("key:100", TimeSpan.FromMinutes(5));
    }

    [Benchmark(Description = "GetTtl TTL查询")]
    public TimeSpan GetTtl()
    {
        return _store.GetTtl("key:100");
    }
}
