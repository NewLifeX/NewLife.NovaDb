# NewLife.NovaDb 实现执行计划（从 需求规格说明书 落地到代码）

本文档基于 `Doc/需求规格说明书.md` 拆分出可执行、可迭代交付的实现计划。目标是让后续每一步都能形成可运行的增量（库可被引用、能创建库/表、能写入/查询、最终具备服务端/驱动/集群）。

## 里程碑（Milestones）

- **M0 仓库基线** ✅：解决方案结构清晰、统一编码风格、测试可运行。
- **M1 本地嵌入式最小可用（单表单索引）** ✅：文件夹即数据库 + 基础读写 + WAL + 恢复。
- **M2 事务/MVCC + 基础 SQL（DDL/DML/Select）** ✅：可并发读写、Read Committed。
- **M3 Nova Engine（SkipList）+ 索引冷热分离** ✅：热点内存索引 + 冷段 MMF 目录。
- **M4 自动切分/分片** ✅：单表逻辑无上限，分片路由与后台维护。
- **M5 Flux Engine（时序 + MQ）** ✅：时间分片 Append Only + Stream/消费组。
- **M6 KV 视图 + TTL** ✅：KV API/SQL 视图、按行 TTL。
- **M7 服务器模式 + ADO.NET** ✅：TCP 二进制协议 + 驱动自动识别。
- **M8 集群与主从同步** ✅：读扩展与容灾，最小一致性策略落地。

## 设计与实现原则

1. **先跑通再优化**：先实现正确的数据结构与持久化，再通过基准优化。
2. **模块边界清晰**：存储（页/编码/MMF/WAL）、事务（MVCC）、执行（SQL）、通信（协议/驱动）分层。
3. **纯托管**：不引入 native 依赖；跨平台优先（.NET 8）。
4. **可测试**：每个模块都要有对应的单元测试与恢复测试（崩溃恢复/一致性）。

## 模块拆分（与需求对应）

- **Core**：配置、错误码、公共类型（数据类型映射/值编码）、诊断。
- **Storage**：文件布局、MMF、页/块、记录编码、校验。
- **WAL**：日志、检查点、恢复。
- **Tx**：事务、MVCC、锁/冲突策略（按 Read Committed 目标）。
- **Engine.Nova**：表/分片、SkipList 热索引、冷索引目录。
- **Engine.Flux**：时间分片文件、Append Only、TTL、Stream/MQ。
- **Sql**：解析、绑定、优化（基础）、执行器。
- **Server**：二进制协议、会话、认证（可后置）、集群复制。
- **Client**：ADO.NET Provider。

---

## 详细执行步骤（按可交付增量分解）

### 1) 仓库与解决方案结构（M0）✅

**目标**：明确项目边界与 TFMs，保证构建/测试稳定。

- 统一 `Directory.Build.props/targets`（如存在）中的：LangVersion、Nullable、TreatWarningsAsErrors 策略（按仓库现状最小调整）。
- 约定工程目录（建议）：
  - `src/NewLife.NovaDb`（主库，.NET 8）
  - `tests/*`（xUnit/NUnit 按仓库现状）
  - `samples/*`（后期）
- 建立基础日志/诊断抽象；优先复用仓库已有基础设施（如 NewLife 系列库）。

**交付物**：
- 解决方案可在本机/CI 构建。
- 测试工程可运行（即便暂时为空）。

### 2) 核心公共契约（M0 -> M1）✅

**目标**：确定后续各模块共享的关键抽象与数据结构。

- `DbOptions`：库路径、WAL 模式（FULL/NORMAL/NONE）、页大小、热数据窗口、分片策略等。
- `IDataCodec`：基础类型（bool/int/long/double/decimal/string/byte[]/datetime）的二进制编码（严格映射 C# 类型）。
- `IFileSystem`（可选）：用于测试注入。
- 统一异常与错误码（解析失败、事务冲突、校验失败、文件损坏等）。

**交付物**：
- 核心类型可被引用；编码/解码可单测。

### 3) 文件夹即数据库 + 文件布局（M1）✅

**目标**：把“表文件组（`.data`, `.idx`, `.wal`）”落地到文件命名与目录结构。

- `DatabaseDirectory`：创建/打开/列举库。
- `TableDirectory`：创建/打开/列举表。
- 表文件组命名约定：
  - 数据分片：`{TableName}/{ShardId}.data`
  - 索引文件：`{TableName}/{IndexName}.idx`
  - WAL：`{TableName}/{ShardId}.wal` 或库级 WAL（需决策，优先表级/分片级便于并发）。
- 元数据位置：库级系统表文件夹（如 `_sys/`）。

**交付物**：
- 可创建空库/空表；目录结构符合约定。

### 4) 页/块/记录编码（M1）✅

**目标**：定义 `.data/.idx/.wal` 的二进制格式（版本化、可校验、可扩展）。

- 全局：文件头（Magic、Version、PageSize、CreatedAt、OptionsHash）。
- 页面：页头（PageId、Type、LSN、Checksum）+ Payload。
- 记录：变长记录（Length + Flags + Columns/Value）。
- 校验：CRC32/xxHash（按仓库依赖选择）；写入链路必须带校验，恢复必须验证。

**交付物**：
- 纯内存编码/解码单测。
- 文件写入后可读回并验证一致性。

### 5) MMF 读写层（M1）✅

**目标**：实现基于 MemoryMappedFile 的分页访问与缓存策略雏形。

- `MmfPager`：打开文件 -> 映射 -> 读页/写页（按页大小对齐）。
- `PageCache`（最小版）：热点页缓存（LRU/Clock 可后置）。
- 写入策略：先写 WAL（若开启）后写数据页。

**交付物**：
- `.data` 文件可随机读写页。

### 6) WAL 日志、检查点与恢复（M1）✅

**目标**：满足 NORMAL(异步 1s)/FULL(同步) 的 durability 语义，并能崩溃恢复。

- WAL 记录类型：BeginTx/UpdatePage/CommitTx/AbortTx/Checkpoint。
- `WalWriter`：append 写、按模式 flush。
- `Recovery`：扫描 WAL -> redo/undo（按实现策略选择）。
- 检查点：周期性把脏页落盘并截断 WAL。

**交付物**：
- 崩溃恢复测试：模拟写入中断后重启仍一致。

### 7) 事务与 MVCC（M2）✅

**目标**：实现 Read Committed 的 MVCC。

- `TransactionManager`：分配 TxId/CommitTs。
- 行版本结构：`(CreatedByTx, DeletedByTx, Payload)` 或时间戳版本链。
- 可见性规则：Read Committed（读取时仅看已提交）。
- 写冲突：同主键/同记录的写写冲突检测。

**交付物**：
- 多事务并发读写测试（含回滚）。

### 8) Nova Engine：表模型 + SkipList 热索引（M2 -> M3）✅

**目标**：先实现“能用”的单表 CRUD，再逐步引入冷热分离。

- 表模型：Schema（列、主键、二级索引）。
- 内存索引：主键 SkipList（或仓库已有 SkipList 实现）。
- 写入路径：Insert/Upsert/Update/Delete -> 事务 -> WAL -> 写 `.data`。

**交付物**：
- 单表主键查询性能可接受；基础 CRUD 通过测试。

### 9) 索引冷热分离 + LRU 淘汰（M3）✅

**目标**：实现“热段完整索引在内存，冷段卸载至 MMF”的核心差异化。

- 热段定义：按最近访问时间窗口（如 10 分钟）或页热度。
- 冷段稀疏目录：例如每 N 行一个锚点（Key -> page/offset）。
- 热索引重建：由 `.idx` 指引按需加载热点范围。
- 后台维护：定期扫描热度，淘汰冷段。

**交付物**：
- 热点查询内存受控的集成测试（基于行数/访问模式的断言）。

### 10) 自动切分与分片路由（M4）✅

**目标**：实现单表逻辑无上限的“分片（Shard）”机制。

- 切分策略：按大小/时间/行数（先实现按大小）。
- `ShardManager`：选择写入分片；读路径定位分片集合。
- 元数据更新：系统表记录分片范围/统计信息。
- 后台任务：合并小分片（可后置）、重建索引。

**交付物**：
- 大量写入自动生成多个 `.data` 分片，读写正确。

### 11) DDL 与系统表（M2 -> M4）✅

**目标**：能用 SQL 创建/修改对象，并持久化元数据。

- 系统表：
  - `_sys.tables`、`_sys.columns`、`_sys.indexes`、`_sys.shards`、`_sys.streams` 等（按模块逐步增加）。
- DDL：Create/Alter/Drop Table/Index。
- 版本迁移：元数据 schema 版本号（为后续升级准备）。

**交付物**：
- DDL 可在重启后保持一致。

### 12) DML 执行管线（M2 -> M4）✅

**目标**：把 SQL DML 跑通到表引擎。

- Insert/Update/Delete/Upsert 的语义与主键约束。
- 参数化执行：为 ADO.NET 做准备。
- 结果集：DataReader 风格抽象。

**交付物**：
- DML SQL 集成测试。

### 13) 查询执行器（M2 -> M4）✅

**目标**：支持 Select/Where/Order/GroupBy（需求“中等”覆盖）。

- 解析：优先复用现有 SQL parser（若仓库已有），否则实现子集 parser。
- 执行迭代器模型：Filter/Project/Sort/Aggregate。
- 索引选择：优先主键/唯一索引；二级索引后置。

**交付物**：
- Select 查询覆盖测试。

### 14) Join 与子查询（M4）⚠️

**目标**：先落地 Nested Loop Join + IN/EXISTS 基础子查询。

- Join：Nested Loop（可带简单索引加速）。
- 子查询：`IN/EXISTS` 转换为半连接/迭代求值。

**交付物**：
- Join/子查询正确性测试。

### 15) Flux Engine：时序存储（M5）✅

**目标**：按时间分片 Append Only 的 `.data` 布局，支持时间范围查询。

- 分片命名：`{StreamOrTable}/{yyyyMMddHH}.data`（按配置粒度）。
- 写入：追加写，尽量顺序 IO。
- TTL：按时间/文件大小删除旧分片。

**交付物**：
- 时间范围查询与 TTL 删除测试。

### 16) MQ：Stream/消费组/Pending（M5）✅

**目标**：参考 Redis Stream 语义。

- 消息 ID：`timestamp-seq`，同毫秒自增。
- 消费组：记录 group offset。
- Pending：读取后进入 pending，Ack 后移除。
- 阻塞读取：长轮询 + 超时。

**交付物**：
- At-Least-Once 行为测试（含崩溃重试导致重复的场景）。

### 17) KV 视图与按行 TTL（M6）✅

**目标**：用 Nova Engine 表实现 KV 模式，并提供 API 屏蔽 SQL。

- KV 表 schema：`Key string PK, Value byte[], Ttl datetime?`。
- API：`Get/Set/Del/Exists`。
- TTL 清理：读时惰性删除 + 后台扫描。

**交付物**：
- KV API 与 TTL 测试。

### 18) 服务器模式 TCP + 二进制协议（M7）✅

**目标**：实现独立进程提供数据库服务。

- 会话：连接、握手、认证（可后置）。
- 请求：Prepare/Execute/Fetch/Close。
- 传输：无压缩的二进制协议；粘包拆包；心跳。

**交付物**：
- 客户端可远程执行基本 SQL。

### 19) ADO.NET Provider（M7）✅

**目标**：让现有生态（XCode/通用 ADO）可接入。

- `DbConnection/DbCommand/DbDataReader/DbParameter`。
- 自动识别嵌入/服务模式（按连接串）。
- 事务支持：`DbTransaction` 映射。

**交付物**：
- ADO.NET 标准测试（或最小兼容集）。

### 20) 集群与主从同步（M8）✅

**目标**：最小可用的复制链路。

- 同步单位：WAL 流复制（主发送 WAL，备重放）。
- 拓扑：一主多从；读请求分配策略（后置）。
- 断点续传：按 LSN/Offset。
- 一致性：先实现最终一致 + 可选强一致读（读主）。

**交付物**：
- 主从一致性测试（含断线重连）。

### 21) 可观测性与管理工具（贯穿）⚠️

- 系统表查询状态（队列、消费组、pending 等）。
- Metrics：WAL 延迟、页命中率、热段大小、GC/内存。
- CLI：创建库、导入导出、检查修复。

### 22) 测试与基准（贯穿）✅

- 正确性：CRUD、事务隔离、恢复一致性、DDL 持久化。
- 性能：热点查询、连续写入、随机读、分片切换、MQ 吞吐。

### 23) 文档与示例（贯穿）✅

- README：快速开始（嵌入/服务/驱动）。
- 示例：Flux MQ、KV、分片表。

### 24) 发布与 CI（贯穿）⚠️

- NuGet 包：`NewLife.NovaDb`、`NewLife.NovaDb.Client`、`NewLife.NovaDb.Server`（按实际拆分）。
- CI：构建、测试、打包、发布。

---

## 风险清单（先期关注）

- **文件格式演进**：一旦发布就要考虑兼容；必须有版本号与迁移策略。
- **恢复与一致性**：WAL/Checkpoint 是正确性基石；建议优先做“崩溃恢复测试”。
- **冷热分离复杂度**：需要明确“目录结构”和“热段重建”的精确定义，避免后期推翻。
- **SQL 子集边界**：尽早在文档中明确 v1 支持的语法范围。

## v1 建议范围（可落地的最小闭环）

- 嵌入式模式
- Nova Engine 单引擎
- WAL NORMAL/FULL
- Read Committed MVCC
- DDL：Create/Drop Table/Index
- DML：Insert/Update/Delete/Upsert
- 查询：Select/Where/Order/GroupBy

> 以上完成后再引入 Flux/MQ/KV/服务端/集群，能有效降低一次性复杂度。

---

## 完成状态总结

| 步骤 | 名称 | 里程碑 | 状态 | 说明 |
|------|------|--------|------|------|
| 1 | 仓库与解决方案结构 | M0 | ✅ 完成 | .NET 8 项目结构、测试工程 |
| 2 | 核心公共契约 | M0→M1 | ✅ 完成 | DbOptions、IDataCodec、DataType、ErrorCode |
| 3 | 文件夹即数据库 + 文件布局 | M1 | ✅ 完成 | DatabaseDirectory、TableDirectory |
| 4 | 页/块/记录编码 | M1 | ✅ 完成 | FileHeader、PageHeader、校验 |
| 5 | MMF 读写层 | M1 | ✅ 完成 | MmfPager、PageCache |
| 6 | WAL 日志与恢复 | M1 | ✅ 完成 | WalWriter、WalRecovery、崩溃恢复 |
| 7 | 事务与 MVCC | M2 | ✅ 完成 | TransactionManager、RowVersion、Read Committed |
| 8 | Nova Engine 表模型 | M2→M3 | ✅ 完成 | NovaTable、SkipList、CRUD |
| 9 | 索引冷热分离 | M3 | ✅ 完成 | HotIndexManager、ColdIndexDirectory |
| 10 | 自动切分与分片 | M4 | ✅ 完成 | ShardManager、ShardInfo |
| 11 | DDL 与系统表 | M2→M4 | ✅ 完成 | SQL CREATE/DROP TABLE/INDEX |
| 12 | DML 执行管线 | M2→M4 | ✅ 完成 | SQL INSERT/UPDATE/DELETE |
| 13 | 查询执行器 | M2→M4 | ✅ 完成 | SELECT/WHERE/ORDER/GROUP BY/HAVING/LIMIT |
| 14 | Join 与子查询 | M4 | ⚠️ 基础 | 预留扩展点，基础框架已就绪 |
| 15 | Flux Engine 时序存储 | M5 | ✅ 完成 | FluxEngine、时间分片 |
| 16 | MQ Stream/消费组 | M5 | ✅ 完成 | StreamManager、ConsumerGroup、Pending |
| 17 | KV 视图 + TTL | M6 | ✅ 完成 | KvStore、TTL 清理 |
| 18 | 服务器 TCP + 协议 | M7 | ✅ 完成 | NovaDbServer、NovaDbProtocol |
| 19 | ADO.NET Provider | M7 | ✅ 完成 | NovaDbConnection/Command/DataReader/Parameter |
| 20 | 集群与主从同步 | M8 | ✅ 完成 | ReplicationManager、ReplicaClient |
| 21 | 可观测性与管理工具 | 贯穿 | ⚠️ 基础 | 系统表框架已就绪 |
| 22 | 测试与基准 | 贯穿 | ✅ 完成 | 251 个单元测试全部通过 |
| 23 | 文档与示例 | 贯穿 | ✅ 完成 | 架构设计文档、需求规格说明书 |
| 24 | 发布与 CI | 贯穿 | ⚠️ 基础 | NuGet 包结构已就绪 |

### 已实现的 SQL 能力

| SQL 功能 | 状态 | 说明 |
|----------|------|------|
| CREATE TABLE | ✅ | 含 IF NOT EXISTS、PRIMARY KEY、NOT NULL |
| DROP TABLE | ✅ | 含 IF EXISTS |
| CREATE INDEX | ✅ | 含 UNIQUE |
| DROP INDEX | ✅ | |
| INSERT | ✅ | 含多行插入、列名指定 |
| UPDATE | ✅ | 含 WHERE 条件 |
| DELETE | ✅ | 含 WHERE 条件 |
| SELECT | ✅ | 列投影、别名、* 通配符 |
| WHERE | ✅ | =, !=, <, >, <=, >=, AND, OR, NOT, LIKE, IS NULL |
| ORDER BY | ✅ | ASC/DESC |
| GROUP BY | ✅ | 含聚合函数 |
| HAVING | ✅ | |
| LIMIT/OFFSET | ✅ | |
| COUNT/SUM/AVG/MIN/MAX | ✅ | 聚合函数 |
| 参数化查询 | ✅ | @param 占位符 |
| JOIN | ⚠️ 待实现 | 框架已预留 |
| 子查询 | ⚠️ 待实现 | 框架已预留 |
