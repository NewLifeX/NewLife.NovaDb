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
🌐 [English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [العربية](README.ar.md)
<!-- lang:end -->

> ⚠️ 이 번역은 최신 버전보다 뒤처질 수 있으며, 기준은 [English README](README.md)입니다.

**C#**로 구현되고 **.NET 플랫폼**(.NET Framework 4.5 ~ .NET 10 지원)에서 실행되는 중대형 하이브리드 데이터베이스로, 임베디드/서버 듀얼 모드를 지원하고 관계형, 시계열, 메시지 큐, NoSQL(KV) 기능을 통합합니다.

## 제품 소개

`NewLife.NovaDb`(약칭 `Nova`)는 NewLife 생태계의 핵심 인프라로, .NET 애플리케이션을 위한 통합 데이터 엔진입니다. 많은 틈새 기능(저장 프로시저/트리거/윈도우 함수 등)을 제거하여 더 높은 읽기/쓰기 성능과 더 낮은 운영 비용을 달성합니다. 데이터 볼륨은 논리적으로 무제한(디스크 및 파티셔닝 전략으로 제약)이며 특정 시나리오에서 SQLite/MySQL/Redis/TDengine을 대체할 수 있습니다.

### 핵심 기능

- **듀얼 배포 모드**:
  - **임베디드 모드**: SQLite처럼 라이브러리 형태로 실행되며 데이터는 로컬 폴더에 저장, 제로 구성
  - **서버 모드**: 독립 프로세스 + TCP 프로토콜, MySQL처럼 네트워크 액세스; 클러스터 배포 및 마스터-슬레이브 복제(1개 마스터, 여러 슬레이브) 지원
- **폴더=데이터베이스**: 폴더를 복사하여 마이그레이션/백업 완료, dump/restore 프로세스 불필요. 각 테이블은 독립적인 파일 그룹(`.data`/`.idx`/`.wal`)을 가집니다.
- **4개 엔진 통합**:
  - **Nova Engine**(범용 관계형): SkipList 인덱스 + MVCC 트랜잭션(Read Committed), CRUD, SQL 쿼리, JOIN 지원
  - **Flux Engine**(시계열 + MQ): 시간 기반 샤딩 Append Only, TTL 자동 정리, Redis Stream 스타일 소비자 그룹 + Pending + Ack 지원. 타입 인식 인코딩(Delta/DeltaDelta/XOR/RLE/딕셔너리)과 LZ4 블록 압축을 적용한 컬럼형 블록 밀봉, 자동 집계 쿼리 라우팅을 갖춘 롤업 사전 집계, SESSION/STATE 윈도우 그룹화, `_sys.flux_compression`을 통한 압축률 보고 지원
  - **KV 모드**(키-값 저장소): 독립적인 Bitcask 엔진(메모리 인덱스 + 디스크 값), TTL, 다중 테이블 격리 지원
  - **ADO.NET Provider**: 임베디드/서버 모드 자동 인식, XCode ORM과 네이티브 통합
- **동적 핫-콜드 인덱스 분리**: 핫 데이터는 물리 메모리에 완전히 로드(SkipList 노드), 콜드 데이터는 MMF로 언로드되어 희소 디렉토리만 유지. 1000만 행 테이블에서 최신 1만 행만 쿼리할 때 메모리 사용량 < 20MB.
- **순수 관리 코드**: 네이티브 컴포넌트 종속성 없음(순수 C#/.NET), 크로스 플랫폼 및 제한된 환경에서 배포 용이.

### 스토리지 엔진

| 엔진 | 데이터 구조 | 사용 사례 |
|------|------------|-----------|
| **Nova Engine** | SkipList(메모리+MMF 핫-콜드 분리) | 범용 CRUD, 구성 테이블, 비즈니스 주문, 사용자 데이터 |
| **Flux Engine** | 시간 기반 샤딩(Append Only) | IoT 센서, 로그 수집, 내부 메시지 큐, 감사 로그 |
| **KV 모드** | Bitcask(메모리 인덱스 + 디스크 값) | 분산 락, 캐싱, 세션 저장소, 카운터, 구성 센터 |

### 데이터 타입

| 카테고리 | SQL 타입 | C# 매핑 | 설명 |
|---------|---------|---------|------|
| Boolean | `BOOL` | `Boolean` | 1바이트 |
| Integer | `INT` / `LONG` | `Int32` / `Int64` | 4/8바이트 |
| Float | `DOUBLE` | `Double` | 8바이트 |
| Decimal | `DECIMAL` | `Decimal` | 128비트, 통일 정밀도 |
| String | `STRING(n)` / `STRING` | `String` | UTF-8, 길이 지정 가능 |
| Binary | `BINARY(n)` / `BLOB` | `Byte[]` | 길이 지정 가능 |
| DateTime | `DATETIME` | `DateTime` | Ticks(100나노초) 정밀도 |
| GeoPoint | `GEOPOINT` | 사용자 정의 구조 | 위도/경도 좌표, 거리/다각형 쿼리 지원 |
| Vector | `VECTOR(n)` | `Single[]` | AI 벡터 검색, KNN 최근접 이웃 쿼리 지원 |

### SQL 기능

표준 SQL 부분 집합 구현, 일반적인 비즈니스 시나리오의 약 60% 커버:

| 기능 | 상태 | 설명 |
|------|------|------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX/DATABASE, ALTER TABLE (ADD/MODIFY/DROP COLUMN, COMMENT), IF NOT EXISTS, PRIMARY KEY, UNIQUE, ENGINE 포함 |
| DML | ✅ | INSERT(여러 행), UPDATE, DELETE, UPSERT(ON DUPLICATE KEY UPDATE), TRUNCATE TABLE |
| 쿼리 | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| 집계 | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN(Nested Loop), 테이블 별칭 지원 |
| 매개변수화 | ✅ | @param 플레이스홀더 |
| 트랜잭션 | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| SQL 함수 | ✅ | 문자열/숫자/날짜/변환/조건/해시 함수(60개 이상) |
| 서브쿼리 | ✅ | IN/EXISTS 서브쿼리 |
| 전문 검색 | ✅ | BM25 스코어링 전체 텍스트 인덱스, RAG 하이브리드 검색을 위한 Top-K 결과 |
| 고급 | ❌ | 뷰/트리거/저장 프로시저/윈도우 함수 없음 |

---

## 사용 가이드

### 설치

NuGet을 통해 NovaDb 코어 패키지를 설치합니다:

```shell
dotnet add package NewLife.NovaDb
```

### 액세스 방법

NovaDb는 다양한 시나리오에 맞는 두 가지 클라이언트 액세스 방법을 제공합니다:

| 액세스 방법 | 대상 엔진 | 설명 |
|------------|----------|------|
| **ADO.NET + SQL** | Nova(관계형), Flux(시계열) | 표준 `DbConnection`/`DbCommand`/`DbDataReader`, 모든 ORM과 호환 |
| **NovaClient** | MQ(메시지 큐), KV(키-값 저장소) | 메시지 발행/소비/확인 및 KV 읽기/쓰기 API를 제공하는 RPC 클라이언트 |

---

### 1. 관계형 데이터베이스 (ADO.NET + SQL)

관계형 엔진(Nova Engine)은 표준 ADO.NET 인터페이스를 통해 액세스합니다. 연결 문자열에서 `Data Source`는 임베디드 모드를, `Server`는 서버 모드를 나타냅니다.

#### 1.1 임베디드 모드 (5분 퀵스타트)

임베디드 모드는 독립 서비스가 필요 없으며, 데스크톱 앱, IoT 디바이스, 단위 테스트에 이상적입니다.

```csharp
using NewLife.NovaDb.Client;

// 연결 생성 (임베디드 모드, 폴더=데이터베이스)
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

// 테이블 생성
using var cmd = conn.CreateCommand();
cmd.CommandText = @"CREATE TABLE IF NOT EXISTS users (
    id   INT PRIMARY KEY AUTO_INCREMENT,
    name STRING(50) NOT NULL,
    age  INT DEFAULT 0,
    created DATETIME
)";
cmd.ExecuteNonQuery();

// 데이터 삽입
cmd.CommandText = "INSERT INTO users (name, age, created) VALUES ('Alice', 25, NOW())";
cmd.ExecuteNonQuery();

// 배치 삽입
cmd.CommandText = @"INSERT INTO users (name, age) VALUES
    ('Bob', 30),
    ('Charlie', 28)";
cmd.ExecuteNonQuery();

// 데이터 조회
cmd.CommandText = "SELECT * FROM users WHERE age >= 25 ORDER BY age";
using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"id={reader["id"]}, name={reader["name"]}, age={reader["age"]}");
}
```

#### 1.2 서버 모드

서버 모드는 TCP를 통한 원격 액세스를 제공하며, 다수의 동시 클라이언트 연결을 지원합니다. 연결 문자열의 각 데이터베이스(`Database=mydb`)는 서버 데이터 폴더 아래의 독립 엔진 디렉토리에 매핑되어, 단일 서버에서 다중 데이터베이스 라우팅이 가능합니다.

**서버 시작:**

```csharp
using NewLife.NovaDb.Server;

var svr = new NovaServer(3306) { DbPath = "./data" };
svr.Start();
Console.ReadLine();
svr.Stop("Manual shutdown");
```

**ADO.NET 클라이언트 연결 (임베디드 모드와 동일한 API):**

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

#### 1.3 매개변수화 쿼리

매개변수화 쿼리는 `@name` 명명된 매개변수를 사용하여 SQL 인젝션을 방지합니다:

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

#### 1.4 집계 및 스칼라 쿼리

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT COUNT(*) FROM users";
var count = Convert.ToInt32(cmd.ExecuteScalar());
Console.WriteLine($"Total users: {count}");

cmd.CommandText = "SELECT AVG(age) FROM users WHERE age > 0";
var avgAge = Convert.ToDouble(cmd.ExecuteScalar());
Console.WriteLine($"Average age: {avgAge:F1}");
```

#### 1.5 트랜잭션

NovaDb는 MVCC 기반 트랜잭션 격리를 구현하며, 기본 격리 수준은 Read Committed입니다:

```csharp
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

using var tx = conn.BeginTransaction();
try
{
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;

    // 재고 차감
    cmd.CommandText = "UPDATE products SET stock = stock - 1 WHERE id = 1 AND stock > 0";
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) throw new InvalidOperationException("Insufficient stock");

    // 주문 생성
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

#### 1.6 JOIN 쿼리

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

#### 1.7 DDL 작업

```sql
-- 데이터베이스 생성/삭제
CREATE DATABASE shop;
DROP DATABASE shop;

-- 테이블 구조 변경
ALTER TABLE products ADD COLUMN category STRING;
ALTER TABLE products MODIFY COLUMN name STRING(200);
ALTER TABLE products DROP COLUMN category;

-- 인덱스 관리
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

#### 1.9 연결 문자열 참조

| 매개변수 | 예시 | 설명 |
|---------|------|------|
| `Data Source` | `Data Source=./mydb` | 임베디드 모드, 데이터베이스 폴더 경로 |
| `Server` | `Server=127.0.0.1` | 서버 모드, 서버 주소 |
| `Port` | `Port=3306` | 서버 포트(기본값 3306) |
| `Database` | `Database=mydb` | 데이터베이스 이름 |
| `WalMode` | `WalMode=Full` | WAL 모드(Full/Normal/None) |
| `ReadOnly` | `ReadOnly=true` | 읽기 전용 모드 |

---

### 2. 시계열 데이터베이스 (ADO.NET + SQL)

시계열 엔진(Flux Engine)도 ADO.NET + SQL을 통해 액세스합니다. 테이블 생성 시 `ENGINE=FLUX`를 지정합니다.

#### 2.1 시계열 테이블 생성

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

#### 2.2 시계열 데이터 쓰기

```csharp
// 단일 삽입
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity)
    VALUES (NOW(), 'sensor-001', 23.5, 65.0)";
cmd.ExecuteNonQuery();

// 배치 삽입
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity) VALUES
    ('2025-07-01 10:00:00', 'sensor-001', 22.1, 60.0),
    ('2025-07-01 10:01:00', 'sensor-001', 22.3, 61.0),
    ('2025-07-01 10:02:00', 'sensor-002', 25.0, 55.0)";
cmd.ExecuteNonQuery();
```

#### 2.3 시간 범위 쿼리

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

#### 2.4 집계 분석

```csharp
// 디바이스별 평균 온도
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

#### 2.5 데이터 보존 정책

시계열 테이블은 TTL 기반으로 만료된 샤드를 자동 정리하며, 시간 또는 용량을 기준으로 데이터를 보존합니다:

```csharp
// DbOptions를 통해 시계열 매개변수 구성
var options = new DbOptions
{
    FluxPartitionHours = 1,        // 1시간당 하나의 파티션
    FluxDefaultTtlSeconds = 86400, // 데이터 24시간 보존
};
```

---

### 3. 메시지 큐 (NovaClient)

NovaDb는 Flux 시계열 엔진을 기반으로 Redis Stream 스타일의 메시지 큐를 구현합니다. 메시지 큐는 `NovaClient` RPC 인터페이스를 통해 액세스합니다.

#### 3.1 서버 연결

```csharp
using NewLife.NovaDb.Client;

using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();
```

#### 3.2 메시지 발행

```csharp
// RPC를 통해 SQL을 실행하여 Flux 테이블에 메시지 삽입
var affected = await client.ExecuteAsync(
    "INSERT INTO order_events (timestamp, orderId, action, amount) " +
    "VALUES (NOW(), 10001, 'created', 299.00)");
Console.WriteLine($"Message published, affected rows: {affected}");
```

#### 3.3 메시지 소비

```csharp
// 메시지 읽기 (시간 범위 기준)
var messages = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT * FROM order_events WHERE timestamp > @since ORDER BY timestamp LIMIT 10",
    new { since = DateTime.Now.AddMinutes(-5) });
```

#### 3.4 하트비트

```csharp
var serverTime = await client.PingAsync();
Console.WriteLine($"Server connected: {serverTime}");
Console.WriteLine($"Is connected: {client.IsConnected}");
```

#### 3.5 MQ 핵심 기능

- **메시지 ID**: 타임스탬프 + 시퀀스 번호(동일 밀리초 내 자동 증가), 전역 순서
- **소비자 그룹**: `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **신뢰성**: At-Least-Once, 읽기 후 Pending에 진입, 비즈니스 성공 후 Ack
- **데이터 보존**: TTL 지원(시간/파일 크기별 오래된 샤드 자동 삭제)
- **지연 메시지**: 지연 시간 또는 정확한 전달 시간 지정
- **데드 레터 큐**: 최대 재시도 횟수 초과 시 자동으로 DLQ 진입

---

### 4. KV 키-값 저장소 (NovaClient)

KV 저장소는 `NovaClient`를 통해 액세스합니다. 테이블 생성 시 `ENGINE=KV`를 지정합니다. KV 테이블은 `Key + Value + TTL`의 고정 스키마를 가집니다.

#### 4.1 KV 테이블 생성

```csharp
using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();

// KV 테이블 생성 (기본 TTL 7200초 = 2시간)
await client.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS session_cache (
    Key STRING(200) PRIMARY KEY, Value BLOB, TTL DATETIME
) ENGINE=KV DEFAULT_TTL=7200");
```

#### 4.2 데이터 읽기/쓰기

```csharp
// 쓰기 (UPSERT 의미론)
await client.ExecuteAsync(
    "INSERT INTO session_cache (Key, Value, TTL) VALUES ('session:1001', 'user-data', " +
    "DATEADD(NOW(), 30, 'MINUTE')) ON DUPLICATE KEY UPDATE Value = 'user-data'");

// 읽기
var result = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT Value FROM session_cache WHERE Key = 'session:1001' " +
    "AND (TTL IS NULL OR TTL > NOW())");

// 삭제
await client.ExecuteAsync("DELETE FROM session_cache WHERE Key = 'session:1001'");
```

#### 4.3 원자적 증가 (카운터)

```csharp
await client.ExecuteAsync(
    "INSERT INTO counters (Key, Value) VALUES ('page:views', 1) " +
    "ON DUPLICATE KEY UPDATE Value = Value + 1");
```

#### 4.4 분산 락

```csharp
// 락 획득 시도 (키가 존재하지 않을 때만 삽입 성공)
var locked = await client.ExecuteAsync(
    "INSERT INTO dist_lock (Key, Value, TTL) VALUES ('lock:order:123', 'worker-1', " +
    "DATEADD(NOW(), 30, 'SECOND'))");

if (locked > 0)
{
    try
    {
        // 락 획득 성공, 비즈니스 로직 실행
    }
    finally
    {
        await client.ExecuteAsync("DELETE FROM dist_lock WHERE Key = 'lock:order:123'");
    }
}
```

#### 4.5 KV 기능 개요

| 작업 | 설명 |
|------|------|
| `Get` | 지연 TTL 확인으로 값 읽기 |
| `Set` | 선택적 TTL로 값 설정 |
| `Add` | Key가 존재하지 않을 때만 추가(분산 락) |
| `Delete` | 키 삭제 |
| `Inc` | 원자적 증가/감소(카운터) |
| `TTL` | 만료 시 자동 비가시, 주기적 백그라운드 정리 |

---

## 데이터 보안 및 WAL 모드

NovaDb는 3가지 WAL 지속성 전략을 제공합니다:

| 모드 | 설명 | 사용 사례 |
|------|------|-----------|
| `FULL` | 동기 디스크 쓰기, 각 커밋 시 즉시 플러시 | 금융/거래 시나리오, 최강 데이터 안전성 |
| `NORMAL` | 비동기 1초 플러시(기본값) | 대부분의 비즈니스 시나리오, 성능과 안전성 균형 |
| `NONE` | 완전 비동기, 능동적 플러시 없음 | 임시 데이터/캐시 시나리오, 최고 처리량 |

> 동기(`FULL`) 이외의 모드를 선택하면 충돌/정전 시나리오에서 데이터 손실 가능성을 수용하는 것을 의미합니다.

## 클러스터 배포

NovaDb는 Binlog를 통한 비동기 데이터 복제를 지원하는 **1개 마스터, 여러 슬레이브** 아키텍처를 지원합니다:

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

- 마스터 노드가 모든 쓰기 작업을 처리하고, 슬레이브 노드는 읽기 전용 쿼리를 제공합니다
- Binlog를 통한 비동기 복제와 중단점 재개 지원
- 애플리케이션 레이어에서 읽기/쓰기 분리를 담당합니다

## 로드맵

| 버전 | 계획 기능 |
|------|----------|
| **v1.0**(완료) | 임베디드+서버 듀얼 모드, Nova/Flux/KV 엔진, SQL DDL/DML/SELECT/JOIN, 트랜잭션/MVCC, WAL/복구, 핫-콜드 분리, 샤딩, MQ 소비자 그룹, ADO.NET Provider, 클러스터 마스터-슬레이브 동기화 |
| **v1.1**(완료) | P0 레벨 SQL 함수(문자열/숫자/날짜/변환/조건 ~30개 함수) |
| **v1.2**(완료) | MQ 블로킹 읽기, KV Add/Inc 작업, P1 레벨 SQL 함수 |
| **v1.3**(완료) | MQ 지연 메시지, 데드 레터 큐 |
| **v2.0**(완료) | GeoPoint 지오코딩 + Vector 타입(AI 벡터 검색), GIS 확장, 전문 검색(BM25), NovaVectorStore(IVectorStore, 다중 컬렉션), 증분 구체화 뷰, Binlog 관리, 관찰성 및 관리 도구 |
| **v2.1**(완료) | 시계열 컬럼형 밀봉 + 블록 압축(수치 인코딩 + LZ4), 백필과 자동 쿼리 라우팅을 포함한 롤업 사전 집계, SESSION/STATE_WINDOW 그룹화, 압축률 보고, 다중 RID CI(win-x64/linux-x64/linux-arm64/loongarch64)를 지원하는 NativeAOT |

## 포지셔닝

NovaDb는 완전한 SQL92 표준 준수를 추구하지 않고 일반적으로 사용되는 비즈니스 부분 집합의 80%를 커버하여 다음과 같은 차별화된 기능을 얻습니다:

| 차별화 | 설명 |
|--------|------|
| **순수 .NET 관리** | 네이티브 종속성 없음, xcopy를 통한 배포, .NET 애플리케이션과 동일 프로세스에서 직렬화 오버헤드 제로 |
| **임베디드+서버 듀얼 모드** | 개발/디버깅에서는 SQLite처럼 임베디드, 프로덕션에서는 MySQL처럼 독립 서비스, 동일 API |
| **폴더=데이터베이스** | 폴더 복사로 마이그레이션/백업 완료, dump/restore 불필요 |
| **핫-콜드 인덱스 분리** | 1000만 행 테이블에서 핫스팟만 쿼리 시 메모리 < 20MB, 콜드 데이터는 자동으로 MMF로 언로드 |
| **4개 엔진 통합** | 단일 컴포넌트가 일반적인 SQLite + TDengine + Redis 시나리오를 커버, 운영 컴포넌트 수 감소 |
| **NewLife 네이티브 통합** | XCode ORM + ADO.NET과 직접 적응, 타사 드라이버 불필요 |

## 생태계 지도

NewLife는 독립적으로 버전이 관리되는 오픈소스 프로젝트의 계층형 생태계입니다. 아래 지도는 모든 NewLife 리포지토리에서 그대로 동일하게 공유되며, 각 프로젝트는 코드 리포지토리와 기본 NuGet 패키지로 연결됩니다.

| 프로젝트 | 한 줄 소개 | 다운로드 |
| --- | --- | --- |
| **기반** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | 로깅, 구성, 캐시, 네트워크, 직렬화, 스케줄링, APM 추적. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | 데이터 미들웨어: 단일 테이블 수백억 건, 자동 분할 테이블, 읽기/쓰기 분리, 10여 종 데이터베이스 지원. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | 고성능 네트워크 라이브러리: 단일 노드 177만 pps, 14.5 Gbps(TCP 1KB, 2026-10 실측), 400만 Tcp 장기 연결. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | CS 애플리케이션과 IoT 장치를 위한 프로토콜 통신 및 RPC 프레임워크. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | .NET 애플리케이션을 운영체제 데몬으로 설치: Windows 서비스와 Linux systemd. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | 콘솔, Web, API, 서비스, 데이터, IoT용 `dotnet new` 프로젝트 템플릿. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **데이터 및 메시징** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | Redis 클라이언트: 마이크로초 수준 지연, 백만 수준 처리량, 메시지 큐 내장. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | Apache RocketMQ와 알리바바 클라우드 메시지 큐를 지원하는 순수 관리형 RocketMQ 클라이언트. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | MQTT 클라이언트와 서버, 알리바바 클라우드 IoT 연결 포함. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | C# 하이브리드 데이터베이스: 임베디드/네트워크 두 모드, 관계형, 시계열, 메시지 큐, KV를 하나로. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | NewLife 데이터 스택 위에 구축한 MySql 드라이버와 도우미 계층. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **Web, AI 및 분산** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | 빠른 관리 플랫폼 프레임워크: 권한, SSO, OAuth 서버, CRUD 스캐폴딩. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | 분산 서비스 플랫폼: 노드 관리, APM, 구성 센터, 등록 센터, 배포 센터. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | 샤딩, 재시도, 백필을 지원하는 분산 작업 및 실시간 데이터 스케줄링 플랫폼. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | 46개 LLM 공급자를 통합 연결하는 클라이언트와 바로 사용할 수 있는 ChatAI 웹 채팅 애플리케이션. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **산업 및 IoT** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | 프로토콜 계약과 장치 추상을 정의하는 IoT 표준 라이브러리. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | NewLife.IoT 기반으로 구현한 Modbus TCP/RTU/ASCII. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | NewLife.IoT 기반으로 구현한 지멘스 PLC 프로토콜. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | NewLife.IoT 기반으로 구현한 오므론 PLC 프로토콜. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | NewLife.IoT 기반으로 구현한 미쓰비시 Melsec PLC 프로토콜. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | 산업 데이터 수집용 OPC 클라이언트. | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | BACnet 빌딩 자동화 프로토콜 스택. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | 게이트웨이와 장치 관리를 갖춘 경량 오픈소스 IoT 플랫폼. | — |
| **차량 텔레매틱스** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | JT/T 808 차량 위치 프로토콜: 70개 이상 메시지 본문, 2011/2013/2019 지원. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | 운송 관리 부서를 위한 JT/T 809 플랫폼 간 프로토콜. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | JT/T 1078 차량용 비디오: RTP 파싱, FLV/fMP4 리먹싱, 라이브 스트리밍. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **도메인 컴포넌트** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | Excel, Word, PPT, PDF 등 15개 이상 문서 형식을 하나의 패키지로. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | 크로스 플랫폼 오디오: 코덱, 장치 IO, DSP, 컨테이너, 스트리밍. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | 비디오 코덱, 컨테이너 형식, 프레임 처리, 스트리밍. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | 바이두/고덕/텐센트/톈디투 지도 통합 인터페이스, 지오코딩과 좌표 변환 지원. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | IPv4 위치 정보: 오프라인 메모리 매핑 데이터베이스와 온라인 API 이중 소스. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **도구 및 제품** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | 개발자 도구 모음: 네트워크, 시리얼 포트, 암호화, 정규식, Modbus, MQTT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | 셀프 호스팅 Git 소스 코드 관리 시스템. | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | 구버전 호스트에서 시작할 때 일치하는 .NET 런타임을 자동으로 설치합니다. | — |

> 배지는 각 프로젝트의 기본 패키지를 나타냅니다. 테마, 확장, 위성 패키지는 해당 NuGet 페이지에서 확인하세요.여기에 나열하지 않은 30여 개 패키지(FTP, LoRa, Office 확장, 각종 프로토콜 드라이버 등)는 [NewLifeX 조직](https://github.com/NewLifeX)과 [newlifex.com](https://newlifex.com)에서 확인할 수 있습니다.


## 뉴라이프 개발팀

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

뉴라이프 팀(NewLife)은 2002년에 설립되어 20여 년간 .NET과 IoT 기술 생태계에 집중하며 100여 개의 오픈소스 프로젝트를 주도하고(이 중 34개가 핵심 프로젝트), 전력, 물류, 의료, 산업 제어, 인터넷 등 산업에 소프트웨어와 하드웨어를 아우르는 통합 솔루션을 제공해 왔습니다. "학문에는 선후가 없으며, 이룬 사람이 스승이다"라는 신조를 따릅니다.

오픈소스 컴포넌트는 NuGet에서 누적 <!--nuget-total:start-->6.48M<!--nuget-total:end-->회 다운로드되었습니다(생태계 전체 기준, 2026-10).

웹사이트: https://newlifex.com · 오픈소스 조직: https://github.com/NewLifeX

<details>
<summary>위챗 공식 계정: 智能大石头(스캔하여 팔로우)</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>

