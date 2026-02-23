using NewLife.NovaDb.Engine.KV;
using NewLife.Remoting;

namespace NewLife.NovaDb.Server;

/// <summary>KV 存储 RPC 控制器，提供键值对操作接口</summary>
/// <remarks>
/// 控制器方法通过 Remoting RPC 暴露为远程接口。
/// 路由格式：Kv/{方法名}，如 Kv/Set、Kv/Get。
/// 控制器实例由 Remoting 框架按请求创建，通过静态字段共享 KV 存储引擎。
/// </remarks>
internal class KvController : IApi
{
    /// <summary>会话</summary>
    public IApiSession Session { get; set; } = null!;

    /// <summary>共享 KV 存储引擎，由 NovaServer 启动时设置</summary>
    internal static KvStore? SharedKvStore { get; set; }

    /// <summary>KV 设置键值对</summary>
    /// <param name="key">键</param>
    /// <param name="value">字符串值</param>
    /// <param name="ttlSeconds">过期时间（秒），0 表示永不过期</param>
    /// <returns>是否成功</returns>
    public Boolean Set(String key, String value, Int32 ttlSeconds = 0)
    {
        if (SharedKvStore == null) return false;

        var ttl = ttlSeconds > 0 ? TimeSpan.FromSeconds(ttlSeconds) : (TimeSpan?)null;
        SharedKvStore.SetString(key, value, ttl);
        return true;
    }

    /// <summary>KV 获取值</summary>
    /// <param name="key">键</param>
    /// <returns>字符串值，不存在返回 null</returns>
    public String? Get(String key)
    {
        if (SharedKvStore == null) return null;

        return SharedKvStore.GetString(key);
    }

    /// <summary>KV 删除键</summary>
    /// <param name="key">键</param>
    /// <returns>是否成功</returns>
    public Boolean Delete(String key)
    {
        if (SharedKvStore == null) return false;

        return SharedKvStore.Delete(key);
    }

    /// <summary>KV 检查键是否存在</summary>
    /// <param name="key">键</param>
    /// <returns>是否存在</returns>
    public Boolean Exists(String key)
    {
        if (SharedKvStore == null) return false;

        return SharedKvStore.Exists(key);
    }
}
