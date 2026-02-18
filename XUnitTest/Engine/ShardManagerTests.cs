using System;
using System.IO;
using System.Threading;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using Xunit;

namespace XUnitTest.Engine;

/// <summary>分片管理器单元测试</summary>
public class ShardManagerTests : IDisposable
{
    private readonly String _tempDir;
    private readonly DbOptions _options;

    public ShardManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NovaDb_ShardTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _options = new DbOptions();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact(DisplayName = "测试创建分片管理器")]
    public void TestCreateShardManager()
    {
        var manager = new ShardManager(_options, _tempDir);

        Assert.NotNull(manager);
        Assert.Equal(0, manager.ShardCount);
    }

    [Fact(DisplayName = "测试添加分片")]
    public void TestAddShard()
    {
        var manager = new ShardManager(_options, _tempDir);

        var shard = new ShardInfo
        {
            ShardId = 0,
            DataFilePath = Path.Combine(_tempDir, "0.data")
        };

        manager.AddShard(shard);

        Assert.Equal(1, manager.ShardCount);

        var found = manager.GetShardById(0);
        Assert.NotNull(found);
        Assert.Equal(0, found!.ShardId);
    }

    [Fact(DisplayName = "测试添加重复分片 ID 抛出异常")]
    public void TestAddDuplicateShardThrows()
    {
        var manager = new ShardManager(_options, _tempDir);

        var shard1 = new ShardInfo { ShardId = 0 };
        var shard2 = new ShardInfo { ShardId = 0 };

        manager.AddShard(shard1);

        var ex = Assert.Throws<NovaException>(() => manager.AddShard(shard2));
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact(DisplayName = "测试获取写入分片")]
    public void TestGetWriteShard()
    {
        var manager = new ShardManager(_options, _tempDir);

        // 无分片时返回 null
        Assert.Null(manager.GetWriteShard());

        var shard0 = new ShardInfo { ShardId = 0 };
        var shard1 = new ShardInfo { ShardId = 1 };
        manager.AddShard(shard0);
        manager.AddShard(shard1);

        // 返回最后一个非只读分片
        var writeShard = manager.GetWriteShard();
        Assert.NotNull(writeShard);
        Assert.Equal(1, writeShard!.ShardId);

        // 标记为只读后，返回前一个
        shard1.IsReadOnly = true;
        writeShard = manager.GetWriteShard();
        Assert.NotNull(writeShard);
        Assert.Equal(0, writeShard!.ShardId);
    }

    [Fact(DisplayName = "测试获取所有分片")]
    public void TestGetAllShards()
    {
        var manager = new ShardManager(_options, _tempDir);

        manager.AddShard(new ShardInfo { ShardId = 0 });
        manager.AddShard(new ShardInfo { ShardId = 1 });
        manager.AddShard(new ShardInfo { ShardId = 2 });

        var all = manager.GetAllShards();
        Assert.Equal(3, all.Count);
    }

    [Fact(DisplayName = "测试根据 ID 查找分片")]
    public void TestGetShardById()
    {
        var manager = new ShardManager(_options, _tempDir);

        manager.AddShard(new ShardInfo { ShardId = 5 });

        var found = manager.GetShardById(5);
        Assert.NotNull(found);
        Assert.Equal(5, found!.ShardId);

        var notFound = manager.GetShardById(99);
        Assert.Null(notFound);
    }

    [Fact(DisplayName = "测试记录写入并检查切分阈值")]
    public void TestRecordWriteAndShouldSplit()
    {
        var options = new DbOptions
        {
            ShardSizeThreshold = 1000,
            ShardRowThreshold = 100
        };
        var manager = new ShardManager(options, _tempDir);

        var shard = new ShardInfo { ShardId = 0 };
        manager.AddShard(shard);

        // 初始不需要切分
        Assert.False(manager.ShouldSplit(0));

        // 写入不到阈值
        for (var i = 0; i < 50; i++)
        {
            manager.RecordWrite(0, 10);
        }

        Assert.Equal(50, shard.RowCount);
        Assert.Equal(500, shard.SizeBytes);
        Assert.False(manager.ShouldSplit(0));

        // 再写入 50 行达到行数阈值
        for (var i = 0; i < 50; i++)
        {
            manager.RecordWrite(0, 10);
        }

        Assert.Equal(100, shard.RowCount);
        Assert.True(manager.ShouldSplit(0));
    }

    [Fact(DisplayName = "测试按大小阈值触发切分")]
    public void TestShouldSplitBySize()
    {
        var options = new DbOptions
        {
            ShardSizeThreshold = 500,
            ShardRowThreshold = 10_000_000
        };
        var manager = new ShardManager(options, _tempDir);

        var shard = new ShardInfo { ShardId = 0 };
        manager.AddShard(shard);

        // 写入大块数据触发大小阈值
        manager.RecordWrite(0, 500);
        Assert.True(manager.ShouldSplit(0));
    }

    [Fact(DisplayName = "测试只读分片不触发切分")]
    public void TestReadOnlyShardDoesNotSplit()
    {
        var options = new DbOptions
        {
            ShardSizeThreshold = 100,
            ShardRowThreshold = 10
        };
        var manager = new ShardManager(options, _tempDir);

        var shard = new ShardInfo { ShardId = 0, IsReadOnly = true, RowCount = 100, SizeBytes = 1000 };
        manager.AddShard(shard);

        Assert.False(manager.ShouldSplit(0));
    }

    [Fact(DisplayName = "测试切分分片")]
    public void TestSplitShard()
    {
        var manager = new ShardManager(_options, _tempDir);

        var shard = new ShardInfo
        {
            ShardId = 0,
            RowCount = 1000,
            SizeBytes = 50000,
            DataFilePath = Path.Combine(_tempDir, "0.data")
        };
        manager.AddShard(shard);

        // 执行切分
        var newShard = manager.Split(0);

        // 验证旧分片变为只读
        Assert.True(shard.IsReadOnly);

        // 验证新分片
        Assert.NotNull(newShard);
        Assert.Equal(1, newShard.ShardId);
        Assert.False(newShard.IsReadOnly);
        Assert.Equal(0, newShard.RowCount);
        Assert.Equal(0, newShard.SizeBytes);

        // 总分片数
        Assert.Equal(2, manager.ShardCount);

        // 写入分片应该是新分片
        var writeShard = manager.GetWriteShard();
        Assert.NotNull(writeShard);
        Assert.Equal(1, writeShard!.ShardId);
    }

    [Fact(DisplayName = "测试多次切分")]
    public void TestMultipleSplits()
    {
        var manager = new ShardManager(_options, _tempDir);

        manager.AddShard(new ShardInfo { ShardId = 0 });

        var shard1 = manager.Split(0);
        Assert.Equal(1, shard1.ShardId);

        var shard2 = manager.Split(1);
        Assert.Equal(2, shard2.ShardId);

        Assert.Equal(3, manager.ShardCount);

        // 只有最后一个分片可写
        var writeShard = manager.GetWriteShard();
        Assert.NotNull(writeShard);
        Assert.Equal(2, writeShard!.ShardId);
    }

    [Fact(DisplayName = "测试按键查找分片")]
    public void TestGetShardsForKey()
    {
        var manager = new ShardManager(_options, _tempDir);

        var shard0 = new ShardInfo { ShardId = 0, MinKey = 1, MaxKey = 100 };
        var shard1 = new ShardInfo { ShardId = 1, MinKey = 101, MaxKey = 200 };
        var shard2 = new ShardInfo { ShardId = 2 }; // 无范围，新分片

        manager.AddShard(shard0);
        manager.AddShard(shard1);
        manager.AddShard(shard2);

        // 键 50 在 shard0 范围内，且 shard2 无范围需要搜索
        var result = manager.GetShardsForKey(50);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.ShardId == 0);
        Assert.Contains(result, s => s.ShardId == 2);

        // 键 150 在 shard1 范围内
        result = manager.GetShardsForKey(150);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.ShardId == 1);
        Assert.Contains(result, s => s.ShardId == 2);

        // 键 300 不在任何有范围的分片中，只匹配无范围分片
        result = manager.GetShardsForKey(300);
        Assert.Single(result);
        Assert.Equal(2, result[0].ShardId);
    }

    [Fact(DisplayName = "测试边界键查找分片")]
    public void TestGetShardsForKeyBoundary()
    {
        var manager = new ShardManager(_options, _tempDir);

        var shard0 = new ShardInfo { ShardId = 0, MinKey = 1, MaxKey = 100 };
        manager.AddShard(shard0);

        // 最小边界
        var result = manager.GetShardsForKey(1);
        Assert.Single(result);
        Assert.Equal(0, result[0].ShardId);

        // 最大边界
        result = manager.GetShardsForKey(100);
        Assert.Single(result);
        Assert.Equal(0, result[0].ShardId);
    }

    [Fact(DisplayName = "测试对不存在的分片操作抛出异常")]
    public void TestOperationsOnNonExistentShardThrow()
    {
        var manager = new ShardManager(_options, _tempDir);

        Assert.Throws<NovaException>(() => manager.RecordWrite(99, 100));
        Assert.Throws<NovaException>(() => manager.ShouldSplit(99));
        Assert.Throws<NovaException>(() => manager.Split(99));
    }

    [Fact(DisplayName = "测试并发写入线程安全")]
    public void TestConcurrentWriteThreadSafety()
    {
        var options = new DbOptions
        {
            ShardSizeThreshold = Int64.MaxValue,
            ShardRowThreshold = Int64.MaxValue
        };
        var manager = new ShardManager(options, _tempDir);

        var shard = new ShardInfo { ShardId = 0 };
        manager.AddShard(shard);

        var threadCount = 10;
        var writesPerThread = 1000;
        var threads = new Thread[threadCount];

        for (var i = 0; i < threadCount; i++)
        {
            threads[i] = new Thread(() =>
            {
                for (var j = 0; j < writesPerThread; j++)
                {
                    manager.RecordWrite(0, 1);
                }
            });
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        var totalWrites = threadCount * writesPerThread;
        Assert.Equal(totalWrites, shard.RowCount);
        Assert.Equal(totalWrites, shard.SizeBytes);
    }

    [Fact(DisplayName = "测试构造参数为空抛出异常")]
    public void TestConstructorNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new ShardManager(null!, _tempDir));
        Assert.Throws<ArgumentNullException>(() => new ShardManager(_options, null!));
    }

    [Fact(DisplayName = "测试添加空分片抛出异常")]
    public void TestAddNullShardThrows()
    {
        var manager = new ShardManager(_options, _tempDir);
        Assert.Throws<ArgumentNullException>(() => manager.AddShard(null!));
    }

    [Fact(DisplayName = "测试按空键查找分片抛出异常")]
    public void TestGetShardsForNullKeyThrows()
    {
        var manager = new ShardManager(_options, _tempDir);
        Assert.Throws<ArgumentNullException>(() => manager.GetShardsForKey(null!));
    }
}
