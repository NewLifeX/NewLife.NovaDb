# NewLife.NovaDb - Nova 引擎架构（关系型）

## 1. 概述

Nova Engine 是 NovaDb 的关系型引擎，提供类 SQL 表的存储与查询能力，支持主键索引、MVCC 事务、冷热数据分离等特性。

## 2. 核心组件

### 2.1 NovaTable（单表引擎）

```csharp
public class NovaTable
{
    public String Name { get; }                     // 表名
    public TableSchema Schema { get; }              // 表架构（列定义）
    public SkipList<Object, RowVersion> PrimaryIndex { get; } // 主键索引
    public HotIndexManager HotIndexManager { get; } // 热索引管理器
    public ColdIndexDirectory ColdIndexDirectory { get; } // 冷索引目录
    public TransactionManager TxManager { get; }    // 事务管理器
    public WalWriter WalWriter { get; }             // WAL 写入器
}
```

### 2.2 TableSchema（表架构）

```csharp
public class TableSchema
{
    public String TableName { get; set; }
    public List<ColumnDef> Columns { get; set; } = [];
    public String? PrimaryKeyColumn { get; set; }
    
    public class ColumnDef
    {
        public String Name { get; set; }
        public DataType Type { get; set; }
        public Boolean Nullable { get; set; } = true;
        public Object? DefaultValue { get; set; }
    }
}
```

### 2.3 SkipList（跳表索引）

```csharp
public class SkipList<K, V> where K : IComparable<K>
{
    public void Insert(K key, V value);
    public V? Get(K key);
    public Boolean Remove(K key);
    public IEnumerable<V> GetAll();
    public IEnumerable<V> Range(K start, K end);
}
```

## 3. 索引冷热分离

### 3.1 架构图

```
┌────────────────────────────┐
│   热索引 (HotIndexManager)  │  ← 最近访问的数据段
│   完整 SkipList 节点        │     内存中完整索引
│   快速随机访问              │     支持 O(log n) 查找
├────────────────────────────┤
│   冷索引 (ColdIndexDirectory)│  ← 长期未访问
│   稀疏目录 (每 N 行一个锚点) │     仅保留锚点映射
│   锚点 → MMF 页偏移         │     查询时按需加载
└────────────────────────────┘
```

### 3.2 热索引管理器 (`HotIndexManager`)

```csharp
public class HotIndexManager
{
    private readonly Dictionary<Object, HotSegment> _hotSegments = [];
    private readonly LruCache<Object, SkipList<Object, RowVersion>> _cache;
    
    public class HotSegment
    {
        public Object StartKey { get; set; }        // 段起始主键
        public Object EndKey { get; set; }          // 段结束主键
        public SkipList<Object, RowVersion> Index { get; set; } // 完整索引
        public DateTime LastAccessTime { get; set; } // 最后访问时间
    }
    
    /// <summary>获取或加载热索引段</summary>
    public SkipList<Object, RowVersion> GetOrLoad(Object key);
    
    /// <summary>淘汰冷数据到冷索引目录</summary>
    public void EvictColdSegments();
}
```

**淘汰策略**：
- 超过 `HotWindowSeconds`（默认 600 秒）未访问 → 降为冷数据
- 超过 `ColdEvictionSeconds`（默认 1800 秒）未访问 → 从内存卸载

### 3.3 冷索引目录 (`ColdIndexDirectory`)

```csharp
public class ColdIndexDirectory
{
    private readonly Dictionary<Object, AnchorPoint> _anchors = [];
    
    public class AnchorPoint
    {
        public Object Key { get; set; }         // 锚点主键
        public Int64 PageOffset { get; set; }   // MMF 页偏移
        public Int32 RowCount { get; set; }     // 锚点后行数（到下一个锚点）
    }
    
    /// <summary>添加锚点</summary>
    public void AddAnchor(Object key, Int64 pageOffset, Int32 rowCount);
    
    /// <summary>查找最近的锚点</summary>
    public AnchorPoint? FindNearestAnchor(Object key);
    
    /// <summary>从锚点加载数据段到热索引</summary>
    public SkipList<Object, RowVersion> LoadSegment(AnchorPoint anchor);
}
```

**锚点生成策略**：
- 每隔 N 行（如 1000 行）生成一个锚点
- 锚点记录：`(主键, MMF 页偏移, 锚点后行数)`

### 3.4 冷热数据访问流程

```
查询主键 key=5000:
  1. 检查热索引 → 未命中
  2. 查找冷索引目录 → 找到最近锚点 (key=4000, offset=1234)
  3. 从 MMF 加载锚点后 1000 行数据
  4. 构建热索引段 (key=4000~5000)
  5. 缓存到热索引管理器
  6. 返回 key=5000 的数据
  7. 更新 LastAccessTime
```

### 3.5 内存优化效果

| 场景 | 传统全量索引 | 冷热分离索引 | 节省比例 |
|------|------------|------------|---------|
| 1000 万行表，查询最近 1 天数据（10 万行） | ~1.5GB | ~15MB | 99% |
| 1 亿行表，查询最近 1 周数据（100 万行） | ~15GB | ~150MB | 99% |

## 4. CRUD 操作

### 4.1 INSERT

```csharp
public void Insert(Object primaryKey, Dictionary<String, Object?> row, Transaction tx)
{
    // 1. 验证主键唯一性
    if (PrimaryIndex.Get(primaryKey) != null)
        throw new NovaException(ErrorCode.DuplicatePrimaryKey, "主键冲突");
    
    // 2. 验证列类型与约束
    ValidateRow(row);
    
    // 3. 序列化行数据
    var payload = SerializeRow(row);
    
    // 4. 写入 WAL
    WalWriter.WriteInsert(tx.TxId, primaryKey, payload);
    
    // 5. 创建行版本
    var rowVersion = new RowVersion
    {
        CreatedByTx = tx.TxId,
        DeletedByTx = null,
        Payload = payload
    };
    
    // 6. 插入主键索引
    PrimaryIndex.Insert(primaryKey, rowVersion);
    
    // 7. 注册回滚动作
    tx.RegisterRollback(() => PrimaryIndex.Remove(primaryKey));
}
```

### 4.2 UPDATE

```csharp
public void Update(Object primaryKey, Dictionary<String, Object?> newRow, Transaction tx)
{
    // 1. 查找可见的行版本
    var oldVersion = PrimaryIndex.Get(primaryKey);
    if (oldVersion == null || !IsVisible(oldVersion, tx))
        throw new NovaException(ErrorCode.RowNotFound, "行不存在");
    
    // 2. 验证列类型与约束
    ValidateRow(newRow);
    
    // 3. 序列化新行数据
    var newPayload = SerializeRow(newRow);
    
    // 4. 标记旧版本为已删除
    oldVersion.DeletedByTx = tx.TxId;
    
    // 5. 创建新版本
    var newVersion = new RowVersion
    {
        CreatedByTx = tx.TxId,
        DeletedByTx = null,
        Payload = newPayload
    };
    
    // 6. 写入 WAL
    WalWriter.WriteUpdate(tx.TxId, primaryKey, oldVersion.Payload, newPayload);
    
    // 7. 更新主键索引（替换版本）
    PrimaryIndex.Insert(primaryKey, newVersion);
    
    // 8. 注册回滚动作
    tx.RegisterRollback(() =>
    {
        oldVersion.DeletedByTx = null;
        PrimaryIndex.Insert(primaryKey, oldVersion);
    });
}
```

### 4.3 DELETE

```csharp
public void Delete(Object primaryKey, Transaction tx)
{
    // 1. 查找可见的行版本
    var rowVersion = PrimaryIndex.Get(primaryKey);
    if (rowVersion == null || !IsVisible(rowVersion, tx))
        throw new NovaException(ErrorCode.RowNotFound, "行不存在");
    
    // 2. 标记为已删除
    rowVersion.DeletedByTx = tx.TxId;
    
    // 3. 写入 WAL
    WalWriter.WriteDelete(tx.TxId, primaryKey);
    
    // 4. 注册回滚动作
    tx.RegisterRollback(() => rowVersion.DeletedByTx = null);
}
```

### 4.4 SELECT (GetAll)

```csharp
public List<Dictionary<String, Object?>> GetAll(Transaction tx)
{
    var result = new List<Dictionary<String, Object?>>();
    
    // 遍历主键索引
    foreach (var rowVersion in PrimaryIndex.GetAll())
    {
        // 过滤可见版本
        if (IsVisible(rowVersion, tx))
        {
            var row = DeserializeRow(rowVersion.Payload);
            result.Add(row);
        }
    }
    
    return result;
}
```

### 4.5 TRUNCATE

```csharp
public void Truncate(Transaction tx)
{
    // 1. 清空主键索引
    var oldIndex = PrimaryIndex;
    PrimaryIndex.Clear();
    
    // 2. 写入 WAL
    WalWriter.WriteTruncate(tx.TxId);
    
    // 3. 清空冷热索引
    HotIndexManager.Clear();
    ColdIndexDirectory.Clear();
    
    // 4. 注册回滚动作（恢复旧索引）
    tx.RegisterRollback(() =>
    {
        PrimaryIndex = oldIndex;
        // 重建冷热索引
        RebuildHotColdIndex();
    });
}
```

**TRUNCATE vs DELETE**：
- TRUNCATE: 直接清空索引，O(1) 复杂度，不可回滚（需保存整个索引）
- DELETE: 逐行删除，O(n) 复杂度，可回滚

## 5. 分片管理

### 5.1 设计原则

**Nova Engine 不分片**（参见设计决策 D8）：
- 以"表"为数据组织单位，不做自动数据分片
- 分片带来的路由/边界管理复杂度通常大于收益
- 如需逻辑分表由上层应用完成（如 `users_2024`, `users_2025`）

### 5.2 表级文件组织

每个 Nova 表对应以下文件：

```
{db_path}/
  {table_name}.nova        # 表数据文件（MMF）
  {table_name}.wal         # WAL 日志文件
  {table_name}.schema      # 表架构定义（JSON 或二进制）
```

详见[存储架构.md](../存储架构.md)。

## 6. 性能优化

### 6.1 批量插入

```csharp
public void BatchInsert(List<(Object key, Dictionary<String, Object?> row)> rows, Transaction tx)
{
    // 1. 批量验证主键唯一性
    var existingKeys = new HashSet<Object>(PrimaryIndex.GetAll().Select(v => v.PrimaryKey));
    foreach (var (key, _) in rows)
    {
        if (existingKeys.Contains(key))
            throw new NovaException(ErrorCode.DuplicatePrimaryKey, $"主键冲突: {key}");
    }
    
    // 2. 批量写入 WAL
    WalWriter.BeginBatch();
    try
    {
        foreach (var (key, row) in rows)
        {
            var payload = SerializeRow(row);
            WalWriter.WriteInsert(tx.TxId, key, payload);
            
            var rowVersion = new RowVersion
            {
                CreatedByTx = tx.TxId,
                DeletedByTx = null,
                Payload = payload
            };
            
            PrimaryIndex.Insert(key, rowVersion);
        }
        
        WalWriter.EndBatch();
    }
    catch
    {
        WalWriter.AbortBatch();
        throw;
    }
}
```

### 6.2 索引预热

```csharp
/// <summary>预热指定范围的数据到热索引</summary>
public void WarmupRange(Object startKey, Object endKey)
{
    var anchors = ColdIndexDirectory.FindAnchorsInRange(startKey, endKey);
    
    foreach (var anchor in anchors)
    {
        var segment = ColdIndexDirectory.LoadSegment(anchor);
        HotIndexManager.AddSegment(segment);
    }
}
```

### 6.3 压缩与重建

```csharp
/// <summary>压缩表数据，移除已删除的行版本</summary>
public void Compact()
{
    var minActiveTxId = TxManager.GetMinActiveTransactionId();
    var newIndex = new SkipList<Object, RowVersion>();
    
    foreach (var (key, rowVersion) in PrimaryIndex.GetAll())
    {
        // 保留未删除或删除事务仍活跃的版本
        if (rowVersion.DeletedByTx == null || rowVersion.DeletedByTx >= minActiveTxId)
        {
            newIndex.Insert(key, rowVersion);
        }
    }
    
    PrimaryIndex = newIndex;
    RebuildHotColdIndex();
}
```

## 7. 设计决策

### D1: SkipList 而非 B+ 树
- SkipList 实现简单、并发友好
- 适合内存索引场景（无需持久化索引结构）
- 查找/插入/删除均为 O(log n)

### D2: 冷热分离索引
- 1000 万行表仅查热点时内存 < 20MB
- 冷数据按需加载，无需常驻内存
- 适合时序数据、日志数据等场景

### D3: 主键索引唯一
- Nova Engine 仅支持主键索引
- 二级索引由上层 SQL 引擎或应用层实现
- 简化引擎实现，保持核心功能稳定

### D4: MVCC 版本链
- 每个主键对应一个版本链（RowVersion 列表）
- 支持并发读写，读不阻塞写
- 旧版本通过垃圾回收机制清理

### D5: 表级 WAL
- 每个表独立 WAL，减少并发竞争
- 简化恢复流程（按表恢复）
- 崩溃后仅恢复受影响的表

## 8. 未来扩展

### 8.1 二级索引
- 支持非主键列的索引（如 `CREATE INDEX idx_age ON users(age)`）
- 索引结构：`SkipList<IndexValue, List<PrimaryKey>>`
- 查询流程：二级索引 → 主键列表 → 主键索引 → 行数据

### 8.2 分区表
- 按范围/哈希/列表分区
- 每个分区独立存储与索引
- 查询时根据分区键路由到特定分区

### 8.3 列式存储
- 按列存储数据，适合分析查询
- 支持列级压缩（如 RLE、Delta）
- 混合行列存储，平衡 OLTP/OLAP 性能

### 8.4 全文索引
- 支持全文搜索（如 `MATCH(content) AGAINST('keyword')`）
- 倒排索引实现（Term → Document IDs）
- 分词器与相关性评分
