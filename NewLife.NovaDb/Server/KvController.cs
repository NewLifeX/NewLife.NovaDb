using NewLife.Data;
using NewLife.NovaDb.Engine.KV;
using NewLife.Remoting;

namespace NewLife.NovaDb.Server;

/// <summary>KV 存储 RPC 控制器，提供键值对操作接口</summary>
/// <remarks>
/// 控制器方法通过 Remoting RPC 暴露为远程接口。
/// 路由格式：Kv/{方法名}，如 Kv/Set、Kv/Get。
/// 每个方法均需指定 tableName 参数来指明操作哪个 KV 表，默认为 "default"。
/// 控制器实例由 Remoting 框架按请求创建，通过静态字段共享引擎。
/// </remarks>
internal class KvController : IApi
{
    /// <summary>会话</summary>
    public IApiSession Session { get; set; } = null!;

    /// <summary>共享默认 KV 存储引擎，由 NovaServer 启动时设置</summary>
    internal static KvStore? SharedKvStore { get; set; }

    /// <summary>共享 NovaServer 实例，用于获取多 KV 表</summary>
    internal static NovaServer? SharedServer { get; set; }

    /// <summary>根据表名获取对应的 KvStore 实例</summary>
    /// <param name="tableName">KV 表名，为空或 "default" 时返回默认表</param>
    /// <returns>KvStore 实例，未初始化时返回 null</returns>
    private static KvStore? GetStore(String? tableName)
    {
        if (String.IsNullOrEmpty(tableName) || "default".Equals(tableName, StringComparison.OrdinalIgnoreCase))
            return SharedKvStore;

        return SharedServer?.GetKvStore(tableName);
    }

    /// <summary>KV 设置键值对</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <param name="value">二进制值</param>
    /// <param name="ttlSeconds">过期时间（秒），0 表示永不过期</param>
    /// <returns>是否成功</returns>
    public Boolean Set(String tableName, String key, Byte[]? value, Int32 ttlSeconds = 0)
    {
        var store = GetStore(tableName);
        if (store == null) return false;

        var ttl = ttlSeconds > 0 ? TimeSpan.FromSeconds(ttlSeconds) : (TimeSpan?)null;
        store.Set(key, value, ttl);
        return true;
    }

    /// <summary>KV 获取值</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <returns>Base64 编码的值，不存在返回 null</returns>
    public String? Get(String tableName, String key)
    {
        var store = GetStore(tableName);
        if (store == null) return null;

        using var pk = store.Get(key);
        if (pk == null) return null;

        return Convert.ToBase64String(pk.ReadBytes());
    }

    /// <summary>KV 获取值（Packet 模式，避免 Base64 编码开销）</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <returns>二进制值，不存在返回 null</returns>
    public Byte[]? GetPacket(String tableName, String key)
    {
        var store = GetStore(tableName);
        if (store == null) return null;

        using var pk = store.Get(key);
        if (pk == null) return null;

        return pk.ReadBytes();
    }

    /// <summary>KV 删除键</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <returns>是否成功</returns>
    public Boolean Delete(String tableName, String key)
    {
        var store = GetStore(tableName);
        if (store == null) return false;

        return store.Delete(key);
    }

    /// <summary>KV 检查键是否存在</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <returns>是否存在</returns>
    public Boolean Exists(String tableName, String key)
    {
        var store = GetStore(tableName);
        if (store == null) return false;

        return store.Exists(key);
    }

    /// <summary>按通配符模式删除键</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="pattern">通配符模式（支持 * 和 ?）</param>
    /// <returns>删除的键个数</returns>
    public Int32 DeleteByPattern(String tableName, String pattern)
    {
        var store = GetStore(tableName);
        if (store == null) return 0;

        return store.DeleteByPattern(pattern);
    }

    /// <summary>获取缓存项总数</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <returns>总数</returns>
    public Int32 GetCount(String tableName)
    {
        var store = GetStore(tableName);
        if (store == null) return 0;

        return store.Count;
    }

    /// <summary>获取所有缓存键</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <returns>键列表</returns>
    public String[] GetAllKeys(String tableName)
    {
        var store = GetStore(tableName);
        if (store == null) return [];

        return store.GetAllKeys().ToArray();
    }

    /// <summary>清空所有缓存项</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    public void Clear(String tableName)
    {
        GetStore(tableName)?.Clear();
    }

    /// <summary>设置缓存项有效期</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <param name="ttlSeconds">过期时间（秒）</param>
    /// <returns>是否成功</returns>
    public Boolean SetExpire(String tableName, String key, Int32 ttlSeconds)
    {
        var store = GetStore(tableName);
        if (store == null) return false;

        return store.SetExpiration(key, TimeSpan.FromSeconds(ttlSeconds));
    }

    /// <summary>获取缓存项剩余有效期</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <returns>剩余 TTL（秒），-1 表示无过期或不存在</returns>
    public Double GetExpire(String tableName, String key)
    {
        var store = GetStore(tableName);
        if (store == null) return -1;

        return store.GetTtl(key).TotalSeconds;
    }

    /// <summary>原子递增（整数）</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <param name="value">变化量</param>
    /// <returns>更新后的值</returns>
    public Int64 Increment(String tableName, String key, Int64 value)
    {
        var store = GetStore(tableName);
        if (store == null) return 0;

        return store.Inc(key, value);
    }

    /// <summary>原子递增（浮点）</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="key">键</param>
    /// <param name="value">变化量</param>
    /// <returns>更新后的值</returns>
    public Double IncrementDouble(String tableName, String key, Double value)
    {
        var store = GetStore(tableName);
        if (store == null) return 0;

        return store.IncDouble(key, value);
    }

    /// <summary>搜索匹配的键</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="pattern">搜索模式</param>
    /// <param name="offset">偏移量</param>
    /// <param name="count">数量，-1 表示不限</param>
    /// <returns>匹配的键列表</returns>
    public String[] Search(String tableName, String pattern, Int32 offset = 0, Int32 count = -1)
    {
        var store = GetStore(tableName);
        if (store == null) return [];

        return store.Search(pattern, offset, count).ToArray();
    }

    /// <summary>批量获取键值对</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="keys">键数组</param>
    /// <returns>键到 Base64 编码值的字典</returns>
    public IDictionary<String, String?> GetAll(String tableName, String[] keys)
    {
        var store = GetStore(tableName);
        if (store == null) return new Dictionary<String, String?>();

        var result = new Dictionary<String, String?>();
        using var data = new CompositeDisposable();
        var raw = store.GetAll(keys);
        foreach (var key in keys)
        {
            if (raw.TryGetValue(key, out var pk) && pk != null)
            {
                data.Add(pk);
                result[key] = Convert.ToBase64String(pk.ReadBytes());
            }
            else
                result[key] = null;
        }
        return result;
    }

    /// <summary>批量设置键值对</summary>
    /// <param name="tableName">KV 表名，默认 "default"</param>
    /// <param name="values">键到二进制值的字典</param>
    /// <param name="ttlSeconds">过期时间（秒），0 表示永不过期</param>
    /// <returns>设置的键个数</returns>
    public Int32 SetAll(String tableName, IDictionary<String, Byte[]?> values, Int32 ttlSeconds = 0)
    {
        var store = GetStore(tableName);
        if (store == null) return 0;

        var ttl = ttlSeconds > 0 ? TimeSpan.FromSeconds(ttlSeconds) : (TimeSpan?)null;
        store.SetAll(values, ttl);
        return values.Count;
    }

    /// <summary>辅助类，用于批量释放 IOwnerPacket 资源</summary>
    private sealed class CompositeDisposable : IDisposable
    {
        private readonly List<IDisposable> _disposables = [];

        public void Add(IDisposable disposable) => _disposables.Add(disposable);

        public void Dispose()
        {
            foreach (var d in _disposables) d.Dispose();
        }
    }
}
