#if NET8_0_OR_GREATER
using System.Text.Json;
#endif
using NewLife.Data;
using NewLife.Reflection;
using NewLife.Serialization;

namespace NewLife.NovaDb.Caching;

/// <summary>NovaDb JSON 编码器，优先使用 System.Text.Json 序列化</summary>
/// <remarks>
/// 参考 RedisJsonEncoder 实现，继承 DefaultPacketEncoder。
/// 编码：基础类型 → ToString → UTF-8 字节；复杂类型 → JSON → UTF-8 字节。
/// 解码：UTF-8 字节 → 字符串 → 基础类型转换 / JSON 反序列化。
/// </remarks>
public class NovaJsonEncoder : DefaultPacketEncoder
{
    #region 属性
    private static IJsonHost _host;
    #endregion

    static NovaJsonEncoder() => _host = GetJsonHost();

    /// <summary>实例化 NovaDb 编码器</summary>
    public NovaJsonEncoder() => JsonHost = _host;

    internal static IJsonHost GetJsonHost()
    {
        // 尝试使用 System.Text.Json，不支持时使用 FastJson
        var host = JsonHelper.Default;
        if (host == null || host.GetType() == typeof(FastJson))
        {
#if NET8_0_OR_GREATER
            // 直接引用类型而非按名称反射查找：裁剪/NativeAOT 下字符串查找可能静默失败。
            // NativeAOT 默认关闭 System.Text.Json 反射序列化，此时保留带裁剪注解的 FastJson，
            // 由调用方以 DynamicallyAccessedMembers 保留被序列化类型的成员
            if (JsonSerializer.IsReflectionEnabledByDefault) host = new SystemJson();
#else
            // 当前组件输出 net45 和 netstandard2.0，SystemJson 要求 net5 以上，通过反射加载
            try
            {
                var type = $"{typeof(FastJson).Namespace}.SystemJson".GetTypeEx();
                if (type != null)
                {
                    host = type.CreateInstance() as IJsonHost;
                }
            }
            catch { }
#endif
        }

        return host ?? JsonHelper.Default;
    }
}
