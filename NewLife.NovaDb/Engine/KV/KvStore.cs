using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NewLife.Buffers;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Utilities;

namespace NewLife.NovaDb.Engine.KV;

/// <summary>KV 键值存储引擎。每个实例对应一个 .kvd 数据文件</summary>
/// <remarks>
/// <para>采用 Bitcask 模型：内存仅保存键到文件偏移的索引（KvEntry），值保留在磁盘按需读取。</para>
/// <para>每个索引项约 20 字节，百万级键仅占约 20MB 内存，不受值大小影响。</para>
/// <para>索引通过 ConcurrentDictionary 实现无锁并发查询，所有文件 IO 通过 _writeLock 串行化。</para>
/// <para>持久化采用顺序追加写入 + CRC32 校验，启动时通过 MemoryMappedFile 快速扫描重建索引。</para>
/// <para>支持 WAL 模式控制刷盘策略：Full(每次写入刷盘)、Normal(定时刷盘)、None(不刷盘)。</para>
/// </remarks>
public partial class KvStore : IDisposable
{
    #region 属性
    private static readonly Encoding _encoding = Encoding.UTF8;
    private readonly ConcurrentDictionary<String, KvEntry> _data = new(StringComparer.Ordinal);
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _writeLock = new();
#else
    private readonly Object _writeLock = new();
#endif
    private readonly DbOptions? _options;
    private readonly TimeSpan? _defaultTtl;
    private readonly String _filePath;
    private Boolean _disposed;

    /// <summary>非过期键的数量</summary>
    public Int32 Count
    {
        get
        {
            var count = 0;
            foreach (var index in _data.Values)
            {
                if (!index.IsExpired())
                    count++;
            }
            return count;
        }
    }

    /// <summary>数据文件路径</summary>
    public String FilePath => _filePath;
    #endregion

    #region 构造
    /// <summary>创建 KvStore 实例</summary>
    /// <param name="options">数据库选项，可为 null</param>
    /// <param name="filePath">数据文件路径（.kvd 文件）</param>
    public KvStore(DbOptions? options, String filePath)
    {
        if (String.IsNullOrEmpty(filePath)) throw new ArgumentException("文件路径不能为空", nameof(filePath));

        _options = options;
        _defaultTtl = options?.DefaultKvTtl;
        _filePath = filePath;

        OpenDataFile();
    }
    #endregion

    #region 释放
    /// <summary>检查是否已释放</summary>
    private void CheckDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(KvStore));
    }

    /// <summary>释放资源</summary>
    /// <param name="disposing">是否由 Dispose 调用</param>
    protected virtual void Dispose(Boolean disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            CloseDataFile();
        }
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    #endregion

    #region 基本操作
    /// <summary>设置键值对</summary>
    /// <param name="key">键</param>
    /// <param name="value">值</param>
    /// <param name="ttl">过期时间，null 表示使用默认 TTL（无默认则永不过期）</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(String key, Byte[]? value, TimeSpan? ttl = null)
        => Set(key, value == null ? ReadOnlySpan<Byte>.Empty : new ReadOnlySpan<Byte>(value), ttl);

    /// <summary>设置键值对</summary>
    /// <param name="key">键</param>
    /// <param name="value">值</param>
    /// <param name="ttl">过期时间，null 表示使用默认 TTL（无默认则永不过期）</param>
    public void Set(String key, ReadOnlySpan<Byte> value, TimeSpan? ttl = null)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));
        CheckDisposed();

        // 未显式指定 TTL 时使用默认 TTL；如果无默认 TTL 则永不过期
        var expiresAt = ttl != null ? DateTime.UtcNow.Add(ttl.Value) : _defaultTtl != null ? DateTime.UtcNow.Add(_defaultTtl.Value) : DateTime.MaxValue;

        lock (_writeLock)
        {
            var valueOffset = WriteSetRecordNoLock(key, value, expiresAt);
            _data[key] = new KvEntry
            {
                ValueOffset = valueOffset,
                ValueLength = value.Length,
                ExpiresAt = expiresAt,
            };

            TryAutoCompactNoLock();
        }
    }

    /// <summary>获取键对应的值（池化数据包），过期则惰性删除并返回 null</summary>
    /// <param name="key">键</param>
    /// <returns>池化数据包，不存在或已过期返回 null。调用方用完后需 Dispose 归还到池中</returns>
    public IOwnerPacket? Get(String key)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));
        CheckDisposed();

        lock (_writeLock)
        {
            if (!_data.TryGetValue(key, out var index))
                return null;

            // 惰性删除
            if (index.IsExpired())
            {
                _data.TryRemove(key, out _);
                return null;
            }

            // 从磁盘读取值
            return ReadValueFromDiskNoLock(index);
        }
    }

    /// <summary>尝试获取键对应的值（池化数据包）</summary>
    /// <param name="key">键</param>
    /// <param name="value">输出池化数据包，用完后需 Dispose 归还</param>
    /// <returns>键存在且未过期返回 true</returns>
    public Boolean TryGet(String key, out IOwnerPacket? value)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));
        CheckDisposed();

        lock (_writeLock)
        {
            if (!_data.TryGetValue(key, out var index))
            {
                value = null;
                return false;
            }

            // 惰性删除过期键
            if (index.IsExpired())
            {
                _data.TryRemove(key, out _);
                value = null;
                return false;
            }

            value = ReadValueFromDiskNoLock(index);
            return true;
        }
    }

    /// <summary>删除键</summary>
    /// <param name="key">键</param>
    /// <returns>键存在且未过期并被删除返回 true，否则返回 false</returns>
    public Boolean Delete(String key)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));
        CheckDisposed();

        lock (_writeLock)
        {
            if (!_data.TryGetValue(key, out var index))
                return false;

            // 无论是否过期，先从内存索引移除
            _data.TryRemove(key, out _);

            // 已过期视为不存在：不写 Delete 磁盘记录，返回 false
            if (index.IsExpired())
                return false;

            WriteDeleteRecordNoLock(key);
            TryAutoCompactNoLock();
            return true;
        }
    }

    /// <summary>检查键是否存在且未过期</summary>
    /// <param name="key">键</param>
    /// <returns>存在且未过期返回 true</returns>
    public Boolean Exists(String key)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));
        CheckDisposed();

        if (!_data.TryGetValue(key, out var index))
            return false;

        // 惰性删除
        if (index.IsExpired())
        {
            _data.TryRemove(key, out _);
            return false;
        }

        return true;
    }

    /// <summary>清空所有键值对</summary>
    public void Clear()
    {
        CheckDisposed();

        lock (_writeLock)
        {
            _data.Clear();
            WriteClearRecordNoLock();
            TryAutoCompactNoLock();
        }
    }
    #endregion

    #region 字符串便捷方法
    /// <summary>设置字符串键值对（UTF-8 编码）</summary>
    /// <param name="key">键</param>
    /// <param name="value">字符串值</param>
    /// <param name="ttl">过期时间，null 表示使用默认 TTL</param>
    public void SetString(String key, String value, TimeSpan? ttl = null)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));

        using var pooledUtf8Bytes = _encoding.GetPooledEncodedBytes(value);
        Set(key, pooledUtf8Bytes.AsSpan(), ttl);
    }

    /// <summary>获取字符串值（UTF-8 解码）</summary>
    /// <param name="key">键</param>
    /// <returns>字符串值，不存在或已过期返回 null</returns>
    public String? GetString(String key)
    {
        using var pk = Get(key);
        if (pk == null) return null;
        if (pk.Length == 0) return String.Empty;

        return pk.ToStr();
    }
    #endregion

    #region 高级操作
    /// <summary>仅当 key 不存在时添加，返回是否成功（分布式锁场景）</summary>
    /// <param name="key">键</param>
    /// <param name="value">值</param>
    /// <param name="ttl">过期时间</param>
    /// <returns>添加成功返回 true，key 已存在且未过期返回 false</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Boolean Add(String key, Byte[] value, TimeSpan ttl) => Add(key, new ReadOnlySpan<Byte>(value), ttl);

    /// <summary>仅当 key 不存在时添加，返回是否成功（分布式锁场景）</summary>
    /// <param name="key">键</param>
    /// <param name="value">值</param>
    /// <param name="ttl">过期时间</param>
    /// <returns>添加成功返回 true，key 已存在且未过期返回 false</returns>
    public Boolean Add(String key, ReadOnlySpan<Byte> value, TimeSpan ttl)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));
        CheckDisposed();

        lock (_writeLock)
        {
            if (_data.TryGetValue(key, out var index) && !index.IsExpired())
                return false;

            var expiresAt = DateTime.UtcNow.Add(ttl);
            var valueOffset = WriteSetRecordNoLock(key, value, expiresAt);
            _data[key] = new KvEntry
            {
                ValueOffset = valueOffset,
                ValueLength = value.Length,
                ExpiresAt = expiresAt,
            };

            TryAutoCompactNoLock();
            return true;
        }
    }

    /// <summary>仅当 key 不存在时添加字符串值</summary>
    /// <param name="key">键</param>
    /// <param name="value">字符串值</param>
    /// <param name="ttl">过期时间</param>
    /// <returns>添加成功返回 true，key 已存在返回 false</returns>
    public Boolean AddString(String key, String value, TimeSpan ttl)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));

        using var pooledUtf8Bytes = _encoding.GetPooledEncodedBytes(value);
        return Add(key, pooledUtf8Bytes.AsSpan(), ttl);
    }

    /// <summary>替换并返回旧值（原子操作）</summary>
    /// <param name="key">键</param>
    /// <param name="value">新值</param>
    /// <param name="ttl">过期时间，null 表示保持原有 TTL</param>
    /// <returns>旧值池化数据包，不存在返回 null。调用方用完后需 Dispose</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IOwnerPacket? Replace(String key, Byte[]? value, TimeSpan? ttl = null)
        => Replace(key, value == null ? ReadOnlySpan<Byte>.Empty : new ReadOnlySpan<Byte>(value), ttl);

    /// <summary>替换并返回旧值（原子操作）</summary>
    /// <param name="key">键</param>
    /// <param name="value">新值</param>
    /// <param name="ttl">过期时间，null 表示保持原有 TTL</param>
    /// <returns>旧值池化数据包，不存在返回 null。调用方用完后需 Dispose</returns>
    public IOwnerPacket? Replace(String key, ReadOnlySpan<Byte> value, TimeSpan? ttl = null)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));
        CheckDisposed();

        lock (_writeLock)
        {
            IOwnerPacket? oldValue = null;
            DateTime expiresAt;

            if (_data.TryGetValue(key, out var index))
            {
                oldValue = index.IsExpired() ? null : ReadValueFromDiskNoLock(index);
                expiresAt = ttl.HasValue ? DateTime.UtcNow.Add(ttl.Value) : index.ExpiresAt;
            }
            else
            {
                expiresAt = ttl != null ? DateTime.UtcNow.Add(ttl.Value) : _defaultTtl != null ? DateTime.UtcNow.Add(_defaultTtl.Value) : DateTime.MaxValue;
            }

            var valueOffset = WriteSetRecordNoLock(key, value, expiresAt);
            _data[key] = new KvEntry
            {
                ValueOffset = valueOffset,
                ValueLength = value.Length,
                ExpiresAt = expiresAt,
            };

            TryAutoCompactNoLock();
            return oldValue;
        }
    }

    /// <summary>原子递增操作（Int64），key 不存在时初始化为 delta</summary>
    /// <param name="key">键</param>
    /// <param name="delta">递增量</param>
    /// <param name="ttl">过期时间（可选，仅在 key 不存在时使用默认 TTL）</param>
    /// <returns>递增后的值</returns>
    public Int64 Inc(String key, Int64 delta = 1, TimeSpan? ttl = null)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));
        CheckDisposed();

        lock (_writeLock)
        {
            Int64 newValue;
            DateTime expiresAt;

            if (_data.TryGetValue(key, out var index) && !index.IsExpired())
            {
                // 从磁盘读取当前值并递增（二进制 Int64）
                using var currentPk = ReadValueFromDiskNoLock(index);
                var current = (currentPk != null && currentPk.Length >= 8) ? new SpanReader(currentPk).ReadInt64() : 0L;
                newValue = current + delta;
                expiresAt = index.ExpiresAt;
            }
            else
            {
                // key 不存在或已过期，初始化为 delta
                newValue = delta;
                expiresAt = ttl != null ? DateTime.UtcNow.Add(ttl.Value) : _defaultTtl != null ? DateTime.UtcNow.Add(_defaultTtl.Value) : DateTime.MaxValue;
            }

            const Int32 length = sizeof(Int64);
            Span<Byte> valueBytes = stackalloc Byte[length];
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(valueBytes), newValue);

            var valueOffset = WriteSetRecordNoLock(key, valueBytes, expiresAt);
            _data[key] = new KvEntry
            {
                ValueOffset = valueOffset,
                ValueLength = valueBytes.Length,
                ExpiresAt = expiresAt,
            };

            TryAutoCompactNoLock();
            return newValue;
        }
    }

    /// <summary>原子递增操作（Double），key 不存在时初始化为 delta</summary>
    /// <param name="key">键</param>
    /// <param name="delta">递增量</param>
    /// <param name="ttl">过期时间（可选）</param>
    /// <returns>递增后的值</returns>
    public Double IncDouble(String key, Double delta, TimeSpan? ttl = null)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));
        CheckDisposed();

        lock (_writeLock)
        {
            Double newValue;
            DateTime expiresAt;

            if (_data.TryGetValue(key, out var index) && !index.IsExpired())
            {
                // 从磁盘读取当前值并递增（二进制 Double）
                using var currentPk = ReadValueFromDiskNoLock(index);
                var current = (currentPk != null && currentPk.Length >= 8) ? new SpanReader(currentPk).ReadDouble() : 0d;
                newValue = current + delta;
                expiresAt = index.ExpiresAt;
            }
            else
            {
                newValue = delta;
                expiresAt = ttl != null ? DateTime.UtcNow.Add(ttl.Value) : _defaultTtl != null ? DateTime.UtcNow.Add(_defaultTtl.Value) : DateTime.MaxValue;
            }

            const Int32 length = sizeof(Double);
            Span<Byte> valueBytes = stackalloc Byte[length];
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(valueBytes), newValue);

            var valueOffset = WriteSetRecordNoLock(key, valueBytes, expiresAt);
            _data[key] = new KvEntry
            {
                ValueOffset = valueOffset,
                ValueLength = valueBytes.Length,
                ExpiresAt = expiresAt,
            };

            TryAutoCompactNoLock();
            return newValue;
        }
    }
    #endregion

    #region 批量操作
    /// <summary>批量获取键值对（池化数据包）</summary>
    /// <param name="keys">键集合</param>
    /// <returns>键值对字典，仅包含存在且未过期的键。每个 IOwnerPacket 用完后需 Dispose</returns>
    public IDictionary<String, IOwnerPacket?> GetAll(IEnumerable<String> keys)
    {
        if (keys == null) throw new ArgumentNullException(nameof(keys));
        CheckDisposed();

        var result = new Dictionary<String, IOwnerPacket?>();
        lock (_writeLock)
        {
            foreach (var key in keys)
            {
                if (String.IsNullOrEmpty(key)) continue;

                if (_data.TryGetValue(key, out var index))
                {
                    if (index.IsExpired())
                        _data.TryRemove(key, out _);
                    else
                        result[key] = ReadValueFromDiskNoLock(index);
                }
            }
        }
        return result;
    }

    /// <summary>批量设置键值对</summary>
    /// <param name="values">键值对字典</param>
    /// <param name="ttl">过期时间，null 表示使用默认 TTL</param>
    public void SetAll(IDictionary<String, Byte[]?> values, TimeSpan? ttl = null)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        CheckDisposed();

        var expiresAt = ttl != null ? DateTime.UtcNow.Add(ttl.Value) : _defaultTtl != null ? DateTime.UtcNow.Add(_defaultTtl.Value) : DateTime.MaxValue;

        lock (_writeLock)
        {
            foreach (var kvp in values)
            {
                if (String.IsNullOrEmpty(kvp.Key)) continue;

                var value = kvp.Value == null ? ReadOnlySpan<Byte>.Empty : new ReadOnlySpan<Byte>(kvp.Value);
                var valueOffset = WriteSetRecordNoLock(kvp.Key, value, expiresAt);
                _data[kvp.Key] = new KvEntry
                {
                    ValueOffset = valueOffset,
                    ValueLength = value.Length,
                    ExpiresAt = expiresAt,
                };
            }

            TryAutoCompactNoLock();
        }
    }

    /// <summary>批量删除键</summary>
    /// <param name="keys">键集合</param>
    /// <returns>删除的键数量</returns>
    public Int32 Delete(IEnumerable<String> keys)
    {
        if (keys == null) return 0;
        CheckDisposed();

        var count = 0;
        lock (_writeLock)
        {
            foreach (var key in keys)
            {
                if (String.IsNullOrEmpty(key)) continue;
                if (_data.TryRemove(key, out _))
                {
                    WriteDeleteRecordNoLock(key);
                    count++;
                }
            }

            TryAutoCompactNoLock();
        }

        return count;
    }
    #endregion

    #region TTL 管理
    /// <summary>获取键的过期时间</summary>
    /// <param name="key">键</param>
    /// <returns>过期时间。键不存在、已过期或永不过期返回 null</returns>
    public DateTime? GetExpiration(String key)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));
        CheckDisposed();

        if (!_data.TryGetValue(key, out var index))
            return null;

        // 已过期视为不存在（惰性删除）
        if (index.IsExpired())
        {
            _data.TryRemove(key, out _);
            return null;
        }

        // 永不过期视为无 TTL
        if (index.ExpiresAt == DateTime.MaxValue) return null;

        return index.ExpiresAt;
    }

    /// <summary>更新键的过期时间</summary>
    /// <param name="key">键</param>
    /// <param name="ttl">新的 TTL。TimeSpan.Zero 表示永不过期</param>
    /// <returns>键存在并更新成功返回 true</returns>
    public Boolean SetExpiration(String key, TimeSpan ttl)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));
        CheckDisposed();

        lock (_writeLock)
        {
            if (!_data.TryGetValue(key, out var index))
                return false;

            if (index.IsExpired())
            {
                _data.TryRemove(key, out _);
                return false;
            }

            var newExpiresAt = ttl == TimeSpan.Zero ? DateTime.MaxValue : DateTime.UtcNow.Add(ttl);

            // 从磁盘读取当前值，以新 TTL 重新追加写入
            using var valuePk = ReadValueFromDiskNoLock(index);
            var value = valuePk == null ? ReadOnlySpan<Byte>.Empty : new ReadOnlySpan<Byte>(valuePk.ReadBytes());
            var valueOffset = WriteSetRecordNoLock(key, value, newExpiresAt);
            _data[key] = new KvEntry
            {
                ValueOffset = valueOffset,
                ValueLength = value.Length,
                ExpiresAt = newExpiresAt,
            };

            TryAutoCompactNoLock();
            return true;
        }
    }

    /// <summary>获取键的剩余存活时间</summary>
    /// <param name="key">键</param>
    /// <returns>剩余 TTL。永不过期返回 TimeSpan.Zero；不存在或已过期返回负值</returns>
    public TimeSpan GetTtl(String key)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));
        CheckDisposed();

        if (!_data.TryGetValue(key, out var index))
            return TimeSpan.FromSeconds(-1);

        if (index.IsExpired())
        {
            _data.TryRemove(key, out _);
            return TimeSpan.FromSeconds(-1);
        }

        if (index.ExpiresAt == DateTime.MaxValue)
            return TimeSpan.Zero;

        return index.ExpiresAt - DateTime.UtcNow;
    }

    /// <summary>清理所有已过期的条目</summary>
    /// <returns>删除的条目数量</returns>
    public Int32 CleanupExpired()
    {
        CheckDisposed();

        var count = 0;
        foreach (var kvp in _data)
        {
            if (kvp.Value.IsExpired())
            {
                if (_data.TryRemove(kvp.Key, out _))
                    count++;
            }
        }

        return count;
    }
    #endregion

    #region 搜索与枚举
    /// <summary>获取所有未过期的键</summary>
    /// <returns>未过期键序列</returns>
    public IEnumerable<String> GetAllKeys()
    {
        CheckDisposed();

        foreach (var kvp in _data)
        {
            if (!kvp.Value.IsExpired())
                yield return kvp.Key;
        }
    }

    /// <summary>按模式搜索未过期的键</summary>
    /// <param name="pattern">搜索模式，支持 * 和 ? 通配符</param>
    /// <param name="offset">起始偏移量</param>
    /// <param name="count">返回数量，-1 表示全部</param>
    /// <returns>匹配的键序列</returns>
    public IEnumerable<String> Search(String pattern, Int32 offset = 0, Int32 count = -1)
    {
        CheckDisposed();

        var current = 0;
        var returned = 0;
        foreach (var kvp in _data)
        {
            if (kvp.Value.IsExpired()) continue;

            if (pattern.IsNullOrEmpty() || pattern == kvp.Key || pattern.IsMatch(kvp.Key))
            {
                // 跳过前 offset 个
                if (current < offset)
                {
                    current++;
                    continue;
                }

                // 达到 count 限制则停止
                if (count >= 0 && returned >= count)
                    break;

                yield return kvp.Key;
                returned++;
            }
        }
    }

    /// <summary>按模式删除键</summary>
    /// <param name="pattern">搜索模式，支持 * 和 ? 通配符</param>
    /// <returns>删除的键数量</returns>
    public Int32 DeleteByPattern(String pattern)
    {
        CheckDisposed();

        var toRemove = new List<String>();
        foreach (var kvp in _data)
        {
            if (pattern.IsMatch(kvp.Key))
                toRemove.Add(kvp.Key);
        }

        lock (_writeLock)
        {
            var count = 0;
            foreach (var key in toRemove)
            {
                if (_data.TryRemove(key, out _))
                {
                    WriteDeleteRecordNoLock(key);
                    count++;
                }
            }

            TryAutoCompactNoLock();
            return count;
        }
    }
    #endregion
}
