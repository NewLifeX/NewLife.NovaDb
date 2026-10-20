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
🌐 [English](README.md) | [简体中文](README.zh-CN.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [한국어](README.ko.md) | [العربية](README.ar.md)
<!-- lang:end -->

> ⚠️ 本翻譯可能落後於最新版本，請以 [English README](README.md) 為準。

以 **C#** 實現，運行於 **.NET 平台**（支援 .NET Framework 4.5 ~ .NET 10）的中大型混合資料庫，支援嵌入式/伺服器雙模，融合關聯式、時序、訊息佇列、NoSQL(KV) 能力。

## 產品介紹

`NewLife.NovaDb`（簡稱 `Nova`）是 NewLife 生態核心基礎設施，面向 .NET 應用的一體化資料引擎。透過裁減大量冷門能力（如儲存程序/觸發器/視窗函數等），換取更高的讀寫效能與更低的維運成本；資料量邏輯上無上限（受磁碟與切分策略約束），可替代 SQLite/MySQL/Redis/TDengine 在特定場景的使用。

### 核心特性

- **雙部署模式**：
  - **嵌入模式**：像 SQLite 一樣以函式庫的形式運行，資料儲存在本地資料夾，零設定
  - **伺服器模式**：獨立程序 + TCP 協定，像 MySQL 一樣網路存取；支援叢集部署與主從同步（一主多從）
- **資料夾即資料庫**：複製資料夾即可完成遷移/備份，無需 dump/restore 流程。每表獨立檔案組（`.data`/`.idx`/`.wal`）。
- **四引擎融合**：
  - **Nova Engine**（通用關聯式）：SkipList 索引 + MVCC 交易（Read Committed），支援 CRUD、SQL 查詢、JOIN
  - **Flux Engine**（時序 + MQ）：按時間分片 Append Only，支援 TTL 自動清理、Redis Stream 風格消費組 + Pending + Ack
  - **KV 模式**（鍵值儲存）：獨立 Bitcask 引擎（記憶體索引 + 磁碟值），支援 TTL、多表隔離
  - **ADO.NET Provider**：嵌入/伺服器自動識別，相容 XCode ORM 原生整合
- **動態冷熱分離索引**：熱資料完整載入至實體記憶體（SkipList 節點），冷資料卸載至 MMF 僅保留稀疏目錄。1000 萬行表僅查最新 1 萬行時，記憶體佔用 < 20MB。
- **純託管程式碼**：不依賴 Native 元件（純 C#/.NET），便於跨平台與受限環境部署。

### 儲存引擎

| 引擎 | 資料結構 | 適用場景 |
|------|----------|----------|
| **Nova Engine** | SkipList（記憶體+MMF 冷熱分離） | 通用 CRUD、設定表、業務訂單、使用者資料 |
| **Flux Engine** | 按時間分片（Append Only） | IoT 感測器、日誌收集、內部訊息佇列、稽核日誌 |
| **KV 模式** | Bitcask（記憶體索引 + 磁碟值） | 分散式鎖、快取、會話儲存、計數器、設定中心 |

### 資料類型

| 類別 | SQL 類型 | C# 對映 | 說明 |
|------|----------|---------|------|
| 布林 | `BOOL` | `Boolean` | 1 位元組 |
| 整數 | `INT` / `LONG` | `Int32` / `Int64` | 4/8 位元組 |
| 浮點 | `DOUBLE` | `Double` | 8 位元組 |
| 定點 | `DECIMAL` | `Decimal` | 128 位元，統一精度 |
| 字串 | `STRING(n)` / `STRING` | `String` | UTF-8，可指定長度 |
| 二進位 | `BINARY(n)` / `BLOB` | `Byte[]` | 可指定長度 |
| 時間 | `DATETIME` | `DateTime` | 精確到 Ticks（100 奈秒） |
| 地理編碼 | `GEOPOINT` | 自訂結構 | 經緯度座標（規劃中） |
| 向量 | `VECTOR(n)` | `Single[]` | AI 向量檢索（規劃中） |

### SQL 能力

已實現標準 SQL 子集，覆蓋約 60% 常用業務場景：

| 功能 | 狀態 | 說明 |
|------|------|------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX/DATABASE，ALTER TABLE（ADD/MODIFY/DROP COLUMN、COMMENT），含 IF NOT EXISTS、PRIMARY KEY、UNIQUE、ENGINE |
| DML | ✅ | INSERT（多行）、UPDATE、DELETE、UPSERT（ON DUPLICATE KEY UPDATE）、TRUNCATE TABLE |
| 查詢 | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| 聚合 | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN（Nested Loop），支援表別名 |
| 參數化 | ✅ | @param 佔位符 |
| 交易 | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| SQL 函數 | ✅ | 字串/數值/日期/轉換/條件/雜湊（60+ 函數） |
| 子查詢 | ✅ | IN/EXISTS 子查詢 |
| 全文檢索 | ✅ | 全文索引 + BM25 評分，Top-K 結果，支援 RAG 混合檢索 |
| 進階 | ❌ | 無視圖/觸發器/儲存程序/視窗函數 |

---

## 使用說明

### 安裝

透過 NuGet 安裝 NovaDb 核心套件：

```shell
dotnet add package NewLife.NovaDb
```

### 接入方式

NovaDb 提供兩種用戶端接入方式，適用於不同場景：

| 接入方式 | 適用引擎 | 說明 |
|---------|---------|------|
| **ADO.NET + SQL** | Nova（關聯式）、Flux（時序） | 標準 `DbConnection`/`DbCommand`/`DbDataReader`，相容所有 ORM |
| **NovaClient** | MQ（訊息佇列）、KV（鍵值儲存） | RPC 用戶端，提供訊息發佈/消費/確認、KV 讀寫等進階 API |

---

### 一、關聯式資料庫（ADO.NET + SQL）

關聯式引擎（Nova Engine）使用標準 ADO.NET 介面存取，連線字串中 `Data Source` 指向本地路徑為嵌入模式，`Server` 指向遠端位址為伺服器模式。

#### 1.1 嵌入模式（5 分鐘上手）

嵌入模式無需啟動獨立服務，適合桌面應用、IoT 裝置、單元測試等場景。

```csharp
using NewLife.NovaDb.Client;

// 創建連線（嵌入模式，資料夾即資料庫）
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

// 插入資料
cmd.CommandText = "INSERT INTO users (name, age, created) VALUES ('Alice', 25, NOW())";
cmd.ExecuteNonQuery();

// 批次插入
cmd.CommandText = @"INSERT INTO users (name, age) VALUES
    ('Bob', 30),
    ('Charlie', 28)";
cmd.ExecuteNonQuery();

// 查詢資料
cmd.CommandText = "SELECT * FROM users WHERE age >= 25 ORDER BY age";
using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"id={reader["id"]}, name={reader["name"]}, age={reader["age"]}");
}
```

#### 1.2 伺服器模式

伺服器模式透過 TCP 提供遠端存取，支援多用戶端並行連線。

**啟動伺服端：**

```csharp
using NewLife.NovaDb.Server;

var svr = new NovaServer(3306) { DbPath = "./data" };
svr.Start();
Console.ReadLine();
svr.Stop("手動關閉");
```

**ADO.NET 用戶端連線（與嵌入模式完全相同的 API）：**

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

#### 1.3 參數化查詢

參數化查詢防止 SQL 注入，使用 `@name` 命名參數：

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

#### 1.4 聚合與純量查詢

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT COUNT(*) FROM users";
var count = Convert.ToInt32(cmd.ExecuteScalar());
Console.WriteLine($"總使用者數: {count}");

cmd.CommandText = "SELECT AVG(age) FROM users WHERE age > 0";
var avgAge = Convert.ToDouble(cmd.ExecuteScalar());
Console.WriteLine($"平均年齡: {avgAge:F1}");
```

#### 1.5 交易

NovaDb 基於 MVCC 實現交易隔離，預設隔離層級為 Read Committed：

```csharp
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

using var tx = conn.BeginTransaction();
try
{
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;

    // 扣減庫存
    cmd.CommandText = "UPDATE products SET stock = stock - 1 WHERE id = 1 AND stock > 0";
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) throw new InvalidOperationException("庫存不足");

    // 建立訂單
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

#### 1.6 多表連接查詢

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
    Console.WriteLine($"訂單 {reader["id"]}: {reader["name"]} - ¥{reader["total"]}");
}
```

#### 1.7 DDL 操作

```sql
-- 建立/刪除資料庫
CREATE DATABASE shop;
DROP DATABASE shop;

-- 修改表結構
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
    INSERT INTO products (id, name, price) VALUES (1, '筆記型電腦', 5499.00)
    ON DUPLICATE KEY UPDATE price = 5499.00";
cmd.ExecuteNonQuery();
```

#### 1.9 連線字串參考

| 參數 | 範例 | 說明 |
|------|------|------|
| `Data Source` | `Data Source=./mydb` | 嵌入模式，指定資料庫路徑 |
| `Server` | `Server=127.0.0.1` | 伺服器模式，指定伺服器位址 |
| `Port` | `Port=3306` | 伺服器埠號（預設 3306） |
| `Database` | `Database=mydb` | 資料庫名稱 |
| `WalMode` | `WalMode=Full` | WAL 模式（Full/Normal/None） |
| `ReadOnly` | `ReadOnly=true` | 唯讀模式 |

---

### 二、時序資料庫（ADO.NET + SQL）

時序引擎（Flux Engine）同樣透過 ADO.NET + SQL 存取，建表時指定 `ENGINE=FLUX`。

#### 2.1 建立時序表

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

#### 2.2 寫入時序資料

```csharp
// 單筆寫入
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity)
    VALUES (NOW(), 'sensor-001', 23.5, 65.0)";
cmd.ExecuteNonQuery();

// 批次寫入
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity) VALUES
    ('2025-07-01 10:00:00', 'sensor-001', 22.1, 60.0),
    ('2025-07-01 10:01:00', 'sensor-001', 22.3, 61.0),
    ('2025-07-01 10:02:00', 'sensor-002', 25.0, 55.0)";
cmd.ExecuteNonQuery();
```

#### 2.3 時間範圍查詢

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
        $"溫度={reader["temperature"]}°C, 濕度={reader["humidity"]}%");
}
```

#### 2.4 聚合分析

```csharp
// 按裝置統計平均溫度
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
        $"最低={reader["min_temp"]}°C, 最高={reader["max_temp"]}°C, 共 {reader["cnt"]} 筆");
}
```

#### 2.5 資料保留策略

時序表支援 TTL 自動清理過期分片，按時間或容量保留資料：

```csharp
// 透過 DbOptions 設定時序參數
var options = new DbOptions
{
    FluxPartitionHours = 1,        // 每小時一個分區
    FluxDefaultTtlSeconds = 86400, // 資料保留 24 小時
};
```

---

### 三、訊息佇列（NovaClient）

NovaDb 基於 Flux 時序引擎實現了 Redis Stream 風格的訊息佇列。訊息佇列透過 `NovaClient` 的 RPC 介面存取。

#### 3.1 連線伺服器

```csharp
using NewLife.NovaDb.Client;

using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();
```

#### 3.2 發佈訊息

```csharp
// 透過 RPC 執行 SQL 插入訊息到 Flux 表
var affected = await client.ExecuteAsync(
    "INSERT INTO order_events (timestamp, orderId, action, amount) " +
    "VALUES (NOW(), 10001, 'created', 299.00)");
Console.WriteLine($"訊息已發佈，影響行數: {affected}");
```

#### 3.3 消費訊息

```csharp
// 讀取訊息（按時間範圍）
var messages = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT * FROM order_events WHERE timestamp > @since ORDER BY timestamp LIMIT 10",
    new { since = DateTime.Now.AddMinutes(-5) });
```

#### 3.4 心跳檢測

```csharp
var serverTime = await client.PingAsync();
Console.WriteLine($"伺服器連線正常: {serverTime}");
Console.WriteLine($"是否已連線: {client.IsConnected}");
```

#### 3.5 MQ 核心特性

- **訊息 ID**：時間戳記 + 序號（同毫秒自增），全域有序
- **消費組**：`Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **可靠性**：At-Least-Once，讀取後進入 Pending，業務成功後 Ack
- **資料保留**：支援 TTL（按時間/檔案大小自動刪除舊分片）
- **延遲訊息**：指定延遲時間或具體投遞時刻
- **死信佇列**：消費失敗超過最大重試次數自動進入 DLQ

---

### 四、KV 鍵值儲存（NovaClient）

KV 儲存透過 `NovaClient` 存取，建表時指定 `ENGINE=KV`。KV 表固定 Schema 為 `Key + Value + TTL`。

#### 4.1 建立 KV 表

```csharp
using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();

// 建立 KV 表（指定預設 TTL 為 7200 秒 = 2 小時）
await client.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS session_cache (
    Key STRING(200) PRIMARY KEY, Value BLOB, TTL DATETIME
) ENGINE=KV DEFAULT_TTL=7200");
```

#### 4.2 讀寫資料

```csharp
// 寫入（UPSERT 語意）
await client.ExecuteAsync(
    "INSERT INTO session_cache (Key, Value, TTL) VALUES ('session:1001', 'user-data', " +
    "DATEADD(NOW(), 30, 'MINUTE')) ON DUPLICATE KEY UPDATE Value = 'user-data'");

// 讀取
var result = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT Value FROM session_cache WHERE Key = 'session:1001' " +
    "AND (TTL IS NULL OR TTL > NOW())");

// 刪除
await client.ExecuteAsync("DELETE FROM session_cache WHERE Key = 'session:1001'");
```

#### 4.3 原子遞增（計數器）

```csharp
await client.ExecuteAsync(
    "INSERT INTO counters (Key, Value) VALUES ('page:views', 1) " +
    "ON DUPLICATE KEY UPDATE Value = Value + 1");
```

#### 4.4 分散式鎖

```csharp
// 嘗試取得鎖（僅當 Key 不存在時插入成功）
var locked = await client.ExecuteAsync(
    "INSERT INTO dist_lock (Key, Value, TTL) VALUES ('lock:order:123', 'worker-1', " +
    "DATEADD(NOW(), 30, 'SECOND'))");

if (locked > 0)
{
    try
    {
        // 取得鎖，執行業務邏輯
    }
    finally
    {
        await client.ExecuteAsync("DELETE FROM dist_lock WHERE Key = 'lock:order:123'");
    }
}
```

#### 4.5 KV 能力概覽

| 操作 | 說明 |
|------|------|
| `Get` | 讀取值，惰性檢查 TTL |
| `Set` | 設定值，支援指定 TTL |
| `Add` | 僅當 Key 不存在時新增（分散式鎖場景） |
| `Delete` | 刪除鍵 |
| `Inc` | 原子遞增/遞減（計數器場景） |
| `TTL` | 到期自動不可見，背景定期清理 |

---

## 資料安全與 WAL 模式

NovaDb 提供三種 WAL 持久化策略：

| 模式 | 說明 | 適用場景 |
|------|------|----------|
| `FULL` | 同步落盤，每次提交立即刷盤 | 金融/交易場景，最強資料安全 |
| `NORMAL` | 非同步 1s 刷盤（預設） | 大多數業務場景，平衡效能與安全 |
| `NONE` | 全非同步，不主動刷盤 | 臨時資料/快取場景，最高吞吐量 |

> 只要不選擇同步模式（`FULL`），就意味著接受在崩潰/斷電等場景下可能發生資料遺失。

## 叢集部署

NovaDb 支援**一主多從**架構，透過 Binlog 實現非同步資料同步：

```
┌──────────┐    Binlog 同步    ┌──────────┐
│  主節點   │ ──────────────→  │  從節點 1  │
│  (讀寫)   │                  │  (唯讀)    │
└──────────┘                  └──────────┘
      │         Binlog 同步    ┌──────────┐
      └──────────────────────→ │  從節點 2  │
                               │  (唯讀)    │
                               └──────────┘
```

- 主節點處理所有寫入操作，從節點提供唯讀查詢
- 基於 Binlog 非同步複製，支援斷點續傳
- 應用層負責讀寫分離

## 規劃能力（Roadmap）

| 版本 | 計劃內容 |
|------|----------|
| **v1.0**（已完成） | 嵌入式+伺服器雙模、Nova/Flux/KV 引擎、SQL DDL/DML/SELECT/JOIN、交易/MVCC、WAL/恢復、冷熱分離、分片、MQ 消費組、ADO.NET Provider、叢集主從同步 |
| **v1.1** | P0 級 SQL 函數（字串/數值/日期/轉換/條件約 30 個函數） |
| **v1.2** | MQ 阻塞讀取、KV Add/Inc 操作、P1 級 SQL 函數 |
| **v1.3** | MQ 延遲訊息、死信佇列 |
| **v2.0**（已完成） | GeoPoint 地理編碼 + Vector 向量類型（AI 向量檢索）、GIS 地理擴充、全文檢索（BM25）、NovaVectorStore（IVectorStore 多集合）、增量物化視圖、Binlog 管理、可觀測性與管理工具 |

## 對比定位

NovaDb 不追求完整 SQL92 標準，而是覆蓋 80% 業務常用子集，換取以下差異化能力：

| 差異化 | 說明 |
|--------|------|
| **純 .NET 託管** | 無 Native 依賴，部署即 xcopy，與 .NET 應用同程序零序列化開銷 |
| **嵌入+服務雙模** | 開發除錯嵌入如 SQLite，生產部署獨立服務如 MySQL，同一套 API |
| **資料夾即資料庫** | 複製資料夾完成遷移/備份，無需 dump/restore |
| **冷熱分離索引** | 1000 萬行表僅查熱點時記憶體 < 20MB，冷資料自動卸載至 MMF |
| **四引擎融合** | 單一元件覆蓋 SQLite + TDengine + Redis 常見場景，減少維運元件數 |
| **NewLife 原生整合** | XCode ORM + ADO.NET 直接適配，無需協力廠商驅動 |

## 生態版圖

NewLife 生態系按相依分層，各專案獨立版本、獨立發行。下面的版圖在所有 NewLife 儲存庫中逐字一致；每個專案連結到程式碼儲存庫與主 NuGet 套件。

| 專案 | 一句話定位 | 下載量 |
| --- | --- | --- |
| **基礎底座** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | 日誌、設定、快取、網路、序列化、排程與 APM 追蹤。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | 資料中介層：單表百億級、自動分表、讀寫分離，支援 10 多種資料庫。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | 高效能網路庫：單機 177 萬 pps、14.5 Gbps（TCP 1KB，2026-10 實測），400 萬 Tcp 長連線。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | 協定通訊與 RPC 框架，面向 CS 應用程式與物聯網裝置。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | 將 .NET 應用程式安裝為作業系統常駐服務：Windows 服務與 Linux systemd。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | `dotnet new` 專案範本：主控台、Web、API、服務、資料與物聯網。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **資料儲存與訊息** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | Redis 用戶端：微秒級延遲、百萬級吞吐，內建訊息佇列。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | 純受控 RocketMQ 用戶端，支援 Apache RocketMQ 與阿里雲訊息佇列。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | MQTT 用戶端與服務端，含阿里雲物聯網接入。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | C# 混合式資料庫：內嵌或網路雙模，關係型、時序、訊息佇列與 KV 於一體。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | 建構於 NewLife 資料堆疊之上的 MySql 驅動與輔助層。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **Web、AI 與分散式** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | 快速後台開發框架：權限、SSO、OAuth 服務端與 CRUD 樣板。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | 分散式服務平台：節點管理、APM、設定中心、註冊中心與發佈中心。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | 分散式任務與即時資料排程平台，支援分片、重試與回溯補算。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | 統一接入 46 家大模型服務商，附帶開箱即用的 ChatAI Web 對話應用。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **工業與物聯網** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | 物聯網標準庫，定義通訊協定契約與裝置抽象。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | 建構於 NewLife.IoT 之上的 Modbus TCP/RTU/ASCII 實作。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | 建構於 NewLife.IoT 之上的西門子 PLC 協定。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | 建構於 NewLife.IoT 之上的歐姆龍 PLC 協定。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | 建構於 NewLife.IoT 之上的三菱 Melsec PLC 協定。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | 工業資料存取的 OPC 用戶端。 | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | BACnet 樓宇自動化協定堆疊。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | 開源輕量級物聯網平台，含閘道與裝置管理。 | — |
| **車聯網** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | JT/T 808 車輛定位協定：70+ 訊息體，涵蓋 2011/2013/2019 三個版本。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | JT/T 809 部標平台互聯協定，面向運輸管理部門。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | JT/T 1078 車載視訊：RTP 解析、FLV/fMP4 轉封裝與直播推送。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **領域元件** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | 一個套件涵蓋 Excel、Word、PPT、PDF 等 15+ 文件格式。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | 跨平台音訊：編解碼、裝置 IO、DSP、容器與串流處理。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | 視訊編解碼、封裝格式、影格處理與串流。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | 百度／高德／騰訊／天地圖統一介面，支援地理編碼與座標轉換。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | IPv4 歸屬地：離線記憶體映射資料庫 + 線上 API 雙來源。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **工具與產品** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | 開發者工具箱：網路、序列埠、加解密、正規表示式、Modbus 與 MQTT。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | 自架 Git 原始碼管理系統。 | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | 從較舊的宿主啟動時，自動安裝相符的 .NET 執行階段。 | — |

> 徽章取各專案主套件；主題套件、擴充套件與衛星套件見對應 NuGet 頁面。未在表中列出的 30 多個套件（FTP、LoRa、Office 擴充、各類協定驅動等）見 [NewLifeX 組織](https://github.com/NewLifeX) 與 [newlifex.com](https://newlifex.com)。


## 新生命開發團隊

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

新生命團隊成立於 2002 年，二十餘年專注 .NET 與物聯網技術生態，主導開源專案近百個（生態版圖收錄 34 個主幹專案），為電力、物流、醫療、工控、網際網路等行業提供軟硬體一體化解決方案；以「學無先後達者為師」為信條。

開源元件在 NuGet 累計下載 <!--nuget-total:start-->6.48M<!--nuget-total:end--> 次（全生態口徑，截至 2026-10）。

網站：https://newlifex.com · 開源組織：https://github.com/NewLifeX

<details>
<summary>微信公眾號：智能大石头（掃碼關注）</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>

