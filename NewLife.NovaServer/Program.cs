using System;
using NewLife;
using NewLife.Configuration;
using NewLife.Log;
using NewLife.Model;
using NewLife.NovaDb.Server;
using Stardust;

// 启用控制台日志，拦截所有异常
XTrace.UseConsole();

// 初始化对象容器，提供注入能力
var services = ObjectContainer.Current;
services.AddSingleton(XTrace.Log);

// 配置星尘。自动读取配置文件 config/star.config 中的服务器地址
var star = services.AddStardust();

// 配置
//var set = MqttSetting.Current;

// 支持命令行 -Port 3390 或环境变量 NovaPort 指定端口（默认3306），便于与 MySQL 等共存
var port = 3306;
var parser = new CommandParser { IgnoreCase = true };
var cfg = parser.Parse(args);
if (cfg.TryGetValue("port", out var portArg))
    port = portArg.ToInt();
else if (!Environment.GetEnvironmentVariable("NovaPort").IsNullOrEmpty())
    port = Environment.GetEnvironmentVariable("NovaPort").ToInt();

//var parser = new CommandParser { IgnoreCase = true };
//var cfg = parser.Parse(args);
//if (cfg.TryGetValue("port", out var port)) set.Port = port.ToInt();
//if (cfg.TryGetValue("clusterPort", out var clusterPort)) set.ClusterPort = clusterPort.ToInt();
//if (cfg.TryGetValue("clusterNodes", out var clusterNodes)) set.ClusterNodes = clusterNodes;

//set.Save();

// 注册MQTT Broker的指令处理器
//services.AddSingleton<DefaultManagedMqttClient, DefaultManagedMqttClient>();

// 注册后台任务 IHostedService
var host = services.BuildHost();
// 服务器
var svr = new NovaServer(port)
{
    //Port = set.Port,
    //ServiceProvider = services.BuildServiceProvider(),

    //Tracer = star.Tracer,
    //Log = XTrace.Log,
};

//if (set.Debug) svr.SessionLog = XTrace.Log;

//#if DEBUG
//svr.SessionLog = svr.Log;
//svr.SocketLog = svr.Log;
//#endif

svr.Start();

if (Runtime.Windows)
    Console.Title = svr + "";

if (star.Service != null)
{
    _ = star.RegisterAsync("NovaServer", $"tcp://*:{svr.Port}");

    //if (svr.Cluster != null)
    //    _ = star.RegisterAsync("MqttCluster", $"tcp://*:{svr.ClusterPort}");
}

Host.RegisterExit((s, e) => svr.Stop(s + ""));

// 异步阻塞，友好退出
await host.RunAsync();