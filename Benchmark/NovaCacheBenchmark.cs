using BenchmarkDotNet.Attributes;
using NewLife.NovaDb.Caching;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.KV;

namespace Benchmark;

/// <summary>NovaCache 缓存层基准测试（嵌入模式，ICache 接口级别）</summary>
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
        _cache.Set("int:bench", 42);

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

    [Benchmark(Description = "Clear 清空")]
    public void Clear()
    {
        // 清空后重新插入测试数据，避免影响后续迭代
        _cache.Clear();
        for (var i = 0; i < 100; i++)
            _cache.Set($"key:{i}", _stringValue);
        _cache.Set("int:bench", 42);
    }

    [Benchmark(Description = "Count 获取总数")]
    public Int32 Count()
    {
        return _cache.Count;
    }

    [Benchmark(Description = "Keys 获取所有键")]
    public Int32 Keys()
    {
        return _cache.Keys.Count;
    }

    [Benchmark(Description = "Remove 通配符删除")]
    public Int32 RemoveByPattern()
    {
        var id = Interlocked.Increment(ref _counter);
        for (var i = 0; i < 5; i++)
            _cache.Set($"pat:{id}:{i}", _stringValue);
        return _cache.Remove($"pat:{id}:*");
    }

    [Benchmark(Description = "SetAll 批量写入(100)")]
    public void SetAll_100()
    {
        var id = Interlocked.Increment(ref _counter);
        var dict = new Dictionary<String, String>(100);
        for (var i = 0; i < 100; i++)
            dict[$"batch:{id}:{i}"] = _stringValue;
        _cache.SetAll(dict);
    }

    [Benchmark(Description = "GetAll 批量读取(100)")]
    public IDictionary<String, String?> GetAll_100()
    {
        var keys = new String[100];
        for (var i = 0; i < 100; i++)
            keys[i] = $"key:{i}";
        return _cache.GetAll<String>(keys);
    }

    [Benchmark(Description = "SetAll 批量写入(1000)")]
    public void SetAll_1000()
    {
        var id = Interlocked.Increment(ref _counter);
        var dict = new Dictionary<String, String>(1000);
        for (var i = 0; i < 1000; i++)
            dict[$"batch:{id}:{i}"] = _stringValue;
        _cache.SetAll(dict);
    }

    [Benchmark(Description = "GetAll 批量读取(1000)")]
    public IDictionary<String, String?> GetAll_1000()
    {
        var keys = new String[1000];
        for (var i = 0; i < 1000; i++)
            keys[i] = $"key:{i}";
        return _cache.GetAll<String>(keys);
    }
}

/// <summary>NovaCache 嵌入模式海量数据基准测试（10万条）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class NovaCacheEmbeddedMassDataBenchmark
{
    private String _storePath = null!;
    private String _stringValue64 = null!;
    private String _stringValue1024 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _storePath = Path.Combine(Path.GetTempPath(), $"NovaBench_CacheMass_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storePath);
        _stringValue64 = new String('A', 64);
        _stringValue1024 = new String('A', 1024);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_storePath, true); } catch { }
    }

    [Benchmark(Description = "嵌入模式海量写入10万条(64B)")]
    public void MassWrite_64B()
    {
        var kvFile = Path.Combine(_storePath, $"emass64_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 100_000; i++)
            cache.Set($"key:{i}", _stringValue64);
    }

    [Benchmark(Description = "嵌入模式海量写入10万条(1024B)")]
    public void MassWrite_1024B()
    {
        var kvFile = Path.Combine(_storePath, $"emass1024_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 100_000; i++)
            cache.Set($"key:{i}", _stringValue1024);
    }

    [Benchmark(Description = "嵌入模式海量写入后读取10万条(64B)")]
    public void MassWriteThenRead_64B()
    {
        var kvFile = Path.Combine(_storePath, $"emassrd64_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 100_000; i++)
            cache.Set($"key:{i}", _stringValue64);
        for (var i = 0; i < 100_000; i++)
            cache.Get<String>($"key:{i}");
    }
}
