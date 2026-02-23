# NewLife.NovaDb

[![English](https://img.shields.io/badge/lang-English-blue.svg)](README.md) [![简体中文](https://img.shields.io/badge/lang-简体中文-red.svg)](README_CN.md) [![繁體中文](https://img.shields.io/badge/lang-繁體中文-orange.svg)](README_TW.md) [![Français](https://img.shields.io/badge/lang-Français-green.svg)](README_FR.md) [![Deutsch](https://img.shields.io/badge/lang-Deutsch-yellow.svg)](README_DE.md) [![日本語](https://img.shields.io/badge/lang-日本語-purple.svg)](README_JA.md) [![한국어](https://img.shields.io/badge/lang-한국어-brightgreen.svg)](README_KO.md) [![Русский](https://img.shields.io/badge/lang-Русский-lightgrey.svg)](README_RU.md) [![Español](https://img.shields.io/badge/lang-Español-yellow.svg)](README_ES.md) [![Português](https://img.shields.io/badge/lang-Português-blue.svg)](README_PT.md)

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
  - **KV 模式**（邏輯視圖）：復用 Nova Engine，API 遮蔽 SQL 細節，每行 `Key + Value + TTL`
  - **ADO.NET Provider**：嵌入/伺服器自動識別，相容 XCode ORM 原生整合
- **動態冷熱分離索引**：熱資料完整載入至實體記憶體（SkipList 節點），冷資料卸載至 MMF 僅保留稀疏目錄。1000 萬行表僅查最新 1 萬行時，記憶體佔用 < 20MB。
- **純託管程式碼**：不依賴 Native 元件（純 C#/.NET），便於跨平台與受限環境部署。

### 儲存引擎

| 引擎 | 資料結構 | 適用場景 |
|------|----------|----------|
| **Nova Engine** | SkipList（記憶體+MMF 冷熱分離） | 通用 CRUD、設定表、業務訂單、使用者資料 |
| **Flux Engine** | 按時間分片（Append Only） | IoT 感測器、日誌收集、內部訊息佇列、稽核日誌 |
| **KV 模式** | Nova 表邏輯視圖 | 分散式鎖、快取、會話儲存、計數器、設定中心 |

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
| **v2.0** | GeoPoint 地理編碼 + Vector 向量類型（AI 向量檢索）、可觀測性與管理工具 |

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
