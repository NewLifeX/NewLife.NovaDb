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

    [Benchmark(Description = "Add 仅新增")]
    public Boolean Add()
    {
        var id = Interlocked.Increment(ref _counter);
        return _store.Add($"add:{id}", _value, TimeSpan.Zero);
    }

    [Benchmark(Description = "Replace 替换")]
    public IOwnerPacket? Replace()
    {
        return _store.Replace("key:500", _value);
    }

    [Benchmark(Description = "GetString 字符串读取")]
    public String? GetString()
    {
        return _store.GetString("key:500");
    }

    [Benchmark(Description = "GetAllKeys 获取所有键")]
    public Int32 GetAllKeys()
    {
        var count = 0;
        foreach (var _ in _store.GetAllKeys())
            count++;
        return count;
    }

    [Benchmark(Description = "GetExpiration 获取过期时间")]
    public DateTime? GetExpiration()
    {
        return _store.GetExpiration("key:100");
    }
}

/// <summary>KV 引擎海量数据基准测试（10万条）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class KvStoreMassDataBenchmark
{
    private String _storePath = null!;
    private KvStore _store = null!;
    private Byte[] _value64 = null!;
    private Byte[] _value1024 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _storePath = Path.Combine(Path.GetTempPath(), $"NovaBench_KvMass_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storePath);
        _value64 = new Byte[64];
        Random.Shared.NextBytes(_value64);
        _value1024 = new Byte[1024];
        Random.Shared.NextBytes(_value1024);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store?.Dispose();
        try { Directory.Delete(_storePath, true); } catch { }
    }

    [Benchmark(Description = "海量写入10万条(64B)")]
    public void MassWrite_64B()
    {
        var kvFile = Path.Combine(_storePath, $"mass64_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        for (var i = 0; i < 100_000; i++)
            store.Set($"key:{i}", _value64);
    }

    [Benchmark(Description = "海量写入10万条(1024B)")]
    public void MassWrite_1024B()
    {
        var kvFile = Path.Combine(_storePath, $"mass1024_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        for (var i = 0; i < 100_000; i++)
            store.Set($"key:{i}", _value1024);
    }

    [Benchmark(Description = "海量写入后读取10万条(64B)")]
    public void MassWriteThenRead_64B()
    {
        var kvFile = Path.Combine(_storePath, $"massrd64_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        for (var i = 0; i < 100_000; i++)
            store.Set($"key:{i}", _value64);
        for (var i = 0; i < 100_000; i++)
            store.Get($"key:{i}");
    }

    [Benchmark(Description = "海量批量写入10万条(SetAll, 64B)")]
    public void MassSetAll_64B()
    {
        var kvFile = Path.Combine(_storePath, $"massall64_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        // 分批写入，每批 1000 条
        for (var batch = 0; batch < 100; batch++)
        {
            var dict = new Dictionary<String, Byte[]?>(1000);
            for (var i = 0; i < 1000; i++)
                dict[$"key:{batch * 1000 + i}"] = _value64;
            store.SetAll(dict);
        }
    }

    [Benchmark(Description = "海量数据搜索(10万条中搜索)")]
    public Int32 MassSearch()
    {
        var kvFile = Path.Combine(_storePath, $"masssrc_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        for (var i = 0; i < 100_000; i++)
            store.Set($"key:{i}", _value64);

        var count = 0;
        foreach (var _ in store.Search("key:999*", 0, 100))
            count++;
        return count;
    }
}

/// <summary>KV 引擎海量数据基准测试（100万条）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class KvStoreMassData100wBenchmark
{
    private String _storePath = null!;
    private Byte[] _value64 = null!;
    private Byte[] _value1024 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _storePath = Path.Combine(Path.GetTempPath(), $"NovaBench_KvMass100w_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storePath);
        _value64 = new Byte[64];
        Random.Shared.NextBytes(_value64);
        _value1024 = new Byte[1024];
        Random.Shared.NextBytes(_value1024);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_storePath, true); } catch { }
    }

    [Benchmark(Description = "海量写入100万条(64B)")]
    public void MassWrite_64B()
    {
        var kvFile = Path.Combine(_storePath, $"mass64_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        for (var i = 0; i < 1_000_000; i++)
            store.Set($"key:{i}", _value64);
    }

    [Benchmark(Description = "海量写入100万条(1024B)")]
    public void MassWrite_1024B()
    {
        var kvFile = Path.Combine(_storePath, $"mass1024_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        for (var i = 0; i < 1_000_000; i++)
            store.Set($"key:{i}", _value1024);
    }

    [Benchmark(Description = "海量写入后读取100万条(64B)")]
    public void MassWriteThenRead_64B()
    {
        var kvFile = Path.Combine(_storePath, $"massrd64_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        for (var i = 0; i < 1_000_000; i++)
            store.Set($"key:{i}", _value64);
        for (var i = 0; i < 1_000_000; i++)
            store.Get($"key:{i}");
    }

    [Benchmark(Description = "海量批量写入100万条(SetAll, 64B)")]
    public void MassSetAll_64B()
    {
        var kvFile = Path.Combine(_storePath, $"massall64_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        // 分批写入，每批 1000 条
        for (var batch = 0; batch < 1000; batch++)
        {
            var dict = new Dictionary<String, Byte[]?>(1000);
            for (var i = 0; i < 1000; i++)
                dict[$"key:{batch * 1000 + i}"] = _value64;
            store.SetAll(dict);
        }
    }

    [Benchmark(Description = "海量数据搜索(100万条中搜索)")]
    public Int32 MassSearch()
    {
        var kvFile = Path.Combine(_storePath, $"masssrc_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        for (var i = 0; i < 1_000_000; i++)
            store.Set($"key:{i}", _value64);

        var count = 0;
        foreach (var _ in store.Search("key:999*", 0, 100))
            count++;
        return count;
    }
}

/// <summary>KV 引擎海量数据基准测试（1000万条）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class KvStoreMassData1000wBenchmark
{
    private String _storePath = null!;
    private Byte[] _value64 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _storePath = Path.Combine(Path.GetTempPath(), $"NovaBench_KvMass1000w_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storePath);
        _value64 = new Byte[64];
        Random.Shared.NextBytes(_value64);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_storePath, true); } catch { }
    }

    [Benchmark(Description = "海量写入1000万条(64B)")]
    public void MassWrite_64B()
    {
        var kvFile = Path.Combine(_storePath, $"mass64_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        for (var i = 0; i < 10_000_000; i++)
            store.Set($"key:{i}", _value64);
    }

    [Benchmark(Description = "海量写入后读取1000万条(64B)")]
    public void MassWriteThenRead_64B()
    {
        var kvFile = Path.Combine(_storePath, $"massrd64_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        for (var i = 0; i < 10_000_000; i++)
            store.Set($"key:{i}", _value64);
        for (var i = 0; i < 10_000_000; i++)
            store.Get($"key:{i}");
    }

    [Benchmark(Description = "海量批量写入1000万条(SetAll, 64B)")]
    public void MassSetAll_64B()
    {
        var kvFile = Path.Combine(_storePath, $"massall64_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        // 分批写入，每批 1000 条
        for (var batch = 0; batch < 10_000; batch++)
        {
            var dict = new Dictionary<String, Byte[]?>(1000);
            for (var i = 0; i < 1000; i++)
                dict[$"key:{batch * 1000 + i}"] = _value64;
            store.SetAll(dict);
        }
    }

    [Benchmark(Description = "海量数据搜索(1000万条中搜索)")]
    public Int32 MassSearch()
    {
        var kvFile = Path.Combine(_storePath, $"masssrc_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        for (var i = 0; i < 10_000_000; i++)
            store.Set($"key:{i}", _value64);

        var count = 0;
        foreach (var _ in store.Search("key:999*", 0, 100))
            count++;
        return count;
    }
}
