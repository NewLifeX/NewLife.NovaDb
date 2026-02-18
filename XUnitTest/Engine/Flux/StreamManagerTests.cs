using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;

namespace XUnitTest.Engine.Flux;

/// <summary>StreamManager 单元测试</summary>
public class StreamManagerTests : IDisposable
{
    private readonly String _testDir;

    public StreamManagerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"StreamManagerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); }
            catch { }
        }
    }

    private (FluxEngine engine, StreamManager manager) CreateManager()
    {
        var options = new DbOptions { FluxPartitionHours = 1 };
        var engine = new FluxEngine(_testDir, options);
        var manager = new StreamManager(engine);
        return (engine, manager);
    }

    [Fact(DisplayName = "测试发布消息")]
    public void TestPublish()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            var now = DateTime.UtcNow.Ticks;
            var entry = new FluxEntry
            {
                Timestamp = now,
                Fields = new Dictionary<String, Object?> { ["msg"] = "hello" }
            };

            var id = manager.Publish(entry);

            Assert.Equal(now, id.Timestamp);
            Assert.Equal(0, id.Sequence);
            Assert.Equal(1, engine.GetEntryCount());
        }
    }

    [Fact(DisplayName = "测试消息 ID 自增")]
    public void TestMessageIdAutoIncrement()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            var now = DateTime.UtcNow.Ticks;

            var id1 = manager.Publish(new FluxEntry { Timestamp = now, Fields = new Dictionary<String, Object?> { ["v"] = 1 } });
            var id2 = manager.Publish(new FluxEntry { Timestamp = now, Fields = new Dictionary<String, Object?> { ["v"] = 2 } });
            var id3 = manager.Publish(new FluxEntry { Timestamp = now, Fields = new Dictionary<String, Object?> { ["v"] = 3 } });

            Assert.Equal(0, id1.Sequence);
            Assert.Equal(1, id2.Sequence);
            Assert.Equal(2, id3.Sequence);
        }
    }

    [Fact(DisplayName = "测试创建消费组")]
    public void TestCreateConsumerGroup()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            manager.CreateConsumerGroup("group1");
            manager.CreateConsumerGroup("group2");

            var names = manager.GetConsumerGroupNames();
            Assert.Equal(2, names.Count);
            Assert.Contains("group1", names);
            Assert.Contains("group2", names);
        }
    }

    [Fact(DisplayName = "测试消费组读取")]
    public void TestReadGroup()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            var now = DateTime.UtcNow.Ticks;

            // 发布 5 条消息
            for (var i = 0; i < 5; i++)
            {
                manager.Publish(new FluxEntry
                {
                    Timestamp = now + i,
                    Fields = new Dictionary<String, Object?> { ["v"] = i }
                });
            }

            manager.CreateConsumerGroup("g1");

            // 读取 3 条
            var messages = manager.ReadGroup("g1", "consumer1", 3);
            Assert.Equal(3, messages.Count);

            // 再读取剩余
            var remaining = manager.ReadGroup("g1", "consumer1", 10);
            Assert.Equal(2, remaining.Count);

            // 没有更多消息
            var empty = manager.ReadGroup("g1", "consumer1", 10);
            Assert.Empty(empty);
        }
    }

    [Fact(DisplayName = "测试确认消息")]
    public void TestAcknowledge()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            var now = DateTime.UtcNow.Ticks;
            var id = manager.Publish(new FluxEntry
            {
                Timestamp = now,
                Fields = new Dictionary<String, Object?> { ["v"] = 1 }
            });

            manager.CreateConsumerGroup("g1");
            manager.ReadGroup("g1", "consumer1", 1);

            // 确认前有 1 条 pending
            var pending = manager.GetPendingEntries("g1");
            Assert.Single(pending);

            // 确认消息
            var acked = manager.Acknowledge("g1", id);
            Assert.True(acked);

            // 确认后 pending 为空
            pending = manager.GetPendingEntries("g1");
            Assert.Empty(pending);
        }
    }

    [Fact(DisplayName = "测试待确认条目")]
    public void TestPendingEntries()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            var now = DateTime.UtcNow.Ticks;

            for (var i = 0; i < 3; i++)
            {
                manager.Publish(new FluxEntry
                {
                    Timestamp = now + i,
                    Fields = new Dictionary<String, Object?> { ["v"] = i }
                });
            }

            manager.CreateConsumerGroup("g1");
            manager.ReadGroup("g1", "consumer1", 3);

            var pending = manager.GetPendingEntries("g1");
            Assert.Equal(3, pending.Count);

            // 每条 pending 都有消费者信息
            foreach (var p in pending)
            {
                Assert.Equal("consumer1", p.Consumer);
                Assert.Equal(1, p.DeliveryCount);
                Assert.NotNull(p.Id);
            }
        }
    }

    [Fact(DisplayName = "测试消费组不存在异常")]
    public void TestConsumerGroupNotFound()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            var ex = Assert.Throws<NovaException>(() => manager.ReadGroup("nonexistent", "c1", 1));
            Assert.Equal(ErrorCode.ConsumerGroupNotFound, ex.Code);
        }
    }

    [Fact(DisplayName = "测试多消费组独立消费")]
    public void TestMultipleConsumerGroups()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            var now = DateTime.UtcNow.Ticks;

            for (var i = 0; i < 3; i++)
            {
                manager.Publish(new FluxEntry
                {
                    Timestamp = now + i,
                    Fields = new Dictionary<String, Object?> { ["v"] = i }
                });
            }

            manager.CreateConsumerGroup("g1");
            manager.CreateConsumerGroup("g2");

            // 两个组各自独立读取
            var g1Messages = manager.ReadGroup("g1", "c1", 10);
            var g2Messages = manager.ReadGroup("g2", "c2", 10);

            Assert.Equal(3, g1Messages.Count);
            Assert.Equal(3, g2Messages.Count);
        }
    }
}
