using System.Text;
using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Engine.KV;

/// <summary>KV 键值存储引擎，支持按行 TTL</summary>
public class KvStore
{
    private readonly Dictionary<String, KvEntry> _data = new(StringComparer.Ordinal);
    private readonly Object _lock = new();
    private readonly DbOptions? _options;
    private readonly TimeSpan _defaultTtl;

    /// <summary>非过期键的数量</summary>
    public Int32 Count
    {
        get
        {
            lock (_lock)
            {
                var count = 0;
                foreach (var entry in _data.Values)
                {
                    if (!entry.IsExpired())
                        count++;
                }
                return count;
            }
        }
    }

    /// <summary>创建 KvStore 实例</summary>
    /// <param name="options">数据库选项，可为 null</param>
    public KvStore(DbOptions? options = null)
    {
        _options = options;
        _defaultTtl = options?.DefaultKvTtl ?? TimeSpan.Zero;
    }

    /// <summary>设置键值对</summary>
    /// <param name="key">键</param>
    /// <param name="value">值</param>
    /// <param name="ttl">过期时间，null 表示使用默认 TTL（无默认则永不过期）</param>
    public void Set(String key, Byte[]? value, TimeSpan? ttl = null)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));

        // 未显式指定 TTL 时使用默认值
        var effectiveTtl = ttl ?? (_defaultTtl > TimeSpan.Zero ? _defaultTtl : (TimeSpan?)null);

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_data.TryGetValue(key, out var existing))
            {
                existing.Value = value;
                existing.ExpiresAt = effectiveTtl.HasValue ? now.Add(effectiveTtl.Value) : null;
                existing.ModifiedAt = now;
            }
            else
            {
                _data[key] = new KvEntry
                {
                    Key = key,
                    Value = value,
                    ExpiresAt = effectiveTtl.HasValue ? now.Add(effectiveTtl.Value) : null,
                    CreatedAt = now,
                    ModifiedAt = now,
                };
            }
        }
    }

    /// <summary>获取键对应的值，过期则惰性删除并返回 null</summary>
    /// <param name="key">键</param>
    /// <returns>值，不存在或已过期返回 null</returns>
    public Byte[]? Get(String key)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));

        lock (_lock)
        {
            if (!_data.TryGetValue(key, out var entry))
                return null;

            // 惰性删除
            if (entry.IsExpired())
            {
                _data.Remove(key);
                return null;
            }

            return entry.Value;
        }
    }

    /// <summary>删除键</summary>
    /// <param name="key">键</param>
    /// <returns>键存在并被删除返回 true，否则返回 false</returns>
    public Boolean Delete(String key)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));

        lock (_lock)
        {
            return _data.Remove(key);
        }
    }

    /// <summary>检查键是否存在且未过期</summary>
    /// <param name="key">键</param>
    /// <returns>存在且未过期返回 true</returns>
    public Boolean Exists(String key)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));

        lock (_lock)
        {
            if (!_data.TryGetValue(key, out var entry))
                return false;

            // 惰性删除
            if (entry.IsExpired())
            {
                _data.Remove(key);
                return false;
            }

            return true;
        }
    }

    /// <summary>设置字符串键值对（UTF-8 编码）</summary>
    /// <param name="key">键</param>
    /// <param name="value">字符串值</param>
    /// <param name="ttl">过期时间，null 表示永不过期</param>
    public void SetString(String key, String value, TimeSpan? ttl = null)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));

        Set(key, Encoding.UTF8.GetBytes(value), ttl);
    }

    /// <summary>获取字符串值（UTF-8 解码）</summary>
    /// <param name="key">键</param>
    /// <returns>字符串值，不存在或已过期返回 null</returns>
    public String? GetString(String key)
    {
        var bytes = Get(key);
        if (bytes == null) return null;

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>仅当 key 不存在时添加，返回是否成功（分布式锁场景）</summary>
    /// <param name="key">键</param>
    /// <param name="value">值</param>
    /// <param name="ttl">过期时间</param>
    /// <returns>添加成功返回 true，key 已存在返回 false</returns>
    public Boolean Add(String key, Byte[] value, TimeSpan ttl)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));

        lock (_lock)
        {
            if (_data.TryGetValue(key, out var entry) && !entry.IsExpired())
                return false;

            var now = DateTime.UtcNow;
            _data[key] = new KvEntry
            {
                Key = key,
                Value = value,
                ExpiresAt = now.Add(ttl),
                CreatedAt = now,
                ModifiedAt = now,
            };
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

        return Add(key, Encoding.UTF8.GetBytes(value), ttl);
    }

    /// <summary>原子递增操作，key 不存在时初始化为 delta</summary>
    /// <param name="key">键</param>
    /// <param name="delta">递增量</param>
    /// <param name="ttl">过期时间（可选，仅在 key 不存在时使用默认 TTL）</param>
    /// <returns>递增后的值</returns>
    public Int64 Inc(String key, Int64 delta = 1, TimeSpan? ttl = null)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            Int64 newValue;

            if (_data.TryGetValue(key, out var entry) && !entry.IsExpired())
            {
                // key 存在且未过期，解析当前值并递增
                var current = entry.Value != null ? Int64.Parse(Encoding.UTF8.GetString(entry.Value)) : 0;
                newValue = current + delta;
                entry.Value = Encoding.UTF8.GetBytes(newValue.ToString());
                entry.ModifiedAt = now;
            }
            else
            {
                // key 不存在或已过期，初始化为 delta
                newValue = delta;
                var effectiveTtl = ttl ?? (_defaultTtl > TimeSpan.Zero ? _defaultTtl : (TimeSpan?)null);
                _data[key] = new KvEntry
                {
                    Key = key,
                    Value = Encoding.UTF8.GetBytes(newValue.ToString()),
                    ExpiresAt = effectiveTtl.HasValue ? now.Add(effectiveTtl.Value) : null,
                    CreatedAt = now,
                    ModifiedAt = now,
                };
            }

            return newValue;
        }
    }

    /// <summary>获取键的过期时间</summary>
    /// <param name="key">键</param>
    /// <returns>过期时间，键不存在返回 null</returns>
    public DateTime? GetExpiration(String key)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));

        lock (_lock)
        {
            if (!_data.TryGetValue(key, out var entry))
                return null;

            return entry.ExpiresAt;
        }
    }

    /// <summary>更新键的过期时间</summary>
    /// <param name="key">键</param>
    /// <param name="ttl">新的 TTL</param>
    /// <returns>键存在并更新成功返回 true</returns>
    public Boolean SetExpiration(String key, TimeSpan ttl)
    {
        if (String.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));

        lock (_lock)
        {
            if (!_data.TryGetValue(key, out var entry))
                return false;

            entry.ExpiresAt = DateTime.UtcNow.Add(ttl);
            entry.ModifiedAt = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>清理所有已过期的条目</summary>
    /// <returns>删除的条目数量</returns>
    public Int32 CleanupExpired()
    {
        var toRemove = new List<String>();

        lock (_lock)
        {
            foreach (var kvp in _data)
            {
                if (kvp.Value.IsExpired())
                    toRemove.Add(kvp.Key);
            }

            foreach (var key in toRemove)
            {
                _data.Remove(key);
            }
        }

        return toRemove.Count;
    }

    /// <summary>获取所有未过期的键</summary>
    /// <returns>未过期键列表</returns>
    public List<String> GetAllKeys()
    {
        var keys = new List<String>();

        lock (_lock)
        {
            foreach (var kvp in _data)
            {
                if (!kvp.Value.IsExpired())
                    keys.Add(kvp.Key);
            }
        }

        return keys;
    }
}
