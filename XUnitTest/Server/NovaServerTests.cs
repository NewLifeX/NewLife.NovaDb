using System;
using NewLife.NovaDb.Server;
using Xunit;

namespace XUnitTest.Server;

/// <summary>NovaDb 服务器单元测试</summary>
[Collection("IntegrationTests")]
public class NovaServerTests : IDisposable
{
    private readonly NovaServer _server;

    public NovaServerTests()
    {
        _server = new NovaServer(0);
    }

    public void Dispose() => _server.Dispose();

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
}
