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
| DDL | ✅ | CREATE/DROP TABLE/INDEX，含 IF NOT EXISTS、PRIMARY KEY、UNIQUE |
| DML | ✅ | INSERT（多行）、UPDATE、DELETE、UPSERT（ON DUPLICATE KEY UPDATE）、TRUNCATE TABLE |
| 查詢 | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| 聚合 | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN（Nested Loop），支援表別名 |
| 參數化 | ✅ | @param 佔位符 |
| 交易 | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| SQL 函數 | ⚠️ | 字串/數值/日期/轉換/條件函數（規劃中） |
| 子查詢 | ⚠️ | IN/EXISTS（規劃中） |
| 進階 | ❌ | 無視圖/觸發器/儲存程序/視窗函數 |

### MQ 能力（Flux Engine）

參考 Redis Stream 的消費組模型：

- **訊息 ID**：時間戳記 + 序號（同毫秒自增），全域有序
- **消費組**：`Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **可靠性**：At-Least-Once，讀取後進入 Pending，業務成功後 Ack
- **資料保留**：支援 TTL（按時間/檔案大小自動刪除舊分片）
- **延遲訊息**：指定 `DelayTime`/`DeliverAt`（規劃中）
- **死信佇列**：消費失敗自動進入 DLQ（規劃中）
- **阻塞讀取**：長輪詢 + 逾時（規劃中）

### KV 能力

- `Get(key)` / `Set(key, value, ttl)` / `Delete(key)` / `Exists(key)`
- 惰性刪除（讀時檢查過期） + 背景清理（`CleanupExpired()`）
- `Add(key, value, ttl)`：僅當 key 不存在時新增（規劃中）
- `Inc(key, delta, ttl)`：原子遞增（規劃中）

## 資料安全與 WAL 模式

NovaDb 提供三種 WAL 持久化策略：

| 模式 | 說明 | 適用場景 |
|------|------|----------|
| `FULL` | 同步落盤，每次提交立即刷盤 | 金融/交易場景，最強資料安全 |
| `NORMAL` | 非同步 1s 刷盤（預設） | 大多數業務場景，平衡效能與安全 |
| `NONE` | 全非同步，不主動刷盤 | 臨時資料/快取場景，最高吞吐量 |

> 只要不選擇同步模式（`FULL`），就意味著接受在崩潰/斷電等場景下可能發生資料遺失。

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
