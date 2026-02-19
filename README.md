# NewLife.NovaDb

[![English](https://img.shields.io/badge/lang-English-blue.svg)](README.md) [![简体中文](https://img.shields.io/badge/lang-简体中文-red.svg)](README_CN.md) [![繁體中文](https://img.shields.io/badge/lang-繁體中文-orange.svg)](README_TW.md) [![Français](https://img.shields.io/badge/lang-Français-green.svg)](README_FR.md) [![Deutsch](https://img.shields.io/badge/lang-Deutsch-yellow.svg)](README_DE.md) [![日本語](https://img.shields.io/badge/lang-日本語-purple.svg)](README_JA.md) [![한국어](https://img.shields.io/badge/lang-한국어-brightgreen.svg)](README_KO.md) [![Русский](https://img.shields.io/badge/lang-Русский-lightgrey.svg)](README_RU.md) [![Español](https://img.shields.io/badge/lang-Español-yellow.svg)](README_ES.md) [![Português](https://img.shields.io/badge/lang-Português-blue.svg)](README_PT.md)

A medium-to-large hybrid database implemented in **C#**, running on the **.NET platform** (supports .NET Framework 4.5 ~ .NET 10), supporting dual embedded/server modes, integrating relational, time-series, message queue, and NoSQL (KV) capabilities.

## Product Introduction

`NewLife.NovaDb` (abbreviated as `Nova`) is the core infrastructure of the NewLife ecosystem, an integrated data engine for .NET applications. By removing many niche features (such as stored procedures/triggers/window functions), it achieves higher read/write performance and lower operational costs; the data volume is logically unlimited (constrained by disk and partitioning strategies), and can replace SQLite/MySQL/Redis/TDengine in specific scenarios.

### Core Features

- **Dual Deployment Modes**:
  - **Embedded Mode**: Runs as a library like SQLite, with data stored in local folders, zero configuration
  - **Server Mode**: Standalone process + TCP protocol, network access like MySQL; supports cluster deployment and master-slave replication (one master, multiple slaves)
- **Folder-as-Database**: Copy the folder to complete migration/backup, no dump/restore process needed. Each table has independent file groups (`.data`/`.idx`/`.wal`).
- **Four-Engine Integration**:
  - **Nova Engine** (General Relational): SkipList index + MVCC transactions (Read Committed), supports CRUD, SQL queries, JOIN
  - **Flux Engine** (Time-Series + MQ): Time-based sharding Append Only, supports TTL auto-cleanup, Redis Stream-style consumer groups + Pending + Ack
  - **KV Mode** (Logical View): Reuses Nova Engine, API hides SQL details, each row contains `Key + Value + TTL`
  - **ADO.NET Provider**: Auto-recognizes embedded/server mode, native integration with XCode ORM
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
| DDL | ✅ | CREATE/DROP TABLE/INDEX, with IF NOT EXISTS, PRIMARY KEY, UNIQUE |
| DML | ✅ | INSERT (multiple rows), UPDATE, DELETE, UPSERT (ON DUPLICATE KEY UPDATE), TRUNCATE TABLE |
| Query | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| Aggregation | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN (Nested Loop), supports table aliases |
| Parameterization | ✅ | @param placeholders |
| Transaction | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| SQL Functions | ⚠️ | String/numeric/date/conversion/conditional functions (planned) |
| Subquery | ⚠️ | IN/EXISTS (planned) |
| Advanced | ❌ | No views/triggers/stored procedures/window functions |

### MQ Capabilities (Flux Engine)

Based on Redis Stream's consumer group model:

- **Message ID**: Timestamp + sequence number (auto-increment within same millisecond), globally ordered
- **Consumer Group**: `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **Reliability**: At-Least-Once, enters Pending after reading, Ack after business success
- **Data Retention**: Supports TTL (auto-deletes old shards by time/file size)
- **Delayed Messages**: Specify `DelayTime`/`DeliverAt` (planned)
- **Dead Letter Queue**: Auto-enters DLQ on consumption failure (planned)
- **Blocking Read**: Long polling + timeout (planned)

### KV Capabilities

- `Get(key)` / `Set(key, value, ttl)` / `Delete(key)` / `Exists(key)`
- Lazy deletion (check expiration on read) + background cleanup (`CleanupExpired()`)
- `Add(key, value, ttl)`: Add only when key does not exist (planned)
- `Inc(key, delta, ttl)`: Atomic increment (planned)

## Data Security and WAL Modes

NovaDb provides three WAL persistence strategies:

| Mode | Description | Use Cases |
|------|-------------|-----------|
| `FULL` | Synchronous disk write, flush immediately on each commit | Financial/trading scenarios, strongest data safety |
| `NORMAL` | Async 1s flush (default) | Most business scenarios, balances performance and safety |
| `NONE` | Fully async, no proactive flush | Temporary data/cache scenarios, highest throughput |

> Choosing any mode other than synchronous (`FULL`) means accepting possible data loss in crash/power failure scenarios.

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
