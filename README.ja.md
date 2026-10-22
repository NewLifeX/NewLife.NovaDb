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
🌐 [English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [한국어](README.ko.md) | [العربية](README.ar.md)
<!-- lang:end -->

> ⚠️ この翻訳は最新版より遅れている場合があります。基準となるのは [English README](README.md) です。

**C#** で実装され、**.NET プラットフォーム**（.NET Framework 4.5 ~ .NET 10 をサポート）で動作する中大規模ハイブリッドデータベース。組み込み/サーバーのデュアルモードをサポートし、リレーショナル、時系列、メッセージキュー、NoSQL(KV) 機能を統合します。

## 製品紹介

`NewLife.NovaDb`（略称 `Nova`）は、NewLife エコシステムのコアインフラストラクチャであり、.NET アプリケーション向けの統合データエンジンです。多くのニッチな機能（ストアドプロシージャ/トリガー/ウィンドウ関数など）を削除することで、より高い読み取り/書き込みパフォーマンスとより低い運用コストを実現します。データ量は論理的に無制限（ディスクとパーティショニング戦略によって制約）であり、特定のシナリオで SQLite/MySQL/Redis/TDengine を置き換えることができます。

### コア機能

- **デュアルデプロイメントモード**:
  - **組み込みモード**: SQLite のようにライブラリとして実行され、データはローカルフォルダに保存され、設定不要
  - **サーバーモード**: スタンドアロンプロセス + TCP プロトコル、MySQL のようにネットワークアクセス。クラスターデプロイメントとマスタースレーブレプリケーション（1マスタ、複数スレーブ）をサポート
- **フォルダ＝データベース**: フォルダをコピーして移行/バックアップを完了、dump/restore プロセス不要。各テーブルには独立したファイルグループ（`.data`/`.idx`/`.wal`）があります。
- **4エンジン統合**:
  - **Nova Engine**（汎用リレーショナル）: SkipList インデックス + MVCC トランザクション（Read Committed）、CRUD、SQL クエリ、JOIN をサポート
  - **Flux Engine**（時系列 + MQ）: 時間ベースのシャーディング Append Only、TTL 自動クリーンアップ、Redis Stream スタイルのコンシューマグループ + Pending + Ack をサポート。カラムナーブロック封止と型別エンコーディング（Delta/DeltaDelta/XOR/RLE/辞書）、LZ4 ブロック圧縮、自動集計クエリルーティング付きのロールアップ事前集計、SESSION/STATE ウィンドウグループ化、`_sys.flux_compression` による圧縮率レポート
  - **KV モード**（キーバリューストア）: 独立した Bitcask エンジン（メモリ内インデックス + ディスク上の値）、TTL、マルチテーブル分離をサポート
  - **ADO.NET Provider**: 組み込み/サーバーモードを自動認識、XCode ORM とのネイティブ統合
- **動的ホットコールドインデックス分離**: ホットデータは物理メモリに完全にロード（SkipList ノード）、コールドデータは MMF にアンロードされ、スパースディレクトリのみが保持されます。1000万行のテーブルで最新の1万行のみをクエリする場合、メモリ使用量 < 20MB。
- **純粋なマネージドコード**: ネイティブコンポーネントへの依存なし（純粋な C#/.NET）、クロスプラットフォームおよび制限された環境での展開が容易。

### ストレージエンジン

| エンジン | データ構造 | ユースケース |
|---------|-----------|-------------|
| **Nova Engine** | SkipList（メモリ+MMF ホットコールド分離） | 汎用 CRUD、設定テーブル、ビジネス注文、ユーザーデータ |
| **Flux Engine** | 時間ベースシャーディング（Append Only） | IoT センサー、ログ収集、内部メッセージキュー、監査ログ |
| **KV モード** | Bitcask（メモリ内インデックス + ディスク上の値） | 分散ロック、キャッシング、セッションストレージ、カウンター、設定センター |

### データ型

| カテゴリ | SQL型 | C# マッピング | 説明 |
|---------|-------|--------------|------|
| Boolean | `BOOL` | `Boolean` | 1バイト |
| Integer | `INT` / `LONG` | `Int32` / `Int64` | 4/8バイト |
| Float | `DOUBLE` | `Double` | 8バイト |
| Decimal | `DECIMAL` | `Decimal` | 128ビット、統一精度 |
| String | `STRING(n)` / `STRING` | `String` | UTF-8、長さ指定可能 |
| Binary | `BINARY(n)` / `BLOB` | `Byte[]` | 長さ指定可能 |
| DateTime | `DATETIME` | `DateTime` | Ticks（100ナノ秒）の精度 |
| GeoPoint | `GEOPOINT` | カスタム構造 | 緯度/経度座標、距離/ポリゴンクエリに対応 |
| Vector | `VECTOR(n)` | `Single[]` | AI ベクトル検索、KNN 近傍クエリに対応 |

### SQL 機能

標準 SQL サブセットを実装し、一般的なビジネスシナリオの約 60% をカバー:

| 機能 | ステータス | 説明 |
|------|----------|------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX/DATABASE、ALTER TABLE（ADD/MODIFY/DROP COLUMN、COMMENT）、IF NOT EXISTS、PRIMARY KEY、UNIQUE、ENGINE を含む |
| DML | ✅ | INSERT（複数行）、UPDATE、DELETE、UPSERT（ON DUPLICATE KEY UPDATE）、TRUNCATE TABLE |
| クエリ | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| 集計 | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN（Nested Loop）、テーブルエイリアスをサポート |
| パラメータ化 | ✅ | @param プレースホルダー |
| トランザクション | ✅ | MVCC、Read Committed、COMMIT/ROLLBACK |
| SQL 関数 | ✅ | 文字列/数値/日付/変換/条件/ハッシュ（60以上の関数） |
| サブクエリ | ✅ | IN/EXISTS サブクエリ |
| 全文検索 | ✅ | BM25 スコアリングによる全文インデックス、RAG ハイブリッド検索用の Top-K 結果 |
| 高度 | ❌ | ビュー/トリガー/ストアドプロシージャ/ウィンドウ関数なし |

---

## 使用ガイド

### インストール

NuGet 経由で NovaDb コアパッケージをインストール:

```shell
dotnet add package NewLife.NovaDb
```

### アクセス方法

NovaDb は異なるシナリオ向けに2つのクライアントアクセス方法を提供します:

| アクセス方法 | 対象エンジン | 説明 |
|-------------|-------------|------|
| **ADO.NET + SQL** | Nova（リレーショナル）、Flux（時系列） | 標準 `DbConnection`/`DbCommand`/`DbDataReader`、すべての ORM と互換 |
| **NovaClient** | MQ（メッセージキュー）、KV（キーバリューストア） | RPC クライアント、メッセージ発行/消費/確認および KV 読み書き API を提供 |

---

### 1. リレーショナルデータベース（ADO.NET + SQL）

リレーショナルエンジン（Nova Engine）は標準 ADO.NET インターフェースを通じてアクセスします。接続文字列の `Data Source` は組み込みモード、`Server` はサーバーモードを示します。

#### 1.1 組み込みモード（5分クイックスタート）

組み込みモードはスタンドアロンサービスが不要で、デスクトップアプリ、IoT デバイス、ユニットテストに最適です。

```csharp
using NewLife.NovaDb.Client;

// 接続の作成（組み込みモード、フォルダ＝データベース）
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

// テーブルの作成
using var cmd = conn.CreateCommand();
cmd.CommandText = @"CREATE TABLE IF NOT EXISTS users (
    id   INT PRIMARY KEY AUTO_INCREMENT,
    name STRING(50) NOT NULL,
    age  INT DEFAULT 0,
    created DATETIME
)";
cmd.ExecuteNonQuery();

// データの挿入
cmd.CommandText = "INSERT INTO users (name, age, created) VALUES ('Alice', 25, NOW())";
cmd.ExecuteNonQuery();

// バッチ挿入
cmd.CommandText = @"INSERT INTO users (name, age) VALUES
    ('Bob', 30),
    ('Charlie', 28)";
cmd.ExecuteNonQuery();

// データのクエリ
cmd.CommandText = "SELECT * FROM users WHERE age >= 25 ORDER BY age";
using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"id={reader["id"]}, name={reader["name"]}, age={reader["age"]}");
}
```

#### 1.2 サーバーモード

サーバーモードは TCP 経由でリモートアクセスを提供し、複数の同時クライアント接続をサポートします。接続文字列の各データベース（`Database=mydb`）は、サーバーのデータフォルダ配下の独立したエンジンディレクトリにマッピングされ、1 台のサーバー上でマルチデータベースルーティングを実現します。

**サーバーの起動:**

```csharp
using NewLife.NovaDb.Server;

var svr = new NovaServer(3306) { DbPath = "./data" };
svr.Start();
Console.ReadLine();
svr.Stop("Manual shutdown");
```

**ADO.NET クライアント接続（組み込みモードと同一の API）:**

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

#### 1.3 パラメータ化クエリ

パラメータ化クエリは `@name` 名前付きパラメータを使用して SQL インジェクションを防止します:

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

#### 1.4 集計とスカラー取得

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT COUNT(*) FROM users";
var count = Convert.ToInt32(cmd.ExecuteScalar());
Console.WriteLine($"Total users: {count}");

cmd.CommandText = "SELECT AVG(age) FROM users WHERE age > 0";
var avgAge = Convert.ToDouble(cmd.ExecuteScalar());
Console.WriteLine($"Average age: {avgAge:F1}");
```

#### 1.5 トランザクション

NovaDb は MVCC ベースのトランザクション分離を実装しており、デフォルトの分離レベルは Read Committed です:

```csharp
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

using var tx = conn.BeginTransaction();
try
{
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;

    // 在庫を減算
    cmd.CommandText = "UPDATE products SET stock = stock - 1 WHERE id = 1 AND stock > 0";
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) throw new InvalidOperationException("Insufficient stock");

    // 注文を作成
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

#### 1.6 JOIN クエリ

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

#### 1.7 DDL 操作

```sql
-- データベースの作成/削除
CREATE DATABASE shop;
DROP DATABASE shop;

-- テーブル構造の変更
ALTER TABLE products ADD COLUMN category STRING;
ALTER TABLE products MODIFY COLUMN name STRING(200);
ALTER TABLE products DROP COLUMN category;

-- インデックス管理
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

#### 1.9 接続文字列リファレンス

| パラメータ | 例 | 説明 |
|-----------|-----|------|
| `Data Source` | `Data Source=./mydb` | 組み込みモード、データベースフォルダパス |
| `Server` | `Server=127.0.0.1` | サーバーモード、サーバーアドレス |
| `Port` | `Port=3306` | サーバーポート（デフォルト 3306） |
| `Database` | `Database=mydb` | データベース名 |
| `WalMode` | `WalMode=Full` | WAL モード（Full/Normal/None） |
| `ReadOnly` | `ReadOnly=true` | 読み取り専用モード |

---

### 2. 時系列データベース（ADO.NET + SQL）

時系列エンジン（Flux Engine）も ADO.NET + SQL 経由でアクセスします。テーブル作成時に `ENGINE=FLUX` を指定します。

#### 2.1 時系列テーブルの作成

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

#### 2.2 時系列データの書き込み

```csharp
// 単一挿入
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity)
    VALUES (NOW(), 'sensor-001', 23.5, 65.0)";
cmd.ExecuteNonQuery();

// バッチ挿入
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity) VALUES
    ('2025-07-01 10:00:00', 'sensor-001', 22.1, 60.0),
    ('2025-07-01 10:01:00', 'sensor-001', 22.3, 61.0),
    ('2025-07-01 10:02:00', 'sensor-002', 25.0, 55.0)";
cmd.ExecuteNonQuery();
```

#### 2.3 時間範囲クエリ

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

#### 2.4 集計分析

```csharp
// デバイスごとの平均気温
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

#### 2.5 データ保持ポリシー

時系列テーブルは TTL による期限切れシャードの自動クリーンアップをサポートし、時間または容量に基づいてデータを保持します:

```csharp
// DbOptions 経由で時系列パラメータを設定
var options = new DbOptions
{
    FluxPartitionHours = 1,        // 1 時間ごとに 1 パーティション
    FluxDefaultTtlSeconds = 86400, // データを 24 時間保持
};
```

---

### 3. メッセージキュー（NovaClient）

NovaDb は Flux 時系列エンジンに基づいた Redis Stream スタイルのメッセージキューを実装しています。メッセージキューは `NovaClient` RPC インターフェースを通じてアクセスします。

#### 3.1 サーバーへの接続

```csharp
using NewLife.NovaDb.Client;

using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();
```

#### 3.2 メッセージの発行

```csharp
// RPC 経由で SQL を実行し、Flux テーブルにメッセージを挿入
var affected = await client.ExecuteAsync(
    "INSERT INTO order_events (timestamp, orderId, action, amount) " +
    "VALUES (NOW(), 10001, 'created', 299.00)");
Console.WriteLine($"Message published, affected rows: {affected}");
```

#### 3.3 メッセージの消費

```csharp
// メッセージを読み取る（時間範囲指定）
var messages = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT * FROM order_events WHERE timestamp > @since ORDER BY timestamp LIMIT 10",
    new { since = DateTime.Now.AddMinutes(-5) });
```

#### 3.4 ハートビート

```csharp
var serverTime = await client.PingAsync();
Console.WriteLine($"Server connected: {serverTime}");
Console.WriteLine($"Is connected: {client.IsConnected}");
```

#### 3.5 MQ コア機能

- **メッセージ ID**: タイムスタンプ + シーケンス番号（同じミリ秒内で自動インクリメント）、グローバル順序
- **コンシューマグループ**: `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **信頼性**: At-Least-Once、読み取り後に Pending に入り、ビジネス成功後に Ack
- **データ保持**: TTL をサポート（時間/ファイルサイズで古いシャードを自動削除）
- **遅延メッセージ**: 遅延時間または正確な配信時刻を指定
- **デッドレターキュー**: 最大リトライ回数を超えた後、自動的に DLQ に入る

---

### 4. KV キーバリューストア（NovaClient）

KV ストレージは `NovaClient` を通じてアクセスします。テーブル作成時に `ENGINE=KV` を指定します。KV テーブルは `Key + Value + TTL` の固定スキーマを持ちます。

#### 4.1 KV テーブルの作成

```csharp
using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();

// KV テーブルを作成（デフォルト TTL 7200 秒 = 2 時間）
await client.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS session_cache (
    Key STRING(200) PRIMARY KEY, Value BLOB, TTL DATETIME
) ENGINE=KV DEFAULT_TTL=7200");
```

#### 4.2 データの読み書き

```csharp
// 書き込み（UPSERT セマンティクス）
await client.ExecuteAsync(
    "INSERT INTO session_cache (Key, Value, TTL) VALUES ('session:1001', 'user-data', " +
    "DATEADD(NOW(), 30, 'MINUTE')) ON DUPLICATE KEY UPDATE Value = 'user-data'");

// 読み取り
var result = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT Value FROM session_cache WHERE Key = 'session:1001' " +
    "AND (TTL IS NULL OR TTL > NOW())");

// 削除
await client.ExecuteAsync("DELETE FROM session_cache WHERE Key = 'session:1001'");
```

#### 4.3 アトミックインクリメント（カウンター）

```csharp
await client.ExecuteAsync(
    "INSERT INTO counters (Key, Value) VALUES ('page:views', 1) " +
    "ON DUPLICATE KEY UPDATE Value = Value + 1");
```

#### 4.4 分散ロック

```csharp
// ロックの取得を試みる（キーが存在しない場合のみ挿入が成功）
var locked = await client.ExecuteAsync(
    "INSERT INTO dist_lock (Key, Value, TTL) VALUES ('lock:order:123', 'worker-1', " +
    "DATEADD(NOW(), 30, 'SECOND'))");

if (locked > 0)
{
    try
    {
        // ロック取得済み、ビジネスロジックを実行
    }
    finally
    {
        await client.ExecuteAsync("DELETE FROM dist_lock WHERE Key = 'lock:order:123'");
    }
}
```

#### 4.5 KV 機能概要

| 操作 | 説明 |
|------|------|
| `Get` | 遅延 TTL チェック付きで値を読み取り |
| `Set` | オプションの TTL 付きで値を設定 |
| `Add` | Key が存在しない場合にのみ追加（分散ロック） |
| `Delete` | キーを削除 |
| `Inc` | アトミックインクリメント/デクリメント（カウンター） |
| `TTL` | 有効期限切れ時に自動的に非表示、定期的なバックグラウンドクリーンアップ |

---

## データセキュリティと WAL モード

NovaDb は 3 つの WAL 永続化戦略を提供します:

| モード | 説明 | ユースケース |
|--------|------|-------------|
| `FULL` | 同期ディスク書き込み、各コミット時に即座にフラッシュ | 金融/取引シナリオ、最強のデータ安全性 |
| `NORMAL` | 非同期 1s フラッシュ（デフォルト） | ほとんどのビジネスシナリオ、パフォーマンスと安全性のバランス |
| `NONE` | 完全非同期、積極的なフラッシュなし | 一時データ/キャッシュシナリオ、最高スループット |

> 同期（`FULL`）以外のモードを選択すると、クラッシュ/停電シナリオでデータ損失が発生する可能性を受け入れることを意味します。

## クラスターデプロイメント

NovaDb は Binlog による非同期データレプリケーションを使用した**1マスタ、複数スレーブ**アーキテクチャをサポートします:

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

- マスターノードがすべての書き込み操作を処理し、スレーブノードは読み取り専用クエリを提供
- Binlog による非同期レプリケーション、ブレークポイントからの再開をサポート
- アプリケーション層が読み書き分離を担当

## ロードマップ

| バージョン | 予定機能 |
|-----------|---------|
| **v1.0**（完了） | 組み込み+サーバーデュアルモード、Nova/Flux/KV エンジン、SQL DDL/DML/SELECT/JOIN、トランザクション/MVCC、WAL/リカバリ、ホットコールド分離、シャーディング、MQ コンシューマグループ、ADO.NET Provider、クラスターマスタースレーブ同期 |
| **v1.1**（完了） | P0 レベル SQL 関数（文字列/数値/日付/変換/条件 ~30 関数） |
| **v1.2**（完了） | MQ ブロッキング読み取り、KV Add/Inc 操作、P1 レベル SQL 関数 |
| **v1.3**（完了） | MQ 遅延メッセージ、デッドレターキュー |
| **v2.0**（完了） | GeoPoint ジオコーディング + Vector 型（AI ベクトル検索）、GIS 拡張、全文検索（BM25）、NovaVectorStore（IVectorStore、マルチコレクション）、インクリメンタルマテリアライズドビュー、Binlog 管理、可観測性と管理ツール |
| **v2.1**（完了） | 時系列のカラムナーブロック封止 + ブロック圧縮（数値エンコーディング + LZ4）、バックフィルと自動クエリルーティング付きのロールアップ事前集計、SESSION/STATE_WINDOW グループ化、圧縮率レポート、マルチ RID CI（win-x64/linux-x64/linux-arm64/loongarch64）による NativeAOT サポート |

## ポジショニング

NovaDb は完全な SQL92 標準準拠を追求せず、一般的に使用されるビジネスサブセットの 80% をカバーし、次の差別化された機能と引き換えにします:

| 差別化 | 説明 |
|--------|------|
| **純粋な .NET マネージド** | ネイティブ依存なし、xcopy 経由でデプロイ、.NET アプリケーションと同じプロセスでシリアル化オーバーヘッドなし |
| **組み込み+サーバーデュアルモード** | 開発/デバッグでは SQLite のように組み込み、本番では MySQL のようにスタンドアロンサービス、同じ API |
| **フォルダ＝データベース** | フォルダをコピーして移行/バックアップを完了、dump/restore 不要 |
| **ホットコールドインデックス分離** | 1000万行テーブルでホットスポットのみをクエリする場合、メモリ < 20MB、コールドデータは自動的に MMF にアンロード |
| **4エンジン統合** | 単一コンポーネントで一般的な SQLite + TDengine + Redis シナリオをカバー、運用コンポーネント数を削減 |
| **NewLife ネイティブ統合** | XCode ORM + ADO.NET と直接適応、サードパーティドライバー不要 |

## エコシステムマップ

NewLife は、独立したバージョン管理を行うオープンソースプロジェクトの階層型エコシステムです。以下のマップはすべての NewLife リポジトリで逐語的に同一の内容を共有しており、各プロジェクトはコードリポジトリと主要な NuGet パッケージにリンクしています。

| プロジェクト | 概要 | ダウンロード数 |
| --- | --- | --- |
| **基盤** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | ログ、構成、キャッシュ、ネットワーク、シリアライズ、スケジューリング、APM トレーシング。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | データミドルウェア：単一テーブルで数百億件級、自動シャーディング、読み書き分離、10 種類以上のデータベースに対応。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | 高性能ネットワークライブラリ：単一マシンで 177 万 pps、14.5 Gbps（TCP 1KB、2026-10 実測）、400 万 Tcp 長接続。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | CS アプリケーションと IoT デバイス向けのプロトコル通信と RPC フレームワーク。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | .NET アプリケーションを OS デーモンとしてインストール：Windows サービスと Linux systemd。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | コンソール、Web、API、サービス、データ、IoT 向けの `dotnet new` プロジェクトテンプレート。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **データとメッセージング** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | マイクロ秒級の遅延、数百万 ops/s のスループット、メッセージキュー内蔵の Redis クライアント。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | Apache RocketMQ と Alibaba Cloud Message Queue に対応した純マネージド RocketMQ クライアント。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | MQTT クライアントとサーバー。Alibaba Cloud IoT への接続を含みます。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | C# 製ハイブリッドデータベース：組み込み/ネットワークの両モード、リレーショナル、時系列、メッセージキュー、KV を 1 つに。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | NewLife データスタック上に構築された MySql ドライバーとヘルパー層。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **Web、AI、分散** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | 高速管理プラットフォームフレームワーク：権限、SSO、OAuth サーバー、CRUD スキャフォールディング。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | 分散サービスプラットフォーム：ノード管理、APM、構成センター、登録センター、リリースセンター。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | シャーディング、リトライ、バックフィルに対応した分散タスクとリアルタイムデータのスケジューリングプラットフォーム。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | 46 社の LLM プロバイダーに統一接続するクライアントと、すぐに使える Web チャットアプリ ChatAI。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **産業と IoT** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | プロトコル契約とデバイス抽象を定義する IoT 標準ライブラリ。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | NewLife.IoT 上に実装された Modbus TCP/RTU/ASCII。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | NewLife.IoT 上に実装されたシーメンス PLC プロトコル。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | NewLife.IoT 上に実装されたオムロン PLC プロトコル。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | NewLife.IoT 上に実装された三菱 Melsec PLC プロトコル。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | 産業データアクセス向けの OPC クライアント。 | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | BACnet ビル管理プロトコルスタック。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | ゲートウェイとデバイス管理を備えた軽量なオープンソース IoT プラットフォーム。 | — |
| **車載テレマティクス** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | JT/T 808 車両位置情報プロトコル：70 以上のメッセージボディ、2011/2013/2019 に対応。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | 運輸管理部門向けの JT/T 809 プラットフォーム間プロトコル。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | JT/T 1078 車載ビデオ：RTP 解析、FLV/fMP4 リマックス、ライブ配信。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **ドメインコンポーネント** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | Excel、Word、PPT、PDF など 15 以上のドキュメント形式を 1 パッケージで。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | クロスプラットフォーム音声：コーデック、デバイス IO、DSP、コンテナ、ストリーミング。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | ビデオコーデック、コンテナ形式、フレーム処理、ストリーミング。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | 百度地図、高徳地図、テンセント地図、天地図を統一インターフェイス化。ジオコーディングと座標変換に対応。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | IPv4 位置情報：オフラインのメモリマップデータベースとオンライン API のフォールバック。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **ツールと製品** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | 開発者向けツールボックス：ネットワーク、シリアルポート、暗号化、正規表現、Modbus、MQTT。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | セルフホスト型 Git ソースコード管理システム。 | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | 古いホストから起動した際に対応する .NET ランタイムを自動インストールします。 | — |

> バッジは各プロジェクトの主要パッケージを示します。テーマ、拡張、サテライトパッケージは各 NuGet ページを参照してください。ここに記載していない 30 以上のパッケージ（FTP、LoRa、Office 拡張、各種プロトコルドライバーなど）は [NewLifeX 組織](https://github.com/NewLifeX) と [newlifex.com](https://newlifex.com) で公開しています。


## 新生命開発チーム

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

新生命チームは 2002 年に設立され、20 年以上にわたり .NET と IoT の技術エコシステムに注力し、100 近いオープンソースプロジェクトを主導し（うち 34 が主要プロジェクト）、電力、物流、医療、産業制御、インターネットなどの業界にソフトウェアとハードウェアを一体化したソリューションを提供しています。「学問に先後なし、達する者が師である」を信条としています。

オープンソースコンポーネントは NuGet で累計 <!--nuget-total:start-->6.48M<!--nuget-total:end--> 回ダウンロードされています（エコシステム全体、2026-10 時点）。

ウェブサイト：https://newlifex.com · オープンソース組織：https://github.com/NewLifeX

<details>
<summary>WeChat 公式アカウント：智能大石头（スキャンしてフォロー）</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>

