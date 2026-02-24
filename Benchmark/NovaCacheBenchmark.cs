using BenchmarkDotNet.Attributes;
using NewLife.NovaDb.Caching;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.KV;

namespace Benchmark;

/// <summary>NovaCache 缓存层基准测试（ICache 接口级别）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class NovaCacheBenchmark
{
    private String _storePath = null!;
    private KvStore _store = null!;
    private NovaCache _cache = null!;
    private Int32 _counter;

    [Params(64, 1024)]
    public Int32 ValueSize { get; set; }

    private String _stringValue = null!;
    private Byte[] _bytesValue = null!;

    [GlobalSetup]
    public void Setup()
    {
        _storePath = Path.Combine(Path.GetTempPath(), $"NovaBench_Cache_{ValueSize}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storePath);
        var kvFile = Path.Combine(_storePath, "bench.kvd");
        _store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        _cache = new NovaCache(_store);

        _bytesValue = new Byte[ValueSize];
        Random.Shared.NextBytes(_bytesValue);
        _stringValue = new String('A', ValueSize);

        // 预置数据
        for (var i = 0; i < 1000; i++)
        {
            _cache.Set($"key:{i}", _stringValue);
        }

        _counter = 2000;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store?.Dispose();
        try { Directory.Delete(_storePath, true); } catch { }
    }

    [Benchmark(Description = "Set<String> 写入字符串")]
    public Boolean SetString()
    {
        var id = Interlocked.Increment(ref _counter);
        return _cache.Set($"key:{id}", _stringValue);
    }

    [Benchmark(Description = "Get<String> 读取字符串")]
    public String? GetString()
    {
        return _cache.Get<String>("key:500");
    }

    [Benchmark(Description = "Set<Int32> 写入整数")]
    public Boolean SetInt()
    {
        var id = Interlocked.Increment(ref _counter);
        return _cache.Set($"int:{id}", 42);
    }

    [Benchmark(Description = "Get<Int32> 读取整数")]
    public Int32 GetInt()
    {
        return _cache.Get<Int32>("int:bench");
    }

    [Benchmark(Description = "Set+Get<String> 读写混合")]
    public String? SetThenGetString()
    {
        var id = Interlocked.Increment(ref _counter);
        var key = $"key:{id}";
        _cache.Set(key, _stringValue);
        return _cache.Get<String>(key);
    }

    [Benchmark(Description = "Remove 删除")]
    public Int32 Remove()
    {
        var id = Interlocked.Increment(ref _counter);
        _cache.Set($"del:{id}", _stringValue);
        return _cache.Remove($"del:{id}");
    }

    [Benchmark(Description = "ContainsKey 存在检查")]
    public Boolean ContainsKey()
    {
        return _cache.ContainsKey("key:500");
    }

    [Benchmark(Description = "Increment Int64 原子递增")]
    public Int64 IncrementInt64()
    {
        return _cache.Increment("counter:bench", 1L);
    }

    [Benchmark(Description = "Increment Double 浮点递增")]
    public Double IncrementDouble()
    {
        return _cache.Increment("dcounter:bench", 1.5d);
    }

    [Benchmark(Description = "Decrement Int64 原子递减")]
    public Int64 DecrementInt64()
    {
        return _cache.Decrement("counter:bench2", 1L);
    }

    [Benchmark(Description = "SetExpire 设置过期时间")]
    public Boolean SetExpire()
    {
        return _cache.SetExpire("key:100", TimeSpan.FromMinutes(5));
    }

    [Benchmark(Description = "GetExpire 获取过期时间")]
    public TimeSpan GetExpire()
    {
        return _cache.GetExpire("key:100");
    }

    [Benchmark(Description = "Search 模式搜索")]
    public Int32 Search()
    {
        var count = 0;
        foreach (var _ in _cache.Search("key:1*", 0, 10))
            count++;
        return count;
    }

    [Benchmark(Description = "Set 带TTL写入")]
    public Boolean SetWithExpire()
    {
        var id = Interlocked.Increment(ref _counter);
        return _cache.Set($"ttl:{id}", _stringValue, 300);
    }
}
