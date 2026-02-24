using BenchmarkDotNet.Attributes;
using NewLife.NovaDb.Caching;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Core;
using NovaServer = NewLife.NovaDb.Server.NovaServer;

namespace Benchmark;

/// <summary>NovaCache 网络模式基准测试（通过 TCP RPC 访问 NovaServer）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class NovaCacheNetworkBenchmark
{
    private NovaServer _server = null!;
    private NovaClient _client = null!;
    private NovaCache _cache = null!;
    private String _dbPath = null!;
    private Int32 _counter;

    [Params(64, 1024)]
    public Int32 ValueSize { get; set; }

    private String _stringValue = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaBench_NetCache_{ValueSize}_{Guid.NewGuid():N}");

        // 使用随机端口避免冲突
        var port = Random.Shared.Next(20000, 60000);
        _server = new NovaServer(port)
        {
            DbPath = _dbPath,
            Options = new ServerDbOptions { WalMode = WalMode.None }
        };
        _server.Start();

        _client = new NovaClient($"Server=127.0.0.1;Port={port}");
        _client.Open();
        _cache = new NovaCache(_client);

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
        _client?.Close();
        _server?.Dispose();
        try { Directory.Delete(_dbPath, true); } catch { }
    }

    [Benchmark(Description = "Net Set<String> 写入字符串")]
    public Boolean SetString()
    {
        var id = Interlocked.Increment(ref _counter);
        return _cache.Set($"key:{id}", _stringValue);
    }

    [Benchmark(Description = "Net Get<String> 读取字符串")]
    public String? GetString()
    {
        return _cache.Get<String>("key:500");
    }

    [Benchmark(Description = "Net Set<Int32> 写入整数")]
    public Boolean SetInt()
    {
        var id = Interlocked.Increment(ref _counter);
        return _cache.Set($"int:{id}", 42);
    }

    [Benchmark(Description = "Net Get<Int32> 读取整数")]
    public Int32 GetInt()
    {
        return _cache.Get<Int32>("int:bench");
    }

    [Benchmark(Description = "Net Set+Get<String> 读写混合")]
    public String? SetThenGetString()
    {
        var id = Interlocked.Increment(ref _counter);
        var key = $"key:{id}";
        _cache.Set(key, _stringValue);
        return _cache.Get<String>(key);
    }

    [Benchmark(Description = "Net Remove 删除")]
    public Int32 Remove()
    {
        var id = Interlocked.Increment(ref _counter);
        _cache.Set($"del:{id}", _stringValue);
        return _cache.Remove($"del:{id}");
    }

    [Benchmark(Description = "Net ContainsKey 存在检查")]
    public Boolean ContainsKey()
    {
        return _cache.ContainsKey("key:500");
    }

    [Benchmark(Description = "Net Increment Int64 原子递增")]
    public Int64 IncrementInt64()
    {
        return _cache.Increment("counter:bench", 1L);
    }

    [Benchmark(Description = "Net Increment Double 浮点递增")]
    public Double IncrementDouble()
    {
        return _cache.Increment("dcounter:bench", 1.5d);
    }

    [Benchmark(Description = "Net Decrement Int64 原子递减")]
    public Int64 DecrementInt64()
    {
        return _cache.Decrement("counter:bench2", 1L);
    }

    [Benchmark(Description = "Net SetExpire 设置过期时间")]
    public Boolean SetExpire()
    {
        return _cache.SetExpire("key:100", TimeSpan.FromMinutes(5));
    }

    [Benchmark(Description = "Net GetExpire 获取过期时间")]
    public TimeSpan GetExpire()
    {
        return _cache.GetExpire("key:100");
    }

    [Benchmark(Description = "Net Search 模式搜索")]
    public Int32 Search()
    {
        var count = 0;
        foreach (var _ in _cache.Search("key:1*", 0, 10))
            count++;
        return count;
    }

    [Benchmark(Description = "Net Set 带TTL写入")]
    public Boolean SetWithExpire()
    {
        var id = Interlocked.Increment(ref _counter);
        return _cache.Set($"ttl:{id}", _stringValue, 300);
    }

    [Benchmark(Description = "Net Clear 清空")]
    public void Clear()
    {
        _cache.Clear();
        for (var i = 0; i < 100; i++)
            _cache.Set($"key:{i}", _stringValue);
        _cache.Set("int:bench", 42);
    }

    [Benchmark(Description = "Net Count 获取总数")]
    public Int32 Count()
    {
        return _cache.Count;
    }

    [Benchmark(Description = "Net SetAll 批量写入(100)")]
    public void SetAll_100()
    {
        var id = Interlocked.Increment(ref _counter);
        var dict = new Dictionary<String, String>(100);
        for (var i = 0; i < 100; i++)
            dict[$"batch:{id}:{i}"] = _stringValue;
        _cache.SetAll(dict);
    }

    [Benchmark(Description = "Net GetAll 批量读取(100)")]
    public IDictionary<String, String?> GetAll_100()
    {
        var keys = new String[100];
        for (var i = 0; i < 100; i++)
            keys[i] = $"key:{i}";
        return _cache.GetAll<String>(keys);
    }

    [Benchmark(Description = "Net SetAll 批量写入(1000)")]
    public void SetAll_1000()
    {
        var id = Interlocked.Increment(ref _counter);
        var dict = new Dictionary<String, String>(1000);
        for (var i = 0; i < 1000; i++)
            dict[$"batch:{id}:{i}"] = _stringValue;
        _cache.SetAll(dict);
    }

    [Benchmark(Description = "Net GetAll 批量读取(1000)")]
    public IDictionary<String, String?> GetAll_1000()
    {
        var keys = new String[1000];
        for (var i = 0; i < 1000; i++)
            keys[i] = $"key:{i}";
        return _cache.GetAll<String>(keys);
    }
}

/// <summary>NovaCache 网络模式海量数据基准测试（10万条）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class NovaCacheNetworkMassDataBenchmark
{
    private NovaServer _server = null!;
    private NovaClient _client = null!;
    private NovaCache _cache = null!;
    private String _dbPath = null!;
    private Int32 _port;
    private String _stringValue64 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaBench_NetMass_{Guid.NewGuid():N}");
        _port = Random.Shared.Next(20000, 60000);
        _server = new NovaServer(_port)
        {
            DbPath = _dbPath,
            Options = new ServerDbOptions { WalMode = WalMode.None }
        };
        _server.Start();

        _client = new NovaClient($"Server=127.0.0.1;Port={_port}");
        _client.Open();
        _cache = new NovaCache(_client);

        _stringValue64 = new String('A', 64);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client?.Close();
        _server?.Dispose();
        try { Directory.Delete(_dbPath, true); } catch { }
    }

    [Benchmark(Description = "网络模式海量写入1万条(64B)")]
    public void MassWrite_10K()
    {
        for (var i = 0; i < 10_000; i++)
            _cache.Set($"mass:{i}", _stringValue64);
        _cache.Clear();
    }

    [Benchmark(Description = "网络模式海量写入后读取1万条(64B)")]
    public void MassWriteThenRead_10K()
    {
        for (var i = 0; i < 10_000; i++)
            _cache.Set($"mass:{i}", _stringValue64);
        for (var i = 0; i < 10_000; i++)
            _cache.Get<String>($"mass:{i}");
        _cache.Clear();
    }

    [Benchmark(Description = "网络模式批量SetAll写入1万条(64B)")]
    public void MassBatchWrite_10K()
    {
        var dict = new Dictionary<String, String>(1000);
        for (var batch = 0; batch < 10; batch++)
        {
            dict.Clear();
            for (var i = 0; i < 1000; i++)
                dict[$"mass:{batch * 1000 + i}"] = _stringValue64;
            _cache.SetAll(dict);
        }
        _cache.Clear();
    }

    [Benchmark(Description = "网络模式批量SetAll+GetAll读写1万条(64B)")]
    public void MassBatchWriteThenRead_10K()
    {
        var dict = new Dictionary<String, String>(1000);
        for (var batch = 0; batch < 10; batch++)
        {
            dict.Clear();
            for (var i = 0; i < 1000; i++)
                dict[$"mass:{batch * 1000 + i}"] = _stringValue64;
            _cache.SetAll(dict);
        }

        var keys = new String[1000];
        for (var batch = 0; batch < 10; batch++)
        {
            for (var i = 0; i < 1000; i++)
                keys[i] = $"mass:{batch * 1000 + i}";
            _cache.GetAll<String>(keys);
        }
        _cache.Clear();
    }
}
