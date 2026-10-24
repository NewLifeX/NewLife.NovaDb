# NewLife.NovaDb



![GitHub License](https://img.shields.io/github/license/NewLifeX/NewLife.NovaDb?logo=github)
![NuGet Downloads](https://img.shields.io/nuget/dt/NewLife.NovaDb?logo=nuget)
![NuGet](https://img.shields.io/nuget/v/NewLife.NovaDb?logo=nuget)
![NuGet prerelease](https://img.shields.io/nuget/vpre/NewLife.NovaDb?label=dev%20nuget&logo=nuget)
[![test](https://img.shields.io/github/actions/workflow/status/NewLifeX/NewLife.NovaDb/test.yml?branch=master&label=test&logo=github)](https://github.com/NewLifeX/NewLife.NovaDb/actions/workflows/test.yml)
[![locales](https://img.shields.io/badge/locales-11-2ea44f?logo=dotnet)](Doc/多语言本地化.md)
![platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS%20%7C%20Android%20%7C%20iOS-blue)
![arch](https://img.shields.io/badge/arch-x64%20%7C%20arm64%20%7C%20loongarch64%20%7C%20riscv64%20%7C%20s390x-orange)
[![contributors](https://img.shields.io/github/contributors/NewLifeX/NewLife.NovaDb?logo=github)](https://github.com/NewLifeX/NewLife.NovaDb/graphs/contributors)

<!-- lang:start -->
🌐 [English](README.md) | [繁體中文](README.zh-TW.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [한국어](README.ko.md) | [العربية](README.ar.md)
<!-- lang:end -->

> ℹ️ 本中文版与 [English README](README.md) 同批维护；若两者内容不一致，以英文版为对外口径。

以 **C#** 实现，运行于 **.NET 平台**（支持 .NET Framework 4.5 ~ .NET 10）的中大型混合数据库，支持嵌入式/网络双模，融合关系型、时序、消息队列、NoSQL(KV) 能力。

## 产品介绍

`NewLife.NovaDb`（简称 `Nova`）是 NewLife 生态核心基础设施，面向 .NET 应用的一体化数据引擎。通过裁剪大量冷门能力（如存储过程/触发器/窗口函数等），换取更高的读写性能与更低的运维成本；数据量逻辑上无上限（受磁盘与切分策略约束），可替代 SQLite/MySQL/Redis/TDengine 在特定场景的使用。

### 核心特性

- **双部署模式**：
  - **嵌入模式**：像 SQLite 一样以库的形式运行，数据存储在本地文件夹，零配置
  - **网络模式**：独立进程 + TCP 协议，像 MySQL 一样网络访问；支持集群部署与主从同步（一主多从）
- **文件夹即数据库**：拷贝文件夹即可完成迁移/备份，无需 dump/restore 流程。每表独立文件组（`.data`/`.idx`/`.wal`）。
- **四引擎融合**：
  - **Nova Engine**（通用关系型）：SkipList 索引 + MVCC 事务（Read Committed），支持 CRUD、SQL 查询、JOIN
  - **Flux Engine**（时序 + MQ）：按时间分片 Append Only，支持 TTL 自动清理、Redis Stream 风格消费组 + Pending + Ack；列式块封存与类型感知编码（Delta/DeltaDelta/XOR/RLE/字典）+ LZ4 块压缩，Rollup 预聚合与聚合查询自动路由，SESSION/STATE 窗口分组，压缩比通过 `_sys.flux_compression` 上报
  - **KV 模式**（键值存储）：独立 Bitcask 引擎（内存索引 + 磁盘值），支持 TTL、多表隔离
  - **ADO.NET Provider**：嵌入/网络自动识别，兼容 XCode ORM 原生集成
- **动态冷热分离索引**：热数据完整加载至物理内存（SkipList 节点），冷数据卸载至 MMF 仅保留稀疏目录。1000 万行表仅查最新 1 万行时，内存占用 < 20MB。
- **纯托管代码**：不依赖 Native 组件（纯 C#/.NET），便于跨平台与受限环境部署。

### 存储引擎

| 引擎 | 数据结构 | 适用场景 |
|------|----------|----------|
| **Nova Engine** | SkipList（内存+MMF 冷热分离） | 通用 CRUD、配置表、业务订单、用户数据 |
| **Flux Engine** | 按时间分片（Append Only） | IoT 传感器、日志收集、内部消息队列、审计日志 |
| **KV 模式** | Bitcask（内存索引 + 磁盘值） | 分布式锁、缓存、会话存储、计数器、配置中心 |

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
| 地理编码 | `GEOPOINT` | 自定义结构 | 经纬度坐标，支持距离/多边形查询 |
| 向量 | `VECTOR(n)` | `Single[]` | AI 向量检索，支持 KNN 最近邻查询 |

### SQL 能力

已实现标准 SQL 子集，覆盖约 60% 常用业务场景：

| 功能 | 状态 | 说明 |
|------|------|------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX/DATABASE，ALTER TABLE（ADD/MODIFY/DROP COLUMN、COMMENT），含 IF NOT EXISTS、PRIMARY KEY、UNIQUE、ENGINE |
| DML | ✅ | INSERT（多行）、UPDATE、DELETE、UPSERT（ON DUPLICATE KEY UPDATE）、TRUNCATE TABLE |
| 查询 | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| 聚合 | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN（Nested Loop），支持表别名 |
| 参数化 | ✅ | @param 占位符 |
| 事务 | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| SQL 函数 | ✅ | 字符串/数值/日期/转换/条件/哈希（60+ 函数） |
| 子查询 | ✅ | IN/EXISTS 子查询 |
| 全文检索 | ✅ | 全文索引 + BM25 评分，Top-K 结果，支持 RAG 混合检索 |
| 高级 | ❌ | 无视图/触发器/存储过程/窗口函数 |

---

## 使用说明

### 安装

通过 NuGet 安装 NovaDb 核心包：

```shell
dotnet add package NewLife.NovaDb
```

### 接入方式

NovaDb 提供两种客户端接入方式，适用于不同场景：

| 接入方式 | 适用引擎 | 说明 |
|---------|---------|------|
| **ADO.NET + SQL** | Nova（关系型）、Flux（时序） | 标准 `DbConnection`/`DbCommand`/`DbDataReader`，兼容所有 ORM |
| **NovaClient** | MQ（消息队列）、KV（键值存储） | RPC 客户端，提供消息发布/消费/确认、KV 读写等高级 API |

---

### 一、关系型数据库（ADO.NET + SQL）

关系型引擎（Nova Engine）使用标准 ADO.NET 接口访问，连接字符串中 `Data Source` 指向本地路径为嵌入模式，`Server` 指向远程地址为网络模式。

#### 1.1 嵌入模式（5 分钟上手）

嵌入模式无需启动独立服务，适合桌面应用、IoT 设备、单元测试等场景。

```csharp
using NewLife.NovaDb.Client;

// 创建连接（嵌入模式，文件夹即数据库）
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

// 建表
using var cmd = conn.CreateCommand();
cmd.CommandText = @"CREATE TABLE IF NOT EXISTS users (
    id   INT PRIMARY KEY AUTO_INCREMENT,
    name STRING(50) NOT NULL,
    age  INT DEFAULT 0,
    created DATETIME
)";
cmd.ExecuteNonQuery();

// 插入数据
cmd.CommandText = "INSERT INTO users (name, age, created) VALUES ('Alice', 25, NOW())";
cmd.ExecuteNonQuery();

// 批量插入
cmd.CommandText = @"INSERT INTO users (name, age) VALUES
    ('Bob', 30),
    ('Charlie', 28)";
cmd.ExecuteNonQuery();

// 查询数据
cmd.CommandText = "SELECT * FROM users WHERE age >= 25 ORDER BY age";
using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"id={reader["id"]}, name={reader["name"]}, age={reader["age"]}");
}
```

#### 1.2 网络模式

网络模式通过 TCP 提供远程访问，支持多客户端并发连接。连接字符串中的每个数据库（`Database=mydb`）映射到服务器数据目录下的独立引擎目录，实现单服务器多库路由。

**启动服务端：**

```csharp
using NewLife.NovaDb.Server;

var svr = new NovaServer(3306) { DbPath = "./data" };
svr.Start();
Console.ReadLine();
svr.Stop("手动关闭");
```

**ADO.NET 客户端连接（与嵌入模式完全相同的 API）：**

```csharp
using var conn = new NovaConnection
{
    ConnectionString = "Server=127.0.0.1;Port=3306;Database=mydb"
};
conn.Open();

using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT * FROM users WHERE age > 20";
using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"name={reader["name"]}");
}
```

#### 1.3 参数化查询

参数化查询防止 SQL 注入，使用 `@name` 命名参数：

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT * FROM users WHERE age > @minAge AND name LIKE @pattern";
cmd.Parameters.Add(new NovaParameter("@minAge", 18));
cmd.Parameters.Add(new NovaParameter("@pattern", "A%"));

using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"{reader["name"]}, {reader["age"]}");
}
```

#### 1.4 聚合与标量查询

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT COUNT(*) FROM users";
var count = Convert.ToInt32(cmd.ExecuteScalar());
Console.WriteLine($"总用户数: {count}");

cmd.CommandText = "SELECT AVG(age) FROM users WHERE age > 0";
var avgAge = Convert.ToDouble(cmd.ExecuteScalar());
Console.WriteLine($"平均年龄: {avgAge:F1}");
```

#### 1.5 事务

NovaDb 基于 MVCC 实现事务隔离，默认隔离级别为 Read Committed：

```csharp
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

using var tx = conn.BeginTransaction();
try
{
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;

    // 扣减库存
    cmd.CommandText = "UPDATE products SET stock = stock - 1 WHERE id = 1 AND stock > 0";
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) throw new InvalidOperationException("库存不足");

    // 创建订单
    cmd.CommandText = "INSERT INTO orders (product_id, amount) VALUES (1, 1)";
    cmd.ExecuteNonQuery();

    tx.Commit();
}
catch
{
    tx.Rollback();
    throw;
}
```

#### 1.6 多表连接查询

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandText = @"
    SELECT o.id, u.name, o.total
    FROM orders o
    INNER JOIN users u ON o.user_id = u.id
    WHERE o.total > @minTotal
    ORDER BY o.total DESC
    LIMIT 10";
cmd.Parameters.Add(new NovaParameter("@minTotal", 100));

using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"订单 {reader["id"]}: {reader["name"]} - ¥{reader["total"]}");
}
```

#### 1.7 DDL 操作

```sql
-- 创建/删除数据库
CREATE DATABASE shop;
DROP DATABASE shop;

-- 修改表结构
ALTER TABLE products ADD COLUMN category STRING;
ALTER TABLE products MODIFY COLUMN name STRING(200);
ALTER TABLE products DROP COLUMN category;

-- 索引管理
CREATE INDEX idx_name ON users (name);
CREATE UNIQUE INDEX idx_email ON users (email);
DROP INDEX idx_name ON users;
```

#### 1.8 UPSERT

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandText = @"
    INSERT INTO products (id, name, price) VALUES (1, '笔记本电脑', 5499.00)
    ON DUPLICATE KEY UPDATE price = 5499.00";
cmd.ExecuteNonQuery();
```

#### 1.9 连接字符串参考

| 参数 | 示例 | 说明 |
|------|------|------|
| `Data Source` | `Data Source=./mydb` | 嵌入模式，指定数据库路径 |
| `Server` | `Server=127.0.0.1` | 网络模式，指定服务器地址 |
| `Port` | `Port=3306` | 服务器端口（默认 3306） |
| `Database` | `Database=mydb` | 数据库名称 |
| `WalMode` | `WalMode=Full` | WAL 模式（Full/Normal/None） |
| `ReadOnly` | `ReadOnly=true` | 只读模式 |

---

### 二、时序数据库（ADO.NET + SQL）

时序引擎（Flux Engine）同样通过 ADO.NET + SQL 访问，建表时指定 `ENGINE=FLUX`。

#### 2.1 创建时序表

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandText = @"CREATE TABLE IF NOT EXISTS metrics (
    timestamp DATETIME,
    device_id STRING(50),
    temperature DOUBLE,
    humidity DOUBLE
) ENGINE=FLUX";
cmd.ExecuteNonQuery();
```

#### 2.2 写入时序数据

```csharp
// 单条写入
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity)
    VALUES (NOW(), 'sensor-001', 23.5, 65.0)";
cmd.ExecuteNonQuery();

// 批量写入
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity) VALUES
    ('2025-07-01 10:00:00', 'sensor-001', 22.1, 60.0),
    ('2025-07-01 10:01:00', 'sensor-001', 22.3, 61.0),
    ('2025-07-01 10:02:00', 'sensor-002', 25.0, 55.0)";
cmd.ExecuteNonQuery();
```

#### 2.3 时间范围查询

```csharp
cmd.CommandText = @"SELECT device_id, temperature, humidity, timestamp
    FROM metrics
    WHERE timestamp >= @start AND timestamp < @end
    ORDER BY timestamp DESC";
cmd.Parameters.Add(new NovaParameter("@start", DateTime.Now.AddHours(-1)));
cmd.Parameters.Add(new NovaParameter("@end", DateTime.Now));

using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"[{reader["timestamp"]}] {reader["device_id"]}: " +
        $"温度={reader["temperature"]}°C, 湿度={reader["humidity"]}%");
}
```

#### 2.4 聚合分析

```csharp
// 按设备统计平均温度
cmd.CommandText = @"SELECT device_id, COUNT(*) AS cnt, AVG(temperature) AS avg_temp,
        MIN(temperature) AS min_temp, MAX(temperature) AS max_temp
    FROM metrics
    WHERE timestamp >= @start
    GROUP BY device_id";
cmd.Parameters.Add(new NovaParameter("@start", DateTime.Now.AddDays(-1)));

using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"{reader["device_id"]}: 平均={reader["avg_temp"]:F1}°C, " +
        $"最低={reader["min_temp"]}°C, 最高={reader["max_temp"]}°C, 共 {reader["cnt"]} 条");
}
```

#### 2.5 数据保留策略

时序表支持 TTL 自动清理过期分片，按时间或容量保留数据：

```csharp
// 通过 DbOptions 配置时序参数
var options = new DbOptions
{
    FluxPartitionHours = 1,        // 每小时一个分区
    FluxDefaultTtlSeconds = 86400, // 数据保留 24 小时
};
```

---

### 三、消息队列（NovaClient）

NovaDb 基于 Flux 时序引擎实现了 Redis Stream 风格的消息队列。消息队列通过 `NovaClient` 的 RPC 接口访问。

#### 3.1 连接服务器

```csharp
using NewLife.NovaDb.Client;

using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();
```

#### 3.2 发布消息

```csharp
// 通过 RPC 执行 SQL 插入消息到 Flux 表
var affected = await client.ExecuteAsync(
    "INSERT INTO order_events (timestamp, orderId, action, amount) " +
    "VALUES (NOW(), 10001, 'created', 299.00)");
Console.WriteLine($"消息已发布，影响行数: {affected}");
```

#### 3.3 消费消息

```csharp
// 读取消息（按时间范围）
var messages = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT * FROM order_events WHERE timestamp > @since ORDER BY timestamp LIMIT 10",
    new { since = DateTime.Now.AddMinutes(-5) });
```

#### 3.4 心跳检测

```csharp
var serverTime = await client.PingAsync();
Console.WriteLine($"服务器连接正常: {serverTime}");
Console.WriteLine($"是否已连接: {client.IsConnected}");
```

#### 3.5 MQ 核心特性

- **消息 ID**：时间戳 + 序列号（同毫秒自增），全局有序
- **消费组**：`Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **可靠性**：At-Least-Once，读取后进入 Pending，业务成功后 Ack
- **数据保留**：支持 TTL（按时间/文件大小自动删除旧分片）
- **延迟消息**：指定延迟时间或具体投递时刻
- **死信队列**：消费失败超过最大重试次数自动进入 DLQ

---

### 四、KV 键值存储（NovaClient）

KV 存储通过 `NovaClient` 访问，建表时指定 `ENGINE=KV`。KV 表固定 Schema 为 `Key + Value + TTL`。

#### 4.1 创建 KV 表

```csharp
using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();

// 创建 KV 表（指定默认 TTL 为 7200 秒 = 2 小时）
await client.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS session_cache (
    Key STRING(200) PRIMARY KEY, Value BLOB, TTL DATETIME
) ENGINE=KV DEFAULT_TTL=7200");
```

#### 4.2 读写数据

```csharp
// 写入（UPSERT 语义）
await client.ExecuteAsync(
    "INSERT INTO session_cache (Key, Value, TTL) VALUES ('session:1001', 'user-data', " +
    "DATEADD(NOW(), 30, 'MINUTE')) ON DUPLICATE KEY UPDATE Value = 'user-data'");

// 读取
var result = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT Value FROM session_cache WHERE Key = 'session:1001' " +
    "AND (TTL IS NULL OR TTL > NOW())");

// 删除
await client.ExecuteAsync("DELETE FROM session_cache WHERE Key = 'session:1001'");
```

#### 4.3 原子递增（计数器）

```csharp
await client.ExecuteAsync(
    "INSERT INTO counters (Key, Value) VALUES ('page:views', 1) " +
    "ON DUPLICATE KEY UPDATE Value = Value + 1");
```

#### 4.4 分布式锁

```csharp
// 尝试获取锁（仅当 Key 不存在时插入成功）
var locked = await client.ExecuteAsync(
    "INSERT INTO dist_lock (Key, Value, TTL) VALUES ('lock:order:123', 'worker-1', " +
    "DATEADD(NOW(), 30, 'SECOND'))");

if (locked > 0)
{
    try
    {
        // 获得锁，执行业务逻辑
    }
    finally
    {
        await client.ExecuteAsync("DELETE FROM dist_lock WHERE Key = 'lock:order:123'");
    }
}
```

#### 4.5 KV 能力概览

| 操作 | 说明 |
|------|------|
| `Get` | 读取值，惰性检查 TTL |
| `Set` | 设置值，支持指定 TTL |
| `Add` | 仅当 Key 不存在时添加（分布式锁场景） |
| `Delete` | 删除键 |
| `Inc` | 原子递增/递减（计数器场景） |
| `TTL` | 到期自动不可见，后台定期清理 |

---

## 数据安全与 WAL 模式

NovaDb 提供三种 WAL 持久化策略：

| 模式 | 说明 | 适用场景 |
|------|------|----------|
| `FULL` | 同步落盘，每次提交立即刷盘 | 金融/交易场景，最强数据安全 |
| `NORMAL` | 异步 1s 刷盘（默认） | 大多数业务场景，平衡性能与安全 |
| `NONE` | 全异步，不主动刷盘 | 临时数据/缓存场景，最高吞吐 |

> 只要不选择同步模式（`FULL`），就意味着接受在崩溃/断电等场景下可能发生数据丢失。

## 集群部署

NovaDb 支持**一主多从**架构，通过 Binlog 实现异步数据同步：

```
┌──────────┐    Binlog 同步    ┌──────────┐
│  主节点   │ ──────────────→  │  从节点 1  │
│  (读写)   │                  │  (只读)    │
└──────────┘                  └──────────┘
      │         Binlog 同步    ┌──────────┐
      └──────────────────────→ │  从节点 2  │
                               │  (只读)    │
                               └──────────┘
```

- 主节点处理所有写操作，从节点提供只读查询
- 基于 Binlog 异步复制，支持断点续传
- 应用层负责读写分离

## 规划能力（Roadmap）

| 版本 | 计划内容 |
|------|----------|
| **v1.0**（已完成） | 嵌入式+网络双模、Nova/Flux/KV 引擎、SQL DDL/DML/SELECT/JOIN、事务/MVCC、WAL/恢复、冷热分离、分片、MQ 消费组、ADO.NET Provider、集群主从同步 |
| **v1.1**（已完成） | P0 级 SQL 函数（字符串/数值/日期/转换/条件约 30 个函数） |
| **v1.2**（已完成） | MQ 阻塞读取、KV Add/Inc 操作、P1 级 SQL 函数 |
| **v1.3**（已完成） | MQ 延迟消息、死信队列 |
| **v2.0**（已完成） | GeoPoint 地理编码 + Vector 向量类型（AI 向量检索）、GIS 地理扩展、全文检索（BM25）、NovaVectorStore（IVectorStore 多集合）、增量物化视图、Binlog 管理、可观测性与管理工具 |
| **v2.1**（已完成） | 时序列式封存 + 块压缩（数值编码 + LZ4）、Rollup 预聚合与回填及聚合查询自动路由、SESSION/STATE_WINDOW 分组、压缩比报表、NativeAOT 支持与多 RID CI（win-x64/linux-x64/linux-arm64/loongarch64） |

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

## 生态版图

NewLife 生态按依赖分层，各项目独立版本、独立发版。下面的版图在所有 NewLife 仓库中逐字一致；每个项目链接到代码仓库与主 NuGet 包。

| 项目 | 一句话定位 | 下载量 |
| --- | --- | --- |
| **基础底座** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | 日志、配置、缓存、网络、序列化、定时调度与 APM 追踪。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | 数据中间件：单表百亿级、自动分表、读写分离，支持 10 多种数据库。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | 高性能网络库：单机 177 万 pps、14.5 Gbps（TCP 1KB，2026-10 实测），400 万 Tcp 长连接。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | 协议通信与 RPC 框架，面向 CS 应用与物联网设备。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | 把 .NET 应用安装为操作系统守护进程：Windows 服务与 Linux systemd。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | `dotnet new` 项目模板：控制台、Web、WebApi、服务、数据与物联网。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **数据存储与消息** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | Redis 客户端：微秒级延迟、百万级吞吐，内置消息队列。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | 纯托管 RocketMQ 客户端，支持 Apache RocketMQ 与阿里云消息队列。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | MQTT 客户端与服务端，客户端支持阿里云物联网。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | C# 混合数据库：嵌入/网络双模，关系型、时序、消息队列与 KV 于一体。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | MySql 驱动与数据访问辅助层，基于 NewLife 数据栈实现。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **Web、AI 与分布式** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | 魔方快速开发平台：用户权限、SSO、OAuth 服务端与 CRUD 脚手架。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | 星尘分布式服务平台：节点管理、APM、配置中心、注册中心与发布中心。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | 蚂蚁调度：分布式任务与实时数据平台，支持分片、重试与回溯补算。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | 统一接入 46 家大模型服务商，附带开箱即用的 ChatAI Web 对话应用。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **工业与物联网** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | IoT 标准库，定义物联网通信协议契约与设备抽象。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | Modbus TCP/RTU/ASCII，基于 NewLife.IoT 实现。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | 西门子 PLC 协议，基于 NewLife.IoT 实现。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | 欧姆龙 PLC 协议，基于 NewLife.IoT 实现。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | 三菱 Melsec PLC 协议，基于 NewLife.IoT 实现。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | OPC 客户端，用于工业数据采集。 | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | BACnet 楼宇自控协议栈。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | 开源轻量级 IoT 平台，含网关与设备管理。 | — |
| **车联网** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | JT/T 808 车辆定位协议：70+ 消息体，覆盖 2011/2013/2019 三个版本。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | JT/T 809 部标平台互联协议，面向运输管理部门。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | JT/T 1078 车载视频：RTP 解析、FLV/fMP4 转封装与直播推送。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **领域组件** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | 一个包覆盖 Excel、Word、PPT、PDF 等 15+ 文档格式。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | 跨平台音频：编解码、设备 IO、DSP、容器与流式处理。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | 视频编解码、封装格式、帧处理与流媒体。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | 百度/高德/腾讯/天地图统一接口，支持地理编码与坐标转换。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | IPv4 归属地：离线内存映射库 + 在线 API 双源。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **工具与产品** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | 码神工具：网络、串口、加解密、正则、Modbus 与 MQTT。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | 自托管 Git 源代码管理系统。 | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | 从低版本宿主启动时，自动安装匹配的 .NET 运行时。 | — |

> 徽章取各项目主包；主题包、扩展包与卫星包见对应 NuGet 页面。未在表中列出的 30 多个包（FTP、LoRa、Office 扩展、各类协议驱动等）见 [NewLifeX 组织](https://github.com/NewLifeX) 与 [newlifex.com](https://newlifex.com)。


## 新生命开发团队

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

新生命团队成立于 2002 年，二十余年专注 .NET 与物联网技术生态，主导开源项目近百个（生态版图收录 34 个主干项目），为电力、物流、医疗、工控、互联网等行业提供软硬件一体化解决方案；以“学无先后达者为师”为信条。

开源组件在 NuGet 累计下载 <!--nuget-total:start-->6.48M<!--nuget-total:end--> 次（全生态口径，截至 2026-10）。

网站：https://newlifex.com · 开源组织：https://github.com/NewLifeX

<details>
<summary>微信公众号：智能大石头（扫码关注）</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>

