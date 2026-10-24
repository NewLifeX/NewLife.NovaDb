# NewLife.NovaDb


![GitHub License](https://img.shields.io/github/license/NewLifeX/NewLife.NovaDb?logo=github)
![NuGet Downloads](https://img.shields.io/nuget/dt/NewLife.NovaDb?logo=nuget)
![NuGet](https://img.shields.io/nuget/v/NewLife.NovaDb?logo=nuget)
![NuGet prerelease](https://img.shields.io/nuget/vpre/NewLife.NovaDb?label=dev%20nuget&logo=nuget)
[![test](https://img.shields.io/github/actions/workflow/status/NewLifeX/NewLife.NovaDb/test.yml?branch=master&label=test&logo=github)](https://github.com/NewLifeX/NewLife.NovaDb/actions/workflows/test.yml)
[![locales](https://img.shields.io/badge/locales-11-2ea44f?logo=dotnet)](Doc/多语言本地化.md)
![platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS%20%7C%20Android%20%7C%20iOS-blue)
![arch](https://img.shields.io/badge/arch-x64%20%7C%20arm64%20%7C%20loongarch64%20%7C%20riscv64%20%7C%20s390x-orange)
[![contributors](https://img.shields.io/github/contributors/NewLifeX/NewLife.NovaDb?logo=github)](https://github.com/NewLifeX/NewLife.NovaDb/graphs/contributors)
<!-- lang:start -->
🌐 [English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [Español](README.es.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [한국어](README.ko.md) | [العربية](README.ar.md)
<!-- lang:end -->

> ⚠️ Diese Übersetzung kann veraltet sein; maßgeblich ist das [englische README](README.md).

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
  - **Flux Engine** (Zeitreihen + MQ): Zeitbasierte Sharding Append Only, unterstützt TTL-Auto-Cleanup, Redis Stream-ähnliche Consumer-Gruppen + Pending + Ack; spaltenweise Blockversiegelung mit typbewusster Kodierung (Delta/DeltaDelta/XOR/RLE/Wörterbuch) plus LZ4-Blockkompression, Rollup-Voraggregation mit automatischem Routing von Aggregatabfragen, SESSION/STATE-Fenstergruppierung und Bericht der Kompressionsrate über `_sys.flux_compression`
  - **KV-Modus** (Schlüssel-Wert-Speicher): Unabhängiger Bitcask-Engine (In-Memory-Index + On-Disk-Werte), unterstützt TTL, Multi-Tabellen-Isolation
  - **ADO.NET Provider**: Erkennt automatisch eingebetteten/Server-Modus, native Integration mit XCode ORM
- **Dynamische Hot-Cold-Index-Trennung**: Hot-Daten vollständig in den physischen Speicher geladen (SkipList-Knoten), Cold-Daten in MMF entladen mit nur spärlichem Verzeichnis beibehalten. 10 Millionen Zeilen Tabelle, die nur die neuesten 10.000 Zeilen abfragt, verwendet < 20 MB Speicher.
- **Reiner verwalteter Code**: Keine nativen Komponentenabhängigkeiten (reines C#/.NET), einfach plattformübergreifend und in eingeschränkten Umgebungen bereitzustellen.

### Speicher-Engines

| Engine | Datenstruktur | Anwendungsfälle |
|--------|---------------|-----------------|
| **Nova Engine** | SkipList (Speicher+MMF Hot-Cold-Trennung) | Allgemeines CRUD, Konfigurationstabellen, Geschäftsbestellungen, Benutzerdaten |
| **Flux Engine** | Zeitbasierte Sharding (Append Only) | IoT-Sensoren, Log-Sammlung, interne Nachrichtenwarteschlangen, Audit-Logs |
| **KV-Modus** | Bitcask (In-Memory-Index + On-Disk-Werte) | Verteilte Sperren, Caching, Sitzungsspeicher, Zähler, Konfigurationszentrum |

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
| GeoPoint | `GEOPOINT` | Benutzerdefinierte Struktur | Breiten-/Längenkoordinaten, mit Distanz-/Polygonabfragen |
| Vector | `VECTOR(n)` | `Single[]` | AI-Vektorsuche, mit KNN-Nachbarschaftsabfragen |

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
| Volltextsuche | ✅ | Volltextindex mit BM25-Bewertung, Top-K-Ergebnisse für hybride RAG-Abfrage |
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

Der Server-Modus bietet Fernzugriff über TCP und unterstützt mehrere gleichzeitige Client-Verbindungen. Jede Datenbank in der Verbindungszeichenfolge (`Database=mydb`) entspricht einem eigenen Engine-Verzeichnis unter dem Datenordner des Servers, wodurch Multi-Datenbank-Routing auf einem einzigen Server möglich ist.

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

#### 1.4 Aggregation und Skalarabfragen

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT COUNT(*) FROM users";
var count = Convert.ToInt32(cmd.ExecuteScalar());
Console.WriteLine($"Total users: {count}");

cmd.CommandText = "SELECT AVG(age) FROM users WHERE age > 0";
var avgAge = Convert.ToDouble(cmd.ExecuteScalar());
Console.WriteLine($"Average age: {avgAge:F1}");
```

#### 1.5 Transaktionen

NovaDb implementiert Transaktionsisolation basierend auf MVCC mit einer Standard-Isolationsstufe von Read Committed:

```csharp
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

using var tx = conn.BeginTransaction();
try
{
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;

    // Bestand abbuchen
    cmd.CommandText = "UPDATE products SET stock = stock - 1 WHERE id = 1 AND stock > 0";
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) throw new InvalidOperationException("Insufficient stock");

    // Bestellung anlegen
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

#### 1.6 JOIN-Abfragen

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

#### 1.7 DDL-Operationen

```sql
-- Datenbank erstellen/löschen
CREATE DATABASE shop;
DROP DATABASE shop;

-- Tabellenstruktur ändern
ALTER TABLE products ADD COLUMN category STRING;
ALTER TABLE products MODIFY COLUMN name STRING(200);
ALTER TABLE products DROP COLUMN category;

-- Indexverwaltung
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

#### 1.9 Verbindungszeichenfolgen-Referenz

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
// Durchschnittstemperatur pro Gerät
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

#### 2.5 Datenaufbewahrungsrichtlinie

Zeitreihen-Tabellen unterstützen die TTL-basierte automatische Bereinigung abgelaufener Shards, wobei Daten nach Zeit oder Kapazität aufbewahrt werden:

```csharp
// Zeitreihen-Parameter über DbOptions konfigurieren
var options = new DbOptions
{
    FluxPartitionHours = 1,        // Eine Partition pro Stunde
    FluxDefaultTtlSeconds = 86400, // Daten 24 Stunden aufbewahren
};
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
// Per RPC SQL ausführen, um eine Nachricht in die Flux-Tabelle einzufügen
var affected = await client.ExecuteAsync(
    "INSERT INTO order_events (timestamp, orderId, action, amount) " +
    "VALUES (NOW(), 10001, 'created', 299.00)");
Console.WriteLine($"Message published, affected rows: {affected}");
```

#### 3.3 Nachrichten konsumieren

```csharp
// Nachrichten lesen (nach Zeitbereich)
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

// KV-Tabelle erstellen (Standard-TTL 7200 Sekunden = 2 Stunden)
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

#### 4.4 Verteilte Sperre

```csharp
// Versuch, die Sperre zu erwerben (Einfügen gelingt nur, wenn der Schlüssel nicht existiert)
var locked = await client.ExecuteAsync(
    "INSERT INTO dist_lock (Key, Value, TTL) VALUES ('lock:order:123', 'worker-1', " +
    "DATEADD(NOW(), 30, 'SECOND'))");

if (locked > 0)
{
    try
    {
        // Sperre erworben, Geschäftslogik ausführen
    }
    finally
    {
        await client.ExecuteAsync("DELETE FROM dist_lock WHERE Key = 'lock:order:123'");
    }
}
```

#### 4.5 Übersicht der KV-Fähigkeiten

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
| **v1.1** (Abgeschlossen) | SQL-Funktionen auf P0-Ebene (String/Numerisch/Datum/Konvertierung/Bedingt ~30 Funktionen) |
| **v1.2** (Abgeschlossen) | Blockierendes Lesen in MQ, KV Add/Inc-Operationen, SQL-Funktionen auf P1-Ebene |
| **v1.3** (Abgeschlossen) | Verzögerte Nachrichten in MQ, Dead Letter Queue |
| **v2.0** (Abgeschlossen) | GeoPoint-Geo-Codierung + Vector-Typ (AI-Vektorsuche), GIS-Erweiterung, Volltextsuche (BM25), NovaVectorStore (IVectorStore, Multi-Collection), inkrementelle materialisierte Sichten, Binlog-Verwaltung, Beobachtbarkeit und Verwaltungstools |
| **v2.1** (Abgeschlossen) | Spaltenweise Versiegelung und Blockkompression für Zeitreihen (numerische Kodierung + LZ4), Rollup-Voraggregation mit Backfill und automatischem Routing von Abfragen, SESSION/STATE_WINDOW-Gruppierung, Bericht der Kompressionsrate, NativeAOT-Unterstützung mit Multi-RID-CI (win-x64/linux-x64/linux-arm64/loongarch64) |

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

## Ökosystem-Karte

NewLife ist ein geschichtetes Ökosystem unabhängig versionierter Open-Source-Projekte. Die folgende Karte wird wortgleich in allen NewLife-Repositories verwendet; jedes Projekt verlinkt auf sein Repository und sein primäres NuGet-Paket.

| Projekt | Funktion | Downloads |
| --- | --- | --- |
| **Grundlage** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | Protokollierung, Konfiguration, Caching, Netzwerk, Serialisierung, Zeitplanung und APM-Tracing. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | Daten-Middleware: einzelne Tabellen in der Größenordnung von zig Milliarden Zeilen, automatisches Sharding, Lese-/Schreibtrennung, über 10 Datenbanken. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | Hochperformante Netzwerkbibliothek: 1,77 Mio. pps / 14,5 Gbps auf einer Maschine (TCP 1KB, 2026-10), 4 Mio. langlebige TCP-Verbindungen. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | Protokollkommunikation und RPC-Framework für Client/Server-Anwendungen und IoT-Geräte. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | Installiert .NET-Anwendungen als Betriebssystem-Dienste: Windows-Dienste und Linux-systemd. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | `dotnet new`-Projektvorlagen für Konsole, Web, API, Dienst, Daten und IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **Daten & Messaging** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | Redis-Client mit Mikrosekunden-Latenz, Millionen Operationen pro Sekunde und integrierten Nachrichtenwarteschlangen. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | Vollständig verwalteter RocketMQ-Client für Apache RocketMQ und Alibaba Cloud Message Queue. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | MQTT-Client und -Server, einschließlich Alibaba Cloud IoT-Anbindung. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | Hybriddatenbank in C#: eingebettet oder im Netzwerk, relational, Zeitreihen, Nachrichtenwarteschlange und KV in einem. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | MySql-Treiber und Hilfsschicht auf Basis des NewLife-Datenstacks. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **Web, KI & verteilt** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | Schnelles Framework für Administrationsplattformen: Berechtigungen, SSO, OAuth-Server und CRUD-Gerüst. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | Verteilte Dienstplattform: Knotenverwaltung, APM, Konfigurationszentrum, Registry und Release-Center. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | Plattform für verteilte Aufgaben und Echtzeit-Datenplanung mit Sharding, Wiederholungen und Backfill. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | Einheitlicher Client für 46 LLM-Anbieter plus ChatAI, eine sofort einsatzbereite Web-Chat-Anwendung. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **Industrie & IoT** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | IoT-Standardbibliothek mit Protokollverträgen und Geräteabstraktionen. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | Modbus-TCP/RTU/ASCII-Implementierung auf Basis von NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | Siemens-SPS-Protokoll auf Basis von NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | Omron-SPS-Protokoll auf Basis von NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | Mitsubishi-Melsec-SPS-Protokoll auf Basis von NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | OPC-Client für industriellen Datenzugriff. | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | BACnet-Protokollstack für die Gebäudeautomation. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | Leichtgewichtige Open-Source-IoT-Plattform mit Gateway- und Geräteverwaltung. | — |
| **Fahrzeugtelematik** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | JT/T-808-Fahrzeugortungsprotokoll: über 70 Nachrichtenkörper, 2011/2013/2019. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | JT/T-809-Plattform-zu-Plattform-Protokoll für Verkehrsbehörden. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | JT/T-1078-Fahrzeugvideo: RTP-Parsing, FLV/fMP4-Remuxing und Live-Streaming. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **Domänenkomponenten** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | Ein Paket für Excel, Word, PPT, PDF und über 15 Dokumentformate. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | Plattformübergreifendes Audio: Codecs, Geräte-I/O, DSP, Container und Streaming. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | Videocodecs, Containerformate, Frame-Verarbeitung und Streaming. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | Einheitliche Schnittstelle für Baidu, AMap, Tencent und Tianditu mit Geokodierung und Koordinatenumrechnung. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | IPv4-Geolokalisierung mit einer offline speichergemappten Datenbank und einer Online-API als Fallback. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **Werkzeuge & Produkte** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | Entwickler-Toolbox: Netzwerk, serielle Schnittstelle, Krypto, Regex, Modbus und MQTT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | Selbst gehostetes Git-Quellcode-Verwaltungssystem. | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | Installiert automatisch die passende .NET-Laufzeit, wenn es von einem älteren Host gestartet wird. | — |

> Die Badges zeigen das primäre Paket jedes Projekts; Theme-, Erweiterungs- und Satellitenpakete sind auf den jeweiligen NuGet-Seiten aufgeführt. Die hier nicht aufgeführten über 30 Pakete (FTP, LoRa, Office-Erweiterungen, Protokolltreiber und mehr) sind in der [NewLifeX-Organisation](https://github.com/NewLifeX) und auf [newlifex.com](https://newlifex.com) verfügbar.


## Team

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

NewLife wurde 2002 gegründet und widmet sich seit mehr als zwei Jahrzehnten dem .NET- und IoT-Ökosystem, betreut fast 100 Open-Source-Projekte (34 davon Hauptprojekte) und liefert integrierte Hard- und Softwarelösungen für die Bereiche Energie, Logistik, Gesundheitswesen, Industrieautomation und Internet. Unser Leitspruch lautet „Lernen kennt kein Dienstalter – wer es beherrscht, wird zum Lehrmeister“.

Die Open-Source-Komponenten wurden <!--nuget-total:start-->6.48M<!--nuget-total:end--> Mal von NuGet heruntergeladen (ökosystemweit, Stand 2026-10).

Website: https://newlifex.com · Open-Source-Organisation: https://github.com/NewLifeX

<details>
<summary>WeChat-Offizieller Account: 智能大石头 (zum Folgen scannen)</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>

