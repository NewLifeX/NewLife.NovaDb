using BenchmarkDotNet.Attributes;
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
        _store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, _storePath);
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
        try { Directory.Delete(_storePath, true); } catch { }
    }

    [Benchmark(Description = "Set 写入")]
    public void Set()
    {
        var id = Interlocked.Increment(ref _counter);
        _store.Set($"key:{id}", _value);
    }

    [Benchmark(Description = "Get 读取")]
    public Byte[]? Get()
    {
        return _store.Get("key:500");
    }

    [Benchmark(Description = "Set+Get 读写混合")]
    public Byte[]? SetThenGet()
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
}
