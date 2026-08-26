using System;
using System.IO;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Server;
using Xunit;

namespace XUnitTest.Server;

/// <summary>NovaDb 服务器单元测试</summary>
[Collection("IntegrationTests")]
public class NovaServerTests : IDisposable
{
    private readonly String _dbPath;
    private readonly NovaServer _server;

    public NovaServerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaServer_{Guid.NewGuid():N}");
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

    [Fact(DisplayName = "测试创建服务器")]
    public void TestCreateServer()
    {
        Assert.NotNull(_server);
        Assert.False(_server.IsRunning);
    }

    [Fact(DisplayName = "测试启动和停止服务器")]
    public void TestStartAndStop()
    {
        _server.Start();
        Assert.True(_server.IsRunning);
        Assert.True(_server.Port > 0);

        _server.Stop();
        Assert.False(_server.IsRunning);
    }

    [Fact(DisplayName = "测试服务器注册了控制器")]
    public void TestServerHasController()
    {
        _server.Start();
        Assert.NotNull(_server.Server);

        // ApiServer should have registered the NovaController
        var manager = _server.Server!.Manager;
        Assert.NotNull(manager);

        // Check that Nova/Ping action is registered
        Assert.True(manager.Services.ContainsKey("Nova/Ping"));
    }

    [Fact(DisplayName = "测试服务器注册了所有 RPC 操作")]
    public void TestAllRpcActions()
    {
        _server.Start();
        var manager = _server.Server!.Manager;

        var expectedActions = new[]
        {
            "Nova/Ping",
            "Nova/Execute",
            "Nova/Query",
            "Nova/BeginTransaction",
            "Nova/CommitTransaction",
            "Nova/RollbackTransaction"
        };

        foreach (var action in expectedActions)
        {
            Assert.True(manager.Services.ContainsKey(action), $"Missing action: {action}");
        }
    }

    [Fact(DisplayName = "测试重复启动无异常")]
    public void TestDoubleStartNoError()
    {
        _server.Start();
        _server.Start(); // Should not throw
        Assert.True(_server.IsRunning);
    }

    [Fact(DisplayName = "服务器注册 GetVersion 操作")]
    public void TestGetVersionRegistered()
    {
        _server.Start();
        var manager = _server.Server!.Manager;
        Assert.True(manager.Services.ContainsKey("Nova/GetVersion"), "Missing action: Nova/GetVersion");
    }

    [Fact(DisplayName = "GetVersion 返回协议版本且客户端连接校验通过")]
    public void GetVersion_ReturnsProtocolVersion()
    {
        _server.Start();

        using var client = new NovaClient($"tcp://127.0.0.1:{_server.Port}");
        client.Open(); // Open 内部校验版本，匹配则通过

        var version = client.InvokeAsync<Int32>("Nova/GetVersion").GetAwaiter().GetResult();
        Assert.Equal(NovaProtocol.ProtocolVersion, version);

        client.Close();
    }
}
