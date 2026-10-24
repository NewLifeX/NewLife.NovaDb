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
🌐 [English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [한국어](README.ko.md)
<!-- lang:end -->

> ⚠️ قد تتأخر هذه الترجمة عن أحدث نسخة؛ المحتوى المعتمد هو [README الإنجليزي](README.md).

قاعدة بيانات هجينة متوسطة إلى كبيرة الحجم، مُنفَّذة بلغة **C#**، تعمل على **منصة .NET** (تدعم .NET Framework 4.5 حتى .NET 10)، وتدعم الوضعين المضمَّن والشبكي، وتجمع بين القدرات العلائقية والسلاسل الزمنية وطوابير الرسائل وNoSQL (KV).

## تقديم المنتج

`NewLife.NovaDb` (ويُختصر إلى `Nova`) هو البنية التحتية الأساسية لمنظومة NewLife، ومحرّك بيانات متكامل موجَّه لتطبيقات .NET. وبحذف كثير من الميزات النادرة (مثل الإجراءات المخزّنة والمُشغّلات ودوال النوافذ)، يحقّق أداءً أعلى في القراءة والكتابة وتكلفة تشغيل أقل؛ وحجم البيانات غير محدود منطقيًا (رهنًا بالقرص واستراتيجية التقسيم)، ويمكنه أن يحل محل SQLite/MySQL/Redis/TDengine في سيناريوهات محدّدة.

### الخصائص الأساسية

- **وضعا النشر**:
  - **الوضع المضمَّن**: يعمل كمكتبة على غرار SQLite، وتُخزَّن البيانات في مجلدات محلية، بلا أي إعدادات
  - **الوضع الشبكي**: عملية مستقلة + بروتوكول TCP، ووصول شبكي على غرار MySQL؛ ويدعم النشر العنقودي والنسخ من الرئيسي إلى التابع (رئيسي واحد وعدة تابعين)
- **المجلد هو قاعدة البيانات**: يكفي نسخ المجلد لإتمام الترحيل/النسخ الاحتياطي، دون الحاجة إلى عمليات dump/restore. ولكل جدول مجموعة ملفات مستقلة (`.data`/`.idx`/`.wal`).
- **تكامل أربعة محرّكات**:
  - **Nova Engine** (علائقي عام): فهرس SkipList + معاملات MVCC (Read Committed)، ويدعم CRUD واستعلامات SQL وJOIN
  - **Flux Engine** (سلاسل زمنية + MQ): تقسيم زمني بأسلوب Append Only، ويدعم التنظيف التلقائي بـ TTL، ومجموعات المستهلكين على نمط Redis Stream مع Pending وAck؛ وتخزين عمودي في كتل مغلقة بترميز واعٍ بالنوع (Delta/DeltaDelta/XOR/RLE/قاموس) مع ضغط كتل LZ4، وتجميع مسبق Rollup مع توجيه تلقائي لاستعلامات التجميع، وتجميع نوافذ SESSION/STATE، والإبلاغ عن نسبة الضغط عبر `_sys.flux_compression`
  - **وضع KV** (مخزن مفتاح-قيمة): محرّك Bitcask مستقل (فهرس في الذاكرة + قيم على القرص)، ويدعم TTL وعزل الجداول المتعددة
  - **موفّر ADO.NET**: يتعرّف تلقائيًا على الوضع المضمَّن/الشبكي، ويتكامل أصليًا مع ORM الخاص بـ XCode
- **فصل ديناميكي للفهارس الساخنة والباردة**: تُحمَّل البيانات الساخنة كاملة في الذاكرة الفيزيائية (عُقد SkipList)، بينما تُفرَّغ البيانات الباردة إلى MMF ويُحتفظ بدليل متفرّق فقط. جدول بـ 10 ملايين صف، عند الاستعلام عن أحدث 10,000 صف فقط، يستهلك أقل من 20MB من الذاكرة.
- **شيفرة مُدارة بالكامل**: لا تبعيات على مكوّنات أصلية (C#/.NET خالصة)، ما يسهّل النشر عبر المنصات وفي البيئات المقيّدة.

### محرّكات التخزين

| المحرّك | بنية البيانات | حالات الاستخدام |
|--------|----------------|-----------|
| **Nova Engine** | SkipList (فصل ساخن/بارد بين الذاكرة وMMF) | CRUD عام، جداول الإعدادات، طلبات الأعمال، بيانات المستخدمين |
| **Flux Engine** | تقسيم زمني (Append Only) | مستشعرات إنترنت الأشياء، جمع السجلات، طوابير الرسائل الداخلية، سجلات التدقيق |
| **وضع KV** | Bitcask (فهرس في الذاكرة + قيم على القرص) | الأقفال الموزّعة، التخزين المؤقت، تخزين الجلسات، العدّادات، مركز الإعدادات |

### أنواع البيانات

| الفئة | نوع SQL | مقابل C# | الوصف |
|----------|----------|------------|-------------|
| منطقي | `BOOL` | `Boolean` | بايت واحد |
| صحيح | `INT` / `LONG` | `Int32` / `Int64` | 4/8 بايت |
| عائم | `DOUBLE` | `Double` | 8 بايت |
| عشري | `DECIMAL` | `Decimal` | 128 بت، دقة موحّدة |
| نصي | `STRING(n)` / `STRING` | `String` | UTF-8، ويمكن تحديد الطول |
| ثنائي | `BINARY(n)` / `BLOB` | `Byte[]` | يمكن تحديد الطول |
| تاريخ ووقت | `DATETIME` | `DateTime` | دقة حتى Ticks (100 نانوثانية) |
| نقطة جغرافية | `GEOPOINT` | بنية مخصّصة | إحداثيات خط العرض/الطول، مع استعلامات المسافة/المضلّع |
| متجه | `VECTOR(n)` | `Single[]` | بحث متجهات الذكاء الاصطناعي، مع استعلامات KNN لأقرب جار |

### قدرات SQL

تم تنفيذ مجموعة فرعية قياسية من SQL، تغطي نحو 60% من سيناريوهات الأعمال الشائعة:

| الميزة | الحالة | الوصف |
|---------|--------|-------------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX/DATABASE، وALTER TABLE (ADD/MODIFY/DROP COLUMN، COMMENT)، مع IF NOT EXISTS وPRIMARY KEY وUNIQUE وENGINE |
| DML | ✅ | INSERT (صفوف متعددة)، UPDATE، DELETE، UPSERT (ON DUPLICATE KEY UPDATE)، TRUNCATE TABLE |
| الاستعلام | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| التجميع | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN (Nested Loop)، ويدعم الأسماء المستعارة للجداول |
| المعلَّمات | ✅ | عناصر بديلة `@param` |
| المعاملات | ✅ | MVCC، Read Committed، COMMIT/ROLLBACK |
| دوال SQL | ✅ | نصية/عددية/تاريخية/تحويلية/شرطية/تجزئة (أكثر من 60 دالة) |
| الاستعلامات الفرعية | ✅ | استعلامات IN/EXISTS الفرعية |
| البحث النصي الكامل | ✅ | فهرس نصي كامل بترجيح BM25، ونتائج Top-K لاسترجاع RAG الهجين |
| متقدّم | ❌ | لا توجد طرق عرض/مُشغّلات/إجراءات مخزّنة/دوال نوافذ |

---

## دليل الاستخدام

### التثبيت

ثبّت حزمة NovaDb الأساسية عبر NuGet:

```shell
dotnet add package NewLife.NovaDb
```

### طرق الوصول

يوفّر NovaDb طريقتين للوصول من جهة العميل تناسبان سيناريوهات مختلفة:

| طريقة الوصول | المحرّك المستهدف | الوصف |
|--------------|---------------|-------------|
| **ADO.NET + SQL** | Nova (علائقي)، Flux (سلاسل زمنية) | `DbConnection`/`DbCommand`/`DbDataReader` قياسية، ومتوافقة مع جميع مكتبات ORM |
| **NovaClient** | MQ (طابور رسائل)، KV (مخزن مفتاح-قيمة) | عميل RPC يوفّر واجهات نشر الرسائل واستهلاكها والاعتراف بها، وواجهات قراءة/كتابة KV |

---

### 1. قاعدة البيانات العلائقية (ADO.NET + SQL)

يُوصَل إلى المحرّك العلائقي (Nova Engine) عبر واجهة ADO.NET القياسية. ووجود `Data Source` في سلسلة الاتصال يعني الوضع المضمَّن، ووجود `Server` يعني الوضع الشبكي.

#### 1.1 الوضع المضمَّن (بدء سريع في 5 دقائق)

لا يحتاج الوضع المضمَّن إلى خدمة مستقلة، وهو مثالي لتطبيقات سطح المكتب وأجهزة إنترنت الأشياء واختبارات الوحدة.

```csharp
using NewLife.NovaDb.Client;

// إنشاء الاتصال (الوضع المضمَّن، والمجلد هو قاعدة البيانات)
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

// إنشاء الجدول
using var cmd = conn.CreateCommand();
cmd.CommandText = @"CREATE TABLE IF NOT EXISTS users (
    id   INT PRIMARY KEY AUTO_INCREMENT,
    name STRING(50) NOT NULL,
    age  INT DEFAULT 0,
    created DATETIME
)";
cmd.ExecuteNonQuery();

// إدراج بيانات
cmd.CommandText = "INSERT INTO users (name, age, created) VALUES ('Alice', 25, NOW())";
cmd.ExecuteNonQuery();

// إدراج دفعة من الصفوف
cmd.CommandText = @"INSERT INTO users (name, age) VALUES
    ('Bob', 30),
    ('Charlie', 28)";
cmd.ExecuteNonQuery();

// الاستعلام عن البيانات
cmd.CommandText = "SELECT * FROM users WHERE age >= 25 ORDER BY age";
using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"id={reader["id"]}, name={reader["name"]}, age={reader["age"]}");
}
```

#### 1.2 الوضع الشبكي

يوفّر الوضع الشبكي وصولًا عن بُعد عبر TCP، ويدعم عدة اتصالات عميل متزامنة. وكل قاعدة بيانات محدّدة في سلسلة الاتصال (`Database=mydb`) تُقابل مجلد محرّك مستقلًا داخل مجلد بيانات الخادم، ما يتيح توجيه قواعد بيانات متعددة على خادم واحد.

**تشغيل الخادم:**

```csharp
using NewLife.NovaDb.Server;

var svr = new NovaServer(3306) { DbPath = "./data" };
svr.Start();
Console.ReadLine();
svr.Stop("Manual shutdown");
```

**اتصال عميل ADO.NET (واجهة برمجية مطابقة للوضع المضمَّن):**

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

#### 1.3 الاستعلامات ذات المعلَّمات

تمنع الاستعلامات ذات المعلَّمات حقن SQL باستخدام معلَّمات مسماة `@name`:

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

#### 1.4 استعلامات التجميع والقيم المفردة

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT COUNT(*) FROM users";
var count = Convert.ToInt32(cmd.ExecuteScalar());
Console.WriteLine($"Total users: {count}");

cmd.CommandText = "SELECT AVG(age) FROM users WHERE age > 0";
var avgAge = Convert.ToDouble(cmd.ExecuteScalar());
Console.WriteLine($"Average age: {avgAge:F1}");
```

#### 1.5 المعاملات

ينفّذ NovaDb عزل المعاملات اعتمادًا على MVCC، وبمستوى عزل افتراضي هو Read Committed:

```csharp
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

using var tx = conn.BeginTransaction();
try
{
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;

    // خصم من المخزون
    cmd.CommandText = "UPDATE products SET stock = stock - 1 WHERE id = 1 AND stock > 0";
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) throw new InvalidOperationException("Insufficient stock");

    // إنشاء الطلب
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

#### 1.6 استعلامات JOIN

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

#### 1.7 عمليات DDL

```sql
-- إنشاء/حذف قاعدة البيانات
CREATE DATABASE shop;
DROP DATABASE shop;

-- تعديل بنية الجدول
ALTER TABLE products ADD COLUMN category STRING;
ALTER TABLE products MODIFY COLUMN name STRING(200);
ALTER TABLE products DROP COLUMN category;

-- إدارة الفهارس
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

#### 1.9 مرجع سلسلة الاتصال

| المعلَّمة | مثال | الوصف |
|-----------|---------|-------------|
| `Data Source` | `Data Source=./mydb` | الوضع المضمَّن، مسار مجلد قاعدة البيانات |
| `Server` | `Server=127.0.0.1` | الوضع الشبكي، عنوان الخادم |
| `Port` | `Port=3306` | منفذ الخادم (الافتراضي 3306) |
| `Database` | `Database=mydb` | اسم قاعدة البيانات |
| `WalMode` | `WalMode=Full` | وضع WAL (Full/Normal/None) |
| `ReadOnly` | `ReadOnly=true` | وضع القراءة فقط |

---

### 2. قاعدة بيانات السلاسل الزمنية (ADO.NET + SQL)

يُوصَل إلى محرّك السلاسل الزمنية (Flux Engine) أيضًا عبر ADO.NET + SQL. حدّد `ENGINE=FLUX` عند إنشاء الجداول.

#### 2.1 إنشاء جدول سلاسل زمنية

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

#### 2.2 كتابة بيانات السلاسل الزمنية

```csharp
// إدراج سجل واحد
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity)
    VALUES (NOW(), 'sensor-001', 23.5, 65.0)";
cmd.ExecuteNonQuery();

// إدراج دفعة من الصفوف
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity) VALUES
    ('2025-07-01 10:00:00', 'sensor-001', 22.1, 60.0),
    ('2025-07-01 10:01:00', 'sensor-001', 22.3, 61.0),
    ('2025-07-01 10:02:00', 'sensor-002', 25.0, 55.0)";
cmd.ExecuteNonQuery();
```

#### 2.3 الاستعلام عن نطاق زمني

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

#### 2.4 تحليل التجميع

```csharp
// متوسط درجة الحرارة لكل جهاز
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

#### 2.5 سياسة الاحتفاظ بالبيانات

تدعم جداول السلاسل الزمنية التنظيف التلقائي للشرائح المنتهية صلاحيتها اعتمادًا على TTL، مع الاحتفاظ بالبيانات حسب الزمن أو السعة:

```csharp
// تكوين معلَّمات السلاسل الزمنية عبر DbOptions
var options = new DbOptions
{
    FluxPartitionHours = 1,        // قسم واحد كل ساعة
    FluxDefaultTtlSeconds = 86400, // الاحتفاظ بالبيانات لمدة 24 ساعة
};
```

---

### 3. طابور الرسائل (NovaClient)

ينفّذ NovaDb طابور رسائل على نمط Redis Stream اعتمادًا على محرّك السلاسل الزمنية Flux. ويُوصَل إلى طابور الرسائل عبر واجهة RPC الخاصة بـ `NovaClient`.

#### 3.1 الاتصال بالخادم

```csharp
using NewLife.NovaDb.Client;

using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();
```

#### 3.2 نشر الرسائل

```csharp
// تنفيذ SQL عبر RPC لإدراج رسالة في جدول Flux
var affected = await client.ExecuteAsync(
    "INSERT INTO order_events (timestamp, orderId, action, amount) " +
    "VALUES (NOW(), 10001, 'created', 299.00)");
Console.WriteLine($"Message published, affected rows: {affected}");
```

#### 3.3 استهلاك الرسائل

```csharp
// قراءة الرسائل (حسب النطاق الزمني)
var messages = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT * FROM order_events WHERE timestamp > @since ORDER BY timestamp LIMIT 10",
    new { since = DateTime.Now.AddMinutes(-5) });
```

#### 3.4 نبض الحياة (Heartbeat)

```csharp
var serverTime = await client.PingAsync();
Console.WriteLine($"Server connected: {serverTime}");
Console.WriteLine($"Is connected: {client.IsConnected}");
```

#### 3.5 الخصائص الأساسية لطابور الرسائل

- **معرّف الرسالة**: طابع زمني + رقم تسلسلي (يتزايد تلقائيًا داخل الميلي ثانية نفسها)، وترتيب عالمي
- **مجموعة المستهلكين**: `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **الموثوقية**: تسليم مرة على الأقل (At-Least-Once)، فتُدرج الرسالة في Pending بعد القراءة، ثم تُؤكَّد بـ Ack بعد نجاح معالجة العمل
- **الاحتفاظ بالبيانات**: يدعم TTL (يحذف الشرائح القديمة تلقائيًا حسب الزمن/حجم الملف)
- **الرسائل المؤجّلة**: تحديد مدة التأخير أو وقت التسليم الدقيق
- **طابور الرسائل الميتة**: تدخل الرسالة DLQ تلقائيًا بعد تجاوز الحد الأقصى لعدد المحاولات

---

### 4. مخزن المفتاح-قيمة KV (NovaClient)

يُوصَل إلى تخزين KV عبر `NovaClient`. حدّد `ENGINE=KV` عند إنشاء الجداول. ولجداول KV مخطط ثابت هو `Key + Value + TTL`.

#### 4.1 إنشاء جدول KV

```csharp
using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();

// إنشاء جدول KV (TTL الافتراضي 7200 ثانية = ساعتان)
await client.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS session_cache (
    Key STRING(200) PRIMARY KEY, Value BLOB, TTL DATETIME
) ENGINE=KV DEFAULT_TTL=7200");
```

#### 4.2 قراءة/كتابة البيانات

```csharp
// كتابة (بدلالات UPSERT)
await client.ExecuteAsync(
    "INSERT INTO session_cache (Key, Value, TTL) VALUES ('session:1001', 'user-data', " +
    "DATEADD(NOW(), 30, 'MINUTE')) ON DUPLICATE KEY UPDATE Value = 'user-data'");

// قراءة
var result = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT Value FROM session_cache WHERE Key = 'session:1001' " +
    "AND (TTL IS NULL OR TTL > NOW())");

// حذف
await client.ExecuteAsync("DELETE FROM session_cache WHERE Key = 'session:1001'");
```

#### 4.3 الزيادة الذرّية (العدّاد)

```csharp
await client.ExecuteAsync(
    "INSERT INTO counters (Key, Value) VALUES ('page:views', 1) " +
    "ON DUPLICATE KEY UPDATE Value = Value + 1");
```

#### 4.4 القفل الموزّع

```csharp
// محاولة الحصول على القفل (لا ينجح الإدراج إلا عندما لا يكون المفتاح موجودًا)
var locked = await client.ExecuteAsync(
    "INSERT INTO dist_lock (Key, Value, TTL) VALUES ('lock:order:123', 'worker-1', " +
    "DATEADD(NOW(), 30, 'SECOND'))");

if (locked > 0)
{
    try
    {
        // تم الحصول على القفل، نفّذ منطق العمل
    }
    finally
    {
        await client.ExecuteAsync("DELETE FROM dist_lock WHERE Key = 'lock:order:123'");
    }
}
```

#### 4.5 نظرة عامة على قدرات KV

| العملية | الوصف |
|-----------|-------------|
| `Get` | قراءة القيمة مع فحص TTL كسول |
| `Set` | تعيين القيمة مع TTL اختياري |
| `Add` | الإضافة فقط عندما لا يكون المفتاح موجودًا (قفل موزّع) |
| `Delete` | حذف المفتاح |
| `Inc` | زيادة/إنقاص ذرّي (عدّاد) |
| `TTL` | يصبح المفتاح غير مرئي عند انتهاء صلاحيته، مع تنظيف دوري في الخلفية |

---

## أمان البيانات وأوضاع WAL

يوفّر NovaDb ثلاث استراتيجيات لاستمرارية WAL:

| الوضع | الوصف | حالات الاستخدام |
|------|-------------|-----------|
| `FULL` | كتابة متزامنة على القرص، مع تفريغ فوري عند كل تأكيد | سيناريوهات مالية/تداول، أعلى مستوى لأمان البيانات |
| `NORMAL` | تفريغ غير متزامن كل ثانية (افتراضي) | معظم سيناريوهات الأعمال، يوازن بين الأداء والأمان |
| `NONE` | غير متزامن بالكامل، بلا تفريغ استباقي | بيانات مؤقتة/تخزين مؤقت، أعلى إنتاجية |

> اختيار أي وضع غير الوضع المتزامن (`FULL`) يعني قبول احتمال فقدان البيانات في سيناريوهات الانهيار أو انقطاع الطاقة.

## النشر العنقودي

يدعم NovaDb بنية **رئيسي واحد وعدة تابعين**، مع نسخ غير متزامن للبيانات عبر Binlog:

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

- تتولى العقدة الرئيسية جميع عمليات الكتابة، بينما تقدّم العقد التابعة استعلامات للقراءة فقط
- نسخ غير متزامن عبر Binlog مع دعم استئناف النسخ من نقطة الانقطاع
- تتحمّل طبقة التطبيق مسؤولية فصل القراءة عن الكتابة

## خريطة الطريق

| الإصدار | الميزات المخطَّطة |
|---------|------------------|
| **v1.0** (مكتمل) | الوضع المزدوج المضمَّن + الخادم، محرّكات Nova/Flux/KV، SQL DDL/DML/SELECT/JOIN، المعاملات/MVCC، WAL/الاستعادة، الفصل الساخن/البارد، التقسيم، مجموعات مستهلكي MQ، موفّر ADO.NET، المزامنة العنقودية رئيسي-تابع |
| **v1.1** (مكتمل) | دوال SQL من المستوى P0 (نصية/عددية/تاريخية/تحويلية/شرطية، نحو 30 دالة) |
| **v1.2** (مكتمل) | القراءة الحاجزة في MQ، عمليات Add/Inc في KV، دوال SQL من المستوى P1 |
| **v1.3** (مكتمل) | الرسائل المؤجّلة في MQ، طابور الرسائل الميتة |
| **v2.0** (مكتمل) | ترميز GeoPoint الجغرافي + نوع Vector (بحث متجهات الذكاء الاصطناعي)، امتداد GIS، البحث النصي الكامل (BM25)، NovaVectorStore (IVectorStore، متعدد المجموعات)، طرق عرض مادّية تزايدية، إدارة Binlog، أدوات الرصد والإدارة |
| **v2.1** (مكتمل) | تخزين عمودي في كتل مغلقة للسلاسل الزمنية + ضغط الكتل (ترميز رقمي + LZ4)، تجميع مسبق Rollup مع إعادة التعبئة والتوجيه التلقائي للاستعلامات، تجميع SESSION/STATE_WINDOW، الإبلاغ عن نسبة الضغط، دعم NativeAOT مع CI متعدد المعرّفات (win-x64/linux-x64/linux-arm64/loongarch64) |

## التموضع

لا يسعى NovaDb إلى مطابقة معيار SQL92 بالكامل، بل يغطي 80% من مجموعة الأعمال الشائعة الاستخدام، مقابل القدرات التالية المتميّزة:

| التمايز | الوصف |
|-----------------|-------------|
| **مُدار بالكامل بـ .NET** | لا تبعيات أصلية، والنشر بنسخ الملفات (xcopy)، وبلا أي كلفة تسلسل داخل العملية نفسها مع تطبيقات .NET |
| **وضع مزدوج: مضمَّن + خادم** | التضمين للتطوير والتنقيح على غرار SQLite، والخدمة المستقلة للإنتاج على غرار MySQL، بنفس واجهة البرمجة |
| **المجلد هو قاعدة البيانات** | نسخ المجلد يكفي لإتمام الترحيل/النسخ الاحتياطي، دون حاجة إلى dump/restore |
| **فصل الفهارس الساخنة والباردة** | جدول بـ 10 ملايين صف عند الاستعلام عن النقاط الساخنة فقط يستهلك أقل من 20MB من الذاكرة، مع تفريغ البيانات الباردة تلقائيًا إلى MMF |
| **تكامل أربعة محرّكات** | مكوّن واحد يغطّي سيناريوهات SQLite + TDengine + Redis الشائعة، ويقلّل عدد مكوّنات التشغيل |
| **تكامل أصلي مع NewLife** | تكييف مباشر مع ORM الخاص بـ XCode + ADO.NET، دون حاجة إلى برامج تشغيل من طرف ثالث |

## خريطة المنظومة

NewLife منظومة متعدّدة الطبقات من مشاريع مفتوحة المصدر تُصدَر بإصدارات مستقلة. الخريطة أدناه مشتركة حرفيًا بين جميع مستودعات NewLife؛ وكل مشروع يرتبط بمستودعه وبحزمته الرئيسية على NuGet.

| المشروع | الوظيفة | التنزيلات |
| --- | --- | --- |
| **الأساس** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | التسجيل والتهيئة والتخزين المؤقت والشبكات والتسلسل والجدولة وتتبّع أداء APM. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | وسيط بيانات: جداول واحدة بمستوى عشرات المليارات من الصفوف، تقسيم تلقائي للجداول، فصل القراءة عن الكتابة، وأكثر من 10 قواعد بيانات. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | مكتبة شبكات عالية الأداء: 1.77 مليون pps / 14.5 Gbps على جهاز واحد (TCP 1KB، 2026-10)، و4 ملايين اتصال TCP طويل الأمد. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | إطار اتصال بروتوكولي وRPC لتطبيقات العميل/الخادم وأجهزة إنترنت الأشياء. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | يثبّت تطبيقات .NET كخدمات حارسة لنظام التشغيل: خدمات Windows وsystemd في Linux. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | قوالب مشاريع `dotnet new` للطرفية وweb وAPI والخدمات والبيانات وإنترنت الأشياء. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **البيانات والمراسلة** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | عميل Redis بزمن استجابة بالميكروثانية، وملايين العمليات في الثانية، وقوائم رسائل مدمجة. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | عميل RocketMQ مُدار بالكامل لـ Apache RocketMQ وخدمة رسائل Alibaba Cloud. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | عميل وخادم MQTT، بما في ذلك الاتصال بـ Alibaba Cloud IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | قاعدة بيانات هجينة بلغة C#: مدمجة أو شبكية، وعلائقية، وسلاسل زمنية، وقائمة رسائل، وKV في واحد. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | برنامج تشغيل MySql وطبقة مساعدة مبنية على مكدّس بيانات NewLife. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **Web والذكاء الاصطناعي والأنظمة الموزّعة** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | إطار منصة إدارة سريعة: الصلاحيات، والدخول الموحّد SSO، وخادم OAuth، وتوليد واجهات CRUD. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | منصة خدمات موزّعة: إدارة العقد، وAPM، ومركز الإعدادات، ومركز التسجيل، ومركز النشر. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | منصة موزّعة للمهام وجدولة البيانات الفورية، مع التقسيم وإعادة المحاولة وإعادة التعبئة. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | عميل موحّد لـ 46 مزوّد LLM بالإضافة إلى ChatAI، تطبيق ويب جاهز للمحادثة. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **الصناعة وإنترنت الأشياء** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | مكتبة معايير إنترنت الأشياء التي تعرّف عقود البروتوكولات وتجريدات الأجهزة. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | تنفيذ Modbus TCP/RTU/ASCII فوق NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | بروتوكول Siemens PLC فوق NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | بروتوكول Omron PLC فوق NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | بروتوكول Mitsubishi Melsec PLC فوق NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | عميل OPC للوصول إلى البيانات الصناعية. | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | مكدّس بروتوكول BACnet لأتمتة المباني. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | منصة إنترنت أشياء مفتوحة المصدر وخفيفة، مع إدارة البوابات والأجهزة. | — |
| **تليماتيكا المركبات** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | بروتوكول تحديد مواقع المركبات JT/T 808: أكثر من 70 جسم رسالة، 2011/2013/2019. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | بروتوكول JT/T 809 بين المنصات لجهات إدارة النقل. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | فيديو داخل المركبة JT/T 1078: تحليل RTP وإعادة التجميع إلى FLV/fMP4 والبث المباشر. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **مكوّنات المجال** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | حزمة واحدة لـ Excel وWord وPPT وPDF وأكثر من 15 صيغة مستندات. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | صوت عبر المنصات: مرمّزات، وإدخال/إخراج الأجهزة، وDSP، وحاويات، وبث. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | مرمّزات الفيديو وصيغ الحاويات ومعالجة الإطارات والبث. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | واجهة موحّدة فوق Baidu وAMap وTencent وTianditu مع الترميز الجغرافي وتحويل الإحداثيات. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | تحديد الموقع الجغرافي لعناوين IPv4 بقاعدة بيانات غير متصلة مُعيّنة في الذاكرة، وواجهة API عبر الإنترنت كبديل. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **الأدوات والمنتجات** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | صندوق أدوات المطوّر: الشبكات والمنفذ التسلسلي والتشفير والتعبيرات النمطية وModbus وMQTT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | نظام استضافة ذاتية لإدارة الشيفرة المصدرية Git. | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | يثبّت بيئة تشغيل .NET المطابقة تلقائيًا عند التشغيل من مضيف أقدم. | — |

> تعرض الشارات الحزمة الرئيسية لكل مشروع؛ أما حزم السمات والإضافات والحزم التابعة فمذكورة في صفحاتها على NuGet. الحزم التي تتجاوز 30 حزمة وغير المذكورة هنا (FTP وLoRa وإضافات Office وبرامج تشغيل البروتوكولات وغيرها) متاحة في [منظمة NewLifeX](https://github.com/NewLifeX) وعلى [newlifex.com](https://newlifex.com).

## الفريق

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

تأسّست NewLife في عام 2002، وأمضت أكثر من عقدين في منظومة .NET وإنترنت الأشياء، وتدير ما يقارب 100 مشروع مفتوح المصدر (34 منها مشاريع رئيسية)، وتقدّم حلولًا متكاملة للبرمجيات والأجهزة لقطاعات الكهرباء والخدمات اللوجستية والرعاية الصحية والتحكّم الصناعي والإنترنت. ونلتزم بمبدأ "لا أسبقية في التعلّم — الإتقان يكسب الاحترام".

نُزّلت المكوّنات مفتوحة المصدر <!--nuget-total:start-->6.48M<!--nuget-total:end--> مرة من NuGet (على مستوى المنظومة بأكملها، حتى 2026-10).

الموقع: https://newlifex.com · المنظمة مفتوحة المصدر: https://github.com/NewLifeX

<details>
<summary>حساب WeChat الرسمي: 智能大石头 (امسح للاشتراك)</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>
