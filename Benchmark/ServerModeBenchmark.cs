using BenchmarkDotNet.Attributes;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Core;
using NovaServer = NewLife.NovaDb.Server.NovaServer;

namespace Benchmark;

/// <summary>独立服务模式基准测试（通过 TCP 协议访问 NovaServer）</summary>
[MemoryDiagnoser]
[Config(typeof(AntiViralConfig))]
public class ServerModeBenchmark
{
    private NovaServer _server = null!;
    private NovaConnection _conn = null!;
    private String _dbPath = null!;
    private Int32 _counter;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaBench_Server_{Guid.NewGuid():N}");

        // 使用随机端口避免冲突
        var port = Random.Shared.Next(20000, 60000);
        _server = new NovaServer(port)
        {
            DbPath = _dbPath,
            Options = new ServerDbOptions { WalMode = WalMode.None }
        };
        _server.Start();

        // 通过 ADO.NET 连接
        _conn = new NovaConnection($"Server=127.0.0.1;Port={port}");
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE bench (id INT PRIMARY KEY, name VARCHAR, age INT, score DOUBLE)";
        cmd.ExecuteNonQuery();

        // 预置数据
        for (var i = 1; i <= 1000; i++)
        {
            cmd.CommandText = $"INSERT INTO bench VALUES ({i}, 'user{i}', {20 + i % 50}, {60.0 + i % 40})";
            cmd.ExecuteNonQuery();
        }

        _counter = 2000;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _conn.Dispose();
        _server.Dispose();
        try { Directory.Delete(_dbPath, true); } catch { }
    }

    [Benchmark(Description = "Server Insert 插入")]
    public void Insert()
    {
        var id = Interlocked.Increment(ref _counter);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO bench VALUES ({id}, 'bench{id}', 25, 88.5)";
        cmd.ExecuteNonQuery();
    }

    [Benchmark(Description = "Server Select 主键查询")]
    public void SelectByPK()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM bench WHERE id = 500";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) { }
    }

    [Benchmark(Description = "Server Select 范围查询")]
    public void SelectRange()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM bench WHERE age > 30 AND age < 50";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) { }
    }

    [Benchmark(Description = "Server Update 更新")]
    public void Update()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE bench SET score = 99.9 WHERE id = 100";
        cmd.ExecuteNonQuery();
    }

    [Benchmark(Description = "Server 聚合查询")]
    public void Aggregate()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), AVG(score) FROM bench WHERE age >= 25";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) { }
    }
}
