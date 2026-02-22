# NewLife.NovaDb - KV 存储架构

## 1. 概述

NovaDb 的 KV 存储通过复用 Nova Engine 实现,采用特殊 Schema 实现键值模式。一个数据库允许创建多个 KV 表,每个 KV 表用于不同业务场景。

## 2. 设计理念

### 2.1 多 KV 表支持

```sql
-- 创建会话缓存 KV 表（默认 TTL = 2 小时）
CREATE TABLE SessionCache (
    Key STRING(200) PRIMARY KEY, 
    Value BLOB, 
    TTL DATETIME
) ENGINE=KV DEFAULT_TTL=7200;

-- 创建配置缓存 KV 表（默认 TTL = 24 小时）
CREATE TABLE ConfigCache (
    Key STRING(200) PRIMARY KEY, 
    Value BLOB, 
    TTL DATETIME
) ENGINE=KV DEFAULT_TTL=86400;

-- 创建分布式锁 KV 表（默认 TTL = 30 秒）
CREATE TABLE DistLock (
    Key STRING(200) PRIMARY KEY, 
    Value BLOB, 
    TTL DATETIME
) ENGINE=KV DEFAULT_TTL=30;
```

### 2.2 固定 Schema

所有 KV 表使用统一 Schema:

```csharp
public class KvSchema
{
    public static TableSchema Create(String tableName, Int32 defaultTtlSeconds)
    {
        return new TableSchema
        {
            TableName = tableName,
            Columns =
            [
                new() { Name = "Key", Type = DataType.String, Nullable = false },
                new() { Name = "Value", Type = DataType.ByteArray, Nullable = true },
                new() { Name = "TTL", Type = DataType.DateTime, Nullable = true }
            ],
            PrimaryKeyColumn = "Key",
            DefaultTtlSeconds = defaultTtlSeconds
        };
    }
}
```

| 列名 | 类型 | 说明 |
|------|------|------|
| Key | STRING(200) | 主键,最大 200 字符 |
| Value | BLOB | 值,字节数组（支持任意类型序列化） |
| TTL | DATETIME | 过期时间（NULL 表示永不过期） |

## 3. API 设计

### 3.1 核心 API

```csharp
public class KvStore
{
    private readonly NovaTable _table;
    
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
    
    /// <summary>获取所有有效键值对</summary>
    public Dictionary<String, Byte[]> GetAll();
}
```

### 3.2 实现示例

```csharp
public Byte[]? Get(String key)
{
    using var tx = _txManager.BeginTransaction(readOnly: true);
    
    // 1. 查询行数据
    var row = _table.Get(key, tx);
    if (row == null)
        return null;
    
    // 2. 检查 TTL
    var ttl = row["TTL"] as DateTime?;
    if (ttl != null && ttl.Value < DateTime.Now)
    {
        // 惰性删除过期数据
        Delete(key);
        return null;
    }
    
    // 3. 返回 Value
    return row["Value"] as Byte[];
}

public void Set(String key, Byte[] value, TimeSpan? ttl = null)
{
    using var tx = _txManager.BeginTransaction();
    
    // 计算过期时间
    var expireTime = ttl.HasValue 
        ? DateTime.Now.Add(ttl.Value) 
        : DateTime.Now.AddSeconds(_defaultTtlSeconds);
    
    var row = new Dictionary<String, Object?>
    {
        ["Key"] = key,
        ["Value"] = value,
        ["TTL"] = expireTime
    };
    
    // 使用 UPSERT 语义（先删除后插入）
    _table.Delete(key, tx);
    _table.Insert(key, row, tx);
    
    tx.Commit();
}

public Boolean Add(String key, Byte[] value, TimeSpan? ttl = null)
{
    using var tx = _txManager.BeginTransaction();
    
    // 检查键是否存在
    if (_table.Get(key, tx) != null)
    {
        tx.Rollback();
        return false; // 键已存在
    }
    
    // 插入新键
    var expireTime = ttl.HasValue 
        ? DateTime.Now.Add(ttl.Value) 
        : DateTime.Now.AddSeconds(_defaultTtlSeconds);
    
    var row = new Dictionary<String, Object?>
    {
        ["Key"] = key,
        ["Value"] = value,
        ["TTL"] = expireTime
    };
    
    _table.Insert(key, row, tx);
    tx.Commit();
    
    return true;
}

public Int64 Inc(String key, Int64 delta = 1, TimeSpan? ttl = null)
{
    using var tx = _txManager.BeginTransaction();
    
    // 1. 读取当前值
    var row = _table.Get(key, tx);
    var currentValue = 0L;
    
    if (row != null)
    {
        var valueBytes = row["Value"] as Byte[];
        if (valueBytes != null && valueBytes.Length == 8)
        {
            currentValue = BitConverter.ToInt64(valueBytes, 0);
        }
    }
    
    // 2. 计算新值
    var newValue = currentValue + delta;
    var newValueBytes = BitConverter.GetBytes(newValue);
    
    // 3. 写入新值
    var expireTime = ttl.HasValue 
        ? DateTime.Now.Add(ttl.Value) 
        : DateTime.Now.AddSeconds(_defaultTtlSeconds);
    
    var newRow = new Dictionary<String, Object?>
    {
        ["Key"] = key,
        ["Value"] = newValueBytes,
        ["TTL"] = expireTime
    };
    
    _table.Delete(key, tx);
    _table.Insert(key, newRow, tx);
    
    tx.Commit();
    
    return newValue;
}
```

## 4. TTL 清理

### 4.1 读时惰性删除

```csharp
/// <summary>读取时检查并删除过期键</summary>
private Byte[]? GetWithTtlCheck(String key, Transaction tx)
{
    var row = _table.Get(key, tx);
    if (row == null)
        return null;
    
    var ttl = row["TTL"] as DateTime?;
    if (ttl != null && ttl.Value < DateTime.Now)
    {
        // 异步删除（避免阻塞读操作）
        Task.Run(() => Delete(key));
        return null;
    }
    
    return row["Value"] as Byte[];
}
```

### 4.2 后台扫描

```csharp
/// <summary>后台定期清理过期数据</summary>
public void StartBackgroundCleaner(Int32 intervalSeconds = 60)
{
    Task.Run(async () =>
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            try
            {
                CleanupExpiredKeys();
            }
            catch (Exception ex)
            {
                Log?.Error($"KV 清理失败: {ex.Message}");
            }
            
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), _cancellationToken);
        }
    });
}

private void CleanupExpiredKeys()
{
    var now = DateTime.Now;
    var expiredKeys = new List<String>();
    
    using var tx = _txManager.BeginTransaction(readOnly: true);
    
    // 扫描所有键
    foreach (var row in _table.GetAll(tx))
    {
        var key = row["Key"] as String;
        var ttl = row["TTL"] as DateTime?;
        
        if (ttl != null && ttl.Value < now)
        {
            expiredKeys.Add(key!);
        }
    }
    
    // 批量删除（新事务）
    if (expiredKeys.Count > 0)
    {
        using var deleteTx = _txManager.BeginTransaction();
        
        foreach (var key in expiredKeys)
        {
            _table.Delete(key, deleteTx);
        }
        
        deleteTx.Commit();
        
        Log?.Info($"清理过期键: {expiredKeys.Count} 个");
    }
}
```

### 4.3 表级默认 TTL

```csharp
public class KvStore
{
    private readonly Int32 _defaultTtlSeconds;
    
    public KvStore(NovaTable table, Int32 defaultTtlSeconds = 3600)
    {
        _table = table;
        _defaultTtlSeconds = defaultTtlSeconds;
    }
    
    /// <summary>获取表级默认 TTL（秒）</summary>
    public Int32 GetDefaultTtl() => _defaultTtlSeconds;
    
    /// <summary>设置表级默认 TTL（秒）</summary>
    public void SetDefaultTtl(Int32 seconds)
    {
        _defaultTtlSeconds = seconds;
    }
}
```

## 5. 应用场景

### 5.1 会话缓存

```csharp
// 创建会话缓存 KV 表（2 小时过期）
var sessionCache = new KvStore(table, defaultTtlSeconds: 7200);

// 存储会话
var sessionData = JsonSerializer.SerializeToUtf8Bytes(new 
{
    UserId = 123,
    UserName = "Alice",
    LoginTime = DateTime.Now
});
sessionCache.Set("session:abc123", sessionData);

// 读取会话
var data = sessionCache.Get("session:abc123");
if (data != null)
{
    var session = JsonSerializer.Deserialize<SessionData>(data);
}
```

### 5.2 配置缓存

```csharp
// 创建配置缓存 KV 表（24 小时过期）
var configCache = new KvStore(table, defaultTtlSeconds: 86400);

// 缓存配置
var config = GetConfigFromDatabase(); // 从数据库读取
configCache.Set("config:app_settings", config);

// 读取配置（优先从缓存）
var cachedConfig = configCache.Get("config:app_settings");
if (cachedConfig == null)
{
    cachedConfig = GetConfigFromDatabase();
    configCache.Set("config:app_settings", cachedConfig);
}
```

### 5.3 分布式锁

```csharp
// 创建分布式锁 KV 表（30 秒过期）
var distLock = new KvStore(table, defaultTtlSeconds: 30);

// 尝试获取锁
var lockKey = "lock:order_123";
var lockValue = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString());

if (distLock.Add(lockKey, lockValue, TimeSpan.FromSeconds(30)))
{
    try
    {
        // 获取锁成功,执行业务逻辑
        ProcessOrder(123);
    }
    finally
    {
        // 释放锁
        distLock.Delete(lockKey);
    }
}
else
{
    // 锁被其他进程持有
    throw new Exception("无法获取锁");
}
```

### 5.4 计数器

```csharp
// 创建计数器 KV 表（永不过期）
var counter = new KvStore(table, defaultTtlSeconds: Int32.MaxValue);

// 页面访问计数
counter.Inc("page_views:home");
counter.Inc("page_views:home");
counter.Inc("page_views:home");

// 读取计数
var countBytes = counter.Get("page_views:home");
var count = BitConverter.ToInt64(countBytes!, 0); // 输出: 3
```

## 6. 性能优化

### 6.1 批量操作

```csharp
/// <summary>批量设置</summary>
public void BatchSet(Dictionary<String, Byte[]> items, TimeSpan? ttl = null)
{
    using var tx = _txManager.BeginTransaction();
    
    var expireTime = ttl.HasValue 
        ? DateTime.Now.Add(ttl.Value) 
        : DateTime.Now.AddSeconds(_defaultTtlSeconds);
    
    foreach (var (key, value) in items)
    {
        var row = new Dictionary<String, Object?>
        {
            ["Key"] = key,
            ["Value"] = value,
            ["TTL"] = expireTime
        };
        
        _table.Delete(key, tx);
        _table.Insert(key, row, tx);
    }
    
    tx.Commit();
}

/// <summary>批量获取</summary>
public Dictionary<String, Byte[]> BatchGet(List<String> keys)
{
    var result = new Dictionary<String, Byte[]>();
    
    using var tx = _txManager.BeginTransaction(readOnly: true);
    
    foreach (var key in keys)
    {
        var value = GetWithTtlCheck(key, tx);
        if (value != null)
        {
            result[key] = value;
        }
    }
    
    return result;
}
```

### 6.2 压缩

对 Value 字段启用透明压缩:

```csharp
public void Set(String key, Byte[] value, TimeSpan? ttl = null, Boolean compress = false)
{
    Byte[] finalValue = value;
    
    if (compress && value.Length > 1024) // 大于 1KB 才压缩
    {
        finalValue = Compress(value); // LZ4 或 Snappy
    }
    
    // ... 后续逻辑
}

public Byte[]? Get(String key, Boolean decompress = false)
{
    var value = GetWithTtlCheck(key);
    
    if (value != null && decompress)
    {
        value = Decompress(value);
    }
    
    return value;
}
```

### 6.3 分片

超大 KV 表可按 Key 前缀分片:

```
SessionCache_0  # Key 以 0-4 开头
SessionCache_1  # Key 以 5-9 开头
SessionCache_2  # Key 以 A-F 开头（十六进制）
SessionCache_3  # Key 以 G-Z 开头
```

## 7. 设计决策

### D1: 复用 Nova Engine
- 无需重复实现索引、事务、WAL
- 统一存储架构,简化维护
- 享受 Nova Engine 的所有优化（冷热分离、MVCC 等）

### D2: 多 KV 表支持
- 不同业务场景使用不同 KV 表
- 每个 KV 表可配置独立的默认 TTL
- 避免单表过大导致性能下降

### D3: 固定 Schema（Key/Value/TTL）
- 简化实现,保持一致性
- Key 最大 200 字符,满足大多数场景
- Value 使用 BLOB,支持任意类型序列化

### D4: 惰性删除 + 后台扫描
- 惰性删除: 读取时检查 TTL,过期则删除（不阻塞读操作）
- 后台扫描: 定期批量清理过期键（降低存储占用）
- 两者结合,平衡性能与资源占用

## 8. 未来扩展

### 8.1 原子操作
- CAS（Compare-And-Swap）: `CompareAndSwap(key, expectedValue, newValue)`
- GetAndSet: 原子读取并设置新值

### 8.2 事务性 KV 操作
- 支持跨多个 Key 的原子操作
- 集成到 NovaDb 的事务系统

### 8.3 持久化 KV（无 TTL）
- 支持永久存储的 KV 表（TTL = NULL）
- 适合配置、元数据等场景

### 8.4 LRU 淘汰
- 超过容量限制时,淘汰最少使用的键
- 适合缓存场景（类似 Redis maxmemory-policy）
