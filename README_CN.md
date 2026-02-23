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
| DML | ✅ | INSERT（多行）、UPDATE、DELETE、UPSERT（ON DUPLICATE KEY UPDATE）、TRUNCATE TABLE |
| 查询 | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| 聚合 | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN（Nested Loop），支持表别名 |
| 参数化 | ✅ | @param 占位符 |
| 事务 | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| SQL 函数 | ✅ | 字符串/数值/日期/转换/条件/哈希（60+ 函数） |
| 子查询 | ✅ | IN/EXISTS 子查询 |
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

关系型引擎（Nova Engine）使用标准 ADO.NET 接口访问，连接字符串中 `Data Source` 指向本地路径为嵌入模式，`Server` 指向远程地址为服务器模式。

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

#### 1.2 服务器模式

服务器模式通过 TCP 提供远程访问，支持多客户端并发连接。

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
| `Server` | `Server=127.0.0.1` | 服务器模式，指定服务器地址 |
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
