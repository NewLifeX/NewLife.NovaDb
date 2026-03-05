using System.Runtime.CompilerServices;

namespace NewLife.NovaDb.Engine.KV;

/// <summary>KV 内存索引项。Bitcask 模型仅索引驻留内存，值保留在磁盘按需读取</summary>
/// <remarks>
/// <para>每个索引项仅约 20 字节（Int64 + Int32 + DateTime），百万级键仅占约 20MB 内存。</para>
/// <para>值数据保留在 .kvd 数据文件中，通过 ValueOffset 和 ValueLength 按需读取。</para>
/// </remarks>
public struct KvEntry
{
    /// <summary>值数据在文件中的起始偏移。值为 null 时此字段为 -1</summary>
    public Int64 ValueOffset;

    /// <summary>值的字节长度。-1 表示值为 null</summary>
    public Int32 ValueLength;

    /// <summary>过期时间（UTC）。DateTime.MaxValue 表示永不过期</summary>
    public DateTime ExpiresAt;

    /// <summary>检查是否已过期</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Boolean IsExpired() => ExpiresAt < DateTime.MaxValue && DateTime.UtcNow >= ExpiresAt;
}
