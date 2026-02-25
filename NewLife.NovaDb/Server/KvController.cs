using NewLife.Data;
using NewLife.NovaDb.Engine.KV;
using NewLife.Remoting;

namespace NewLife.NovaDb.Server;

/// <summary>KV 存储 RPC 控制器，提供键值对操作接口</summary>
/// <remarks>
/// 控制器方法通过 Remoting RPC 暴露为远程接口。
/// 路由格式：Kv/{方法名}，如 Kv/Set、Kv/Get。
/// 所有方法入参均为 IPacket（Remoting 检测到 IsPacketParameter=True 后直接传原始消息体），
/// 通过 SpanReader 直接读取二进制参数，跳过 JSON 序列化。
/// 返回值也为 IPacket（IsPacketReturn=True），通过 SpanWriter 写入 ArrayPacket，
/// 或直接返回 KvStore 的 IOwnerPacket（网络层发送后自动释放回池）。
/// 控制器实例由 Remoting 框架按请求创建，通过静态字段共享引擎。
/// 二进制协议说明：
///   EncodedString = EncodedInt(UTF8字节数) + UTF8字节
///   EncodedInt    = 变长整数（1-5 字节，最高位 1 表示后续还有字节）
///   NullableBytes = EncodedInt(-1)=null / EncodedInt(0)=空 / EncodedInt(n)+n字节
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
    /// <param name="data">
    /// 请求包：
    ///   [EncodedString tableName] 表名
    ///   [EncodedString key]       键
    ///   [NullableBytes value]     值（null 或任意字节）
    ///   [Int32 ttlSeconds]        过期秒数，0=永不过期（4字节小端）
    /// </param>
    /// <returns>响应包：[1B: 0=失败, 1=成功]</returns>
    public IPacket Set(IPacket data)
    {
        var (tableName, key, value, ttlSeconds) = KvPacket.DecodeSet(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeBoolean(false);

        var ttl = ttlSeconds > 0 ? TimeSpan.FromSeconds(ttlSeconds) : (TimeSpan?)null;
        store.Set(key, value, ttl);
        return KvPacket.EncodeBoolean(true);
    }

    /// <summary>KV 获取值（跳过 Base64 与 JSON 开销，直接返回原始字节包）</summary>
    /// <param name="data">
    /// 请求包：
    ///   [EncodedString tableName] 表名
    ///   [EncodedString key]       键
    /// </param>
    /// <returns>
    /// 响应包：
    ///   键存在时返回存储的原始字节（ArrayPacket）；
    ///   键不存在或存储未初始化时返回空包（Length=0）
    /// </returns>
    public IPacket Get(IPacket data)
    {
        var (tableName, key) = KvPacket.DecodeTableKey(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeEmpty();

        using var pk = store.Get(key);
        if (pk == null) return KvPacket.EncodeEmpty();

        // GetSpan() 直接读取底层缓冲区，避免 ReadBytes() 分配中间字节数组，再包装 IPacket 的双重分配
        return new ArrayPacket(pk.GetSpan().ToArray());
    }

    /// <summary>KV 删除键</summary>
    /// <param name="data">
    /// 请求包：
    ///   [EncodedString tableName] 表名
    ///   [EncodedString key]       键
    /// </param>
    /// <returns>响应包：[1B: 0=键不存在/失败, 1=删除成功]</returns>
    public IPacket Delete(IPacket data)
    {
        var (tableName, key) = KvPacket.DecodeTableKey(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeBoolean(false);

        return KvPacket.EncodeBoolean(store.Delete(key));
    }

    /// <summary>KV 检查键是否存在</summary>
    /// <param name="data">
    /// 请求包：
    ///   [EncodedString tableName] 表名
    ///   [EncodedString key]       键
    /// </param>
    /// <returns>响应包：[1B: 0=不存在, 1=存在]</returns>
    public IPacket Exists(IPacket data)
    {
        var (tableName, key) = KvPacket.DecodeTableKey(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeBoolean(false);

        return KvPacket.EncodeBoolean(store.Exists(key));
    }

    /// <summary>按通配符模式删除键</summary>
    /// <param name="data">
    /// 请求包：
    ///   [EncodedString tableName] 表名
    ///   [EncodedString pattern]   通配符模式（* 匹配任意字符，? 匹配单个字符）
    /// </param>
    /// <returns>响应包：[Int32 删除数量]（4字节小端）</returns>
    public IPacket DeleteByPattern(IPacket data)
    {
        var (tableName, pattern) = KvPacket.DecodeDeleteByPattern(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeInt32(0);

        return KvPacket.EncodeInt32(store.DeleteByPattern(pattern));
    }

    /// <summary>获取缓存项总数</summary>
    /// <param name="data">
    /// 请求包：
    ///   [EncodedString tableName] 表名
    /// </param>
    /// <returns>响应包：[Int32 总数]（4字节小端）</returns>
    public IPacket GetCount(IPacket data)
    {
        var tableName = KvPacket.DecodeTableOnly(data);
        var store = GetStore(tableName);
        return KvPacket.EncodeInt32(store?.Count ?? 0);
    }

    /// <summary>获取所有缓存键</summary>
    /// <param name="data">
    /// 请求包：
    ///   [EncodedString tableName] 表名
    /// </param>
    /// <returns>
    /// 响应包：
    ///   [Int32 count]              键数量（4字节小端）
    ///   [EncodedString key1] ...   各键字符串（count 个）
    /// </returns>
    public IPacket GetAllKeys(IPacket data)
    {
        var tableName = KvPacket.DecodeTableOnly(data);
        var store = GetStore(tableName);
        return KvPacket.EncodeStringArray(store?.GetAllKeys().ToArray() ?? []);
    }

    /// <summary>清空所有缓存项</summary>
    /// <param name="data">
    /// 请求包：
    ///   [EncodedString tableName] 表名
    /// </param>
    /// <returns>空响应包（Length=0）</returns>
    public IPacket Clear(IPacket data)
    {
        var tableName = KvPacket.DecodeTableOnly(data);
        GetStore(tableName)?.Clear();
        return KvPacket.EncodeEmpty();
    }

    /// <summary>设置缓存项有效期</summary>
    /// <param name="data">
    /// 请求包：
    ///   [EncodedString tableName] 表名
    ///   [EncodedString key]       键
    ///   [Int32 ttlSeconds]        新的过期秒数（4字节小端）
    /// </param>
    /// <returns>响应包：[1B: 0=键不存在/失败, 1=设置成功]</returns>
    public IPacket SetExpire(IPacket data)
    {
        var (tableName, key, ttlSeconds) = KvPacket.DecodeSetExpire(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeBoolean(false);

        return KvPacket.EncodeBoolean(store.SetExpiration(key, TimeSpan.FromSeconds(ttlSeconds)));
    }

    /// <summary>获取缓存项剩余有效期</summary>
    /// <param name="data">
    /// 请求包：
    ///   [EncodedString tableName] 表名
    ///   [EncodedString key]       键
    /// </param>
    /// <returns>
    /// 响应包：[Double TTL秒数]（8字节小端）
    ///   正数=剩余秒数，负数=键不存在或永不过期
    /// </returns>
    public IPacket GetExpire(IPacket data)
    {
        var (tableName, key) = KvPacket.DecodeTableKey(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeDouble(-1);

        return KvPacket.EncodeDouble(store.GetTtl(key).TotalSeconds);
    }

    /// <summary>原子递增（整数）</summary>
    /// <param name="data">
    /// 请求包：
    ///   [EncodedString tableName] 表名
    ///   [EncodedString key]       键
    ///   [Int64 delta]             变化量（8字节小端，可为负数）
    /// </param>
    /// <returns>响应包：[Int64 更新后的值]（8字节小端）</returns>
    public IPacket Increment(IPacket data)
    {
        var (tableName, key, delta) = KvPacket.DecodeIncrement(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeInt64(0);

        return KvPacket.EncodeInt64(store.Inc(key, delta));
    }

    /// <summary>原子递增（浮点）</summary>
    /// <param name="data">
    /// 请求包：
    ///   [EncodedString tableName] 表名
    ///   [EncodedString key]       键
    ///   [Double delta]            变化量（8字节小端，可为负数）
    /// </param>
    /// <returns>响应包：[Double 更新后的值]（8字节小端）</returns>
    public IPacket IncrementDouble(IPacket data)
    {
        var (tableName, key, delta) = KvPacket.DecodeIncrementDouble(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeDouble(0);

        return KvPacket.EncodeDouble(store.IncDouble(key, delta));
    }

    /// <summary>搜索匹配的键</summary>
    /// <param name="data">
    /// 请求包：
    ///   [EncodedString tableName] 表名
    ///   [EncodedString pattern]   搜索模式
    ///   [Int32 offset]            偏移量（4字节小端）
    ///   [Int32 count]             最大返回数量，-1=不限（4字节小端）
    /// </param>
    /// <returns>
    /// 响应包：
    ///   [Int32 count]              匹配键数量（4字节小端）
    ///   [EncodedString key1] ...   各键字符串（count 个）
    /// </returns>
    public IPacket Search(IPacket data)
    {
        var (tableName, pattern, offset, count) = KvPacket.DecodeSearch(data);
        var store = GetStore(tableName);
        if (store == null) return KvPacket.EncodeStringArray([]);

        return KvPacket.EncodeStringArray(store.Search(pattern, offset, count).ToArray());
    }

    /// <summary>批量获取键值对（跳过 Base64 与 JSON 开销）</summary>
    /// <param name="data">
    /// 请求包：
    ///   [EncodedString tableName] 表名
    ///   [Int32 keyCount]          键数量（4字节小端）
    ///   [EncodedString key1] ...  各键字符串（keyCount 个）
    /// </param>
    /// <returns>
    /// 响应包：
    ///   [Int32 count]             键数量（4字节小端，与请求一致）
    ///   对每个键，依次写入：
    ///     [EncodedString key]     键字符串
    ///     [1B flag]               0=键不存在/null, 1=有值
    ///     [if flag=1: EncodedInt(valueLen)][valueLen 字节 value]
    /// </returns>
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

    /// <summary>批量设置键值对</summary>
    /// <param name="data">
    /// 请求包：
    ///   [EncodedString tableName] 表名
    ///   [Int32 ttlSeconds]        过期秒数，0=永不过期（4字节小端）
    ///   [Int32 count]             键值对数量（4字节小端）
    ///   对每个键值对，依次写入：
    ///     [EncodedString key]     键
    ///     [NullableBytes value]   值（null 或任意字节）
    /// </param>
    /// <returns>响应包：[Int32 成功设置的键个数]（4字节小端）</returns>
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
