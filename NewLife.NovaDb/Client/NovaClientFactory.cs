using System.Collections.Concurrent;
using System.Data.Common;
using NewLife.Caching;
using NewLife.Log;
using NewLife.NovaDb.Caching;
using NewLife.NovaDb.Queues;

namespace NewLife.NovaDb.Client;

/// <summary>NovaDb ADO.NET 客户端工厂。统一入口，覆盖嵌入/网络双模式和关系型/时序/MQ/KV四大场景</summary>
/// <remarks>
/// 一个连接字符串对应一个数据库，数据库内可包含多张关系表、多张时序表（即多个 MQ）、多个 KV 库。
/// 
/// 嵌入模式连接字符串：Data Source=../data/mydb
/// 网络模式连接字符串：Server=localhost;Port=3306;Database=mydb
/// 
/// 四大场景用法：
/// - 关系型/时序：通过 ADO.NET（CreateConnection）使用 SQL 操作
/// - KV 缓存：通过 GetCache 获取 ICache 实例，支持多个 KV 表
/// - MQ 队列：通过 GetQueue 获取 IProducerConsumer 实例，支持多个 topic
/// - 综合：通过 GetCacheProvider 获取 ICacheProvider，同时提供缓存和队列
/// </remarks>
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

    /// <summary>连接池管理器（网络模式）</summary>
    public NovaPoolManager PoolManager { get; } = new();
    #endregion

    #region 静态
    /// <summary>默认实例</summary>
    public static NovaClientFactory Instance = new();

    /// <summary>嵌入模式引擎缓存。同一数据库目录共用引擎实例，避免重复创建</summary>
    private static readonly ConcurrentDictionary<String, EmbeddedDatabase> _embeddedDatabases = new(StringComparer.OrdinalIgnoreCase);

    static NovaClientFactory()
    {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP
        DbProviderFactories.RegisterFactory("NewLife.NovaDb.Client", Instance);
#endif
    }
    #endregion

    #region ADO.NET 工厂方法
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

    #region 缓存/队列/事件总线
    /// <summary>创建缓存架构服务（ICacheProvider），同时提供缓存和队列能力</summary>
    /// <remarks>
    /// 同一连接字符串对应同一数据库，内部共用引擎实例。
    /// 嵌入模式下，KV 和 MQ 引擎按数据库目录缓存共用。
    /// 网络模式下，连接从连接池获取共用。
    /// </remarks>
    /// <param name="connectionString">连接字符串。嵌入模式：Data Source=../data/mydb；网络模式：Server=localhost;Port=3306</param>
    /// <returns>NovaCacheProvider 实例</returns>
    public NovaCacheProvider GetCacheProvider(String connectionString) => new(connectionString, this);

    /// <summary>获取 KV 缓存实例</summary>
    /// <remarks>
    /// 同一数据库内可包含多个 KV 表，通过 tableName 区分。
    /// 嵌入模式下每个 KV 表对应独立的 KvStore 文件。
    /// 网络模式下通过 NovaClient 远程操作指定 KV 表。
    /// </remarks>
    /// <param name="connectionString">连接字符串</param>
    /// <param name="tableName">KV 表名。默认 "kv"</param>
    /// <returns>ICache 缓存实例</returns>
    public ICache GetCache(String connectionString, String tableName = "kv")
    {
        if (String.IsNullOrEmpty(connectionString))
            throw new ArgumentNullException(nameof(connectionString));

        var csb = new NovaConnectionStringBuilder(connectionString);
        if (csb.IsEmbedded)
        {
            var db = GetOrCreateEmbeddedDatabase(csb);
            var kvStore = db.GetKvStore(tableName);
            return new NovaCache(kvStore) { Name = tableName };
        }
        else
        {
            var pool = PoolManager.GetPool(csb);
            var client = pool.Get();
            try
            {
                return new NovaCache(client) { Name = tableName };
            }
            catch
            {
                pool.Return(client);
                throw;
            }
        }
    }

    /// <summary>获取消息队列实例</summary>
    /// <remarks>
    /// 同一数据库内可包含多个队列（topic），每个 topic 对应一个时序表。
    /// 嵌入模式下使用本地 FluxEngine 引擎；网络模式下通过 NovaClient 远程操作。
    /// </remarks>
    /// <typeparam name="T">消息类型</typeparam>
    /// <param name="connectionString">连接字符串</param>
    /// <param name="topic">队列主题名称</param>
    /// <param name="group">消费组名称。指定后使用消费组模式</param>
    /// <returns>IProducerConsumer 队列实例</returns>
    public IProducerConsumer<T> GetQueue<T>(String connectionString, String topic, String? group = null)
    {
        if (String.IsNullOrEmpty(connectionString))
            throw new ArgumentNullException(nameof(connectionString));
        if (String.IsNullOrEmpty(topic))
            throw new ArgumentNullException(nameof(topic));

        var csb = new NovaConnectionStringBuilder(connectionString);
        if (csb.IsEmbedded)
        {
            var db = GetOrCreateEmbeddedDatabase(csb);
            var queue = new NovaQueue<T>(db.FluxEngine, topic);
            if (!String.IsNullOrEmpty(group))
                queue.SetGroup(group!);
            return queue;
        }
        else
        {
            // 网络模式下暂使用本地桥接，后续由服务端 MQ 协议支持
            throw new NotSupportedException("网络模式下队列功能尚在开发中，请使用嵌入模式或 ADO.NET SQL 操作");
        }
    }

    /// <summary>创建 NovaCacheProvider 实例（兼容旧接口）</summary>
    /// <param name="connectionString">连接字符串</param>
    /// <returns>NovaCacheProvider 实例</returns>
    public static NovaCacheProvider CreateCacheProvider(String connectionString) => new(connectionString, Instance);
    #endregion

    #region 嵌入模式引擎管理
    /// <summary>获取或创建嵌入模式数据库引擎。同一数据库目录共用引擎实例</summary>
    /// <param name="csb">连接字符串设置</param>
    /// <returns>嵌入模式数据库实例</returns>
    internal EmbeddedDatabase GetOrCreateEmbeddedDatabase(NovaConnectionStringBuilder csb)
    {
        var dataSource = csb.DataSource;
        if (String.IsNullOrEmpty(dataSource))
            throw new InvalidOperationException("嵌入模式需要指定 DataSource");

        // 标准化路径作为缓存键
        var fullPath = Path.GetFullPath(dataSource);
        return _embeddedDatabases.GetOrAdd(fullPath, p => new EmbeddedDatabase(p, csb));
    }
    #endregion
}
