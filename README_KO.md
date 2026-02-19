# NewLife.NovaDb

[![English](https://img.shields.io/badge/lang-English-blue.svg)](README.md) [![简体中文](https://img.shields.io/badge/lang-简体中文-red.svg)](README_CN.md) [![繁體中文](https://img.shields.io/badge/lang-繁體中文-orange.svg)](README_TW.md) [![Français](https://img.shields.io/badge/lang-Français-green.svg)](README_FR.md) [![Deutsch](https://img.shields.io/badge/lang-Deutsch-yellow.svg)](README_DE.md) [![日本語](https://img.shields.io/badge/lang-日本語-purple.svg)](README_JA.md) [![한국어](https://img.shields.io/badge/lang-한국어-brightgreen.svg)](README_KO.md) [![Русский](https://img.shields.io/badge/lang-Русский-lightgrey.svg)](README_RU.md) [![Español](https://img.shields.io/badge/lang-Español-yellow.svg)](README_ES.md) [![Português](https://img.shields.io/badge/lang-Português-blue.svg)](README_PT.md)

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
  - **Flux Engine**(시계열 + MQ): 시간 기반 샤딩 Append Only, TTL 자동 정리, Redis Stream 스타일 소비자 그룹 + Pending + Ack 지원
  - **KV 모드**(논리 뷰): Nova Engine 재사용, API는 SQL 세부 사항 숨김, 각 행에 `Key + Value + TTL` 포함
  - **ADO.NET Provider**: 임베디드/서버 모드 자동 인식, XCode ORM과 네이티브 통합
- **동적 핫-콜드 인덱스 분리**: 핫 데이터는 물리 메모리에 완전히 로드(SkipList 노드), 콜드 데이터는 MMF로 언로드되어 희소 디렉토리만 유지. 1000만 행 테이블에서 최신 1만 행만 쿼리할 때 메모리 사용량 < 20MB.
- **순수 관리 코드**: 네이티브 컴포넌트 종속성 없음(순수 C#/.NET), 크로스 플랫폼 및 제한된 환경에서 배포 용이.

### 스토리지 엔진

| 엔진 | 데이터 구조 | 사용 사례 |
|------|------------|-----------|
| **Nova Engine** | SkipList(메모리+MMF 핫-콜드 분리) | 범용 CRUD, 구성 테이블, 비즈니스 주문, 사용자 데이터 |
| **Flux Engine** | 시간 기반 샤딩(Append Only) | IoT 센서, 로그 수집, 내부 메시지 큐, 감사 로그 |
| **KV 모드** | Nova 테이블 논리 뷰 | 분산 락, 캐싱, 세션 저장소, 카운터, 구성 센터 |

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
| GeoPoint | `GEOPOINT` | 사용자 정의 구조 | 위도/경도 좌표(계획 중) |
| Vector | `VECTOR(n)` | `Single[]` | AI 벡터 검색(계획 중) |

### SQL 기능

표준 SQL 부분 집합 구현, 일반적인 비즈니스 시나리오의 약 60% 커버:

| 기능 | 상태 | 설명 |
|------|------|------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX, IF NOT EXISTS, PRIMARY KEY, UNIQUE 포함 |
| DML | ✅ | INSERT(여러 행), UPDATE, DELETE, UPSERT(ON DUPLICATE KEY UPDATE), TRUNCATE TABLE |
| 쿼리 | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| 집계 | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN(Nested Loop), 테이블 별칭 지원 |
| 매개변수화 | ✅ | @param 플레이스홀더 |
| 트랜잭션 | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| SQL 함수 | ⚠️ | 문자열/숫자/날짜/변환/조건 함수(계획 중) |
| 서브쿼리 | ⚠️ | IN/EXISTS(계획 중) |
| 고급 | ❌ | 뷰/트리거/저장 프로시저/윈도우 함수 없음 |

### MQ 기능(Flux Engine)

Redis Stream의 소비자 그룹 모델 기반:

- **메시지 ID**: 타임스탬프 + 시퀀스 번호(동일 밀리초 내 자동 증가), 전역 순서
- **소비자 그룹**: `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **신뢰성**: At-Least-Once, 읽기 후 Pending에 진입, 비즈니스 성공 후 Ack
- **데이터 보존**: TTL 지원(시간/파일 크기별 오래된 샤드 자동 삭제)
- **지연 메시지**: `DelayTime`/`DeliverAt` 지정(계획 중)
- **데드 레터 큐**: 소비 실패 시 자동으로 DLQ 진입(계획 중)
- **블로킹 읽기**: 롱 폴링 + 타임아웃(계획 중)

### KV 기능

- `Get(key)` / `Set(key, value, ttl)` / `Delete(key)` / `Exists(key)`
- 지연 삭제(읽기 시 만료 확인) + 백그라운드 정리(`CleanupExpired()`)
- `Add(key, value, ttl)`: 키가 존재하지 않을 때만 추가(계획 중)
- `Inc(key, delta, ttl)`: 원자적 증가(계획 중)

## 데이터 보안 및 WAL 모드

NovaDb는 3가지 WAL 지속성 전략을 제공합니다:

| 모드 | 설명 | 사용 사례 |
|------|------|-----------|
| `FULL` | 동기 디스크 쓰기, 각 커밋 시 즉시 플러시 | 금융/거래 시나리오, 최강 데이터 안전성 |
| `NORMAL` | 비동기 1초 플러시(기본값) | 대부분의 비즈니스 시나리오, 성능과 안전성 균형 |
| `NONE` | 완전 비동기, 능동적 플러시 없음 | 임시 데이터/캐시 시나리오, 최고 처리량 |

> 동기(`FULL`) 이외의 모드를 선택하면 충돌/정전 시나리오에서 데이터 손실 가능성을 수용하는 것을 의미합니다.

## 로드맵

| 버전 | 계획 기능 |
|------|----------|
| **v1.0**(완료) | 임베디드+서버 듀얼 모드, Nova/Flux/KV 엔진, SQL DDL/DML/SELECT/JOIN, 트랜잭션/MVCC, WAL/복구, 핫-콜드 분리, 샤딩, MQ 소비자 그룹, ADO.NET Provider, 클러스터 마스터-슬레이브 동기화 |
| **v1.1** | P0 레벨 SQL 함수(문자열/숫자/날짜/변환/조건 ~30개 함수) |
| **v1.2** | MQ 블로킹 읽기, KV Add/Inc 작업, P1 레벨 SQL 함수 |
| **v1.3** | MQ 지연 메시지, 데드 레터 큐 |
| **v2.0** | GeoPoint 지오코딩 + Vector 타입(AI 벡터 검색), 관찰성 및 관리 도구 |

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
