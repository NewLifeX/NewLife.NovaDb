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
| DDL | ✅ | CREATE/DROP TABLE/INDEX, mit IF NOT EXISTS, PRIMARY KEY, UNIQUE |
| DML | ✅ | INSERT (mehrere Zeilen), UPDATE, DELETE, UPSERT (ON DUPLICATE KEY UPDATE), TRUNCATE TABLE |
| Abfrage | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| Aggregation | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN (Nested Loop), unterstützt Tabellenaliase |
| Parametrisierung | ✅ | @param-Platzhalter |
| Transaktion | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| SQL-Funktionen | ⚠️ | String-/Numerische/Datum-/Konvertierungs-/Bedingte Funktionen (geplant) |
| Unterabfrage | ⚠️ | IN/EXISTS (geplant) |
| Erweitert | ❌ | Keine Views/Trigger/Gespeicherte Prozeduren/Fensterfunktionen |

### MQ-Fähigkeiten (Flux Engine)

Basierend auf dem Consumer-Gruppen-Modell von Redis Stream:

- **Nachrichten-ID**: Zeitstempel + Sequenznummer (Auto-Inkrement innerhalb derselben Millisekunde), global geordnet
- **Consumer-Gruppe**: `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **Zuverlässigkeit**: At-Least-Once, geht nach dem Lesen in Pending, Ack nach Geschäftserfolg
- **Datenspeicherung**: Unterstützt TTL (löscht automatisch alte Shards nach Zeit/Dateigröße)
- **Verzögerte Nachrichten**: Geben Sie `DelayTime`/`DeliverAt` an (geplant)
- **Dead Letter Queue**: Geht automatisch bei Verbrauchsfehlern in DLQ (geplant)
- **Blockierendes Lesen**: Langes Polling + Timeout (geplant)

### KV-Fähigkeiten

- `Get(key)` / `Set(key, value, ttl)` / `Delete(key)` / `Exists(key)`
- Lazy Deletion (Ablauf beim Lesen überprüfen) + Hintergrundbereinigung (`CleanupExpired()`)
- `Add(key, value, ttl)`: Nur hinzufügen, wenn Schlüssel nicht existiert (geplant)
- `Inc(key, delta, ttl)`: Atomarer Inkrement (geplant)

## Datensicherheit und WAL-Modi

NovaDb bietet drei WAL-Persistenzstrategien:

| Modus | Beschreibung | Anwendungsfälle |
|-------|--------------|-----------------|
| `FULL` | Synchrone Festplattenschreibung, sofortiges Flush bei jedem Commit | Finanz-/Handelsszenarien, stärkste Datensicherheit |
| `NORMAL` | Async 1s Flush (Standard) | Die meisten Geschäftsszenarien, balanciert Leistung und Sicherheit |
| `NONE` | Vollständig asynchron, kein proaktives Flush | Temporäre Daten/Cache-Szenarien, höchster Durchsatz |

> Die Wahl eines anderen Modus als synchron (`FULL`) bedeutet, möglichen Datenverlust in Crash-/Stromausfallszenarien zu akzeptieren.

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
