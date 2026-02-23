using NewLife.Log;
using NewLife.NovaDb.Cluster;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;
using NewLife.NovaDb.Engine.KV;
using NewLife.NovaDb.Sql;
using NewLife.NovaDb.Storage;
using NewLife.Remoting;

namespace NewLife.NovaDb.Server;

/// <summary>NovaDb TCP 服务器，基于 Remoting 的 ApiServer 实现 RPC 通信</summary>
public class NovaServer : DisposeBase
{
    private ApiServer? _server;
    private readonly Int32 _port;
    private SqlEngine? _sqlEngine;
    private DatabaseManager? _dbManager;
    private ReplicationManager? _replicationManager;
    private KvStore? _kvStore;
    private FluxEngine? _fluxEngine;
    private StreamManager? _streamManager;

    /// <summary>端口</summary>
    public Int32 Port => _server?.Port ?? _port;

    /// <summary>是否运行中</summary>
    public Boolean IsRunning => _server?.Active ?? false;

    /// <summary>内部 ApiServer 实例</summary>
    public ApiServer? Server => _server;

    /// <summary>数据库路径。为空时使用当前目录下的 NovaData 文件夹</summary>
    public String DbPath { get; set; } = String.Empty;

    /// <summary>服务模式数据库配置选项</summary>
    public ServerDbOptions Options { get; set; } = new();

    /// <summary>SQL 执行引擎</summary>
    public SqlEngine? SqlEngine => _sqlEngine;

    /// <summary>数据库管理器</summary>
    public DatabaseManager? DbManager => _dbManager;

    /// <summary>复制管理器（主节点模式时可用）</summary>
    public ReplicationManager? ReplicationManager => _replicationManager;

    /// <summary>KV 存储引擎</summary>
    public KvStore? KvStore => _kvStore;

    /// <summary>流管理器（消息队列）</summary>
    public StreamManager? StreamManager => _streamManager;

    /// <summary>节点 ID，用于集群标识</summary>
    public String NodeId { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>节点角色</summary>
    public NodeRole NodeRole { get; set; } = NodeRole.Master;

    /// <summary>创建服务器实例</summary>
    /// <param name="port">监听端口</param>
    public NovaServer(Int32 port = 3306)
    {
        _port = port;
    }

    /// <summary>启动服务器</summary>
    public void Start()
    {
        if (_server != null && _server.Active) return;

        // 初始化 SQL 引擎
        var dbPath = DbPath;
        if (String.IsNullOrEmpty(dbPath))
            dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NovaData");

        // 初始化数据库管理器，创建/打开系统库并扫描发现所有数据库
        var dbOptions = Options;
        dbOptions.Path = dbPath;
        _dbManager = new DatabaseManager(dbPath, dbOptions);
        _dbManager.Initialize();

        _sqlEngine = new SqlEngine(dbPath, dbOptions);

        // 初始化 KV 存储引擎
        var kvPath = Path.Combine(dbPath, "kv");
        _kvStore = new KvStore(dbOptions, kvPath);

        // 初始化消息队列引擎
        var mqPath = Path.Combine(dbPath, "mq");
        _fluxEngine = new FluxEngine(mqPath, dbOptions);
        _streamManager = new StreamManager(_fluxEngine);

        // 初始化复制管理器（主节点模式）
        if (NodeRole == NodeRole.Master)
        {
            var masterInfo = new NodeInfo
            {
                NodeId = NodeId,
                Endpoint = $"127.0.0.1:{_port}",
                Role = NodeRole.Master
            };
            _replicationManager = new ReplicationManager(Path.Combine(dbPath, "wal"), masterInfo);
        }

        // 设置共享引擎供控制器使用
        NovaController.SharedEngine = _sqlEngine;
        NovaController.SharedReplication = _replicationManager;
        KvController.SharedKvStore = _kvStore;
        FluxController.SharedStreamManager = _streamManager;

        var server = new ApiServer(_port)
        {
            Log = XTrace.Log,
        };

        // 注册 NovaDb 业务控制器
        server.Register<NovaController>();
        server.Register<KvController>();
        server.Register<FluxController>();

        server.Start();
        _server = server;

        // 启动复制循环
        _replicationManager?.StartReplication();
    }

    /// <summary>停止服务器</summary>
    /// <param name="reason">停止原因</param>
    public void Stop(String? reason = null)
    {
        _replicationManager?.StopReplication();
        _replicationManager?.Dispose();
        _replicationManager = null;

        _server?.Stop(reason ?? "NovaServer.Stop");
        _server = null;

        NovaController.SharedEngine = null;
        NovaController.SharedReplication = null;
        KvController.SharedKvStore = null;
        FluxController.SharedStreamManager = null;

        _streamManager?.Dispose();
        _streamManager = null;
        _fluxEngine?.Dispose();
        _fluxEngine = null;
        _kvStore?.CloseKvLog();
        _kvStore = null;

        _sqlEngine?.Dispose();
        _sqlEngine = null;
        _dbManager = null;
    }

    /// <summary>释放资源</summary>
    /// <param name="disposing">是否由 Dispose 调用</param>
    protected override void Dispose(Boolean disposing)
    {
        base.Dispose(disposing);

        _replicationManager?.StopReplication();
        _replicationManager?.Dispose();
        _replicationManager = null;

        _server?.Stop(disposing ? "Dispose" : "GC");
        _server.TryDispose();
        _server = null;

        NovaController.SharedEngine = null;
        NovaController.SharedReplication = null;
        KvController.SharedKvStore = null;
        FluxController.SharedStreamManager = null;

        _streamManager?.Dispose();
        _streamManager = null;
        _fluxEngine?.Dispose();
        _fluxEngine = null;
        _kvStore?.CloseKvLog();
        _kvStore = null;

        _sqlEngine?.Dispose();
        _sqlEngine = null;
        _dbManager = null;
    }
}

