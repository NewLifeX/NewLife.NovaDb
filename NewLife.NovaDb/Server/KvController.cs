using NewLife.Data;
using NewLife.NovaDb.Engine.KV;
using NewLife.Remoting;

namespace NewLife.NovaDb.Server;

/// <summary>KV 存储 RPC 控制器，提供键值对操作接口</summary>
/// <remarks>
/// 控制器方法通过 Remoting RPC 暴露为远程接口。
/// 路由格式：Kv/{方法名}，如 Kv/Set、Kv/Get。
/// 所有方法入参均为 IPacket，通过 SpanReader 直接读取二进制参数，跳过 JSON 序列化。
/// 返回值也为 IPacket，通过 SpanWriter 写入 ArrayPacket，跳过 JSON 序列化。
/// Remoting 框架检测到 IsPacketParameter/IsPacketReturn 后自动走原生二进制通道。
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

    /// <summary>KV 设置键值对（IPacket 二进制协议）</summary>
    /// <param name="data">请求包：[tableName][key][value(nullable)][ttlSeconds]</param>
    /// <returns>响应包：[1B: 0=false, 1=true]</returns>
    public IPacket Set(IPacket data)
    {
        var (tableName, key, value, ttlSeconds) = KvPacket.DecodeSet(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeBoolean(false);

        var ttl = ttlSeconds > 0 ? TimeSpan.FromSeconds(ttlSeconds) : (TimeSpan?)null;
        store.Set(key, value, ttl);
        return KvPacket.EncodeBoolean(true);
    }

    /// <summary>KV 获取值（IPacket 二进制协议，跳过 Base64 与 JSON 开销）</summary>
    /// <param name="data">请求包：[tableName][key]</param>
    /// <returns>响应包：[1B 0=notfound] 或 [1B 1=found][value bytes]</returns>
    public IPacket Get(IPacket data)
    {
        var (tableName, key) = KvPacket.DecodeTableKey(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeNullableValue(null);

        using var pk = store.Get(key);
        return KvPacket.EncodeNullableValue(pk?.ReadBytes());
    }

    /// <summary>KV 删除键（IPacket 二进制协议）</summary>
    /// <param name="data">请求包：[tableName][key]</param>
    /// <returns>响应包：[1B: 0=false, 1=true]</returns>
    public IPacket Delete(IPacket data)
    {
        var (tableName, key) = KvPacket.DecodeTableKey(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeBoolean(false);

        return KvPacket.EncodeBoolean(store.Delete(key));
    }

    /// <summary>KV 检查键是否存在（IPacket 二进制协议）</summary>
    /// <param name="data">请求包：[tableName][key]</param>
    /// <returns>响应包：[1B: 0=false, 1=true]</returns>
    public IPacket Exists(IPacket data)
    {
        var (tableName, key) = KvPacket.DecodeTableKey(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeBoolean(false);

        return KvPacket.EncodeBoolean(store.Exists(key));
    }

    /// <summary>按通配符模式删除键（IPacket 二进制协议）</summary>
    /// <param name="data">请求包：[tableName][pattern]</param>
    /// <returns>响应包：[4B Int32: 删除数量]</returns>
    public IPacket DeleteByPattern(IPacket data)
    {
        var (tableName, pattern) = KvPacket.DecodeDeleteByPattern(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeInt32(0);

        return KvPacket.EncodeInt32(store.DeleteByPattern(pattern));
    }

    /// <summary>获取缓存项总数（IPacket 二进制协议）</summary>
    /// <param name="data">请求包：[tableName]</param>
    /// <returns>响应包：[4B Int32: 总数]</returns>
    public IPacket GetCount(IPacket data)
    {
        var tableName = KvPacket.DecodeTableOnly(data);
        var store = GetStore(tableName);
        return KvPacket.EncodeInt32(store?.Count ?? 0);
    }

    /// <summary>获取所有缓存键（IPacket 二进制协议）</summary>
    /// <param name="data">请求包：[tableName]</param>
    /// <returns>响应包：[4B count][key1...][keyN]</returns>
    public IPacket GetAllKeys(IPacket data)
    {
        var tableName = KvPacket.DecodeTableOnly(data);
        var store = GetStore(tableName);
        return KvPacket.EncodeStringArray(store?.GetAllKeys().ToArray() ?? []);
    }

    /// <summary>清空所有缓存项（IPacket 二进制协议）</summary>
    /// <param name="data">请求包：[tableName]</param>
    /// <returns>空响应包</returns>
    public IPacket Clear(IPacket data)
    {
        var tableName = KvPacket.DecodeTableOnly(data);
        GetStore(tableName)?.Clear();
        return KvPacket.EncodeEmpty();
    }

    /// <summary>设置缓存项有效期（IPacket 二进制协议）</summary>
    /// <param name="data">请求包：[tableName][key][ttlSeconds]</param>
    /// <returns>响应包：[1B: 0=false, 1=true]</returns>
    public IPacket SetExpire(IPacket data)
    {
        var (tableName, key, ttlSeconds) = KvPacket.DecodeSetExpire(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeBoolean(false);

        return KvPacket.EncodeBoolean(store.SetExpiration(key, TimeSpan.FromSeconds(ttlSeconds)));
    }

    /// <summary>获取缓存项剩余有效期（IPacket 二进制协议）</summary>
    /// <param name="data">请求包：[tableName][key]</param>
    /// <returns>响应包：[8B Double: TTL 秒数]</returns>
    public IPacket GetExpire(IPacket data)
    {
        var (tableName, key) = KvPacket.DecodeTableKey(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeDouble(-1);

        return KvPacket.EncodeDouble(store.GetTtl(key).TotalSeconds);
    }

    /// <summary>原子递增（整数，IPacket 二进制协议）</summary>
    /// <param name="data">请求包：[tableName][key][Int64 delta]</param>
    /// <returns>响应包：[8B Int64: 更新后的值]</returns>
    public IPacket Increment(IPacket data)
    {
        var (tableName, key, delta) = KvPacket.DecodeIncrement(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeInt64(0);

        return KvPacket.EncodeInt64(store.Inc(key, delta));
    }

    /// <summary>原子递增（浮点，IPacket 二进制协议）</summary>
    /// <param name="data">请求包：[tableName][key][Double delta]</param>
    /// <returns>响应包：[8B Double: 更新后的值]</returns>
    public IPacket IncrementDouble(IPacket data)
    {
        var (tableName, key, delta) = KvPacket.DecodeIncrementDouble(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeDouble(0);

        return KvPacket.EncodeDouble(store.IncDouble(key, delta));
    }

    /// <summary>搜索匹配的键（IPacket 二进制协议）</summary>
    /// <param name="data">请求包：[tableName][pattern][offset][count]</param>
    /// <returns>响应包：[4B count][key1...][keyN]</returns>
    public IPacket Search(IPacket data)
    {
        var (tableName, pattern, offset, count) = KvPacket.DecodeSearch(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeStringArray([]);

        return KvPacket.EncodeStringArray(store.Search(pattern, offset, count).ToArray());
    }

    /// <summary>批量获取键值对（IPacket 二进制协议，跳过 Base64 与 JSON 开销）</summary>
    /// <param name="data">请求包：[tableName][keyCount][key1...][keyN]</param>
    /// <returns>响应包：[count][key1][value1Flag][value1Len][value1]...[keyN][valueNFlag]</returns>
    public IPacket GetAll(IPacket data)
    {
        var (tableName, keys) = KvPacket.DecodeGetAll(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeGetAllResponse([], new Dictionary<String, IOwnerPacket?>());

        var raw = store.GetAll(keys);
        try
        {
            return KvPacket.EncodeGetAllResponse(keys, raw);
        }
        finally
        {
            foreach (var pk in raw.Values) pk?.Dispose();
        }
    }

    /// <summary>批量设置键值对（IPacket 二进制协议）</summary>
    /// <param name="data">请求包：[tableName][ttlSeconds][count][key1][value1]...[keyN][valueN]</param>
    /// <returns>响应包：[4B Int32: 设置的键个数]</returns>
    public IPacket SetAll(IPacket data)
    {
        var (tableName, values, ttlSeconds) = KvPacket.DecodeSetAll(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeInt32(0);

        var ttl = ttlSeconds > 0 ? TimeSpan.FromSeconds(ttlSeconds) : (TimeSpan?)null;
        store.SetAll(values, ttl);
        return KvPacket.EncodeInt32(values.Count);
    }
}
