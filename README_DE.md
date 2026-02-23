# NewLife.NovaDb

[![English](https://img.shields.io/badge/lang-English-blue.svg)](README.md) [![简体中文](https://img.shields.io/badge/lang-简体中文-red.svg)](README_CN.md) [![繁體中文](https://img.shields.io/badge/lang-繁體中文-orange.svg)](README_TW.md) [![Français](https://img.shields.io/badge/lang-Français-green.svg)](README_FR.md) [![Deutsch](https://img.shields.io/badge/lang-Deutsch-yellow.svg)](README_DE.md) [![日本語](https://img.shields.io/badge/lang-日本語-purple.svg)](README_JA.md) [![한국어](https://img.shields.io/badge/lang-한국어-brightgreen.svg)](README_KO.md) [![Русский](https://img.shields.io/badge/lang-Русский-lightgrey.svg)](README_RU.md) [![Español](https://img.shields.io/badge/lang-Español-yellow.svg)](README_ES.md) [![Português](https://img.shields.io/badge/lang-Português-blue.svg)](README_PT.md)

Eine in **C#** implementierte mittelgroße bis große Hybrid-Datenbank, die auf der **.NET-Plattform** läuft (unterstützt .NET Framework 4.5 ~ .NET 10), den dualen eingebetteten/Server-Modus unterstützt und relationale, Zeitreihen-, Nachrichtenwarteschlangen- und NoSQL (KV)-Funktionen integriert.

## Produkteinführung

`NewLife.NovaDb` (abgekürzt `Nova`) ist die Kerninfrastruktur des NewLife-Ökosystems, eine integrierte Daten-Engine für .NET-Anwendungen. Durch den Verzicht auf viele Nischenfunktionen (wie gespeicherte Prozeduren/Trigger/Fensterfunktionen) erreicht es eine höhere Lese-/Schreibleistung und niedrigere Betriebskosten; das Datenvolumen ist logisch unbegrenzt (eingeschränkt durch Festplatte und Partitionierungsstrategien) und kann SQLite/MySQL/Redis/TDengine in bestimmten Szenarien ersetzen.

### Kernfunktionen

- **Duale Bereitstellungsmodi**:
  - **Eingebetteter Modus**: Läuft als Bibliothek wie SQLite, mit Daten in lokalen Ordnern gespeichert, keine Konfiguration
  - **Server-Modus**: Eigenständiger Prozess + TCP-Protokoll, Netzwerkzugriff wie MySQL; unterstützt Cluster-Bereitstellung und Master-Slave-Replikation (ein Master, mehrere Slaves)
- **Ordner als Datenbank**: Kopieren Sie den Ordner, um Migration/Backup abzuschließen, kein dump/restore-Prozess erforderlich. Jede Tabelle hat unabhängige Dateigruppen (`.data`/`.idx`/`.wal`).
- **Vier-Motoren-Integration**:
  - **Nova Engine** (Allgemein relational): SkipList-Index + MVCC-Transaktionen (Read Committed), unterstützt CRUD, SQL-Abfragen, JOIN
  - **Flux Engine** (Zeitreihen + MQ): Zeitbasierte Sharding Append Only, unterstützt TTL-Auto-Cleanup, Redis Stream-ähnliche Consumer-Gruppen + Pending + Ack
  - **KV-Modus** (Logische Ansicht): Verwendet Nova Engine wieder, API verbirgt SQL-Details, jede Zeile enthält `Key + Value + TTL`
  - **ADO.NET Provider**: Erkennt automatisch eingebetteten/Server-Modus, native Integration mit XCode ORM
- **Dynamische Hot-Cold-Index-Trennung**: Hot-Daten vollständig in den physischen Speicher geladen (SkipList-Knoten), Cold-Daten in MMF entladen mit nur spärlichem Verzeichnis beibehalten. 10 Millionen Zeilen Tabelle, die nur die neuesten 10.000 Zeilen abfragt, verwendet < 20 MB Speicher.
- **Reiner verwalteter Code**: Keine nativen Komponentenabhängigkeiten (reines C#/.NET), einfach plattformübergreifend und in eingeschränkten Umgebungen bereitzustellen.

### Speicher-Engines

| Engine | Datenstruktur | Anwendungsfälle |
|--------|---------------|-----------------|
| **Nova Engine** | SkipList (Speicher+MMF Hot-Cold-Trennung) | Allgemeines CRUD, Konfigurationstabellen, Geschäftsbestellungen, Benutzerdaten |
| **Flux Engine** | Zeitbasierte Sharding (Append Only) | IoT-Sensoren, Log-Sammlung, interne Nachrichtenwarteschlangen, Audit-Logs |
| **KV-Modus** | Nova-Tabellenlogische Ansicht | Verteilte Sperren, Caching, Sitzungsspeicher, Zähler, Konfigurationszentrum |

### Datentypen

| Kategorie | SQL-Typ | C#-Mapping | Beschreibung |
|-----------|---------|------------|--------------|
| Boolean | `BOOL` | `Boolean` | 1 Byte |
| Integer | `INT` / `LONG` | `Int32` / `Int64` | 4/8 Bytes |
| Float | `DOUBLE` | `Double` | 8 Bytes |
| Decimal | `DECIMAL` | `Decimal` | 128-Bit, einheitliche Präzision |
| String | `STRING(n)` / `STRING` | `String` | UTF-8, Länge kann angegeben werden |
| Binary | `BINARY(n)` / `BLOB` | `Byte[]` | Länge kann angegeben werden |
| DateTime | `DATETIME` | `DateTime` | Präzision auf Ticks (100 Nanosekunden) |
| GeoPoint | `GEOPOINT` | Benutzerdefinierte Struktur | Breiten-/Längenkoordinaten (geplant) |
| Vector | `VECTOR(n)` | `Single[]` | AI-Vektorsuche (geplant) |

### SQL-Fähigkeiten

Implementierte Standard-SQL-Teilmenge, die etwa 60% der gängigen Geschäftsszenarien abdeckt:

| Funktion | Status | Beschreibung |
|----------|--------|--------------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX/DATABASE, ALTER TABLE (ADD/MODIFY/DROP COLUMN, COMMENT), mit IF NOT EXISTS, PRIMARY KEY, UNIQUE, ENGINE |
| DML | ✅ | INSERT (mehrere Zeilen), UPDATE, DELETE, UPSERT (ON DUPLICATE KEY UPDATE), TRUNCATE TABLE |
| Abfrage | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| Aggregation | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN (Nested Loop), unterstützt Tabellenaliase |
| Parametrisierung | ✅ | @param-Platzhalter |
| Transaktion | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| SQL-Funktionen | ✅ | String-/Numerische/Datum-/Konvertierungs-/Bedingte/Hash-Funktionen (60+ Funktionen) |
| Unterabfrage | ✅ | IN/EXISTS-Unterabfragen |
| Erweitert | ❌ | Keine Views/Trigger/Gespeicherte Prozeduren/Fensterfunktionen |

---

## Nutzungsanleitung

### Installation

Installieren Sie das NovaDb-Kernpaket über NuGet:

```shell
dotnet add package NewLife.NovaDb
```

### Zugriffsmethoden

NovaDb bietet zwei Client-Zugriffsmethoden für verschiedene Szenarien:

| Zugriffsmethode | Ziel-Engine | Beschreibung |
|-----------------|-------------|--------------|
| **ADO.NET + SQL** | Nova (Relational), Flux (Zeitreihen) | Standard `DbConnection`/`DbCommand`/`DbDataReader`, kompatibel mit allen ORMs |
| **NovaClient** | MQ (Nachrichtenwarteschlange), KV (Key-Value-Speicher) | RPC-Client mit Nachrichten-Publish/Consume/Acknowledge und KV-Lese-/Schreib-APIs |

---

### 1. Relationale Datenbank (ADO.NET + SQL)

Die relationale Engine (Nova Engine) wird über die Standard-ADO.NET-Schnittstelle angesprochen. Ein `Data Source` in der Verbindungszeichenfolge gibt den eingebetteten Modus an; ein `Server` gibt den Server-Modus an.

#### 1.1 Eingebetteter Modus (5-Minuten-Schnellstart)

Der eingebettete Modus erfordert keinen eigenständigen Dienst, ideal für Desktop-Apps, IoT-Geräte und Unit-Tests.

```csharp
using NewLife.NovaDb.Client;

// Verbindung erstellen (eingebetteter Modus, Ordner als Datenbank)
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

// Tabelle erstellen
using var cmd = conn.CreateCommand();
cmd.CommandText = @"CREATE TABLE IF NOT EXISTS users (
    id   INT PRIMARY KEY AUTO_INCREMENT,
    name STRING(50) NOT NULL,
    age  INT DEFAULT 0,
    created DATETIME
)";
cmd.ExecuteNonQuery();

// Daten einfügen
cmd.CommandText = "INSERT INTO users (name, age, created) VALUES ('Alice', 25, NOW())";
cmd.ExecuteNonQuery();

// Batch-Einfügung
cmd.CommandText = @"INSERT INTO users (name, age) VALUES
    ('Bob', 30),
    ('Charlie', 28)";
cmd.ExecuteNonQuery();

// Daten abfragen
cmd.CommandText = "SELECT * FROM users WHERE age >= 25 ORDER BY age";
using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"id={reader["id"]}, name={reader["name"]}, age={reader["age"]}");
}
```

#### 1.2 Server-Modus

Der Server-Modus bietet Fernzugriff über TCP und unterstützt mehrere gleichzeitige Client-Verbindungen.

**Server starten:**

```csharp
using NewLife.NovaDb.Server;

var svr = new NovaServer(3306) { DbPath = "./data" };
svr.Start();
Console.ReadLine();
svr.Stop("Manual shutdown");
```

**ADO.NET-Client-Verbindung (identische API wie im eingebetteten Modus):**

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

#### 1.3 Parametrisierte Abfragen

Parametrisierte Abfragen verhindern SQL-Injection mit `@name`-benannten Parametern:

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

#### 1.4 Transaktionen

NovaDb implementiert Transaktionsisolation basierend auf MVCC mit einer Standard-Isolationsstufe von Read Committed:

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

#### 1.5 JOIN-Abfragen

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

#### 1.6 Verbindungszeichenfolgen-Referenz

| Parameter | Beispiel | Beschreibung |
|-----------|----------|--------------|
| `Data Source` | `Data Source=./mydb` | Eingebetteter Modus, Datenbank-Ordnerpfad |
| `Server` | `Server=127.0.0.1` | Server-Modus, Serveradresse |
| `Port` | `Port=3306` | Server-Port (Standard 3306) |
| `Database` | `Database=mydb` | Datenbankname |
| `WalMode` | `WalMode=Full` | WAL-Modus (Full/Normal/None) |
| `ReadOnly` | `ReadOnly=true` | Nur-Lese-Modus |

---

### 2. Zeitreihen-Datenbank (ADO.NET + SQL)

Die Zeitreihen-Engine (Flux Engine) wird ebenfalls über ADO.NET + SQL angesprochen. Geben Sie `ENGINE=FLUX` beim Erstellen von Tabellen an.

#### 2.1 Zeitreihen-Tabelle erstellen

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

#### 2.2 Zeitreihen-Daten schreiben

```csharp
// Einzelne Einfügung
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity)
    VALUES (NOW(), 'sensor-001', 23.5, 65.0)";
cmd.ExecuteNonQuery();

// Batch-Einfügung
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity) VALUES
    ('2025-07-01 10:00:00', 'sensor-001', 22.1, 60.0),
    ('2025-07-01 10:01:00', 'sensor-001', 22.3, 61.0),
    ('2025-07-01 10:02:00', 'sensor-002', 25.0, 55.0)";
cmd.ExecuteNonQuery();
```

#### 2.3 Zeitbereichsabfrage

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

#### 2.4 Aggregationsanalyse

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

### 3. Nachrichtenwarteschlange (NovaClient)

NovaDb implementiert eine Redis Stream-ähnliche Nachrichtenwarteschlange basierend auf der Flux-Zeitreihen-Engine. Der Zugriff auf die Nachrichtenwarteschlange erfolgt über die `NovaClient`-RPC-Schnittstelle.

#### 3.1 Verbindung zum Server herstellen

```csharp
using NewLife.NovaDb.Client;

using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();
```

#### 3.2 Nachrichten veröffentlichen

```csharp
var affected = await client.ExecuteAsync(
    "INSERT INTO order_events (timestamp, orderId, action, amount) " +
    "VALUES (NOW(), 10001, 'created', 299.00)");
Console.WriteLine($"Message published, affected rows: {affected}");
```

#### 3.3 Nachrichten konsumieren

```csharp
var messages = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT * FROM order_events WHERE timestamp > @since ORDER BY timestamp LIMIT 10",
    new { since = DateTime.Now.AddMinutes(-5) });
```

#### 3.4 Heartbeat

```csharp
var serverTime = await client.PingAsync();
Console.WriteLine($"Server connected: {serverTime}");
Console.WriteLine($"Is connected: {client.IsConnected}");
```

#### 3.5 MQ-Kernfunktionen

- **Nachrichten-ID**: Zeitstempel + Sequenznummer (Auto-Inkrement innerhalb derselben Millisekunde), global geordnet
- **Consumer-Gruppe**: `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **Zuverlässigkeit**: At-Least-Once, geht nach dem Lesen in Pending, Ack nach Geschäftserfolg
- **Datenspeicherung**: Unterstützt TTL (löscht automatisch alte Shards nach Zeit/Dateigröße)
- **Verzögerte Nachrichten**: Verzögerungsdauer oder genauen Lieferzeitpunkt angeben
- **Dead Letter Queue**: Automatischer Eintritt in DLQ nach Überschreitung der maximalen Wiederholungsanzahl

---

### 4. KV-Key-Value-Speicher (NovaClient)

Der KV-Speicher wird über `NovaClient` angesprochen. Geben Sie `ENGINE=KV` beim Erstellen von Tabellen an. KV-Tabellen haben ein festes Schema von `Key + Value + TTL`.

#### 4.1 KV-Tabelle erstellen

```csharp
using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();

await client.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS session_cache (
    Key STRING(200) PRIMARY KEY, Value BLOB, TTL DATETIME
) ENGINE=KV DEFAULT_TTL=7200");
```

#### 4.2 Daten lesen/schreiben

```csharp
// Schreiben (UPSERT-Semantik)
await client.ExecuteAsync(
    "INSERT INTO session_cache (Key, Value, TTL) VALUES ('session:1001', 'user-data', " +
    "DATEADD(NOW(), 30, 'MINUTE')) ON DUPLICATE KEY UPDATE Value = 'user-data'");

// Lesen
var result = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT Value FROM session_cache WHERE Key = 'session:1001' " +
    "AND (TTL IS NULL OR TTL > NOW())");

// Löschen
await client.ExecuteAsync("DELETE FROM session_cache WHERE Key = 'session:1001'");
```

#### 4.3 Atomarer Inkrement (Zähler)

```csharp
await client.ExecuteAsync(
    "INSERT INTO counters (Key, Value) VALUES ('page:views', 1) " +
    "ON DUPLICATE KEY UPDATE Value = Value + 1");
```

#### 4.4 Übersicht der KV-Fähigkeiten

| Operation | Beschreibung |
|-----------|--------------|
| `Get` | Wert lesen mit Lazy-TTL-Prüfung |
| `Set` | Wert setzen mit optionalem TTL |
| `Add` | Nur hinzufügen, wenn Schlüssel nicht existiert (verteilte Sperre) |
| `Delete` | Schlüssel löschen |
| `Inc` | Atomarer Inkrement/Dekrement (Zähler) |
| `TTL` | Automatisch unsichtbar bei Ablauf, periodische Hintergrundbereinigung |

---

## Datensicherheit und WAL-Modi

NovaDb bietet drei WAL-Persistenzstrategien:

| Modus | Beschreibung | Anwendungsfälle |
|-------|--------------|-----------------|
| `FULL` | Synchrone Festplattenschreibung, sofortiges Flush bei jedem Commit | Finanz-/Handelsszenarien, stärkste Datensicherheit |
| `NORMAL` | Async 1s Flush (Standard) | Die meisten Geschäftsszenarien, balanciert Leistung und Sicherheit |
| `NONE` | Vollständig asynchron, kein proaktives Flush | Temporäre Daten/Cache-Szenarien, höchster Durchsatz |

> Die Wahl eines anderen Modus als synchron (`FULL`) bedeutet, möglichen Datenverlust in Crash-/Stromausfallszenarien zu akzeptieren.

## Cluster-Bereitstellung

NovaDb unterstützt eine **Ein-Master-Mehrere-Slaves**-Architektur mit asynchroner Datenreplikation über Binlog:

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

- Master-Knoten verarbeitet alle Schreiboperationen; Slave-Knoten bieten Nur-Lese-Abfragen
- Asynchrone Replikation über Binlog mit Unterstützung für Wiederaufnahme nach Unterbrechung
- Anwendungsschicht ist verantwortlich für Lese-/Schreibtrennung

## Roadmap

| Version | Geplante Funktionen |
|---------|---------------------|
| **v1.0** (Abgeschlossen) | Dualer eingebetteter+Server-Modus, Nova/Flux/KV-Engines, SQL DDL/DML/SELECT/JOIN, Transaktionen/MVCC, WAL/Wiederherstellung, Hot-Cold-Trennung, Sharding, MQ-Consumer-Gruppen, ADO.NET Provider, Cluster-Master-Slave-Sync |
| **v1.1** | P0-Level SQL-Funktionen (String/Numerisch/Datum/Konvertierung/Bedingt ~30 Funktionen) |
| **v1.2** | MQ-blockierendes Lesen, KV Add/Inc-Operationen, P1-Level SQL-Funktionen |
| **v1.3** | MQ-verzögerte Nachrichten, Dead Letter Queue |
| **v2.0** | GeoPoint-Geo-Codierung + Vector-Typ (AI-Vektorsuche), Beobachtbarkeit und Verwaltungstools |

## Positionierung

NovaDb strebt keine vollständige SQL92-Standard-Konformität an, sondern deckt die 80% häufig verwendete Geschäftsteilmenge ab, um die folgenden differenzierten Fähigkeiten zu erhalten:

| Differenzierung | Beschreibung |
|-----------------|--------------|
| **Reines .NET-Managed** | Keine nativen Abhängigkeiten, Bereitstellung über xcopy, keine Serialisierungskosten im selben Prozess mit .NET-Anwendungen |
| **Dualer eingebetteter+Server-Modus** | Eingebettet für Entwicklung/Debugging wie SQLite, eigenständiger Service für Produktion wie MySQL, gleiche API |
| **Ordner als Datenbank** | Ordner kopieren, um Migration/Backup abzuschließen, kein dump/restore erforderlich |
| **Hot-Cold-Index-Trennung** | 10M-Zeilen-Tabelle, die nur Hotspots abfragt, verwendet < 20 MB Speicher, Cold-Daten automatisch in MMF entladen |
| **Vier-Motoren-Integration** | Einzelne Komponente deckt gängige SQLite + TDengine + Redis-Szenarien ab, reduziert die Anzahl der Betriebskomponenten |
| **NewLife Native Integration** | Direkte Anpassung mit XCode ORM + ADO.NET, keine Drittanbieter-Treiber erforderlich |
