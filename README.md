# NewLife.NovaDb


![GitHub License](https://img.shields.io/github/license/NewLifeX/NewLife.NovaDb?logo=github)
![NuGet Downloads](https://img.shields.io/nuget/dt/NewLife.NovaDb?logo=nuget)
![NuGet](https://img.shields.io/nuget/v/NewLife.NovaDb?logo=nuget)
![NuGet prerelease](https://img.shields.io/nuget/vpre/NewLife.NovaDb?label=dev%20nuget&logo=nuget)
[![test](https://github.com/NewLifeX/NewLife.NovaDb/actions/workflows/test.yml/badge.svg?branch=master)](https://github.com/NewLifeX/NewLife.NovaDb/actions/workflows/test.yml)
[![locales](https://img.shields.io/badge/locales-11-2ea44f?logo=dotnet)](Doc/多语言本地化.md)
![platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS%20%7C%20Android%20%7C%20iOS-blue)
![arch](https://img.shields.io/badge/arch-x64%20%7C%20arm64%20%7C%20loongarch64%20%7C%20riscv64%20%7C%20s390x-orange)
[![contributors](https://img.shields.io/github/contributors/NewLifeX/NewLife.NovaDb?logo=github)](https://github.com/NewLifeX/NewLife.NovaDb/graphs/contributors)
<!-- lang:start -->
🌐 [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [한국어](README.ko.md) | [العربية](README.ar.md)
<!-- lang:end -->

A medium-to-large hybrid database implemented in **C#**, running on the **.NET platform** (supports .NET Framework 4.5 ~ .NET 10), supporting dual embedded/network modes, integrating relational, time-series, message queue, and NoSQL (KV) capabilities.

## Product Introduction

`NewLife.NovaDb` (abbreviated as `Nova`) is the core infrastructure of the NewLife ecosystem, an integrated data engine for .NET applications. By removing many niche features (such as stored procedures/triggers/window functions), it achieves higher read/write performance and lower operational costs; the data volume is logically unlimited (constrained by disk and partitioning strategies), and can replace SQLite/MySQL/Redis/TDengine in specific scenarios.

### Core Features

- **Dual Deployment Modes**:
  - **Embedded Mode**: Runs as a library like SQLite, with data stored in local folders, zero configuration
  - **Network Mode**: Standalone process + TCP protocol, network access like MySQL; supports cluster deployment and master-slave replication (one master, multiple slaves)
- **Folder-as-Database**: Copy the folder to complete migration/backup, no dump/restore process needed. Each table has independent file groups (`.data`/`.idx`/`.wal`).
- **Four-Engine Integration**:
  - **Nova Engine** (General Relational): SkipList index + MVCC transactions (Read Committed), supports CRUD, SQL queries, JOIN
  - **Flux Engine** (Time-Series + MQ): Time-based sharding Append Only, supports TTL auto-cleanup, Redis Stream-style consumer groups + Pending + Ack; columnar block sealing with type-aware encoding (Delta/DeltaDelta/XOR/RLE/dictionary) plus LZ4 block compression, rollup pre-aggregation with automatic aggregate query routing, SESSION/STATE window grouping, and compression-ratio reporting via `_sys.flux_compression`
  - **KV Mode** (Key-Value Store): Independent Bitcask engine (in-memory index + on-disk values), supports TTL, multi-table isolation
  - **ADO.NET Provider**: Auto-recognizes embedded/network mode, native integration with XCode ORM
- **Dynamic Hot-Cold Index Separation**: Hot data fully loaded into physical memory (SkipList nodes), cold data unloaded to MMF with only sparse directory retained. 10 million row table querying only the latest 10,000 rows uses < 20MB memory.
- **Pure Managed Code**: No native component dependencies (pure C#/.NET), easy to deploy across platforms and in restricted environments.

### Storage Engines

| Engine | Data Structure | Use Cases |
|--------|----------------|-----------|
| **Nova Engine** | SkipList (Memory+MMF Hot-Cold Separation) | General CRUD, configuration tables, business orders, user data |
| **Flux Engine** | Time-based Sharding (Append Only) | IoT sensors, log collection, internal message queues, audit logs |
| **KV Mode** | Bitcask (In-Memory Index + On-Disk Values) | Distributed locks, caching, session storage, counters, configuration center |

### Data Types

| Category | SQL Type | C# Mapping | Description |
|----------|----------|------------|-------------|
| Boolean | `BOOL` | `Boolean` | 1 byte |
| Integer | `INT` / `LONG` | `Int32` / `Int64` | 4/8 bytes |
| Float | `DOUBLE` | `Double` | 8 bytes |
| Decimal | `DECIMAL` | `Decimal` | 128-bit, unified precision |
| String | `STRING(n)` / `STRING` | `String` | UTF-8, length can be specified |
| Binary | `BINARY(n)` / `BLOB` | `Byte[]` | Length can be specified |
| DateTime | `DATETIME` | `DateTime` | Precision to Ticks (100 nanoseconds) |
| GeoPoint | `GEOPOINT` | Custom Structure | Latitude/longitude coordinates, with distance/polygon queries |
| Vector | `VECTOR(n)` | `Single[]` | AI vector search, with KNN nearest-neighbor queries |

### SQL Capabilities

Implemented standard SQL subset, covering approximately 60% of common business scenarios:

| Feature | Status | Description |
|---------|--------|-------------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX/DATABASE, ALTER TABLE (ADD/MODIFY/DROP COLUMN, COMMENT), with IF NOT EXISTS, PRIMARY KEY, UNIQUE, ENGINE |
| DML | ✅ | INSERT (multiple rows), UPDATE, DELETE, UPSERT (ON DUPLICATE KEY UPDATE), TRUNCATE TABLE |
| Query | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| Aggregation | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN (Nested Loop), supports table aliases |
| Parameterization | ✅ | @param placeholders |
| Transaction | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| SQL Functions | ✅ | String/numeric/date/conversion/conditional/hash (60+ functions) |
| Subquery | ✅ | IN/EXISTS subqueries |
| Full-Text Search | ✅ | Full-text index with BM25 scoring, Top-K results for RAG hybrid retrieval |
| Advanced | ❌ | No views/triggers/stored procedures/window functions |

---

## Usage Guide

### Installation

Install the NovaDb core package via NuGet:

```shell
dotnet add package NewLife.NovaDb
```

### Access Methods

NovaDb provides two client access methods for different scenarios:

| Access Method | Target Engine | Description |
|--------------|---------------|-------------|
| **ADO.NET + SQL** | Nova (Relational), Flux (Time-Series) | Standard `DbConnection`/`DbCommand`/`DbDataReader`, compatible with all ORMs |
| **NovaClient** | MQ (Message Queue), KV (Key-Value Store) | RPC client providing message publish/consume/acknowledge and KV read/write APIs |

---

### 1. Relational Database (ADO.NET + SQL)

The relational engine (Nova Engine) is accessed through the standard ADO.NET interface. A `Data Source` in the connection string indicates embedded mode; a `Server` indicates network mode.

#### 1.1 Embedded Mode (5-Minute Quick Start)

Embedded mode requires no standalone service, ideal for desktop apps, IoT devices, and unit tests.

```csharp
using NewLife.NovaDb.Client;

// Create connection (embedded mode, folder-as-database)
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

// Create table
using var cmd = conn.CreateCommand();
cmd.CommandText = @"CREATE TABLE IF NOT EXISTS users (
    id   INT PRIMARY KEY AUTO_INCREMENT,
    name STRING(50) NOT NULL,
    age  INT DEFAULT 0,
    created DATETIME
)";
cmd.ExecuteNonQuery();

// Insert data
cmd.CommandText = "INSERT INTO users (name, age, created) VALUES ('Alice', 25, NOW())";
cmd.ExecuteNonQuery();

// Batch insert
cmd.CommandText = @"INSERT INTO users (name, age) VALUES
    ('Bob', 30),
    ('Charlie', 28)";
cmd.ExecuteNonQuery();

// Query data
cmd.CommandText = "SELECT * FROM users WHERE age >= 25 ORDER BY age";
using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"id={reader["id"]}, name={reader["name"]}, age={reader["age"]}");
}
```

#### 1.2 Network Mode

Network mode provides remote access via TCP, supporting multiple concurrent client connections. Each database in the connection string (`Database=mydb`) maps to an independent engine directory under the server's data folder, enabling multi-database routing on a single server.

**Start the server:**

```csharp
using NewLife.NovaDb.Server;

var svr = new NovaServer(3306) { DbPath = "./data" };
svr.Start();
Console.ReadLine();
svr.Stop("Manual shutdown");
```

**ADO.NET client connection (identical API to embedded mode):**

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

#### 1.3 Parameterized Queries

Parameterized queries prevent SQL injection using `@name` named parameters:

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

#### 1.4 Aggregation and Scalar Queries

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT COUNT(*) FROM users";
var count = Convert.ToInt32(cmd.ExecuteScalar());
Console.WriteLine($"Total users: {count}");

cmd.CommandText = "SELECT AVG(age) FROM users WHERE age > 0";
var avgAge = Convert.ToDouble(cmd.ExecuteScalar());
Console.WriteLine($"Average age: {avgAge:F1}");
```

#### 1.5 Transactions

NovaDb implements transaction isolation based on MVCC with a default isolation level of Read Committed:

```csharp
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

using var tx = conn.BeginTransaction();
try
{
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;

    // Deduct stock
    cmd.CommandText = "UPDATE products SET stock = stock - 1 WHERE id = 1 AND stock > 0";
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) throw new InvalidOperationException("Insufficient stock");

    // Create order
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

#### 1.6 JOIN Queries

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
    Console.WriteLine($"Order {reader["id"]}: {reader["name"]} - ${reader["total"]}");
}
```

#### 1.7 DDL Operations

```sql
-- Create/drop database
CREATE DATABASE shop;
DROP DATABASE shop;

-- Alter table structure
ALTER TABLE products ADD COLUMN category STRING;
ALTER TABLE products MODIFY COLUMN name STRING(200);
ALTER TABLE products DROP COLUMN category;

-- Index management
CREATE INDEX idx_name ON users (name);
CREATE UNIQUE INDEX idx_email ON users (email);
DROP INDEX idx_name ON users;
```

#### 1.8 UPSERT

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandText = @"
    INSERT INTO products (id, name, price) VALUES (1, 'Laptop', 5499.00)
    ON DUPLICATE KEY UPDATE price = 5499.00";
cmd.ExecuteNonQuery();
```

#### 1.9 Connection String Reference

| Parameter | Example | Description |
|-----------|---------|-------------|
| `Data Source` | `Data Source=./mydb` | Embedded mode, database folder path |
| `Server` | `Server=127.0.0.1` | Network mode, server address |
| `Port` | `Port=3306` | Server port (default 3306) |
| `Database` | `Database=mydb` | Database name |
| `WalMode` | `WalMode=Full` | WAL mode (Full/Normal/None) |
| `ReadOnly` | `ReadOnly=true` | Read-only mode |

---

### 2. Time-Series Database (ADO.NET + SQL)

The time-series engine (Flux Engine) is also accessed via ADO.NET + SQL. Specify `ENGINE=FLUX` when creating tables.

#### 2.1 Create a Time-Series Table

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

#### 2.2 Write Time-Series Data

```csharp
// Single insert
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity)
    VALUES (NOW(), 'sensor-001', 23.5, 65.0)";
cmd.ExecuteNonQuery();

// Batch insert
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity) VALUES
    ('2025-07-01 10:00:00', 'sensor-001', 22.1, 60.0),
    ('2025-07-01 10:01:00', 'sensor-001', 22.3, 61.0),
    ('2025-07-01 10:02:00', 'sensor-002', 25.0, 55.0)";
cmd.ExecuteNonQuery();
```

#### 2.3 Time Range Query

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
        $"temp={reader["temperature"]}°C, humidity={reader["humidity"]}%");
}
```

#### 2.4 Aggregation Analysis

```csharp
// Average temperature per device
cmd.CommandText = @"SELECT device_id, COUNT(*) AS cnt, AVG(temperature) AS avg_temp,
        MIN(temperature) AS min_temp, MAX(temperature) AS max_temp
    FROM metrics
    WHERE timestamp >= @start
    GROUP BY device_id";
cmd.Parameters.Add(new NovaParameter("@start", DateTime.Now.AddDays(-1)));

using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"{reader["device_id"]}: avg={reader["avg_temp"]:F1}°C, " +
        $"min={reader["min_temp"]}°C, max={reader["max_temp"]}°C, count={reader["cnt"]}");
}
```

#### 2.5 Data Retention Policy

Time-series tables support TTL-based automatic cleanup of expired shards, retaining data by time or capacity:

```csharp
// Configure time-series parameters via DbOptions
var options = new DbOptions
{
    FluxPartitionHours = 1,        // One partition per hour
    FluxDefaultTtlSeconds = 86400, // Retain data for 24 hours
};
```

---

### 3. Message Queue (NovaClient)

NovaDb implements a Redis Stream-style message queue based on the Flux time-series engine. The message queue is accessed via `NovaClient` RPC interface.

#### 3.1 Connect to Server

```csharp
using NewLife.NovaDb.Client;

using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();
```

#### 3.2 Publish Messages

```csharp
// Execute SQL via RPC to insert a message into the Flux table
var affected = await client.ExecuteAsync(
    "INSERT INTO order_events (timestamp, orderId, action, amount) " +
    "VALUES (NOW(), 10001, 'created', 299.00)");
Console.WriteLine($"Message published, affected rows: {affected}");
```

#### 3.3 Consume Messages

```csharp
// Read messages (by time range)
var messages = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT * FROM order_events WHERE timestamp > @since ORDER BY timestamp LIMIT 10",
    new { since = DateTime.Now.AddMinutes(-5) });
```

#### 3.4 Heartbeat

```csharp
var serverTime = await client.PingAsync();
Console.WriteLine($"Server connected: {serverTime}");
Console.WriteLine($"Is connected: {client.IsConnected}");
```

#### 3.5 MQ Core Features

- **Message ID**: Timestamp + sequence number (auto-increment within same millisecond), globally ordered
- **Consumer Group**: `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **Reliability**: At-Least-Once, enters Pending after reading, Ack after business success
- **Data Retention**: Supports TTL (auto-deletes old shards by time/file size)
- **Delayed Messages**: Specify delay duration or exact delivery time
- **Dead Letter Queue**: Auto-enters DLQ after exceeding max retry count

---

### 4. KV Key-Value Store (NovaClient)

KV storage is accessed via `NovaClient`. Specify `ENGINE=KV` when creating tables. KV tables have a fixed schema of `Key + Value + TTL`.

#### 4.1 Create a KV Table

```csharp
using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();

// Create a KV table (default TTL 7200 seconds = 2 hours)
await client.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS session_cache (
    Key STRING(200) PRIMARY KEY, Value BLOB, TTL DATETIME
) ENGINE=KV DEFAULT_TTL=7200");
```

#### 4.2 Read/Write Data

```csharp
// Write (UPSERT semantics)
await client.ExecuteAsync(
    "INSERT INTO session_cache (Key, Value, TTL) VALUES ('session:1001', 'user-data', " +
    "DATEADD(NOW(), 30, 'MINUTE')) ON DUPLICATE KEY UPDATE Value = 'user-data'");

// Read
var result = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT Value FROM session_cache WHERE Key = 'session:1001' " +
    "AND (TTL IS NULL OR TTL > NOW())");

// Delete
await client.ExecuteAsync("DELETE FROM session_cache WHERE Key = 'session:1001'");
```

#### 4.3 Atomic Increment (Counter)

```csharp
await client.ExecuteAsync(
    "INSERT INTO counters (Key, Value) VALUES ('page:views', 1) " +
    "ON DUPLICATE KEY UPDATE Value = Value + 1");
```

#### 4.4 Distributed Lock

```csharp
// Try to acquire the lock (insert succeeds only when the key does not exist)
var locked = await client.ExecuteAsync(
    "INSERT INTO dist_lock (Key, Value, TTL) VALUES ('lock:order:123', 'worker-1', " +
    "DATEADD(NOW(), 30, 'SECOND'))");

if (locked > 0)
{
    try
    {
        // Lock acquired, execute business logic
    }
    finally
    {
        await client.ExecuteAsync("DELETE FROM dist_lock WHERE Key = 'lock:order:123'");
    }
}
```

#### 4.5 KV Capabilities Overview

| Operation | Description |
|-----------|-------------|
| `Get` | Read value with lazy TTL check |
| `Set` | Set value with optional TTL |
| `Add` | Add only when Key does not exist (distributed lock) |
| `Delete` | Delete key |
| `Inc` | Atomic increment/decrement (counter) |
| `TTL` | Auto-invisible on expiry, periodic background cleanup |

---

## Data Security and WAL Modes

NovaDb provides three WAL persistence strategies:

| Mode | Description | Use Cases |
|------|-------------|-----------|
| `FULL` | Synchronous disk write, flush immediately on each commit | Financial/trading scenarios, strongest data safety |
| `NORMAL` | Async 1s flush (default) | Most business scenarios, balances performance and safety |
| `NONE` | Fully async, no proactive flush | Temporary data/cache scenarios, highest throughput |

> Choosing any mode other than synchronous (`FULL`) means accepting possible data loss in crash/power failure scenarios.

## Cluster Deployment

NovaDb supports a **one-master, multiple-slave** architecture with asynchronous data replication via Binlog:

```
┌──────────┐    Binlog Sync    ┌──────────┐
│  Master   │ ──────────────→  │  Slave 1  │
│  (R/W)    │                  │  (R/O)    │
└──────────┘                  └──────────┘
      │         Binlog Sync    ┌──────────┐
      └──────────────────────→ │  Slave 2  │
                               │  (R/O)    │
                               └──────────┘
```

- Master node handles all write operations; slave nodes provide read-only queries
- Asynchronous replication via Binlog with resume-from-breakpoint support
- Application layer is responsible for read/write splitting

## Roadmap

| Version | Planned Features |
|---------|------------------|
| **v1.0** (Completed) | Embedded+Server dual mode, Nova/Flux/KV engines, SQL DDL/DML/SELECT/JOIN, transactions/MVCC, WAL/recovery, hot-cold separation, sharding, MQ consumer groups, ADO.NET Provider, cluster master-slave sync |
| **v1.1** (Completed) | P0-level SQL functions (string/numeric/date/conversion/conditional ~30 functions) |
| **v1.2** (Completed) | MQ blocking read, KV Add/Inc operations, P1-level SQL functions |
| **v1.3** (Completed) | MQ delayed messages, dead letter queue |
| **v2.0** (Completed) | GeoPoint geo-encoding + Vector type (AI vector search), GIS extension, full-text search (BM25), NovaVectorStore (IVectorStore, multi-collection), incremental materialized views, Binlog management, observability and management tools |
| **v2.1** (Completed) | Time-series columnar sealing + block compression (numerical encoding + LZ4), rollup pre-aggregation with backfill and automatic query routing, SESSION/STATE_WINDOW grouping, compression-ratio reporting, NativeAOT support with multi-RID CI (win-x64/linux-x64/linux-arm64/loongarch64) |

## Positioning

NovaDb does not pursue full SQL92 standard compliance, but covers the 80% commonly used business subset in exchange for the following differentiated capabilities:

| Differentiation | Description |
|-----------------|-------------|
| **Pure .NET Managed** | No native dependencies, deploy via xcopy, zero serialization overhead in same process with .NET applications |
| **Embedded+Server Dual Mode** | Embedded for development/debugging like SQLite, standalone service for production like MySQL, same API |
| **Folder-as-Database** | Copy folder to complete migration/backup, no dump/restore needed |
| **Hot-Cold Index Separation** | 10M row table querying only hotspots uses < 20MB memory, cold data auto-unloaded to MMF |
| **Four-Engine Integration** | Single component covers common SQLite + TDengine + Redis scenarios, reduces operational component count |
| **NewLife Native Integration** | Direct adaptation with XCode ORM + ADO.NET, no third-party drivers needed |

## Ecosystem Map

NewLife is a layered ecosystem of independently versioned open-source projects. The map below is shared verbatim by every NewLife repository; each project links to its repository and its primary NuGet package.

| Project | What it does | Downloads |
| --- | --- | --- |
| **Foundation** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | Logging, configuration, caching, networking, serialization, scheduling and APM tracing. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | Data middleware: single tables at the tens-of-billions scale, automatic sharding, read-write splitting, 10+ databases. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | High-performance network library: 1.77M pps / 14.5 Gbps on a single machine (TCP 1KB, 2026-10), 4M long-lived TCP connections. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | Protocol communication and RPC framework for client/server applications and IoT devices. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | Installs .NET applications as OS daemons: Windows services and Linux systemd. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | `dotnet new` project templates for console, web, API, service, data and IoT projects. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **Data & Messaging** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | Redis client with microsecond latency, millions of ops/s and built-in message queues. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | Pure managed RocketMQ client for Apache RocketMQ and Alibaba Cloud Message Queue. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | MQTT client and server, including Alibaba Cloud IoT connectivity. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | Hybrid database in C#: embedded or networked, relational, time-series, message queue and KV in one. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | MySql driver and helper layer built on the NewLife data stack. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **Web, AI & Distributed** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | Rapid admin-platform framework: permissions, SSO, OAuth server and CRUD scaffolding. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | Distributed service platform: node management, APM, configuration center, registry and release center. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | Distributed task and real-time data scheduling platform with sharding, retries and backfill. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | Unified client for 46 LLM providers plus ChatAI, a ready-to-run web chat application. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **Industrial & IoT** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | IoT standard library defining protocol contracts and device abstractions. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | Modbus TCP/RTU/ASCII implementation on top of NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | Siemens PLC protocol on top of NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | Omron PLC protocol on top of NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | Mitsubishi Melsec PLC protocol on top of NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | OPC client for industrial data access. | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | BACnet building-automation protocol stack. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | Lightweight open-source IoT platform with gateway and device management. | — |
| **Vehicle Telematics** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | JT/T 808 vehicle positioning protocol: 70+ message bodies, 2011/2013/2019. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | JT/T 809 platform-to-platform protocol for transport authorities. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | JT/T 1078 in-vehicle video: RTP parsing, FLV/fMP4 remuxing and live streaming. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **Domain Components** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | One package for Excel, Word, PPT, PDF and 15+ document formats. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | Cross-platform audio: codecs, device I/O, DSP, containers and streaming. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | Video codecs, container formats, frame processing and streaming. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | Unified interface over Baidu, AMap, Tencent and Tianditu with geocoding and coordinate conversion. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | IPv4 geolocation with an offline memory-mapped database and an online API fallback. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **Tools & Products** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | Developer toolbox: network, serial port, crypto, regex, Modbus and MQTT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | Self-hosted Git source code management system. | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | Installs the matching .NET runtime automatically when launched from an older host. | — |

> Badges show the primary package of each project; theme, extension and satellite packages are listed on their NuGet pages. The 30+ packages not listed here (FTP, LoRa, Office extensions, protocol drivers and more) are available under the [NewLifeX organization](https://github.com/NewLifeX) and on [newlifex.com](https://newlifex.com).


## Team

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

Founded in 2002, the NewLife team has spent more than two decades on the .NET and IoT ecosystem, maintains nearly 100 open-source projects (34 of them core to this map), and delivers integrated software and hardware solutions for power, logistics, healthcare, industrial control and internet industries. We live by the motto "learning has no seniority — mastery earns respect".

Open-source components have been downloaded <!--nuget-total:start-->6.48M<!--nuget-total:end--> times from NuGet (ecosystem-wide, as of 2026-10).

Website: https://newlifex.com · Open source organization: https://github.com/NewLifeX

<details>
<summary>WeChat Official Account: 智能大石头 (scan to follow)</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>

