using BenchmarkDotNet.Attributes;
using NewLife.NovaDb.Caching;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Core;
using NovaServer = NewLife.NovaDb.Server.NovaServer;

namespace Benchmark;

/// <summary>NovaCache 网络模式分操作基准测试（1万条）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class NovaCacheNetworkScale1wBenchmark
{
    private NovaServer _server = null!;
    private NovaClient _client = null!;
    private NovaCache _cache = null!;
    private String _dbPath = null!;
    private String _stringValue64 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaBench_NScale1w_{Guid.NewGuid():N}");
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

        _stringValue64 = new String('A', 64);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client?.Close();
        _server?.Dispose();
        try { Directory.Delete(_dbPath, true); } catch { }
    }

    [Benchmark(Description = "网络 Set 1万条(64B)")]
    public void Set_1w()
    {
        for (var i = 0; i < 10_000; i++)
            _cache.Set($"s:{i}", _stringValue64);
        _cache.Clear();
    }

    [Benchmark(Description = "网络 Get 1万条(64B)")]
    public void Get_1w()
    {
        for (var i = 0; i < 10_000; i++)
            _cache.Set($"g:{i}", _stringValue64);
        for (var i = 0; i < 10_000; i++)
            _cache.Get<String>($"g:{i}");
        _cache.Clear();
    }

    [Benchmark(Description = "网络 ContainsKey 1万条")]
    public void ContainsKey_1w()
    {
        for (var i = 0; i < 10_000; i++)
            _cache.Set($"c:{i}", _stringValue64);
        for (var i = 0; i < 10_000; i++)
            _cache.ContainsKey($"c:{i}");
        _cache.Clear();
    }

    [Benchmark(Description = "网络 Inc 1万条")]
    public void Inc_1w()
    {
        for (var i = 0; i < 10_000; i++)
            _cache.Increment($"n:{i}", 1L);
        _cache.Clear();
    }

    [Benchmark(Description = "网络 Remove 1万条")]
    public void Remove_1w()
    {
        for (var i = 0; i < 10_000; i++)
            _cache.Set($"r:{i}", _stringValue64);
        for (var i = 0; i < 10_000; i++)
            _cache.Remove($"r:{i}");
    }
}

/// <summary>NovaCache 网络模式分操作基准测试（10万条）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class NovaCacheNetworkScale10wBenchmark
{
    private NovaServer _server = null!;
    private NovaClient _client = null!;
    private NovaCache _cache = null!;
    private String _dbPath = null!;
    private String _stringValue64 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaBench_NScale10w_{Guid.NewGuid():N}");
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

        _stringValue64 = new String('A', 64);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client?.Close();
        _server?.Dispose();
        try { Directory.Delete(_dbPath, true); } catch { }
    }

    [Benchmark(Description = "网络 Set 10万条(64B)")]
    public void Set_10w()
    {
        for (var i = 0; i < 100_000; i++)
            _cache.Set($"s:{i}", _stringValue64);
        _cache.Clear();
    }

    [Benchmark(Description = "网络 Get 10万条(64B)")]
    public void Get_10w()
    {
        for (var i = 0; i < 100_000; i++)
            _cache.Set($"g:{i}", _stringValue64);
        for (var i = 0; i < 100_000; i++)
            _cache.Get<String>($"g:{i}");
        _cache.Clear();
    }

    [Benchmark(Description = "网络 ContainsKey 10万条")]
    public void ContainsKey_10w()
    {
        for (var i = 0; i < 100_000; i++)
            _cache.Set($"c:{i}", _stringValue64);
        for (var i = 0; i < 100_000; i++)
            _cache.ContainsKey($"c:{i}");
        _cache.Clear();
    }

    [Benchmark(Description = "网络 Inc 10万条")]
    public void Inc_10w()
    {
        for (var i = 0; i < 100_000; i++)
            _cache.Increment($"n:{i}", 1L);
        _cache.Clear();
    }

    [Benchmark(Description = "网络 Remove 10万条")]
    public void Remove_10w()
    {
        for (var i = 0; i < 100_000; i++)
            _cache.Set($"r:{i}", _stringValue64);
        for (var i = 0; i < 100_000; i++)
            _cache.Remove($"r:{i}");
    }
}

/// <summary>NovaCache 网络模式分操作基准测试（100万条）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class NovaCacheNetworkScale100wBenchmark
{
    private NovaServer _server = null!;
    private NovaClient _client = null!;
    private NovaCache _cache = null!;
    private String _dbPath = null!;
    private String _stringValue64 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaBench_NScale100w_{Guid.NewGuid():N}");
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

        _stringValue64 = new String('A', 64);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client?.Close();
        _server?.Dispose();
        try { Directory.Delete(_dbPath, true); } catch { }
    }

    [Benchmark(Description = "网络 Set 100万条(64B)")]
    public void Set_100w()
    {
        for (var i = 0; i < 1_000_000; i++)
            _cache.Set($"s:{i}", _stringValue64);
        _cache.Clear();
    }

    [Benchmark(Description = "网络 Get 100万条(64B)")]
    public void Get_100w()
    {
        for (var i = 0; i < 1_000_000; i++)
            _cache.Set($"g:{i}", _stringValue64);
        for (var i = 0; i < 1_000_000; i++)
            _cache.Get<String>($"g:{i}");
        _cache.Clear();
    }

    [Benchmark(Description = "网络 ContainsKey 100万条")]
    public void ContainsKey_100w()
    {
        for (var i = 0; i < 1_000_000; i++)
            _cache.Set($"c:{i}", _stringValue64);
        for (var i = 0; i < 1_000_000; i++)
            _cache.ContainsKey($"c:{i}");
        _cache.Clear();
    }

    [Benchmark(Description = "网络 Inc 100万条")]
    public void Inc_100w()
    {
        for (var i = 0; i < 1_000_000; i++)
            _cache.Increment($"n:{i}", 1L);
        _cache.Clear();
    }

    [Benchmark(Description = "网络 Remove 100万条")]
    public void Remove_100w()
    {
        for (var i = 0; i < 1_000_000; i++)
            _cache.Set($"r:{i}", _stringValue64);
        for (var i = 0; i < 1_000_000; i++)
            _cache.Remove($"r:{i}");
    }
}

/// <summary>NovaCache 网络模式分操作基准测试（1000万条）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class NovaCacheNetworkScale1000wBenchmark
{
    private NovaServer _server = null!;
    private NovaClient _client = null!;
    private NovaCache _cache = null!;
    private String _dbPath = null!;
    private String _stringValue64 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaBench_NScale1000w_{Guid.NewGuid():N}");
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

        _stringValue64 = new String('A', 64);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client?.Close();
        _server?.Dispose();
        try { Directory.Delete(_dbPath, true); } catch { }
    }

    [Benchmark(Description = "网络 Set 1000万条(64B)")]
    public void Set_1000w()
    {
        for (var i = 0; i < 10_000_000; i++)
            _cache.Set($"s:{i}", _stringValue64);
        _cache.Clear();
    }

    [Benchmark(Description = "网络 Get 1000万条(64B)")]
    public void Get_1000w()
    {
        for (var i = 0; i < 10_000_000; i++)
            _cache.Set($"g:{i}", _stringValue64);
        for (var i = 0; i < 10_000_000; i++)
            _cache.Get<String>($"g:{i}");
        _cache.Clear();
    }

    [Benchmark(Description = "网络 ContainsKey 1000万条")]
    public void ContainsKey_1000w()
    {
        for (var i = 0; i < 10_000_000; i++)
            _cache.Set($"c:{i}", _stringValue64);
        for (var i = 0; i < 10_000_000; i++)
            _cache.ContainsKey($"c:{i}");
        _cache.Clear();
    }

    [Benchmark(Description = "网络 Inc 1000万条")]
    public void Inc_1000w()
    {
        for (var i = 0; i < 10_000_000; i++)
            _cache.Increment($"n:{i}", 1L);
        _cache.Clear();
    }

    [Benchmark(Description = "网络 Remove 1000万条")]
    public void Remove_1000w()
    {
        for (var i = 0; i < 10_000_000; i++)
            _cache.Set($"r:{i}", _stringValue64);
        for (var i = 0; i < 10_000_000; i++)
            _cache.Remove($"r:{i}");
    }
}
