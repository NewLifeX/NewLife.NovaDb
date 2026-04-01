using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Caching;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Queues;
using Xunit;

#nullable enable

namespace XUnitTest.Queues;

/// <summary>MQ 消息队列集成测试夹具。维护固定测试目录，每次启动前清空</summary>
public sealed class QueueDbFixture
{
    /// <summary>固定测试目录，位于项目根目录 TestData/QueueDb/</summary>
    public static readonly String DbPath = "../TestData/QueueDb/".GetFullPath();

    /// <summary>嵌入式连接字符串</summary>
    public String ConnectionString => $"Data Source={DbPath}";

    /// <summary>Flux 日志文件路径（EmbeddedDatabase 将 FluxEngine 创建在 flux/ 子目录）</summary>
    public String FluxLogPath => Path.Combine(DbPath, "flux", "flux.rlog");

    public QueueDbFixture()
    {
        // 每次测试启动前清空目录
        if (Directory.Exists(DbPath))
            Directory.Delete(DbPath, recursive: true);
        Directory.CreateDirectory(DbPath);
    }
}

/// <summary>MQ 消息队列嵌入模式集成测试，覆盖发布/消费/ACK/多 topic 及数据文件验证</summary>
/// <remarks>
/// 通过 NovaClientFactory.GetQueue&lt;T&gt;() 统一入口创建队列。
/// 测试数据保存到 TestData/QueueDb/ 目录（项目根目录），每次运行前清空，
/// 测试后文件留存，可人工检查 flux/flux.rlog 文件内容。
/// 每个测试使用唯一 topic 名，互不干扰。
/// </remarks>
public class NovaQueueIntegrationTests : IClassFixture<QueueDbFixture>
{
    private readonly QueueDbFixture _fixture;
    private readonly NovaClientFactory _factory = NovaClientFactory.Instance;

    public NovaQueueIntegrationTests(QueueDbFixture fixture)
    {
        _fixture = fixture;
    }

    private IProducerConsumer<String> GetQueue(String topic, String? group = null)
        => _factory.GetQueue<String>(_fixture.ConnectionString, topic, group);

    #region 文件验证测试

    [Fact(DisplayName = "MQ集成-首次发布后Flux日志文件已创建")]
    public void FileCreated_AfterFirstPublish()
    {
        var queue = GetQueue("file_check_topic", "file_check_group");
        queue.Add("hello");

        Assert.True(File.Exists(_fixture.FluxLogPath), "flux.rlog 应在首次发布后创建");
        Assert.True(new FileInfo(_fixture.FluxLogPath).Length > 0, "flux.rlog 应有内容");
    }

    [Fact(DisplayName = "MQ集成-多次发布后Flux文件大小增长")]
    public void FileGrows_WithMoreMessages()
    {
        // 先确保文件已创建（依赖前面的 Fact 使文件存在，或自行触发）
        var queue = GetQueue("filegrow_topic", "filegrow_group");
        queue.Add("seed");  // 确保文件存在

        var sizeBefore = new FileInfo(_fixture.FluxLogPath).Length;

        for (var i = 0; i < 30; i++)
            queue.Add($"message_{i}");

        var sizeAfter = new FileInfo(_fixture.FluxLogPath).Length;
        Assert.True(sizeAfter > sizeBefore, "发布 30 条消息后 flux.rlog 应大于之前");
    }

    #endregion

    #region 基本发布与消费

    [Fact(DisplayName = "MQ集成-Add单条消息并Take消费")]
    public void Add_And_Take_SingleMessage()
    {
        var queue = GetQueue("take_single_topic", "take_single_group");

        var added = queue.Add("hello_world");
        Assert.Equal(1, added);

        var messages = queue.Take(1000).ToList();
        Assert.Single(messages);
        Assert.Equal("hello_world", messages[0]);
    }

    [Fact(DisplayName = "MQ集成-Add多条消息并按顺序Take消费")]
    public void Add_MultipleMessages_TakeInOrder()
    {
        const String Topic = "order_topic";
        const String Group = "order_group";
        var queue = GetQueue(Topic, Group);

        queue.Add("msg_1", "msg_2", "msg_3");

        var messages = queue.Take(1000).ToList();
        Assert.Equal(3, messages.Count);
        Assert.Equal("msg_1", messages[0]);
        Assert.Equal("msg_2", messages[1]);
        Assert.Equal("msg_3", messages[2]);
    }

    [Fact(DisplayName = "MQ集成-Count反映未消费消息数")]
    public void Count_ReflectsUnconsumedMessages()
    {
        var queue = GetQueue("count_topic", "count_group");

        var countBefore = queue.Count;
        queue.Add("a", "b", "c");
        // Count 返回 FluxEngine 全局条目总数（所有 topic 累加），用 delta 验证
        Assert.Equal(countBefore + 3, queue.Count);
    }

    #endregion

    #region 消费组测试

    [Fact(DisplayName = "MQ集成-SetGroup创建消费组后可查到组名")]
    public void SetGroup_Creates_GroupName()
    {
        var novaQueue = (NovaQueue<String>)GetQueue("setgroup_topic");
        var created = novaQueue.SetGroup("my_group");

        Assert.True(created);
        var groups = novaQueue.GetConsumerGroupNames();
        Assert.Contains("my_group", groups);
    }

    [Fact(DisplayName = "MQ集成-消费组消费后消息进入Pending状态")]
    public void ConsumerGroup_ConsumedMessages_GoToPending()
    {
        const String Topic = "pending_topic";
        const String GroupName = "pending_group";

        var novaQueue = (NovaQueue<String>)GetQueue(Topic, GroupName);
        novaQueue.Add("task_1", "task_2");

        // 消费消息（进入 Pending 等待 ACK）
        var messages = novaQueue.Take(1000).ToList();
        Assert.True(messages.Count >= 2);

        // Pending 列表中应有记录
        var pending = novaQueue.GetPendingEntries(GroupName);
        Assert.NotEmpty(pending);
    }

    [Fact(DisplayName = "MQ集成-ACK确认后消息从Pending移除")]
    public void Acknowledge_RemovesFromPending()
    {
        const String Topic = "ack_topic";
        const String GroupName = "ack_group";

        var novaQueue = (NovaQueue<String>)GetQueue(Topic, GroupName);
        novaQueue.Add("task_ack");

        // 消费
        novaQueue.Take(1000).ToList();

        var pendingBefore = novaQueue.GetPendingEntries(GroupName);
        Assert.NotEmpty(pendingBefore);

        // ACK 确认
        var ackedCount = novaQueue.Acknowledge(pendingBefore[0].Id.ToString());
        Assert.Equal(1, ackedCount);

        // 确认后 Pending 应减少
        var pendingAfter = novaQueue.GetPendingEntries(GroupName);
        Assert.True(pendingAfter.Count < pendingBefore.Count);
    }

    [Fact(DisplayName = "MQ集成-ReadGroup直接读取消费组消息")]
    public void ReadGroup_ReturnsMessages_ByConsumerName()
    {
        const String Topic = "readgroup_topic";
        const String GroupName = "readgroup_main";
        const String Consumer = "consumer_01";

        var novaQueue = (NovaQueue<String>)GetQueue(Topic, GroupName);
        novaQueue.Add("item_A", "item_B");

        var entries = novaQueue.ReadGroup(GroupName, Consumer, 1000);
        Assert.True(entries.Count >= 2);
    }

    #endregion

    #region 多 topic 隔离

    [Fact(DisplayName = "MQ集成-多Topic消息互不干扰")]
    public void MultipleTopics_MessagesIsolated()
    {
        var queueA = GetQueue("isolated_topic_A", "group_A");
        var queueB = GetQueue("isolated_topic_B", "group_B");

        queueA.Add("message_for_A");
        queueB.Add("message_for_B");

        var messagesA = queueA.Take(1000).ToList();
        var messagesB = queueB.Take(1000).ToList();

        Assert.Single(messagesA);
        Assert.Equal("message_for_A", messagesA[0]);

        Assert.Single(messagesB);
        Assert.Equal("message_for_B", messagesB[0]);
    }

    [Fact(DisplayName = "MQ集成-不同Group消费同一Topic互不影响")]
    public void MultipleGroups_SameTopic_Independent()
    {
        const String Topic = "shared_topic";

        var queueG1 = (NovaQueue<String>)GetQueue(Topic, "grp_indep_1");
        var queueG2 = (NovaQueue<String>)GetQueue(Topic, "grp_indep_2");

        queueG1.Add("shared_msg_1", "shared_msg_2");

        // 两个消费组应各自读取全量消息（Take 使用大 count 保证覆盖整个日志）
        var msgsG1 = queueG1.Take(1000).ToList();
        var msgsG2 = queueG2.Take(1000).ToList();

        Assert.True(msgsG1.Count >= 2);
        Assert.True(msgsG2.Count >= 2);
    }

    #endregion

    #region TakeOne 与 IsEmpty

    [Fact(DisplayName = "MQ集成-TakeOne从空队列返回null")]
    public void TakeOne_EmptyQueue_ReturnsNull()
    {
        var queue = (NovaQueue<String>)GetQueue("empty_topic", "empty_group");

        var result = queue.TakeOne(timeout: 0);
        Assert.Null(result);
    }

    [Fact(DisplayName = "MQ集成-добавить消息后IsEmpty为false")]
    public void IsEmpty_FalseAfterAdd()
    {
        var queue = GetQueue("nonempty_topic", "nonempty_group");

        // Count 是 FluxEngine 全局总数，新 queue 加消息后不为空
        queue.Add("hello");
        Assert.False(queue.IsEmpty);
    }

    #endregion

    #region 异步消费测试

    [Fact(DisplayName = "MQ集成-ConsumeAsync循环接收消息")]
    public async Task ConsumeAsync_ReceivesMessages()
    {
        var queue = (NovaQueue<String>)GetQueue("consume_async_topic", "consume_async_group");
        queue.BlockTime = 1;

        queue.Add("async_msg_1", "async_msg_2");

        var received = new List<String>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await queue.ConsumeAsync(msg =>
        {
            lock (received) received.Add(msg);
            if (received.Count >= 2) cts.Cancel();
        }, cts.Token).ContinueWith(_ => { });  // 忽略 OperationCanceledException

        Assert.Equal(2, received.Count);
        Assert.Contains("async_msg_1", received);
        Assert.Contains("async_msg_2", received);
    }

    #endregion
}
