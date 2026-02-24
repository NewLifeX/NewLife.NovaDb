using BenchmarkDotNet.Attributes;
using NewLife.NovaDb.Caching;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.KV;

namespace Benchmark;

/// <summary>NovaCache 嵌入模式分操作基准测试（1万条）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class NovaCacheEmbeddedScale1wBenchmark
{
    private String _storePath = null!;
    private String _stringValue64 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _storePath = Path.Combine(Path.GetTempPath(), $"NovaBench_EScale1w_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storePath);
        _stringValue64 = new String('A', 64);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_storePath, true); } catch { }
    }

    [Benchmark(Description = "嵌入 Set 1万条(64B)")]
    public void Set_1w()
    {
        var kvFile = Path.Combine(_storePath, $"set1w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 10_000; i++)
            cache.Set($"key:{i}", _stringValue64);
    }

    [Benchmark(Description = "嵌入 Get 1万条(64B)")]
    public void Get_1w()
    {
        var kvFile = Path.Combine(_storePath, $"get1w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 10_000; i++)
            cache.Set($"key:{i}", _stringValue64);
        for (var i = 0; i < 10_000; i++)
            cache.Get<String>($"key:{i}");
    }

    [Benchmark(Description = "嵌入 ContainsKey 1万条")]
    public void ContainsKey_1w()
    {
        var kvFile = Path.Combine(_storePath, $"ck1w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 10_000; i++)
            cache.Set($"key:{i}", _stringValue64);
        for (var i = 0; i < 10_000; i++)
            cache.ContainsKey($"key:{i}");
    }

    [Benchmark(Description = "嵌入 Inc 1万条")]
    public void Inc_1w()
    {
        var kvFile = Path.Combine(_storePath, $"inc1w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 10_000; i++)
            cache.Increment($"counter:{i}", 1L);
    }

    [Benchmark(Description = "嵌入 Remove 1万条")]
    public void Remove_1w()
    {
        var kvFile = Path.Combine(_storePath, $"rm1w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 10_000; i++)
            cache.Set($"key:{i}", _stringValue64);
        for (var i = 0; i < 10_000; i++)
            cache.Remove($"key:{i}");
    }
}

/// <summary>NovaCache 嵌入模式分操作基准测试（10万条）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class NovaCacheEmbeddedScale10wBenchmark
{
    private String _storePath = null!;
    private String _stringValue64 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _storePath = Path.Combine(Path.GetTempPath(), $"NovaBench_EScale10w_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storePath);
        _stringValue64 = new String('A', 64);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_storePath, true); } catch { }
    }

    [Benchmark(Description = "嵌入 Set 10万条(64B)")]
    public void Set_10w()
    {
        var kvFile = Path.Combine(_storePath, $"set10w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 100_000; i++)
            cache.Set($"key:{i}", _stringValue64);
    }

    [Benchmark(Description = "嵌入 Get 10万条(64B)")]
    public void Get_10w()
    {
        var kvFile = Path.Combine(_storePath, $"get10w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 100_000; i++)
            cache.Set($"key:{i}", _stringValue64);
        for (var i = 0; i < 100_000; i++)
            cache.Get<String>($"key:{i}");
    }

    [Benchmark(Description = "嵌入 ContainsKey 10万条")]
    public void ContainsKey_10w()
    {
        var kvFile = Path.Combine(_storePath, $"ck10w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 100_000; i++)
            cache.Set($"key:{i}", _stringValue64);
        for (var i = 0; i < 100_000; i++)
            cache.ContainsKey($"key:{i}");
    }

    [Benchmark(Description = "嵌入 Inc 10万条")]
    public void Inc_10w()
    {
        var kvFile = Path.Combine(_storePath, $"inc10w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 100_000; i++)
            cache.Increment($"counter:{i}", 1L);
    }

    [Benchmark(Description = "嵌入 Remove 10万条")]
    public void Remove_10w()
    {
        var kvFile = Path.Combine(_storePath, $"rm10w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 100_000; i++)
            cache.Set($"key:{i}", _stringValue64);
        for (var i = 0; i < 100_000; i++)
            cache.Remove($"key:{i}");
    }
}

/// <summary>NovaCache 嵌入模式分操作基准测试（100万条）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class NovaCacheEmbeddedScale100wBenchmark
{
    private String _storePath = null!;
    private String _stringValue64 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _storePath = Path.Combine(Path.GetTempPath(), $"NovaBench_EScale100w_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storePath);
        _stringValue64 = new String('A', 64);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_storePath, true); } catch { }
    }

    [Benchmark(Description = "嵌入 Set 100万条(64B)")]
    public void Set_100w()
    {
        var kvFile = Path.Combine(_storePath, $"set100w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 1_000_000; i++)
            cache.Set($"key:{i}", _stringValue64);
    }

    [Benchmark(Description = "嵌入 Get 100万条(64B)")]
    public void Get_100w()
    {
        var kvFile = Path.Combine(_storePath, $"get100w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 1_000_000; i++)
            cache.Set($"key:{i}", _stringValue64);
        for (var i = 0; i < 1_000_000; i++)
            cache.Get<String>($"key:{i}");
    }

    [Benchmark(Description = "嵌入 ContainsKey 100万条")]
    public void ContainsKey_100w()
    {
        var kvFile = Path.Combine(_storePath, $"ck100w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 1_000_000; i++)
            cache.Set($"key:{i}", _stringValue64);
        for (var i = 0; i < 1_000_000; i++)
            cache.ContainsKey($"key:{i}");
    }

    [Benchmark(Description = "嵌入 Inc 100万条")]
    public void Inc_100w()
    {
        var kvFile = Path.Combine(_storePath, $"inc100w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 1_000_000; i++)
            cache.Increment($"counter:{i}", 1L);
    }

    [Benchmark(Description = "嵌入 Remove 100万条")]
    public void Remove_100w()
    {
        var kvFile = Path.Combine(_storePath, $"rm100w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 1_000_000; i++)
            cache.Set($"key:{i}", _stringValue64);
        for (var i = 0; i < 1_000_000; i++)
            cache.Remove($"key:{i}");
    }
}

/// <summary>NovaCache 嵌入模式分操作基准测试（1000万条）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class NovaCacheEmbeddedScale1000wBenchmark
{
    private String _storePath = null!;
    private String _stringValue64 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _storePath = Path.Combine(Path.GetTempPath(), $"NovaBench_EScale1000w_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storePath);
        _stringValue64 = new String('A', 64);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_storePath, true); } catch { }
    }

    [Benchmark(Description = "嵌入 Set 1000万条(64B)")]
    public void Set_1000w()
    {
        var kvFile = Path.Combine(_storePath, $"set1000w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 10_000_000; i++)
            cache.Set($"key:{i}", _stringValue64);
    }

    [Benchmark(Description = "嵌入 Get 1000万条(64B)")]
    public void Get_1000w()
    {
        var kvFile = Path.Combine(_storePath, $"get1000w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 10_000_000; i++)
            cache.Set($"key:{i}", _stringValue64);
        for (var i = 0; i < 10_000_000; i++)
            cache.Get<String>($"key:{i}");
    }

    [Benchmark(Description = "嵌入 ContainsKey 1000万条")]
    public void ContainsKey_1000w()
    {
        var kvFile = Path.Combine(_storePath, $"ck1000w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 10_000_000; i++)
            cache.Set($"key:{i}", _stringValue64);
        for (var i = 0; i < 10_000_000; i++)
            cache.ContainsKey($"key:{i}");
    }

    [Benchmark(Description = "嵌入 Inc 1000万条")]
    public void Inc_1000w()
    {
        var kvFile = Path.Combine(_storePath, $"inc1000w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 10_000_000; i++)
            cache.Increment($"counter:{i}", 1L);
    }

    [Benchmark(Description = "嵌入 Remove 1000万条")]
    public void Remove_1000w()
    {
        var kvFile = Path.Combine(_storePath, $"rm1000w_{Guid.NewGuid():N}.kvd");
        using var store = new KvStore(new DbOptions { DefaultKvTtl = TimeSpan.Zero }, kvFile);
        var cache = new NovaCache(store);
        for (var i = 0; i < 10_000_000; i++)
            cache.Set($"key:{i}", _stringValue64);
        for (var i = 0; i < 10_000_000; i++)
            cache.Remove($"key:{i}");
    }
}
