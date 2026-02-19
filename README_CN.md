# NewLife.NovaDb

[![English](https://img.shields.io/badge/lang-English-blue.svg)](README.md) [![简体中文](https://img.shields.io/badge/lang-简体中文-red.svg)](README_CN.md) [![繁體中文](https://img.shields.io/badge/lang-繁體中文-orange.svg)](README_TW.md) [![Français](https://img.shields.io/badge/lang-Français-green.svg)](README_FR.md) [![Deutsch](https://img.shields.io/badge/lang-Deutsch-yellow.svg)](README_DE.md) [![日本語](https://img.shields.io/badge/lang-日本語-purple.svg)](README_JA.md) [![한국어](https://img.shields.io/badge/lang-한국어-brightgreen.svg)](README_KO.md) [![Русский](https://img.shields.io/badge/lang-Русский-lightgrey.svg)](README_RU.md) [![Español](https://img.shields.io/badge/lang-Español-yellow.svg)](README_ES.md) [![Português](https://img.shields.io/badge/lang-Português-blue.svg)](README_PT.md)

以 **C#** 实现，运行于 **.NET 平台**（支持 .NET Framework 4.5 ~ .NET 10）的中大型混合数据库，支持嵌入式/服务器双模，融合关系型、时序、消息队列、NoSQL(KV) 能力。

## 产品介绍

`NewLife.NovaDb`（简称 `Nova`）是 NewLife 生态核心基础设施，面向 .NET 应用的一体化数据引擎。通过裁剪大量冷门能力（如存储过程/触发器/窗口函数等），换取更高的读写性能与更低的运维成本；数据量逻辑上无上限（受磁盘与切分策略约束），可替代 SQLite/MySQL/Redis/TDengine 在特定场景的使用。

### 核心特性

- **双部署模式**：
  - **嵌入模式**：像 SQLite 一样以库的形式运行，数据存储在本地文件夹，零配置
  - **服务器模式**：独立进程 + TCP 协议，像 MySQL 一样网络访问；支持集群部署与主从同步（一主多从）
- **文件夹即数据库**：拷贝文件夹即可完成迁移/备份，无需 dump/restore 流程。每表独立文件组（`.data`/`.idx`/`.wal`）。
- **四引擎融合**：
  - **Nova Engine**（通用关系型）：SkipList 索引 + MVCC 事务（Read Committed），支持 CRUD、SQL 查询、JOIN
  - **Flux Engine**（时序 + MQ）：按时间分片 Append Only，支持 TTL 自动清理、Redis Stream 风格消费组 + Pending + Ack
  - **KV 模式**（逻辑视图）：复用 Nova Engine，API 屏蔽 SQL 细节，每行 `Key + Value + TTL`
  - **ADO.NET Provider**：嵌入/服务器自动识别，兼容 XCode ORM 原生集成
- **动态冷热分离索引**：热数据完整加载至物理内存（SkipList 节点），冷数据卸载至 MMF 仅保留稀疏目录。1000 万行表仅查最新 1 万行时，内存占用 < 20MB。
- **纯托管代码**：不依赖 Native 组件（纯 C#/.NET），便于跨平台与受限环境部署。

### 存储引擎

| 引擎 | 数据结构 | 适用场景 |
|------|----------|----------|
| **Nova Engine** | SkipList（内存+MMF 冷热分离） | 通用 CRUD、配置表、业务订单、用户数据 |
| **Flux Engine** | 按时间分片（Append Only） | IoT 传感器、日志收集、内部消息队列、审计日志 |
| **KV 模式** | Nova 表逻辑视图 | 分布式锁、缓存、会话存储、计数器、配置中心 |

### 数据类型

| 类别 | SQL 类型 | C# 映射 | 说明 |
|------|----------|---------|------|
| 布尔 | `BOOL` | `Boolean` | 1 字节 |
| 整数 | `INT` / `LONG` | `Int32` / `Int64` | 4/8 字节 |
| 浮点 | `DOUBLE` | `Double` | 8 字节 |
| 定点 | `DECIMAL` | `Decimal` | 128 位，统一精度 |
| 字符串 | `STRING(n)` / `STRING` | `String` | UTF-8，可指定长度 |
| 二进制 | `BINARY(n)` / `BLOB` | `Byte[]` | 可指定长度 |
| 时间 | `DATETIME` | `DateTime` | 精确到 Ticks（100 纳秒） |
| 地理编码 | `GEOPOINT` | 自定义结构 | 经纬度坐标（规划中） |
| 向量 | `VECTOR(n)` | `Single[]` | AI 向量检索（规划中） |

### SQL 能力

已实现标准 SQL 子集，覆盖约 60% 常用业务场景：

| 功能 | 状态 | 说明 |
|------|------|------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX/DATABASE，ALTER TABLE（ADD/MODIFY/DROP COLUMN、COMMENT），含 IF NOT EXISTS、PRIMARY KEY、UNIQUE、ENGINE |
| DML | ✅ | INSERT（多行）、UPDATE、DELETE、UPSERT |
| 查询 | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| 聚合 | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN（Nested Loop），支持表别名 |
| 参数化 | ✅ | @param 占位符 |
| 事务 | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| SQL 函数 | ⚠️ | 字符串/数值/日期/转换/条件函数（规划中） |
| 子查询 | ⚠️ | IN/EXISTS（规划中） |
| 高级 | ❌ | 无视图/触发器/存储过程/窗口函数 |

### MQ 能力（Flux Engine）

参考 Redis Stream 的消费组模型：

- **消息 ID**：时间戳 + 序列号（同毫秒自增），全局有序
- **消费组**：`Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **可靠性**：At-Least-Once，读取后进入 Pending，业务成功后 Ack
- **数据保留**：支持 TTL（按时间/文件大小自动删除旧分片）
- **延迟消息**：指定 `DelayTime`/`DeliverAt`（规划中）
- **死信队列**：消费失败自动进入 DLQ（规划中）
- **阻塞读取**：长轮询 + 超时（规划中）

### KV 能力

- `Get(key)` / `Set(key, value, ttl)` / `Delete(key)` / `Exists(key)`
- 惰性删除（读时检查过期） + 后台清理（`CleanupExpired()`）
- `Add(key, value, ttl)`：仅当 key 不存在时添加（规划中）
- `Inc(key, delta, ttl)`：原子递增（规划中）

## 数据安全与 WAL 模式

NovaDb 提供三种 WAL 持久化策略：

| 模式 | 说明 | 适用场景 |
|------|------|----------|
| `FULL` | 同步落盘，每次提交立即刷盘 | 金融/交易场景，最强数据安全 |
| `NORMAL` | 异步 1s 刷盘（默认） | 大多数业务场景，平衡性能与安全 |
| `NONE` | 全异步，不主动刷盘 | 临时数据/缓存场景，最高吞吐 |

> 只要不选择同步模式（`FULL`），就意味着接受在崩溃/断电等场景下可能发生数据丢失。

## 规划能力（Roadmap）

| 版本 | 计划内容 |
|------|----------|
| **v1.0**（已完成） | 嵌入式+服务器双模、Nova/Flux/KV 引擎、SQL DDL/DML/SELECT/JOIN、事务/MVCC、WAL/恢复、冷热分离、分片、MQ 消费组、ADO.NET Provider、集群主从同步 |
| **v1.1** | P0 级 SQL 函数（字符串/数值/日期/转换/条件约 30 个函数） |
| **v1.2** | MQ 阻塞读取、KV Add/Inc 操作、P1 级 SQL 函数 |
| **v1.3** | MQ 延迟消息、死信队列 |
| **v2.0** | GeoPoint 地理编码 + Vector 向量类型（AI 向量检索）、可观测性与管理工具 |

## 对比定位

NovaDb 不追求完整 SQL92 标准，而是覆盖 80% 业务常用子集，换取以下差异化能力：

| 差异化 | 说明 |
|--------|------|
| **纯 .NET 托管** | 无 Native 依赖，部署即 xcopy，与 .NET 应用同进程零序列化开销 |
| **嵌入+服务双模** | 开发调试嵌入如 SQLite，生产部署独立服务如 MySQL，同一套 API |
| **文件夹即数据库** | 拷贝文件夹完成迁移/备份，无需 dump/restore |
| **冷热分离索引** | 1000 万行表仅查热点时内存 < 20MB，冷数据自动卸载至 MMF |
| **四引擎融合** | 单一组件覆盖 SQLite + TDengine + Redis 常见场景，减少运维组件数 |
| **NewLife 原生集成** | XCode ORM + ADO.NET 直接适配，无需第三方驱动 |
