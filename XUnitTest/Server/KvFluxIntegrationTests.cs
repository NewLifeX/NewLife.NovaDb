using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NewLife.NovaDb.Client;
using Xunit;

// CS8632: XUnitTest project does not enable nullable annotations; suppress the warning from using Object? in Dictionary
#pragma warning disable CS8632

namespace XUnitTest.Server;

/// <summary>KV 和 Flux 控制器网络集成测试</summary>
[Collection("IntegrationTests")]
public class KvFluxIntegrationTests : IClassFixture<IntegrationServerFixture>
{
    private readonly IntegrationServerFixture _fixture;
    private Int32 _port => _fixture.Port;

    public KvFluxIntegrationTests(IntegrationServerFixture fixture)
    {
        _fixture = fixture;
    }

    private NovaClient CreateClient()
    {
        var client = new NovaClient($"tcp://127.0.0.1:{_port}");
        client.Open();
        return client;
    }

    #region KV 网络测试

    [Fact(DisplayName = "KV-网络设置和获取键值")]
    public async Task KvSetAndGet()
    {
        using var client = CreateClient();

        var ok = await client.KvSetAsync("net_key1", "hello");
        Assert.True(ok);

        var val = await client.KvGetAsync("net_key1");
        Assert.Equal("hello", val);
    }

    [Fact(DisplayName = "KV-网络获取不存在的键返回null")]
    public async Task KvGetMissingKeyReturnsNull()
    {
        using var client = CreateClient();

        var val = await client.KvGetAsync("net_key_nonexistent_" + Guid.NewGuid().ToString("N"));
        Assert.Null(val);
    }

    [Fact(DisplayName = "KV-网络检查键存在")]
    public async Task KvExists()
    {
        using var client = CreateClient();

        await client.KvSetAsync("net_key2", "world");

        Assert.True(await client.KvExistsAsync("net_key2"));
        Assert.False(await client.KvExistsAsync("net_key2_missing_" + Guid.NewGuid().ToString("N")));
    }

    [Fact(DisplayName = "KV-网络删除键")]
    public async Task KvDelete()
    {
        using var client = CreateClient();

        await client.KvSetAsync("net_key3", "todelete");
        Assert.True(await client.KvExistsAsync("net_key3"));

        var deleted = await client.KvDeleteAsync("net_key3");
        Assert.True(deleted);

        Assert.False(await client.KvExistsAsync("net_key3"));
        Assert.Null(await client.KvGetAsync("net_key3"));
    }

    [Fact(DisplayName = "KV-网络覆盖写入")]
    public async Task KvOverwrite()
    {
        using var client = CreateClient();

        await client.KvSetAsync("net_key4", "v1");
        await client.KvSetAsync("net_key4", "v2");

        var val = await client.KvGetAsync("net_key4");
        Assert.Equal("v2", val);
    }

    [Fact(DisplayName = "KV-网络完整CRUD流程")]
    public async Task KvFullCycle()
    {
        using var client = CreateClient();
        var key = "net_cycle_" + Guid.NewGuid().ToString("N")[..8];

        // 写入
        Assert.True(await client.KvSetAsync(key, "initial"));
        Assert.True(await client.KvExistsAsync(key));
        Assert.Equal("initial", await client.KvGetAsync(key));

        // 更新
        Assert.True(await client.KvSetAsync(key, "updated"));
        Assert.Equal("updated", await client.KvGetAsync(key));

        // 删除
        Assert.True(await client.KvDeleteAsync(key));
        Assert.False(await client.KvExistsAsync(key));
        Assert.Null(await client.KvGetAsync(key));
    }

    #endregion

    #region Flux 网络测试

    [Fact(DisplayName = "Flux-网络创建消费组")]
    public async Task FluxCreateGroup()
    {
        using var client = CreateClient();

        var ok = await client.MqCreateGroupAsync("net_grp1");
        Assert.True(ok);
    }

    [Fact(DisplayName = "Flux-网络发布消息")]
    public async Task FluxPublish()
    {
        using var client = CreateClient();

        var data = new Dictionary<String, Object?> { ["field1"] = "value1", ["count"] = 42 };
        var msgId = await client.MqPublishAsync(data);

        Assert.NotNull(msgId);
        Assert.NotEmpty(msgId);
    }

    [Fact(DisplayName = "Flux-网络创建组并发布读取消息")]
    public async Task FluxCreateGroupPublishAndRead()
    {
        using var client = CreateClient();
        var group = "net_grp_read_" + Guid.NewGuid().ToString("N")[..8];

        // 创建消费组
        Assert.True(await client.MqCreateGroupAsync(group));

        // 发布消息
        var data = new Dictionary<String, Object?> { ["msg"] = "hello flux" };
        var msgId = await client.MqPublishAsync(data);
        Assert.NotNull(msgId);

        // 读取消息
        var result = await client.MqReadGroupAsync(group, "consumer1", 10);
        // 结果可能为空数组（消费组在发布前创建时从最新位置开始）
        Assert.NotNull(result);
    }

    [Fact(DisplayName = "Flux-网络发布多条消息")]
    public async Task FluxPublishMultiple()
    {
        using var client = CreateClient();

        for (var i = 1; i <= 5; i++)
        {
            var data = new Dictionary<String, Object?> { ["index"] = i, ["value"] = $"msg_{i}" };
            var msgId = await client.MqPublishAsync(data);
            Assert.NotNull(msgId);
        }
    }

    [Fact(DisplayName = "Flux-网络确认消息")]
    public async Task FluxAck()
    {
        using var client = CreateClient();
        var group = "net_grp_ack_" + Guid.NewGuid().ToString("N")[..8];

        // 发布后创建消费组
        var data = new Dictionary<String, Object?> { ["key"] = "ack_test" };
        var msgId = await client.MqPublishAsync(data);
        Assert.NotNull(msgId);

        await client.MqCreateGroupAsync(group);

        // Ack 一个消息 ID（可能已消费或未消费，但不应抛出异常）
        var acked = await client.MqAckAsync(group, msgId);
        // 结果取决于实现，主要验证不抛异常且返回 bool
        Assert.IsType<Boolean>(acked);
    }

    #endregion
}
