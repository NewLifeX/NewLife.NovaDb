using System.Data.Common;
using NewLife.Log;
using NewLife.NovaDb.Caching;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;
using NewLife.NovaDb.Engine.KV;

namespace NewLife.NovaDb.Client;

/// <summary>NovaDb ADO.NET 客户端工厂</summary>
public sealed class NovaClientFactory : DbProviderFactory
{
    #region 属性
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP
    /// <summary>是否支持创建命令构建器。不支持</summary>
    public override Boolean CanCreateCommandBuilder => false;

    /// <summary>是否支持创建数据适配器</summary>
    public override Boolean CanCreateDataAdapter => true;
#endif

#if NET6_0_OR_GREATER
    /// <summary>是否支持创建批量命令</summary>
    public override Boolean CanCreateBatch => true;
#endif

    /// <summary>不支持创建数据源枚举</summary>
    public override Boolean CanCreateDataSourceEnumerator => false;

    /// <summary>性能跟踪器</summary>
    public ITracer? Tracer { get; set; }

    /// <summary>连接池管理器</summary>
    public NovaPoolManager PoolManager { get; } = new();
    #endregion

    #region 静态
    /// <summary>默认实例</summary>
    public static NovaClientFactory Instance = new();

    static NovaClientFactory()
    {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP
        DbProviderFactories.RegisterFactory("NewLife.NovaDb.Client", Instance);
#endif
    }
    #endregion

    #region 方法
    /// <summary>创建命令</summary>
    /// <returns>命令实例</returns>
    public override DbCommand CreateCommand() => new NovaCommand();

    /// <summary>创建连接</summary>
    /// <returns>连接实例</returns>
    public override DbConnection CreateConnection() => new NovaConnection { Factory = this };

    /// <summary>创建参数</summary>
    /// <returns>参数实例</returns>
    public override DbParameter CreateParameter() => new NovaParameter();

    /// <summary>创建连接字符串构建器</summary>
    /// <returns>连接字符串构建器实例</returns>
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new NovaConnectionStringBuilder();

    /// <summary>创建数据适配器</summary>
    /// <returns>数据适配器实例</returns>
    public override DbDataAdapter CreateDataAdapter() => new NovaDataAdapter();

#if NET6_0_OR_GREATER
    /// <summary>创建批量命令</summary>
    /// <returns>批量命令实例</returns>
    public override DbBatch CreateBatch() => new NovaBatch();

    /// <summary>创建批量命令项</summary>
    /// <returns>批量命令项实例</returns>
    public override DbBatchCommand CreateBatchCommand() => new NovaBatchCommand();
#endif
    #endregion

    #region 缓存
    /// <summary>创建 NovaCache 实例，根据连接字符串自动识别嵌入模式或网络服务模式</summary>
    /// <param name="connectionString">连接字符串。嵌入模式：Data Source=../data；网络模式：Server=localhost;Port=3306</param>
    /// <returns>NovaCache 实例</returns>
    public static NovaCache CreateCache(String connectionString)
    {
        if (String.IsNullOrEmpty(connectionString))
            throw new ArgumentNullException(nameof(connectionString));

        var csb = new NovaConnectionStringBuilder(connectionString);

        if (csb.IsEmbedded)
        {
            // 嵌入模式：直接使用本地 KvStore
            var dataSource = csb.DataSource!;
            var kvStore = new KvStore(null, dataSource);

            // 同时创建流管理器用于队列功能
            var mqPath = Path.Combine(dataSource, "mq");
            var fluxEngine = new FluxEngine(mqPath, new DbOptions());
            var streamManager = new StreamManager(fluxEngine);

            return new NovaCache(kvStore) { StreamManager = streamManager };
        }
        else
        {
            // 网络服务模式：通过 NovaClient 远程调用
            var server = csb.Server ?? "localhost";
            var port = csb.Port;
            var uri = $"tcp://{server}:{port}";

            var client = new NovaClient(uri);
            client.Open();

            return new NovaCache(client);
        }
    }

    /// <summary>创建 NovaCacheProvider 实例，根据连接字符串自动识别嵌入模式或网络服务模式</summary>
    /// <param name="connectionString">连接字符串。嵌入模式：Data Source=../data；网络模式：Server=localhost;Port=3306</param>
    /// <returns>NovaCacheProvider 实例</returns>
    public static NovaCacheProvider CreateCacheProvider(String connectionString) => new(connectionString);
    #endregion
}
