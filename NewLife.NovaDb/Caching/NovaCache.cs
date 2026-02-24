using System.Diagnostics.CodeAnalysis;
using NewLife.Caching;
using NewLife.Data;
using NewLife.Messaging;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Engine.Flux;
using NewLife.NovaDb.Engine.KV;
using NewLife.NovaDb.Queues;

namespace NewLife.NovaDb.Caching;

/// <summary>NovaDb 缓存实现，基于 KvStore 引擎封装 ICache 接口</summary>
/// <remarks>
/// 支持嵌入模式（直接使用本地 KvStore）和网络模式（通过 NovaClient 远程调用）。
/// 嵌入模式下直接操作 KvStore 引擎，性能更高；网络模式下通过 RPC 远程操作。
/// </remarks>
public class NovaCache : Cache
{
    #region 属性
    private readonly KvStore? _kvStore;
    private readonly NovaClient? _client;
    private FluxEngine? _fluxEngine;

    /// <summary>是否为嵌入模式</summary>
    public Boolean IsEmbedded => _kvStore != null;

    /// <summary>编码器，负责应用层类型与二进制之间的转换</summary>
    public IPacketEncoder Encoder { get; set; } = new NovaJsonEncoder();

    /// <summary>Flux 引擎（嵌入模式下可用于队列功能）</summary>
    public FluxEngine? FluxEngine
    {
        get => _fluxEngine;
        set => _fluxEngine = value;
    }
    #endregion

    #region 构造
    /// <summary>创建嵌入模式的 NovaCache</summary>
    /// <param name="kvStore">KV 存储引擎</param>
    public NovaCache(KvStore kvStore)
    {
        _kvStore = kvStore ?? throw new ArgumentNullException(nameof(kvStore));
        Name = "Nova";
    }

    /// <summary>创建网络模式的 NovaCache</summary>
    /// <param name="client">NovaDb 远程客户端</param>
    public NovaCache(NovaClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Name = "Nova";
    }
    #endregion

    #region 基本操作
    /// <summary>缓存项总数</summary>
    public override Int32 Count
    {
        get
        {
            if (_kvStore != null) return _kvStore.Count;
            if (_client != null) return _client.KvGetCountAsync(Name).ConfigureAwait(false).GetAwaiter().GetResult();
            return 0;
        }
    }

    /// <summary>所有缓存键集合</summary>
    public override ICollection<String> Keys
    {
        get
        {
            if (_kvStore != null) return _kvStore.GetAllKeys().ToList();
            if (_client != null) return _client.KvGetAllKeysAsync(Name).ConfigureAwait(false).GetAwaiter().GetResult();
            return [];
        }
    }

    /// <summary>检查缓存项是否存在</summary>
    /// <param name="key">键</param>
    /// <returns>是否存在</returns>
    public override Boolean ContainsKey(String key)
    {
        if (_kvStore != null) return _kvStore.Exists(key);
        if (_client != null) return _client.KvExistsAsync(Name, key).ConfigureAwait(false).GetAwaiter().GetResult();
        return false;
    }

    /// <summary>设置缓存项</summary>
    /// <param name="key">键</param>
    /// <param name="value">值</param>
    /// <param name="expire">过期时间（秒）。小于0时采用默认缓存时间；0 表示永不过期</param>
    /// <returns>是否成功设置</returns>
    public override Boolean Set<T>(String key, T value, Int32 expire = -1)
    {
        if (expire < 0) expire = Expire;
        var ttl = expire > 0 ? TimeSpan.FromSeconds(expire) : (TimeSpan?)null;
        var buf = Encoder.Encode(value)?.ReadBytes();

        if (_kvStore != null)
        {
            _kvStore.Set(key, buf, ttl);
            return true;
        }

        if (_client != null)
            return _client.KvSetAsync(Name, key, buf, expire).ConfigureAwait(false).GetAwaiter().GetResult();

        return false;
    }

    /// <summary>获取缓存项</summary>
    /// <param name="key">键</param>
    /// <returns>缓存值</returns>
    [return: MaybeNull]
    public override T Get<T>(String key)
    {
        if (_kvStore != null)
        {
            using var pk = _kvStore.Get(key);
            if (pk == null || pk.Total == 0) return default;

            return (T?)Encoder.Decode(pk, typeof(T));
        }

        if (_client != null)
        {
            var buf = _client.KvGetAsync(Name, key).ConfigureAwait(false).GetAwaiter().GetResult();
            if (buf == null || buf.Length == 0) return default;

            return (T?)Encoder.Decode(new ArrayPacket(buf), typeof(T));
        }

        return default;
    }

    /// <summary>移除缓存项</summary>
    /// <param name="key">键</param>
    /// <returns>受影响的键个数</returns>
    public override Int32 Remove(String key)
    {
        if (_kvStore != null)
        {
            // 支持通配符模式
            if (key.Contains('*') || key.Contains('?'))
                return _kvStore.DeleteByPattern(key);

            return _kvStore.Delete(key) ? 1 : 0;
        }

        if (_client != null)
        {
            // 支持通配符模式
            if (key.Contains('*') || key.Contains('?'))
                return _client.KvDeleteByPatternAsync(Name, key).ConfigureAwait(false).GetAwaiter().GetResult();

            return _client.KvDeleteAsync(Name, key).ConfigureAwait(false).GetAwaiter().GetResult() ? 1 : 0;
        }

        return 0;
    }

    /// <summary>批量移除缓存项</summary>
    /// <param name="keys">键集合</param>
    /// <returns>受影响的键个数</returns>
    public override Int32 Remove(params String[] keys)
    {
        if (keys == null || keys.Length == 0) return 0;

        var count = 0;
        foreach (var key in keys)
        {
            count += Remove(key);
        }
        return count;
    }

    /// <summary>清空所有缓存项</summary>
    public override void Clear()
    {
        if (_kvStore != null)
            _kvStore.Clear();
        else if (_client != null)
            _client.KvClearAsync(Name).ConfigureAwait(false).GetAwaiter().GetResult();
    }
    #endregion

    #region 过期时间
    /// <summary>设置缓存项有效期</summary>
    /// <param name="key">键</param>
    /// <param name="expire">过期时间</param>
    /// <returns>是否成功</returns>
    public override Boolean SetExpire(String key, TimeSpan expire)
    {
        if (_kvStore != null) return _kvStore.SetExpiration(key, expire);
        if (_client != null) return _client.KvSetExpireAsync(Name, key, (Int32)expire.TotalSeconds).ConfigureAwait(false).GetAwaiter().GetResult();
        return false;
    }

    /// <summary>获取缓存项有效期</summary>
    /// <param name="key">键</param>
    /// <returns>剩余 TTL</returns>
    public override TimeSpan GetExpire(String key)
    {
        if (_kvStore != null) return _kvStore.GetTtl(key);
        if (_client != null) return TimeSpan.FromSeconds(_client.KvGetExpireAsync(Name, key).ConfigureAwait(false).GetAwaiter().GetResult());
        return TimeSpan.FromSeconds(-1);
    }
    #endregion

    #region 高级操作
    /// <summary>原子递增</summary>
    /// <param name="key">键</param>
    /// <param name="value">变化量</param>
    /// <returns>更新后的值</returns>
    public override Int64 Increment(String key, Int64 value)
    {
        if (_kvStore != null) return _kvStore.Inc(key, value);
        if (_client != null) return _client.KvIncrementAsync(Name, key, value).ConfigureAwait(false).GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>原子递增（浮点）</summary>
    /// <param name="key">键</param>
    /// <param name="value">变化量</param>
    /// <returns>更新后的值</returns>
    public override Double Increment(String key, Double value)
    {
        if (_kvStore != null) return _kvStore.IncDouble(key, value);
        if (_client != null) return _client.KvIncrementDoubleAsync(Name, key, value).ConfigureAwait(false).GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>原子递减</summary>
    /// <param name="key">键</param>
    /// <param name="value">变化量</param>
    /// <returns>更新后的值</returns>
    public override Int64 Decrement(String key, Int64 value) => Increment(key, -value);

    /// <summary>原子递减（浮点）</summary>
    /// <param name="key">键</param>
    /// <param name="value">变化量</param>
    /// <returns>更新后的值</returns>
    public override Double Decrement(String key, Double value) => Increment(key, -value);

    /// <summary>搜索匹配的键</summary>
    /// <param name="pattern">搜索模式</param>
    /// <param name="offset">偏移量</param>
    /// <param name="count">数量</param>
    /// <returns>匹配的键集合</returns>
    public override IEnumerable<String> Search(String pattern, Int32 offset = 0, Int32 count = -1)
    {
        if (_kvStore != null) return _kvStore.Search(pattern, offset, count);
        if (_client != null) return _client.KvSearchAsync(Name, pattern, offset, count).ConfigureAwait(false).GetAwaiter().GetResult();
        return [];
    }

    /// <summary>提交变更</summary>
    /// <returns>0</returns>
    public override Int32 Commit() => 0;
    #endregion

    #region 队列与事件总线
    /// <summary>获取队列</summary>
    /// <typeparam name="T">元素类型</typeparam>
    /// <param name="key">键</param>
    /// <returns>队列实例</returns>
    public override IProducerConsumer<T> GetQueue<T>(String key)
    {
        if (_fluxEngine == null)
            throw new NotSupportedException("队列功能需要设置 FluxEngine");

        return new NovaQueue<T>(_fluxEngine, key);
    }

    /// <summary>创建事件总线</summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="topic">主题</param>
    /// <param name="clientId">客户标识</param>
    /// <returns>事件总线实例</returns>
    public override IEventBus<TEvent> CreateEventBus<TEvent>(String topic, String clientId = "")
    {
        if (_fluxEngine == null) return base.CreateEventBus<TEvent>(topic, clientId);

        return new NovaEventBus<TEvent>(_fluxEngine, topic, clientId);
    }
    #endregion

}
