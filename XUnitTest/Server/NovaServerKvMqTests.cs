using System;
using System.IO;
using NewLife.NovaDb.Server;
using Xunit;

namespace XUnitTest.Server;

/// <summary>NovaDb 服务器 KV 和消息队列功能测试</summary>
[Collection("IntegrationTests")]
public class NovaServerKvMqTests : IDisposable
{
    private readonly String _dbPath;
    private readonly NovaServer _server;

    public NovaServerKvMqTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaServerKvMq_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbPath);
        _server = new NovaServer(0) { DbPath = _dbPath };
    }

    public void Dispose()
    {
        _server.Dispose();

        if (!String.IsNullOrEmpty(_dbPath) && Directory.Exists(_dbPath))
        {
            try { Directory.Delete(_dbPath, recursive: true); }
            catch { }
        }
    }

    [Fact(DisplayName = "服务器启动后KV存储可用")]
    public void ServerStartInitializesKvStore()
    {
        _server.Start();

        Assert.NotNull(_server.KvStore);
    }

    [Fact(DisplayName = "服务器启动后消息队列可用")]
    public void ServerStartInitializesStreamManager()
    {
        _server.Start();

        Assert.NotNull(_server.StreamManager);
    }

    [Fact(DisplayName = "服务器停止后KV存储释放")]
    public void ServerStopCleansKvStore()
    {
        _server.Start();
        Assert.NotNull(_server.KvStore);

        _server.Stop();
        Assert.Null(_server.KvStore);
    }

    [Fact(DisplayName = "服务器停止后消息队列释放")]
    public void ServerStopCleansStreamManager()
    {
        _server.Start();
        Assert.NotNull(_server.StreamManager);

        _server.Stop();
        Assert.Null(_server.StreamManager);
    }

    [Fact(DisplayName = "服务器注册了KV操作")]
    public void ServerRegistersKvActions()
    {
        _server.Start();
        var manager = _server.Server!.Manager;

        var kvActions = new[] { "Nova/KvSet", "Nova/KvGet", "Nova/KvDelete", "Nova/KvExists" };
        foreach (var action in kvActions)
        {
            Assert.True(manager.Services.ContainsKey(action), $"Missing KV action: {action}");
        }
    }

    [Fact(DisplayName = "服务器注册了MQ操作")]
    public void ServerRegistersMqActions()
    {
        _server.Start();
        var manager = _server.Server!.Manager;

        var mqActions = new[] { "Nova/MqPublish", "Nova/MqCreateGroup", "Nova/MqReadGroup", "Nova/MqAck" };
        foreach (var action in mqActions)
        {
            Assert.True(manager.Services.ContainsKey(action), $"Missing MQ action: {action}");
        }
    }
}
