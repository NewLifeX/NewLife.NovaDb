# NewLife.NovaDb - WAL 与 Binlog 架构

## 1. 概述

NovaDb 采用双日志系统：
- **WAL（Write-Ahead Log）**：表级物理日志，用于崩溃恢复与持久性保证
- **Binlog（Binary Log）**：数据库级逻辑日志，用于主从复制、增量备份与审计追踪

两者职责正交、互不依赖。

## 2. WAL 日志层

### 2.1 职责

- 记录写事务造成的物理变更（页面级）
- 支持崩溃后恢复到一致状态
- 支持 Checkpoint（将脏页刷盘后回收 WAL）
- 提供持久性保证（ACID 中的 D）

### 2.2 WAL 模式

| WalMode | 持久性 | 性能 | 刷盘策略 | 数据丢失风险 |
|---------|-------|------|---------|-------------|
| Full | 最强 | 最慢 | 每次事务提交同步刷盘（fsync） | 崩溃后零数据丢失 |
| Normal | 适中 | 适中 | 准实时异步刷盘（后台线程，约 1 秒） | 崩溃后可能丢失最后几秒数据 |
| None | 无保证 | 最快 | 不写 WAL | 崩溃后数据无法恢复 |

### 2.3 WAL 文件组织

详见[存储架构.md](存储架构.md)，此处仅概述：

- 每个表有独立的 WAL 文件：`{table_name}.wal`
- WAL 文件格式：文件头 + WAL 记录序列
- WAL 记录类型：`BeginTransaction`, `Write`, `Delete`, `Commit`, `Rollback`

### 2.4 WAL 生命周期

```
1. BeginTransaction → WAL 记录 BeginTransaction
2. Insert/Update/Delete → WAL 记录 Write/Delete
3. Commit → WAL 记录 Commit → 根据 WalMode 刷盘
4. Checkpoint → 脏页刷盘 → 截断 WAL
5. 崩溃恢复 → 读取 WAL → 重放未提交事务（回滚）或已提交事务（重做）
```

### 2.5 Checkpoint 机制

- **触发时机**：
  - WAL 文件大小超过阈值（如 10MB）
  - 定期后台任务（如每 5 分钟）
  - 手动触发（SQL: `CHECKPOINT`）
- **流程**：
  1. 暂停新事务
  2. 等待活跃事务完成
  3. 将所有脏页刷盘
  4. 截断 WAL 文件
  5. 恢复正常操作

### 2.6 崩溃恢复流程

```
启动时检查 WAL:
  1. 读取 WAL 文件头，验证完整性
  2. 扫描所有 WAL 记录，构建事务状态表
  3. 对于已提交事务：重放 Write/Delete 操作（Redo）
  4. 对于未提交事务：丢弃所有变更（Undo）
  5. 恢复完成，数据库进入一致状态
```

### 2.7 WAL 核心类

| 类名 | 职责 |
|------|------|
| `WalWriter` | 写入 WAL 记录，管理 WAL 文件 |
| `WalRecord` | WAL 记录结构（类型、事务 ID、数据） |
| `WalRecovery` | 崩溃恢复逻辑，重放/回滚事务 |
| `WalCheckpointer` | Checkpoint 管理，脏页刷盘 |

## 3. Binlog 日志层

### 3.1 职责

- 记录已提交事务的逻辑操作（INSERT/UPDATE/DELETE/DDL）
- 支持主从复制（传输给从节点重放）
- 支持增量备份（定期备份 Binlog 文件）
- 支持审计追踪（记录所有数据变更历史）

### 3.2 与 WAL 的区别

| 维度 | WAL | Binlog |
|------|-----|--------|
| 作用域 | 表级 | 数据库级 |
| 日志类型 | 物理日志（页面级） | 逻辑日志（行级） |
| 主要用途 | 崩溃恢复、持久性 | 主从复制、备份、审计 |
| 生命周期 | Checkpoint 后回收 | 按保留策略清理（天数/总大小） |
| 必须启用 | 嵌入模式可选（WalMode） | 网络模式默认启用，嵌入模式可选 |

### 3.3 Binlog 文件组织

详见[存储架构.md](存储架构.md)，此处仅概述：

- Binlog 文件位于数据库目录下的 `binlog/` 子目录
- 文件命名：`binlog.{sequence:D10}`（如 `binlog.0000000001`）
- 文件格式：文件头 + Binlog 记录序列
- 滚动策略：单文件达到 `BinlogMaxFileSize`（默认 256MB）时滚动到新文件

### 3.4 Binlog 记录类型

| 记录类型 | 说明 | 包含字段 |
|---------|------|---------|
| `Insert` | 插入记录 | 表名、主键、所有列值 |
| `Update` | 更新记录 | 表名、主键、更新前列值、更新后列值 |
| `Delete` | 删除记录 | 表名、主键 |
| `CreateTable` | 创建表 | 表名、列定义 |
| `DropTable` | 删除表 | 表名 |
| `AlterTable` | 修改表结构 | 表名、DDL 语句 |

### 3.5 Binlog 写入时机

```
事务提交后:
  1. 事务成功提交（已写入 WAL）
  2. 如果 EnableBinlog = true:
     a. 构造 Binlog 记录（逻辑操作）
     b. 写入 Binlog 文件
     c. 根据配置决定是否同步刷盘
  3. 返回客户端提交成功
```

**注意**：Binlog 写入在事务提交后，即使 Binlog 写入失败也不影响事务本身（事务已提交）。

### 3.6 Binlog 滚动策略

- **触发条件**：当前 Binlog 文件大小 >= `BinlogMaxFileSize`
- **流程**：
  1. 关闭当前 Binlog 文件
  2. 创建新文件：`binlog.{sequence+1}`
  3. 写入新文件头
  4. 继续写入 Binlog 记录

### 3.7 Binlog 保留与清理策略

Binlog 支持两种清理策略（可同时启用）：

| 策略 | 配置项 | 说明 |
|------|-------|------|
| 按时间保留 | `BinlogRetentionDays` | 保留最近 N 天的 Binlog，删除更早的文件 |
| 按总大小限制 | `BinlogMaxTotalSize` | Binlog 总大小超过限制时，删除最旧的文件 |

**清理时机**：
- 后台定期扫描（如每小时）
- Binlog 滚动时检查
- 手动触发（SQL: `PURGE BINLOG BEFORE '{date}'`）

**安全保证**：
- 清理前检查从节点同步位置（LSN），确保所有从节点已同步
- 如果从节点落后太多，拒绝清理并记录警告日志

### 3.8 Binlog 核心类

| 类名 | 职责 |
|------|------|
| `BinlogWriter` | 写入 Binlog 记录，管理 Binlog 文件 |
| `BinlogRecord` | Binlog 记录结构（类型、LSN、数据） |
| `BinlogReader` | 读取 Binlog 文件，用于复制/备份 |
| `BinlogCleaner` | 清理过期 Binlog 文件 |

### 3.9 Binlog 在主从复制中的应用

主节点：
```
1. 写事务提交后，写入 Binlog
2. ReplicationManager 监听 Binlog 新记录
3. 将 Binlog 记录推送给所有从节点
```

从节点：
```
1. ReplicaClient 接收 Binlog 记录
2. 解析 Binlog 记录（Insert/Update/Delete）
3. 在本地数据库重放操作（不写 Binlog，避免循环）
4. 更新同步位置（LSN）
```

## 4. 双日志协同工作流程

### 4.1 写事务完整流程

```
1. 客户端发起写事务（INSERT/UPDATE/DELETE）
2. 事务管理器分配事务 ID
3. 写入 WAL（BeginTransaction）
4. 执行数据变更：
   a. 读取相关页面（可能触发缺页）
   b. 修改页面数据（内存中）
   c. 写入 WAL（Write/Delete）
   d. 标记页面为脏页
5. 提交事务：
   a. 写入 WAL（Commit）
   b. 根据 WalMode 刷盘（Full: 同步刷盘，Normal: 异步刷盘）
6. 如果 EnableBinlog = true:
   a. 写入 Binlog（逻辑操作）
   b. 可选刷盘
7. 返回客户端提交成功
```

### 4.2 崩溃恢复流程

```
数据库启动时:
  1. 检查 WAL 文件:
     a. 读取 WAL 记录
     b. 重放已提交事务（Redo）
     c. 回滚未提交事务（Undo）
     d. 恢复到一致状态
  2. Binlog 不参与崩溃恢复:
     - Binlog 只记录已提交事务
     - WAL 恢复后，Binlog 自然一致
```

### 4.3 主从复制流程

```
主节点:
  1. 写事务提交 → 写入 WAL → 写入 Binlog
  2. ReplicationManager 监听 Binlog
  3. 推送 Binlog 记录给从节点

从节点:
  1. ReplicaClient 接收 Binlog
  2. 重放操作 → 写入 WAL（从节点自己的 WAL）
  3. 更新同步位置（LSN）
  4. 不写 Binlog（避免无限传播）
```

## 5. 性能优化

### 5.1 WAL 批量刷盘

- 在 `Normal` 模式下，多个事务的 WAL 记录可批量刷盘
- 后台刷盘线程每 1 秒或缓冲区满时刷盘
- 减少 fsync 调用次数，提升吞吐量

### 5.2 Binlog 异步写入

- Binlog 写入可配置为异步模式
- 事务提交后立即返回，Binlog 由后台线程写入
- 风险：崩溃后最后几秒的 Binlog 可能丢失（但事务本身已提交）

### 5.3 Binlog 压缩

- 对 Binlog 记录启用压缩（如 LZ4、Snappy）
- 减少磁盘占用与网络传输量
- 在从节点解压后重放

### 5.4 Binlog 分段传输

- 大事务的 Binlog 记录可分段传输
- 避免单次传输过大导致内存压力或超时

## 6. 设计决策

### D1: WAL 表级，Binlog 数据库级
- WAL 表级隔离，减少并发竞争，简化恢复流程
- Binlog 数据库级统一，方便主从复制与备份

### D2: WAL 物理日志，Binlog 逻辑日志
- WAL 记录页面级变更，适合快速恢复
- Binlog 记录行级操作，适合跨版本复制与审计

### D3: Binlog 写入在事务提交后
- 保证 Binlog 只记录已提交事务
- 简化事务回滚逻辑（不需要回滚 Binlog）

### D4: Binlog 文件可安全手工删除
- Binlog 以"文件段"为管理单位，段内自描述
- 运维人员删除旧段释放空间不会导致引擎崩溃
- 代价：落后且未同步到该段的从节点需要全量重建

### D5: 只读模式不写 WAL
- 只读模式禁止写操作，无需 WAL
- 跳过 WAL 初始化，提升启动速度与读性能

## 7. 未来扩展

### 7.1 WAL 并行恢复
- 多个表的 WAL 可并行恢复
- 缩短大型数据库的启动时间

### 7.2 Binlog 增量备份工具
- 定期备份 Binlog 文件到远程存储
- 支持按时间点恢复（PITR）

### 7.3 Binlog 查询接口
- 提供 SQL 接口查询 Binlog 内容
- 支持审计与数据追踪（如 `SELECT * FROM binlog WHERE table='users' AND operation='DELETE'`）

### 7.4 Binlog 过滤与转换
- 支持按表/操作类型过滤 Binlog
- 支持 Binlog 格式转换（如转为 JSON、CSV）
