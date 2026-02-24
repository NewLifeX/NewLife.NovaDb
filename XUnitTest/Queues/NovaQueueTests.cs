using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;
using NewLife.NovaDb.Queues;
using Xunit;

#nullable enable

namespace XUnitTest.Queues;

/// <summary>NovaQueue 单元测试</summary>
public class NovaQueueTests : IDisposable
{
    private readonly String _testDir;

    public NovaQueueTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"NovaQueueTests_{Guid.NewGuid():N}");
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

    [Fact(DisplayName = "测试生产和消费消息")]
    public void TestAddAndTake()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            var queue = new NovaQueue<String>(manager, "test-topic");
            queue.SetGroup("test-group");

            // 生产消息
            var added = queue.Add("hello", "world");
            Assert.Equal(2, added);

            // 消费消息
            var messages = queue.Take(10).ToList();
            Assert.Equal(2, messages.Count);
            Assert.Equal("hello", messages[0]);
            Assert.Equal("world", messages[1]);
        }
    }

    [Fact(DisplayName = "测试消息计数")]
    public void TestCount()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            var queue = new NovaQueue<String>(manager, "test-topic");

            Assert.True(queue.IsEmpty);
            Assert.Equal(0, queue.Count);

            queue.Add("msg1");
            Assert.False(queue.IsEmpty);
        }
    }

    [Fact(DisplayName = "测试确认消息")]
    public void TestAcknowledge()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            var queue = new NovaQueue<String>(manager, "test-topic");
            queue.SetGroup("ack-group");

            queue.Add("msg1");

            var messages = queue.Take(1).ToList();
            Assert.Single(messages);

            // 从 pending 中获取 ID 进行确认
            var pending = manager.GetPendingEntries("ack-group");
            Assert.NotEmpty(pending);

            var count = queue.Acknowledge(pending[0].Id.ToString());
            Assert.Equal(1, count);
        }
    }

    [Fact(DisplayName = "测试SetGroup创建消费组")]
    public void TestSetGroup()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            var queue = new NovaQueue<String>(manager, "test-topic");
            Assert.True(queue.SetGroup("my-group"));

            var groups = manager.GetConsumerGroupNames();
            Assert.Contains("my-group", groups);
        }
    }

    [Fact(DisplayName = "测试主题名称")]
    public void TestTopic()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            var queue = new NovaQueue<String>(manager, "my-topic");
            Assert.Equal("my-topic", queue.Topic);
        }
    }

    [Fact(DisplayName = "测试异步消费循环")]
    public async Task TestConsumeAsync()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            var queue = new NovaQueue<String>(manager, "consume-topic");
            queue.SetGroup("consume-group");
            queue.BlockTime = 1;

            var received = new List<String>();
            var cts = new CancellationTokenSource();

            // 先发布消息
            queue.Add("msg1", "msg2");

            // 启动消费循环
            var consumeTask = queue.ConsumeAsync(msg => received.Add(msg), cts.Token);

            // 等待消费
            await Task.Delay(2000);
            cts.Cancel();

            try { await consumeTask; } catch (OperationCanceledException) { }

            Assert.Contains("msg1", received);
            Assert.Contains("msg2", received);
        }
    }

    [Fact(DisplayName = "测试整数类型消息")]
    public void TestIntMessages()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            var queue = new NovaQueue<Int32>(manager, "int-topic");
            queue.SetGroup("int-group");

            queue.Add(1, 2, 3);

            var messages = queue.Take(10).ToList();
            Assert.Equal(3, messages.Count);
            Assert.Equal(1, messages[0]);
            Assert.Equal(2, messages[1]);
            Assert.Equal(3, messages[2]);
        }
    }

    [Fact(DisplayName = "测试无消费组时Take抛出异常")]
    public void TestTakeWithoutGroup()
    {
        var (engine, manager) = CreateManager();
        using (engine)
        using (manager)
        {
            var queue = new NovaQueue<String>(manager, "test-topic");

            Assert.Throws<InvalidOperationException>(() => queue.Take(1).ToList());
        }
    }
}
