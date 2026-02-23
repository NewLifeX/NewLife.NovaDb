# NewLife.NovaDb

[![English](https://img.shields.io/badge/lang-English-blue.svg)](README.md) [![简体中文](https://img.shields.io/badge/lang-简体中文-red.svg)](README_CN.md) [![繁體中文](https://img.shields.io/badge/lang-繁體中文-orange.svg)](README_TW.md) [![Français](https://img.shields.io/badge/lang-Français-green.svg)](README_FR.md) [![Deutsch](https://img.shields.io/badge/lang-Deutsch-yellow.svg)](README_DE.md) [![日本語](https://img.shields.io/badge/lang-日本語-purple.svg)](README_JA.md) [![한국어](https://img.shields.io/badge/lang-한국어-brightgreen.svg)](README_KO.md) [![Русский](https://img.shields.io/badge/lang-Русский-lightgrey.svg)](README_RU.md) [![Español](https://img.shields.io/badge/lang-Español-yellow.svg)](README_ES.md) [![Português](https://img.shields.io/badge/lang-Português-blue.svg)](README_PT.md)

Гибридная база данных среднего и большого размера, реализованная на **C#**, работающая на **платформе .NET** (поддержка .NET Framework 4.5 ~ .NET 10), поддерживающая двойной встроенный/серверный режим, интегрирующая реляционные, временные ряды, очереди сообщений и NoSQL (KV) возможности.

## Представление продукта

`NewLife.NovaDb` (сокращенно `Nova`) — это основная инфраструктура экосистемы NewLife, интегрированный движок данных для приложений .NET. Удалив многие нишевые функции (такие как хранимые процедуры/триггеры/оконные функции), он достигает более высокой производительности чтения/записи и более низких эксплуатационных расходов; объем данных логически не ограничен (ограничен диском и стратегиями разбиения) и может заменить SQLite/MySQL/Redis/TDengine в конкретных сценариях.

### Основные возможности

- **Двойной режим развертывания**:
  - **Встроенный режим**: Работает как библиотека, как SQLite, с данными, хранящимися в локальных папках, нулевая конфигурация
  - **Серверный режим**: Автономный процесс + протокол TCP, сетевой доступ, как MySQL; поддерживает развертывание кластера и репликацию master-slave (один master, несколько slave)
- **Папка как база данных**: Скопируйте папку для завершения миграции/резервного копирования, процесс dump/restore не требуется. Каждая таблица имеет независимые группы файлов (`.data`/`.idx`/`.wal`).
- **Интеграция четырех движков**:
  - **Nova Engine** (общий реляционный): Индекс SkipList + транзакции MVCC (Read Committed), поддерживает CRUD, SQL-запросы, JOIN
  - **Flux Engine** (временные ряды + MQ): Временное разбиение Append Only, поддерживает автоочистку TTL, группы потребителей в стиле Redis Stream + Pending + Ack
  - **Режим KV** (логическое представление): Повторно использует Nova Engine, API скрывает детали SQL, каждая строка содержит `Key + Value + TTL`
  - **ADO.NET Provider**: Автоматически распознает встроенный/серверный режим, нативная интеграция с XCode ORM
- **Динамическое разделение горячих и холодных индексов**: Горячие данные полностью загружаются в физическую память (узлы SkipList), холодные данные выгружаются в MMF с сохранением только разреженного каталога. Таблица из 10 миллионов строк, запрашивающая только последние 10 000 строк, использует < 20 МБ памяти.
- **Чистый управляемый код**: Нет зависимостей от нативных компонентов (чистый C#/.NET), легко развертывать на разных платформах и в ограниченных средах.

### Движки хранения

| Движок | Структура данных | Варианты использования |
|--------|-----------------|------------------------|
| **Nova Engine** | SkipList (Разделение памяти+MMF горячих-холодных данных) | Общий CRUD, таблицы конфигурации, бизнес-заказы, пользовательские данные |
| **Flux Engine** | Временное разбиение (Append Only) | Датчики IoT, сбор логов, внутренние очереди сообщений, журналы аудита |
| **Режим KV** | Логическое представление таблицы Nova | Распределенные блокировки, кэширование, хранилище сеансов, счетчики, центр конфигурации |

### Типы данных

| Категория | Тип SQL | Соответствие C# | Описание |
|-----------|---------|-----------------|----------|
| Boolean | `BOOL` | `Boolean` | 1 байт |
| Integer | `INT` / `LONG` | `Int32` / `Int64` | 4/8 байт |
| Float | `DOUBLE` | `Double` | 8 байт |
| Decimal | `DECIMAL` | `Decimal` | 128 бит, единая точность |
| String | `STRING(n)` / `STRING` | `String` | UTF-8, длина может быть указана |
| Binary | `BINARY(n)` / `BLOB` | `Byte[]` | Длина может быть указана |
| DateTime | `DATETIME` | `DateTime` | Точность до Ticks (100 наносекунд) |
| GeoPoint | `GEOPOINT` | Пользовательская структура | Координаты широты/долготы (планируется) |
| Vector | `VECTOR(n)` | `Single[]` | Поиск AI-векторов (планируется) |

### Возможности SQL

Реализовано стандартное подмножество SQL, охватывающее примерно 60% распространенных бизнес-сценариев:

| Функция | Статус | Описание |
|---------|--------|----------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX/DATABASE, ALTER TABLE (ADD/MODIFY/DROP COLUMN, COMMENT), с IF NOT EXISTS, PRIMARY KEY, UNIQUE, ENGINE |
| DML | ✅ | INSERT (несколько строк), UPDATE, DELETE, UPSERT (ON DUPLICATE KEY UPDATE), TRUNCATE TABLE |
| Запрос | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| Агрегация | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN (Nested Loop), поддерживает псевдонимы таблиц |
| Параметризация | ✅ | Заполнители @param |
| Транзакция | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| Функции SQL | ✅ | Строковые/числовые/даты/преобразования/условные/хэш функции (60+ функций) |
| Подзапрос | ✅ | Подзапросы IN/EXISTS |
| Расширенный | ❌ | Нет представлений/триггеров/хранимых процедур/оконных функций |

---

## Руководство по использованию

### Установка

Установите основной пакет NovaDb через NuGet:

```shell
dotnet add package NewLife.NovaDb
```

### Способы доступа

NovaDb предоставляет два клиентских способа доступа для различных сценариев:

| Способ доступа | Целевой движок | Описание |
|----------------|----------------|----------|
| **ADO.NET + SQL** | Nova (реляционный), Flux (временные ряды) | Стандартные `DbConnection`/`DbCommand`/`DbDataReader`, совместимы со всеми ORM |
| **NovaClient** | MQ (очередь сообщений), KV (хранилище ключ-значение) | RPC-клиент, предоставляющий API для публикации/потребления/подтверждения сообщений и чтения/записи KV |

---

### 1. Реляционная база данных (ADO.NET + SQL)

Реляционный движок (Nova Engine) доступен через стандартный интерфейс ADO.NET. `Data Source` в строке подключения указывает на встроенный режим; `Server` — на серверный режим.

#### 1.1 Встроенный режим (быстрый старт за 5 минут)

Встроенный режим не требует отдельного сервиса, идеален для настольных приложений, IoT-устройств и модульных тестов.

```csharp
using NewLife.NovaDb.Client;

// Создание подключения (встроенный режим, папка как база данных)
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

// Создание таблицы
using var cmd = conn.CreateCommand();
cmd.CommandText = @"CREATE TABLE IF NOT EXISTS users (
    id   INT PRIMARY KEY AUTO_INCREMENT,
    name STRING(50) NOT NULL,
    age  INT DEFAULT 0,
    created DATETIME
)";
cmd.ExecuteNonQuery();

// Вставка данных
cmd.CommandText = "INSERT INTO users (name, age, created) VALUES ('Alice', 25, NOW())";
cmd.ExecuteNonQuery();

// Пакетная вставка
cmd.CommandText = @"INSERT INTO users (name, age) VALUES
    ('Bob', 30),
    ('Charlie', 28)";
cmd.ExecuteNonQuery();

// Запрос данных
cmd.CommandText = "SELECT * FROM users WHERE age >= 25 ORDER BY age";
using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"id={reader["id"]}, name={reader["name"]}, age={reader["age"]}");
}
```

#### 1.2 Серверный режим

Серверный режим обеспечивает удаленный доступ по TCP, поддерживая множество одновременных клиентских подключений.

**Запуск сервера:**

```csharp
using NewLife.NovaDb.Server;

var svr = new NovaServer(3306) { DbPath = "./data" };
svr.Start();
Console.ReadLine();
svr.Stop("Manual shutdown");
```

**Подключение клиента ADO.NET (API идентичен встроенному режиму):**

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

#### 1.3 Параметризованные запросы

Параметризованные запросы предотвращают SQL-инъекции с использованием именованных параметров `@name`:

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

#### 1.4 Транзакции

NovaDb реализует изоляцию транзакций на основе MVCC с уровнем изоляции по умолчанию Read Committed:

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

#### 1.5 JOIN-запросы

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

#### 1.6 Справочник по строке подключения

| Параметр | Пример | Описание |
|----------|--------|----------|
| `Data Source` | `Data Source=./mydb` | Встроенный режим, путь к папке базы данных |
| `Server` | `Server=127.0.0.1` | Серверный режим, адрес сервера |
| `Port` | `Port=3306` | Порт сервера (по умолчанию 3306) |
| `Database` | `Database=mydb` | Имя базы данных |
| `WalMode` | `WalMode=Full` | Режим WAL (Full/Normal/None) |
| `ReadOnly` | `ReadOnly=true` | Режим только для чтения |

---

### 2. База данных временных рядов (ADO.NET + SQL)

Движок временных рядов (Flux Engine) также доступен через ADO.NET + SQL. Укажите `ENGINE=FLUX` при создании таблиц.

#### 2.1 Создание таблицы временных рядов

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

#### 2.2 Запись данных временных рядов

```csharp
// Одиночная вставка
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity)
    VALUES (NOW(), 'sensor-001', 23.5, 65.0)";
cmd.ExecuteNonQuery();

// Пакетная вставка
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity) VALUES
    ('2025-07-01 10:00:00', 'sensor-001', 22.1, 60.0),
    ('2025-07-01 10:01:00', 'sensor-001', 22.3, 61.0),
    ('2025-07-01 10:02:00', 'sensor-002', 25.0, 55.0)";
cmd.ExecuteNonQuery();
```

#### 2.3 Запрос по временному диапазону

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

#### 2.4 Агрегационный анализ

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

### 3. Очередь сообщений (NovaClient)

NovaDb реализует очередь сообщений в стиле Redis Stream на основе движка временных рядов Flux. Доступ к очереди сообщений осуществляется через RPC-интерфейс `NovaClient`.

#### 3.1 Подключение к серверу

```csharp
using NewLife.NovaDb.Client;

using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();
```

#### 3.2 Публикация сообщений

```csharp
var affected = await client.ExecuteAsync(
    "INSERT INTO order_events (timestamp, orderId, action, amount) " +
    "VALUES (NOW(), 10001, 'created', 299.00)");
Console.WriteLine($"Message published, affected rows: {affected}");
```

#### 3.3 Потребление сообщений

```csharp
var messages = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT * FROM order_events WHERE timestamp > @since ORDER BY timestamp LIMIT 10",
    new { since = DateTime.Now.AddMinutes(-5) });
```

#### 3.4 Проверка соединения (Heartbeat)

```csharp
var serverTime = await client.PingAsync();
Console.WriteLine($"Server connected: {serverTime}");
Console.WriteLine($"Is connected: {client.IsConnected}");
```

#### 3.5 Основные возможности MQ

- **ID сообщения**: Временная метка + порядковый номер (автоинкремент в той же миллисекунде), глобально упорядочены
- **Группа потребителей**: `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **Надёжность**: At-Least-Once, входит в Pending после чтения, Ack после успеха бизнес-логики
- **Хранение данных**: Поддерживает TTL (автоматически удаляет старые шарды по времени/размеру файла)
- **Отложенные сообщения**: Укажите длительность задержки или точное время доставки
- **Очередь недоставленных сообщений**: Автоматически попадает в DLQ после превышения максимального числа повторов

---

### 4. KV-хранилище ключ-значение (NovaClient)

Доступ к KV-хранилищу осуществляется через `NovaClient`. Укажите `ENGINE=KV` при создании таблиц. KV-таблицы имеют фиксированную схему `Key + Value + TTL`.

#### 4.1 Создание KV-таблицы

```csharp
using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();

await client.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS session_cache (
    Key STRING(200) PRIMARY KEY, Value BLOB, TTL DATETIME
) ENGINE=KV DEFAULT_TTL=7200");
```

#### 4.2 Чтение/запись данных

```csharp
// Запись (семантика UPSERT)
await client.ExecuteAsync(
    "INSERT INTO session_cache (Key, Value, TTL) VALUES ('session:1001', 'user-data', " +
    "DATEADD(NOW(), 30, 'MINUTE')) ON DUPLICATE KEY UPDATE Value = 'user-data'");

// Чтение
var result = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT Value FROM session_cache WHERE Key = 'session:1001' " +
    "AND (TTL IS NULL OR TTL > NOW())");

// Удаление
await client.ExecuteAsync("DELETE FROM session_cache WHERE Key = 'session:1001'");
```

#### 4.3 Атомарный инкремент (счётчик)

```csharp
await client.ExecuteAsync(
    "INSERT INTO counters (Key, Value) VALUES ('page:views', 1) " +
    "ON DUPLICATE KEY UPDATE Value = Value + 1");
```

#### 4.4 Обзор возможностей KV

| Операция | Описание |
|----------|----------|
| `Get` | Чтение значения с ленивой проверкой TTL |
| `Set` | Установка значения с опциональным TTL |
| `Add` | Добавление только при отсутствии ключа (распределённая блокировка) |
| `Delete` | Удаление ключа |
| `Inc` | Атомарный инкремент/декремент (счётчик) |
| `TTL` | Автоматическая невидимость по истечении, периодическая фоновая очистка |

---

## Безопасность данных и режимы WAL

NovaDb предоставляет три стратегии персистентности WAL:

| Режим | Описание | Варианты использования |
|-------|----------|------------------------|
| `FULL` | Синхронная запись на диск, немедленный сброс при каждом коммите | Финансовые/торговые сценарии, максимальная безопасность данных |
| `NORMAL` | Асинхронный сброс каждую 1с (по умолчанию) | Большинство бизнес-сценариев, баланс производительности и безопасности |
| `NONE` | Полностью асинхронный, без проактивного сброса | Временные данные/кэш сценарии, максимальная пропускная способность |

> Выбор режима, отличного от синхронного (`FULL`), означает принятие возможной потери данных в сценариях сбоя/отключения питания.

## Кластерное развертывание

NovaDb поддерживает архитектуру **один master — несколько slave** с асинхронной репликацией данных через Binlog:

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

- Узел master обрабатывает все операции записи; узлы slave обеспечивают запросы только для чтения
- Асинхронная репликация через Binlog с поддержкой возобновления с точки прерывания
- Уровень приложения отвечает за разделение чтения/записи

## Дорожная карта

| Версия | Планируемые функции |
|--------|---------------------|
| **v1.0** (Завершено) | Двойной встроенный+серверный режим, движки Nova/Flux/KV, SQL DDL/DML/SELECT/JOIN, транзакции/MVCC, WAL/восстановление, разделение горячих-холодных данных, разбиение, группы потребителей MQ, ADO.NET Provider, синхронизация master-slave кластера |
| **v1.1** | Функции SQL уровня P0 (строковые/числовые/даты/преобразования/условные ~30 функций) |
| **v1.2** | Блокирующее чтение MQ, операции KV Add/Inc, функции SQL уровня P1 |
| **v1.3** | Отложенные сообщения MQ, очередь недоставленных сообщений |
| **v2.0** | Геокодирование GeoPoint + тип Vector (поиск AI-векторов), наблюдаемость и инструменты управления |

## Позиционирование

NovaDb не стремится к полному соответствию стандарту SQL92, а охватывает 80% часто используемого бизнес-подмножества в обмен на следующие дифференцированные возможности:

| Дифференциация | Описание |
|----------------|----------|
| **Чистый .NET управляемый** | Нет нативных зависимостей, развертывание через xcopy, нулевые накладные расходы на сериализацию в том же процессе с приложениями .NET |
| **Двойной встроенный+серверный режим** | Встроенный для разработки/отладки, как SQLite, автономный сервис для продакшена, как MySQL, тот же API |
| **Папка как база данных** | Скопируйте папку для завершения миграции/резервного копирования, dump/restore не требуется |
| **Разделение горячих-холодных индексов** | Таблица из 10M строк, запрашивающая только горячие точки, использует < 20 МБ памяти, холодные данные автоматически выгружаются в MMF |
| **Интеграция четырех движков** | Один компонент охватывает распространенные сценарии SQLite + TDengine + Redis, уменьшает количество операционных компонентов |
| **Нативная интеграция NewLife** | Прямая адаптация с XCode ORM + ADO.NET, драйверы третьих сторон не требуются |
