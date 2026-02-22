# NewLife.NovaDb - 事务与 MVCC 架构

## 1. 概述

NovaDb 采用 MVCC（多版本并发控制）实现事务隔离，支持 Read Committed 隔离级别，提供乐观并发控制与回滚机制。

## 2. 隔离级别

### 2.1 Read Committed

NovaDb 实现 Read Committed 隔离级别：
- **读已提交**：事务只能读取已提交的数据
- **避免脏读**：不会读取未提交事务的变更
- **允许不可重复读**：同一事务内多次读取同一数据可能得到不同结果
- **允许幻读**：同一事务内多次查询可能得到不同的行集合

### 2.2 选择 Read Committed 的理由

| 隔离级别 | 并发性能 | 实现复杂度 | 适用场景 |
|---------|---------|-----------|---------|
| Read Uncommitted | 最高 | 最低 | 极少使用（允许脏读） |
| **Read Committed** | 高 | 适中 | **大多数业务场景** |
| Repeatable Read | 中 | 较高 | 需要一致性快照的场景 |
| Serializable | 最低 | 最高 | 需要严格串行化的场景 |

NovaDb 选择 Read Committed 平衡了一致性与并发性能，满足 90% 的业务需求。

## 3. MVCC 实现

### 3.1 行版本结构 (`RowVersion`)

```csharp
public class RowVersion
{
    public Int64 CreatedByTx { get; set; }  // 创建该版本的事务 ID
    public Int64? DeletedByTx { get; set; } // 删除该版本的事务 ID（NULL 表示未删除）
    public Byte[] Payload { get; set; }     // 行数据（序列化后）
}
```

### 3.2 可见性规则

给定当前事务 `CurrentTx` 和行版本 `RowVersion`，判断该版本是否可见：

```csharp
Boolean IsVisible(RowVersion row, Transaction currentTx)
{
    // 规则 1: 当前事务创建且未删除 → 可见
    if (row.CreatedByTx == currentTx.TxId && row.DeletedByTx == null)
        return true;
    
    // 规则 2: 其他活跃事务创建 → 不可见
    if (IsActiveTransaction(row.CreatedByTx))
        return false;
    
    // 规则 3: 已提交且未被删除 → 可见
    if (row.DeletedByTx == null)
        return true;
    
    // 规则 4: 已被活跃事务删除 → 可见（删除未提交）
    if (IsActiveTransaction(row.DeletedByTx.Value))
        return true;
    
    // 规则 5: 已被已提交事务删除 → 不可见
    return false;
}
```

### 3.3 版本链

每个主键对应一个版本链（单向链表）：

```
主键 = 1:
  RowVersion { CreatedByTx=100, DeletedByTx=NULL, Payload="Alice,25" }
  → RowVersion { CreatedByTx=200, DeletedByTx=NULL, Payload="Alice,26" }
  → RowVersion { CreatedByTx=300, DeletedByTx=NULL, Payload="Alice,27" }

当前活跃事务 = [300]:
  - 事务 100: 读到 "Alice,25"
  - 事务 200: 读到 "Alice,26"
  - 事务 300: 读到 "Alice,27"
  - 新事务 400: 读到 "Alice,27"（事务 300 未提交，读上一个已提交版本）
```

### 3.4 垃圾回收

旧版本在满足以下条件时可被回收：
1. 该版本已被删除（`DeletedByTx != NULL`）
2. 删除事务已提交
3. 没有活跃事务需要读取该版本（所有活跃事务的 TxId > DeletedByTx）

回收策略：
- **即时回收**：事务提交时检查并回收旧版本
- **后台回收**：定期扫描版本链，批量回收旧版本

## 4. 事务生命周期

### 4.1 事务状态

```
┌──────────┐
│  未开始  │
└─────┬────┘
      ↓ BeginTransaction
┌──────────┐
│  活跃中  │ ← 执行 SQL 操作
└─────┬────┘
      ↓ Commit / Rollback
┌──────────┐
│ 已提交/  │
│ 已回滚   │
└──────────┘
```

### 4.2 事务管理器 (`TransactionManager`)

| 方法 | 功能 |
|------|------|
| `BeginTransaction()` | 开始新事务，分配事务 ID |
| `CommitTransaction(txId)` | 提交事务，移除活跃事务集合 |
| `RollbackTransaction(txId)` | 回滚事务，执行回滚动作 |
| `GetActiveTransactions()` | 获取所有活跃事务 ID |
| `IsActiveTransaction(txId)` | 判断事务是否活跃 |

### 4.3 事务 ID 分配

```csharp
private Int64 _nextTxId = 1;

public Transaction BeginTransaction()
{
    var txId = Interlocked.Increment(ref _nextTxId);
    var tx = new Transaction { TxId = txId, Status = TransactionStatus.Active };
    _activeTransactions.TryAdd(txId, tx);
    return tx;
}
```

事务 ID 单调递增，保证全局有序。

## 5. 回滚机制

### 5.1 回滚动作 (`RollbackAction`)

每个写操作注册一个回滚动作：

```csharp
public delegate void RollbackAction();

public class Transaction
{
    public Int64 TxId { get; set; }
    public TransactionStatus Status { get; set; }
    public List<RollbackAction> RollbackActions { get; } = new();
    
    public void RegisterRollback(RollbackAction action)
    {
        RollbackActions.Add(action);
    }
}
```

### 5.2 回滚流程

```csharp
public void Rollback()
{
    // 按逆序执行回滚动作（后进先出）
    for (var i = RollbackActions.Count - 1; i >= 0; i--)
    {
        RollbackActions[i]();
    }
    
    Status = TransactionStatus.Aborted;
    _activeTransactions.TryRemove(TxId, out _);
}
```

### 5.3 回滚动作示例

```csharp
// INSERT 的回滚动作：删除插入的行版本
tx.RegisterRollback(() =>
{
    _versionChain.Remove(primaryKey, rowVersion);
});

// UPDATE 的回滚动作：恢复旧版本的 DeletedByTx 为 NULL
tx.RegisterRollback(() =>
{
    oldVersion.DeletedByTx = null;
});

// DELETE 的回滚动作：恢复被删除的行版本
tx.RegisterRollback(() =>
{
    rowVersion.DeletedByTx = null;
});
```

## 6. CRUD 操作与 MVCC

### 6.1 INSERT

```csharp
public void Insert(Object primaryKey, Byte[] payload, Transaction tx)
{
    // 1. 创建新行版本
    var rowVersion = new RowVersion
    {
        CreatedByTx = tx.TxId,
        DeletedByTx = null,
        Payload = payload
    };
    
    // 2. 写入 WAL
    _walWriter.WriteInsert(tx.TxId, primaryKey, payload);
    
    // 3. 添加到版本链
    _versionChain.Add(primaryKey, rowVersion);
    
    // 4. 注册回滚动作
    tx.RegisterRollback(() => _versionChain.Remove(primaryKey, rowVersion));
}
```

### 6.2 UPDATE

```csharp
public void Update(Object primaryKey, Byte[] newPayload, Transaction tx)
{
    // 1. 查找可见的行版本
    var oldVersion = _versionChain.Get(primaryKey)
        .FirstOrDefault(v => IsVisible(v, tx));
    
    if (oldVersion == null)
        throw new NovaException(ErrorCode.RowNotFound, "行不存在");
    
    // 2. 标记旧版本为已删除
    oldVersion.DeletedByTx = tx.TxId;
    
    // 3. 创建新版本
    var newVersion = new RowVersion
    {
        CreatedByTx = tx.TxId,
        DeletedByTx = null,
        Payload = newPayload
    };
    
    // 4. 写入 WAL
    _walWriter.WriteUpdate(tx.TxId, primaryKey, oldVersion.Payload, newPayload);
    
    // 5. 添加新版本到版本链
    _versionChain.Add(primaryKey, newVersion);
    
    // 6. 注册回滚动作
    tx.RegisterRollback(() =>
    {
        oldVersion.DeletedByTx = null; // 恢复旧版本
        _versionChain.Remove(primaryKey, newVersion); // 移除新版本
    });
}
```

### 6.3 DELETE

```csharp
public void Delete(Object primaryKey, Transaction tx)
{
    // 1. 查找可见的行版本
    var rowVersion = _versionChain.Get(primaryKey)
        .FirstOrDefault(v => IsVisible(v, tx));
    
    if (rowVersion == null)
        throw new NovaException(ErrorCode.RowNotFound, "行不存在");
    
    // 2. 标记为已删除
    rowVersion.DeletedByTx = tx.TxId;
    
    // 3. 写入 WAL
    _walWriter.WriteDelete(tx.TxId, primaryKey);
    
    // 4. 注册回滚动作
    tx.RegisterRollback(() => rowVersion.DeletedByTx = null);
}
```

### 6.4 SELECT

```csharp
public List<Byte[]> Select(Transaction tx)
{
    var result = new List<Byte[]>();
    
    foreach (var (primaryKey, versions) in _versionChain)
    {
        // 查找对当前事务可见的版本
        var visibleVersion = versions.FirstOrDefault(v => IsVisible(v, tx));
        
        if (visibleVersion != null)
        {
            result.Add(visibleVersion.Payload);
        }
    }
    
    return result;
}
```

## 7. 并发控制

### 7.1 写写冲突

两个事务同时更新同一行时，后提交的事务失败：

```
事务 A (TxId=100): UPDATE users SET age=26 WHERE id=1
事务 B (TxId=101): UPDATE users SET age=27 WHERE id=1

执行顺序:
1. 事务 A 执行 UPDATE → 旧版本 DeletedByTx=100，新版本 CreatedByTx=100
2. 事务 B 执行 UPDATE → 查找可见版本，发现旧版本 DeletedByTx=100（事务 A 未提交）→ 抛出冲突异常
3. 事务 A 提交成功
4. 事务 B 回滚
```

### 7.2 读写并发

读操作不阻塞写操作，写操作不阻塞读操作：

```
事务 A (TxId=100): SELECT * FROM users WHERE id=1
事务 B (TxId=101): UPDATE users SET age=27 WHERE id=1

执行顺序:
1. 事务 A 读取 → 读到旧版本（age=26）
2. 事务 B 写入 → 创建新版本（age=27），标记旧版本 DeletedByTx=101
3. 事务 A 再次读取 → 仍读到旧版本（age=26，因为事务 B 未提交）
4. 事务 B 提交
5. 事务 A 再次读取 → 读到新版本（age=27）
```

### 7.3 乐观并发控制

NovaDb 采用乐观并发控制：
- 读操作不加锁
- 写操作通过 MVCC 检测冲突
- 冲突时回滚后来的事务

适合读多写少的场景。

## 8. 性能优化

### 8.1 版本链压缩

定期扫描版本链，移除不可见的旧版本：

```csharp
public void CompactVersionChain()
{
    var minActiveTxId = GetMinActiveTransactionId();
    
    foreach (var (primaryKey, versions) in _versionChain)
    {
        // 移除所有 DeletedByTx < minActiveTxId 的版本
        versions.RemoveAll(v => v.DeletedByTx != null && v.DeletedByTx < minActiveTxId);
    }
}
```

### 8.2 批量提交

多个小事务可合并为一个大事务，减少事务开销：

```csharp
using var tx = _txManager.BeginTransaction();

foreach (var row in rows)
{
    table.Insert(row.PrimaryKey, row.Payload, tx);
}

tx.Commit();
```

### 8.3 只读事务优化

只读事务可跳过回滚动作注册：

```csharp
public Transaction BeginTransaction(Boolean readOnly = false)
{
    var tx = new Transaction
    {
        TxId = Interlocked.Increment(ref _nextTxId),
        Status = TransactionStatus.Active,
        ReadOnly = readOnly
    };
    
    if (!readOnly)
    {
        _activeTransactions.TryAdd(tx.TxId, tx);
    }
    
    return tx;
}
```

## 9. 设计决策

### D1: Read Committed 隔离级别
平衡一致性与并发性能，满足大多数业务场景。避免 Repeatable Read / Serializable 的复杂实现与性能开销。

### D2: 乐观并发控制
读多写少的场景下性能优于悲观锁。写写冲突时回滚后来的事务，保证数据一致性。

### D3: 单向版本链
每个主键对应一个单向链表，简化实现。无需维护双向指针或复杂的版本树。

### D4: 回滚动作列表
每个写操作注册回滚动作，回滚时按逆序执行。简单直观，易于理解和维护。

### D5: 事务 ID 单调递增
保证全局有序，简化可见性判断。无需维护事务快照或读视图。

## 10. 未来扩展

### 10.1 支持 Repeatable Read
- 引入事务快照（Snapshot）
- 事务开始时记录活跃事务集合
- 可见性判断基于快照而非当前活跃事务

### 10.2 支持 Serializable
- 引入谓词锁（Predicate Lock）或序列化验证
- 检测读写依赖关系，避免幻读

### 10.3 长事务优化
- 长事务可能导致版本链无限增长
- 支持长事务中断与恢复
- 限制活跃事务最大数量

### 10.4 分布式事务
- 支持两阶段提交（2PC）
- 支持跨节点事务协调
- 集成外部事务管理器（如 XA 协议）
