# NewLife.NovaDb

[![English](https://img.shields.io/badge/lang-English-blue.svg)](README.md) [![简体中文](https://img.shields.io/badge/lang-简体中文-red.svg)](README_CN.md) [![繁體中文](https://img.shields.io/badge/lang-繁體中文-orange.svg)](README_TW.md) [![Français](https://img.shields.io/badge/lang-Français-green.svg)](README_FR.md) [![Deutsch](https://img.shields.io/badge/lang-Deutsch-yellow.svg)](README_DE.md) [![日本語](https://img.shields.io/badge/lang-日本語-purple.svg)](README_JA.md) [![한국어](https://img.shields.io/badge/lang-한국어-brightgreen.svg)](README_KO.md) [![Русский](https://img.shields.io/badge/lang-Русский-lightgrey.svg)](README_RU.md) [![Español](https://img.shields.io/badge/lang-Español-yellow.svg)](README_ES.md) [![Português](https://img.shields.io/badge/lang-Português-blue.svg)](README_PT.md)

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
  - **Flux Engine**（時系列 + MQ）: 時間ベースのシャーディング Append Only、TTL 自動クリーンアップ、Redis Stream スタイルのコンシューマグループ + Pending + Ack をサポート
  - **KV モード**（論理ビュー）: Nova Engine を再利用、API は SQL の詳細を隠し、各行に `Key + Value + TTL` が含まれます
  - **ADO.NET Provider**: 組み込み/サーバーモードを自動認識、XCode ORM とのネイティブ統合
- **動的ホットコールドインデックス分離**: ホットデータは物理メモリに完全にロード（SkipList ノード）、コールドデータは MMF にアンロードされ、スパースディレクトリのみが保持されます。1000万行のテーブルで最新の1万行のみをクエリする場合、メモリ使用量 < 20MB。
- **純粋なマネージドコード**: ネイティブコンポーネントへの依存なし（純粋な C#/.NET）、クロスプラットフォームおよび制限された環境での展開が容易。

### ストレージエンジン

| エンジン | データ構造 | ユースケース |
|---------|-----------|-------------|
| **Nova Engine** | SkipList（メモリ+MMF ホットコールド分離） | 汎用 CRUD、設定テーブル、ビジネス注文、ユーザーデータ |
| **Flux Engine** | 時間ベースシャーディング（Append Only） | IoT センサー、ログ収集、内部メッセージキュー、監査ログ |
| **KV モード** | Nova テーブル論理ビュー | 分散ロック、キャッシング、セッションストレージ、カウンター、設定センター |

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
| GeoPoint | `GEOPOINT` | カスタム構造 | 緯度/経度座標（予定） |
| Vector | `VECTOR(n)` | `Single[]` | AIベクトル検索（予定） |

### SQL 機能

標準 SQL サブセットを実装し、一般的なビジネスシナリオの約 60% をカバー:

| 機能 | ステータス | 説明 |
|------|----------|------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX、IF NOT EXISTS、PRIMARY KEY、UNIQUE を含む |
| DML | ✅ | INSERT（複数行）、UPDATE、DELETE、UPSERT（ON DUPLICATE KEY UPDATE）、TRUNCATE TABLE |
| クエリ | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| 集計 | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN（Nested Loop）、テーブルエイリアスをサポート |
| パラメータ化 | ✅ | @param プレースホルダー |
| トランザクション | ✅ | MVCC、Read Committed、COMMIT/ROLLBACK |
| SQL 関数 | ⚠️ | 文字列/数値/日付/変換/条件関数（予定） |
| サブクエリ | ⚠️ | IN/EXISTS（予定） |
| 高度 | ❌ | ビュー/トリガー/ストアドプロシージャ/ウィンドウ関数なし |

### MQ 機能（Flux Engine）

Redis Stream のコンシューマグループモデルに基づく:

- **メッセージ ID**: タイムスタンプ + シーケンス番号（同じミリ秒内で自動インクリメント）、グローバル順序
- **コンシューマグループ**: `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **信頼性**: At-Least-Once、読み取り後に Pending に入り、ビジネス成功後に Ack
- **データ保持**: TTL をサポート（時間/ファイルサイズで古いシャードを自動削除）
- **遅延メッセージ**: `DelayTime`/`DeliverAt` を指定（予定）
- **デッドレターキュー**: 消費失敗時に自動的に DLQ に入る（予定）
- **ブロッキング読み取り**: ロングポーリング + タイムアウト（予定）

### KV 機能

- `Get(key)` / `Set(key, value, ttl)` / `Delete(key)` / `Exists(key)`
- 遅延削除（読み取り時に有効期限をチェック） + バックグラウンドクリーンアップ（`CleanupExpired()`）
- `Add(key, value, ttl)`: キーが存在しない場合にのみ追加（予定）
- `Inc(key, delta, ttl)`: アトミックインクリメント（予定）

## データセキュリティと WAL モード

NovaDb は 3 つの WAL 永続化戦略を提供します:

| モード | 説明 | ユースケース |
|--------|------|-------------|
| `FULL` | 同期ディスク書き込み、各コミット時に即座にフラッシュ | 金融/取引シナリオ、最強のデータ安全性 |
| `NORMAL` | 非同期 1s フラッシュ（デフォルト） | ほとんどのビジネスシナリオ、パフォーマンスと安全性のバランス |
| `NONE` | 完全非同期、積極的なフラッシュなし | 一時データ/キャッシュシナリオ、最高スループット |

> 同期（`FULL`）以外のモードを選択すると、クラッシュ/停電シナリオでデータ損失が発生する可能性を受け入れることを意味します。

## ロードマップ

| バージョン | 予定機能 |
|-----------|---------|
| **v1.0**（完了） | 組み込み+サーバーデュアルモード、Nova/Flux/KV エンジン、SQL DDL/DML/SELECT/JOIN、トランザクション/MVCC、WAL/リカバリ、ホットコールド分離、シャーディング、MQ コンシューマグループ、ADO.NET Provider、クラスターマスタースレーブ同期 |
| **v1.1** | P0 レベル SQL 関数（文字列/数値/日付/変換/条件 ~30 関数） |
| **v1.2** | MQ ブロッキング読み取り、KV Add/Inc 操作、P1 レベル SQL 関数 |
| **v1.3** | MQ 遅延メッセージ、デッドレターキュー |
| **v2.0** | GeoPoint ジオコーディング + Vector 型（AI ベクトル検索）、可観測性と管理ツール |

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
