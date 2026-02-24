using NewLife.Caching;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Queues;

namespace NewLife.NovaDb.Caching;

/// <summary>Nova 缓存架构服务。提供基础缓存及队列服务</summary>
/// <remarks>
/// 参考 RedisCacheProvider 实现，根据连接字符串自动识别嵌入模式还是网络服务模式。
/// 嵌入模式下同一数据库目录共用引擎实例（通过 NovaClientFactory 的 EmbeddedDatabase 缓存）。
/// 网络模式通过 NovaClient 连接池远程操作。
/// 
/// 一个连接字符串 = 一个数据库 = 一个 NovaCacheProvider：
/// - 默认 KV 缓存通过 Cache 属性访问
/// - 多 KV 表通过 GetCache(tableName) 获取
/// - 多队列通过 GetQueue(topic, group) 获取
/// </remarks>
public class NovaCacheProvider : CacheProvider
{
    #region 属性
    private readonly String _connectionString;
    private readonly NovaClientFactory _factory;
    private NovaCache? _novaCache;
    private EmbeddedDatabase? _embeddedDb;
    private NovaClient? _client;
    #endregion

    #region 构造
    /// <summary>使用连接字符串创建 NovaCacheProvider</summary>
    /// <param name="connectionString">连接字符串。嵌入模式：Data Source=../data/mydb；网络模式：Server=localhost;Port=3306</param>
    /// <param name="factory">客户端工厂实例。默认使用全局单例</param>
    public NovaCacheProvider(String connectionString, NovaClientFactory? factory = null)
    {
        if (String.IsNullOrEmpty(connectionString))
            throw new ArgumentNullException(nameof(connectionString));

        _connectionString = connectionString;
        _factory = factory ?? NovaClientFactory.Instance;
        Init();
    }
    #endregion

    #region 方法
    /// <summary>根据连接字符串初始化</summary>
    private void Init()
    {
        var csb = new NovaConnectionStringBuilder(_connectionString);
        if (csb.IsEmbedded)
        {
            // 嵌入模式：通过工厂获取共用的 EmbeddedDatabase 实例
            _embeddedDb = _factory.GetOrCreateEmbeddedDatabase(csb);
            var kvStore = _embeddedDb.GetKvStore("default");

            _novaCache = new NovaCache(kvStore) { FluxEngine = _embeddedDb.FluxEngine };
            Cache = _novaCache;
        }
        else
        {
            // 网络服务模式：从连接池获取 NovaClient
            var pool = _factory.PoolManager.GetPool(csb);
            _client = pool.Get();

            _novaCache = new NovaCache(_client);
            Cache = _novaCache;
        }
    }

    /// <summary>获取指定名称的 KV 缓存实例</summary>
    /// <remarks>同一数据库内可包含多个 KV 表，通过 tableName 区分隔离</remarks>
    /// <param name="tableName">KV 表名</param>
    /// <returns>ICache 缓存实例</returns>
    public ICache GetCache(String tableName)
    {
        if (String.IsNullOrEmpty(tableName))
            throw new ArgumentNullException(nameof(tableName));

        if (_embeddedDb != null)
        {
            var kvStore = _embeddedDb.GetKvStore(tableName);
            return new NovaCache(kvStore) { Name = tableName, FluxEngine = _embeddedDb.FluxEngine };
        }

        if (_client != null)
            return new NovaCache(_client) { Name = tableName };

        throw new InvalidOperationException("NovaCacheProvider 未初始化");
    }

    /// <summary>获取队列</summary>
    /// <typeparam name="T">消息类型</typeparam>
    /// <param name="topic">主题</param>
    /// <param name="group">消费组</param>
    /// <returns>队列实例</returns>
    public override IProducerConsumer<T> GetQueue<T>(String topic, String? group = null)
    {
        if (_embeddedDb != null)
        {
            var queue = new NovaQueue<T>(_embeddedDb.FluxEngine, topic);
            if (!String.IsNullOrEmpty(group))
                queue.SetGroup(group!);
            return queue;
        }

        return base.GetQueue<T>(topic, group);
    }
    #endregion
}
