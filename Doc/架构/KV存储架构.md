# NewLife.NovaDb KV 存储架构

> 对应模块：K01（KV 表与 TTL）、K02（KV API）
> 关联模块：N01-N03（Nova 引擎复用）、T01（事务）

---

## 1. 概述

KV 存储通过复用 Nova Engine 实现，采用固定 Schema 的特殊表。一个数据库允许创建多个 KV 表，每个 KV 表用于不同业务场景，可配置独立的默认 TTL。

---

## 2. 多 KV 表

```sql
-- 会话缓存（TTL = 2 小时）
CREATE TABLE SessionCache (
    Key STRING(200) PRIMARY KEY, Value BLOB, TTL DATETIME
) ENGINE=KV DEFAULT_TTL=7200;

-- 配置缓存（TTL = 24 小时）
CREATE TABLE ConfigCache (
    Key STRING(200) PRIMARY KEY, Value BLOB, TTL DATETIME
) ENGINE=KV DEFAULT_TTL=86400;

-- 分布式锁（TTL = 30 秒）
CREATE TABLE DistLock (
    Key STRING(200) PRIMARY KEY, Value BLOB, TTL DATETIME
) ENGINE=KV DEFAULT_TTL=30;
```

### 固定 Schema

| 列名 | 类型 | 说明 |
|------|------|------|
| Key | STRING(200) | 主键 |
| Value | BLOB | 字节数组（任意类型序列化） |
| TTL | DATETIME | 过期时间（NULL = 永不过期） |

---

## 3. API 设计

### 3.1 核心接口

```csharp
public class KvStore
{
    /// <summary>获取值（惰性检查 TTL）</summary>
    public Byte[]? Get(String key);

    /// <summary>设置值（未指定 TTL 时使用表级默认 TTL）</summary>
    public void Set(String key, Byte[] value, TimeSpan? ttl = null);

    /// <summary>仅当 key 不存在时添加（分布式锁场景）</summary>
    public Boolean Add(String key, Byte[] value, TimeSpan? ttl = null);

    /// <summary>删除键</summary>
    public Boolean Delete(String key);

    /// <summary>判断键是否存在</summary>
    public Boolean ContainsKey(String key);

    /// <summary>原子递增（计数器场景）</summary>
    public Int64 Inc(String key, Int64 delta = 1, TimeSpan? ttl = null);

    /// <summary>批量设置</summary>
    public void BatchSet(Dictionary<String, Byte[]> items, TimeSpan? ttl = null);

    /// <summary>批量获取</summary>
    public Dictionary<String, Byte[]> BatchGet(List<String> keys);
}
```

### 3.2 Get 实现

```csharp
public Byte[]? Get(String key)
{
    using var tx = _txManager.BeginTransaction(readOnly: true);
    var row = _table.Get(key, tx);
    if (row == null) return null;

    // 惰性 TTL 检查
    var ttl = row["TTL"] as DateTime?;
    if (ttl != null && ttl.Value < DateTime.Now)
    {
        Delete(key);
        return null;
    }

    return row["Value"] as Byte[];
}
```

### 3.3 Set 实现

```csharp
public void Set(String key, Byte[] value, TimeSpan? ttl = null)
{
    using var tx = _txManager.BeginTransaction();

    var expireTime = ttl.HasValue
        ? DateTime.Now.Add(ttl.Value)
        : DateTime.Now.AddSeconds(_defaultTtlSeconds);

    var row = new Dictionary<String, Object?>
    {
        ["Key"] = key, ["Value"] = value, ["TTL"] = expireTime
    };

    // UPSERT 语义
    _table.Delete(key, tx);
    _table.Insert(key, row, tx);
    tx.Commit();
}
```

### 3.4 Add 实现（分布式锁）

```csharp
public Boolean Add(String key, Byte[] value, TimeSpan? ttl = null)
{
    using var tx = _txManager.BeginTransaction();

    if (_table.Get(key, tx) != null)
    {
        tx.Rollback();
        return false; // 键已存在
    }

    var expireTime = ttl.HasValue
        ? DateTime.Now.Add(ttl.Value)
        : DateTime.Now.AddSeconds(_defaultTtlSeconds);

    _table.Insert(key, new Dictionary<String, Object?>
    {
        ["Key"] = key, ["Value"] = value, ["TTL"] = expireTime
    }, tx);

    tx.Commit();
    return true;
}
```

### 3.5 Inc 实现（计数器）

```csharp
public Int64 Inc(String key, Int64 delta = 1, TimeSpan? ttl = null)
{
    using var tx = _txManager.BeginTransaction();

    var row = _table.Get(key, tx);
    var currentValue = 0L;

    if (row != null)
    {
        var bytes = row["Value"] as Byte[];
        if (bytes != null && bytes.Length == 8)
            currentValue = BitConverter.ToInt64(bytes, 0);
    }

    var newValue = currentValue + delta;
    var expireTime = ttl.HasValue
        ? DateTime.Now.Add(ttl.Value)
        : DateTime.Now.AddSeconds(_defaultTtlSeconds);

    _table.Delete(key, tx);
    _table.Insert(key, new Dictionary<String, Object?>
    {
        ["Key"] = key,
        ["Value"] = BitConverter.GetBytes(newValue),
        ["TTL"] = expireTime
    }, tx);

    tx.Commit();
    return newValue;
}
```

---

## 4. TTL 清理

### 4.1 双重策略

| 策略 | 触发时机 | 说明 |
|------|---------|------|
| 惰性删除 | 读取时 | Get/ContainsKey 发现过期 → 删除并返回 null |
| 后台扫描 | 定时（默认 60 秒） | 扫描全部键，批量删除过期键 |

### 4.2 后台清理

```csharp
public void StartBackgroundCleaner(Int32 intervalSeconds = 60)
{
    Task.Run(async () =>
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            try { CleanupExpiredKeys(); }
            catch (Exception ex) { Log?.Error($"KV 清理失败: {ex.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), _cancellationToken);
        }
    });
}

private void CleanupExpiredKeys()
{
    var now = DateTime.Now;
    var expiredKeys = new List<String>();

    using var tx = _txManager.BeginTransaction(readOnly: true);
    foreach (var row in _table.GetAll(tx))
    {
        var ttl = row["TTL"] as DateTime?;
        if (ttl != null && ttl.Value < now)
            expiredKeys.Add((row["Key"] as String)!);
    }

    if (expiredKeys.Count > 0)
    {
        using var deleteTx = _txManager.BeginTransaction();
        foreach (var key in expiredKeys) _table.Delete(key, deleteTx);
        deleteTx.Commit();
        Log?.Info($"清理过期键: {expiredKeys.Count} 个");
    }
}
```

---

## 5. 应用场景

### 5.1 会话缓存

```csharp
var cache = new KvStore(table, defaultTtlSeconds: 7200);
cache.Set("session:abc123", JsonSerializer.SerializeToUtf8Bytes(sessionData));
var data = cache.Get("session:abc123");
```

### 5.2 分布式锁

```csharp
var distLock = new KvStore(table, defaultTtlSeconds: 30);
if (distLock.Add("lock:order_123", lockValue, TimeSpan.FromSeconds(30)))
{
    try { ProcessOrder(123); }
    finally { distLock.Delete("lock:order_123"); }
}
```

### 5.3 计数器

```csharp
var counter = new KvStore(table, defaultTtlSeconds: Int32.MaxValue);
counter.Inc("page_views:home");
var count = BitConverter.ToInt64(counter.Get("page_views:home")!, 0);
```

---

## 6. 与 SQL 的统一

KV 操作与 SQL 行为一致：

```sql
-- Get
SELECT Value FROM SessionCache WHERE Key = 'session:abc' AND (TTL IS NULL OR TTL > NOW());

-- Set (UPSERT)
INSERT INTO SessionCache (Key, Value, TTL) VALUES ('k1', x'...', '2025-01-01')
ON DUPLICATE KEY UPDATE Value = x'...', TTL = '2025-01-01';

-- Add (仅不存在时)
INSERT INTO DistLock (Key, Value, TTL) VALUES ('lock:x', x'...', '2025-01-01');

-- Inc
UPDATE Counter SET Value = Value + 1 WHERE Key = 'page_views:home';

-- Delete
DELETE FROM SessionCache WHERE Key = 'session:abc';
```

---

## 7. 设计决策

| # | 决策 | 要点 |
|---|------|------|
| D1 | 复用 Nova Engine | KV 表 = 固定 Schema 的 Nova 表，无额外存储引擎 |
| D2 | 多 KV 表 | 每个业务场景独立 KV 表，独立 TTL 配置 |
| D3 | 惰性 + 后台双重清理 | 惰性保证读不到过期数据，后台回收空间 |
| D4 | SQL 统一 | KV API 与 SQL 行为一致，可互换使用 |

---

（完）
