# NewLife.NovaDb

[![English](https://img.shields.io/badge/lang-English-blue.svg)](README.md) [![简体中文](https://img.shields.io/badge/lang-简体中文-red.svg)](README_CN.md) [![繁體中文](https://img.shields.io/badge/lang-繁體中文-orange.svg)](README_TW.md) [![Français](https://img.shields.io/badge/lang-Français-green.svg)](README_FR.md) [![Deutsch](https://img.shields.io/badge/lang-Deutsch-yellow.svg)](README_DE.md) [![日本語](https://img.shields.io/badge/lang-日本語-purple.svg)](README_JA.md) [![한국어](https://img.shields.io/badge/lang-한국어-brightgreen.svg)](README_KO.md) [![Русский](https://img.shields.io/badge/lang-Русский-lightgrey.svg)](README_RU.md) [![Español](https://img.shields.io/badge/lang-Español-yellow.svg)](README_ES.md) [![Português](https://img.shields.io/badge/lang-Português-blue.svg)](README_PT.md)

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
  - **Flux Engine** (Time-Series + MQ): Time-based sharding Append Only, supports TTL auto-cleanup, Redis Stream-style consumer groups + Pending + Ack
  - **KV Mode** (Logical View): Reuses Nova Engine, API hides SQL details, each row contains `Key + Value + TTL`
  - **ADO.NET Provider**: Auto-recognizes embedded/network mode, native integration with XCode ORM
- **Dynamic Hot-Cold Index Separation**: Hot data fully loaded into physical memory (SkipList nodes), cold data unloaded to MMF with only sparse directory retained. 10 million row table querying only the latest 10,000 rows uses < 20MB memory.
- **Pure Managed Code**: No native component dependencies (pure C#/.NET), easy to deploy across platforms and in restricted environments.

### Storage Engines

| Engine | Data Structure | Use Cases |
|--------|----------------|-----------|
| **Nova Engine** | SkipList (Memory+MMF Hot-Cold Separation) | General CRUD, configuration tables, business orders, user data |
| **Flux Engine** | Time-based Sharding (Append Only) | IoT sensors, log collection, internal message queues, audit logs |
| **KV Mode** | Nova Table Logical View | Distributed locks, caching, session storage, counters, configuration center |

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
| GeoPoint | `GEOPOINT` | Custom Structure | Latitude/longitude coordinates (planned) |
| Vector | `VECTOR(n)` | `Single[]` | AI vector search (planned) |

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

Network mode provides remote access via TCP, supporting multiple concurrent client connections.

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

#### 1.4 Transactions

NovaDb implements transaction isolation based on MVCC with a default isolation level of Read Committed:

```csharp
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

using var tx = conn.BeginTransaction();
try
{
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;

    cmd.CommandText = "UPDATE products SET stock = stock - 1 WHERE id = 1 AND stock > 0";
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) throw new InvalidOperationException("Insufficient stock");

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

#### 1.5 JOIN Queries

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

#### 1.6 Connection String Reference

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
var affected = await client.ExecuteAsync(
    "INSERT INTO order_events (timestamp, orderId, action, amount) " +
    "VALUES (NOW(), 10001, 'created', 299.00)");
Console.WriteLine($"Message published, affected rows: {affected}");
```

#### 3.3 Consume Messages

```csharp
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

#### 4.4 KV Capabilities Overview

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
| **v1.1** | P0-level SQL functions (string/numeric/date/conversion/conditional ~30 functions) |
| **v1.2** | MQ blocking read, KV Add/Inc operations, P1-level SQL functions |
| **v1.3** | MQ delayed messages, dead letter queue |
| **v2.0** | GeoPoint geo-encoding + Vector type (AI vector search), observability and management tools |

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
