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
🌐 [English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [한국어](README.ko.md) | [العربية](README.ar.md)
<!-- lang:end -->

> ⚠️ Cette traduction peut être en retard sur la dernière version ; la référence est le [README anglais](README.md).

Une base de données hybride de moyenne à grande taille implémentée en **C#**, fonctionnant sur la **plateforme .NET** (prend en charge .NET Framework 4.5 ~ .NET 10), prenant en charge le mode double embarqué/serveur, intégrant les capacités relationnelles, de séries chronologiques, de file d'attente de messages et NoSQL (KV).

## Présentation du produit

`NewLife.NovaDb` (abrégé `Nova`) est l'infrastructure de base de l'écosystème NewLife, un moteur de données intégré pour les applications .NET. En supprimant de nombreuses fonctionnalités de niche (telles que les procédures stockées/déclencheurs/fonctions de fenêtre), il obtient de meilleures performances de lecture/écriture et des coûts opérationnels plus faibles ; le volume de données est logiquement illimité (contraint par le disque et les stratégies de partitionnement), et peut remplacer SQLite/MySQL/Redis/TDengine dans des scénarios spécifiques.

### Caractéristiques principales

- **Modes de déploiement doubles** :
  - **Mode embarqué** : S'exécute comme une bibliothèque comme SQLite, avec des données stockées dans des dossiers locaux, configuration zéro
  - **Mode serveur** : Processus autonome + protocole TCP, accès réseau comme MySQL ; prend en charge le déploiement en cluster et la réplication maître-esclave (un maître, plusieurs esclaves)
- **Dossier comme base de données** : Copiez le dossier pour terminer la migration/sauvegarde, aucun processus dump/restore nécessaire. Chaque table a des groupes de fichiers indépendants (`.data`/`.idx`/`.wal`).
- **Intégration de quatre moteurs** :
  - **Nova Engine** (relationnel général) : Index SkipList + transactions MVCC (Read Committed), prend en charge CRUD, requêtes SQL, JOIN
  - **Flux Engine** (séries temporelles + MQ) : Partitionnement basé sur le temps Append Only, prend en charge le nettoyage automatique TTL, groupes de consommateurs de style Redis Stream + Pending + Ack
  - **Mode KV** (stockage clé-valeur) : Moteur Bitcask indépendant (index en mémoire + valeurs sur disque), prend en charge TTL, isolation multi-tables
  - **ADO.NET Provider** : Reconnaît automatiquement le mode embarqué/serveur, intégration native avec XCode ORM
- **Séparation d'index chaud-froid dynamique** : Les données chaudes sont entièrement chargées dans la mémoire physique (nœuds SkipList), les données froides sont déchargées vers MMF avec seulement un répertoire clairsemé conservé. Une table de 10 millions de lignes interrogeant uniquement les 10 000 dernières lignes utilise < 20 Mo de mémoire.
- **Code purement géré** : Aucune dépendance de composant natif (pur C#/.NET), facile à déployer sur plusieurs plateformes et dans des environnements restreints.

### Moteurs de stockage

| Moteur | Structure de données | Cas d'utilisation |
|--------|----------------------|-------------------|
| **Nova Engine** | SkipList (Séparation mémoire+MMF chaud-froid) | CRUD général, tables de configuration, commandes commerciales, données utilisateur |
| **Flux Engine** | Partitionnement basé sur le temps (Append Only) | Capteurs IoT, collecte de journaux, files d'attente de messages internes, journaux d'audit |
| **Mode KV** | Bitcask (index en mémoire + valeurs sur disque) | Verrous distribués, mise en cache, stockage de session, compteurs, centre de configuration |

### Types de données

| Catégorie | Type SQL | Mapping C# | Description |
|-----------|----------|------------|-------------|
| Booléen | `BOOL` | `Boolean` | 1 octet |
| Entier | `INT` / `LONG` | `Int32` / `Int64` | 4/8 octets |
| Flottant | `DOUBLE` | `Double` | 8 octets |
| Décimal | `DECIMAL` | `Decimal` | 128 bits, précision unifiée |
| Chaîne | `STRING(n)` / `STRING` | `String` | UTF-8, la longueur peut être spécifiée |
| Binaire | `BINARY(n)` / `BLOB` | `Byte[]` | La longueur peut être spécifiée |
| DateTime | `DATETIME` | `DateTime` | Précision en Ticks (100 nanosecondes) |
| GeoPoint | `GEOPOINT` | Structure personnalisée | Coordonnées latitude/longitude (prévu) |
| Vecteur | `VECTOR(n)` | `Single[]` | Recherche vectorielle IA (prévu) |

### Capacités SQL

Sous-ensemble SQL standard implémenté, couvrant environ 60% des scénarios commerciaux courants :

| Fonctionnalité | Statut | Description |
|----------------|--------|-------------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX/DATABASE, ALTER TABLE (ADD/MODIFY/DROP COLUMN, COMMENT), avec IF NOT EXISTS, PRIMARY KEY, UNIQUE, ENGINE |
| DML | ✅ | INSERT (plusieurs lignes), UPDATE, DELETE, UPSERT (ON DUPLICATE KEY UPDATE), TRUNCATE TABLE |
| Requête | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| Agrégation | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN (Nested Loop), prend en charge les alias de table |
| Paramétrage | ✅ | Espaces réservés @param |
| Transaction | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| Fonctions SQL | ✅ | Fonctions de chaîne/numérique/date/conversion/conditionnelle/hachage (60+ fonctions) |
| Sous-requête | ✅ | Sous-requêtes IN/EXISTS |
| Recherche plein texte | ✅ | Index de texte intégral avec score BM25, résultats Top-K pour la récupération hybride RAG |
| Avancé | ❌ | Pas de vues/déclencheurs/procédures stockées/fonctions de fenêtre |

---

## Guide d'utilisation

### Installation

Installez le package principal NovaDb via NuGet :

```shell
dotnet add package NewLife.NovaDb
```

### Méthodes d'accès

NovaDb fournit deux méthodes d'accès client pour différents scénarios :

| Méthode d'accès | Moteur cible | Description |
|-----------------|--------------|-------------|
| **ADO.NET + SQL** | Nova (Relationnel), Flux (Séries temporelles) | `DbConnection`/`DbCommand`/`DbDataReader` standard, compatible avec tous les ORM |
| **NovaClient** | MQ (File d'attente de messages), KV (Stockage clé-valeur) | Client RPC fournissant des API de publication/consommation/acquittement de messages et de lecture/écriture KV |

---

### 1. Base de données relationnelle (ADO.NET + SQL)

Le moteur relationnel (Nova Engine) est accessible via l'interface standard ADO.NET. Un `Data Source` dans la chaîne de connexion indique le mode embarqué ; un `Server` indique le mode serveur.

#### 1.1 Mode embarqué (Démarrage rapide en 5 minutes)

Le mode embarqué ne nécessite aucun service autonome, idéal pour les applications de bureau, les appareils IoT et les tests unitaires.

```csharp
using NewLife.NovaDb.Client;

// Créer une connexion (mode embarqué, dossier comme base de données)
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

// Créer une table
using var cmd = conn.CreateCommand();
cmd.CommandText = @"CREATE TABLE IF NOT EXISTS users (
    id   INT PRIMARY KEY AUTO_INCREMENT,
    name STRING(50) NOT NULL,
    age  INT DEFAULT 0,
    created DATETIME
)";
cmd.ExecuteNonQuery();

// Insérer des données
cmd.CommandText = "INSERT INTO users (name, age, created) VALUES ('Alice', 25, NOW())";
cmd.ExecuteNonQuery();

// Insertion par lots
cmd.CommandText = @"INSERT INTO users (name, age) VALUES
    ('Bob', 30),
    ('Charlie', 28)";
cmd.ExecuteNonQuery();

// Requête de données
cmd.CommandText = "SELECT * FROM users WHERE age >= 25 ORDER BY age";
using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"id={reader["id"]}, name={reader["name"]}, age={reader["age"]}");
}
```

#### 1.2 Mode serveur

Le mode serveur fournit un accès distant via TCP, prenant en charge plusieurs connexions clients simultanées.

**Démarrer le serveur :**

```csharp
using NewLife.NovaDb.Server;

var svr = new NovaServer(3306) { DbPath = "./data" };
svr.Start();
Console.ReadLine();
svr.Stop("Manual shutdown");
```

**Connexion client ADO.NET (API identique au mode embarqué) :**

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

#### 1.3 Requêtes paramétrées

Les requêtes paramétrées empêchent l'injection SQL en utilisant des paramètres nommés `@name` :

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

#### 1.4 Transactions

NovaDb implémente l'isolation des transactions basée sur MVCC avec un niveau d'isolation par défaut de Read Committed :

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

#### 1.5 Requêtes JOIN

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

#### 1.6 Référence de chaîne de connexion

| Paramètre | Exemple | Description |
|-----------|---------|-------------|
| `Data Source` | `Data Source=./mydb` | Mode embarqué, chemin du dossier de la base de données |
| `Server` | `Server=127.0.0.1` | Mode serveur, adresse du serveur |
| `Port` | `Port=3306` | Port du serveur (par défaut 3306) |
| `Database` | `Database=mydb` | Nom de la base de données |
| `WalMode` | `WalMode=Full` | Mode WAL (Full/Normal/None) |
| `ReadOnly` | `ReadOnly=true` | Mode lecture seule |

---

### 2. Base de données de séries temporelles (ADO.NET + SQL)

Le moteur de séries temporelles (Flux Engine) est également accessible via ADO.NET + SQL. Spécifiez `ENGINE=FLUX` lors de la création des tables.

#### 2.1 Créer une table de séries temporelles

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

#### 2.2 Écrire des données de séries temporelles

```csharp
// Insertion unique
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity)
    VALUES (NOW(), 'sensor-001', 23.5, 65.0)";
cmd.ExecuteNonQuery();

// Insertion par lots
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity) VALUES
    ('2025-07-01 10:00:00', 'sensor-001', 22.1, 60.0),
    ('2025-07-01 10:01:00', 'sensor-001', 22.3, 61.0),
    ('2025-07-01 10:02:00', 'sensor-002', 25.0, 55.0)";
cmd.ExecuteNonQuery();
```

#### 2.3 Requête par plage temporelle

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

#### 2.4 Analyse d'agrégation

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

### 3. File d'attente de messages (NovaClient)

NovaDb implémente une file d'attente de messages de style Redis Stream basée sur le moteur de séries temporelles Flux. La file d'attente de messages est accessible via l'interface RPC `NovaClient`.

#### 3.1 Connexion au serveur

```csharp
using NewLife.NovaDb.Client;

using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();
```

#### 3.2 Publier des messages

```csharp
var affected = await client.ExecuteAsync(
    "INSERT INTO order_events (timestamp, orderId, action, amount) " +
    "VALUES (NOW(), 10001, 'created', 299.00)");
Console.WriteLine($"Message published, affected rows: {affected}");
```

#### 3.3 Consommer des messages

```csharp
var messages = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT * FROM order_events WHERE timestamp > @since ORDER BY timestamp LIMIT 10",
    new { since = DateTime.Now.AddMinutes(-5) });
```

#### 3.4 Battement de cœur

```csharp
var serverTime = await client.PingAsync();
Console.WriteLine($"Server connected: {serverTime}");
Console.WriteLine($"Is connected: {client.IsConnected}");
```

#### 3.5 Fonctionnalités principales MQ

- **ID de message** : Horodatage + numéro de séquence (auto-incrément dans la même milliseconde), ordonné globalement
- **Groupe de consommateurs** : `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **Fiabilité** : At-Least-Once, entre dans Pending après lecture, Ack après succès métier
- **Rétention des données** : Prend en charge TTL (supprime automatiquement les anciens fragments par temps/taille de fichier)
- **Messages différés** : Spécifier la durée du délai ou l'heure de livraison exacte
- **File d'attente de lettres mortes** : Entre automatiquement dans la DLQ après dépassement du nombre maximal de tentatives

---

### 4. Stockage clé-valeur KV (NovaClient)

Le stockage KV est accessible via `NovaClient`. Spécifiez `ENGINE=KV` lors de la création des tables. Les tables KV ont un schéma fixe de `Key + Value + TTL`.

#### 4.1 Créer une table KV

```csharp
using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();

await client.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS session_cache (
    Key STRING(200) PRIMARY KEY, Value BLOB, TTL DATETIME
) ENGINE=KV DEFAULT_TTL=7200");
```

#### 4.2 Lire/Écrire des données

```csharp
// Écriture (sémantique UPSERT)
await client.ExecuteAsync(
    "INSERT INTO session_cache (Key, Value, TTL) VALUES ('session:1001', 'user-data', " +
    "DATEADD(NOW(), 30, 'MINUTE')) ON DUPLICATE KEY UPDATE Value = 'user-data'");

// Lecture
var result = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT Value FROM session_cache WHERE Key = 'session:1001' " +
    "AND (TTL IS NULL OR TTL > NOW())");

// Suppression
await client.ExecuteAsync("DELETE FROM session_cache WHERE Key = 'session:1001'");
```

#### 4.3 Incrément atomique (Compteur)

```csharp
await client.ExecuteAsync(
    "INSERT INTO counters (Key, Value) VALUES ('page:views', 1) " +
    "ON DUPLICATE KEY UPDATE Value = Value + 1");
```

#### 4.4 Aperçu des capacités KV

| Opération | Description |
|-----------|-------------|
| `Get` | Lire la valeur avec vérification paresseuse du TTL |
| `Set` | Définir la valeur avec TTL optionnel |
| `Add` | Ajouter uniquement lorsque la clé n'existe pas (verrou distribué) |
| `Delete` | Supprimer la clé |
| `Inc` | Incrément/décrément atomique (compteur) |
| `TTL` | Invisible automatiquement à l'expiration, nettoyage périodique en arrière-plan |

---

## Sécurité des données et modes WAL

NovaDb fournit trois stratégies de persistance WAL :

| Mode | Description | Cas d'utilisation |
|------|-------------|-------------------|
| `FULL` | Écriture disque synchrone, flush immédiatement à chaque commit | Scénarios financiers/de trading, sécurité des données la plus forte |
| `NORMAL` | Flush asynchrone 1s (par défaut) | La plupart des scénarios commerciaux, équilibre performance et sécurité |
| `NONE` | Entièrement asynchrone, pas de flush proactif | Scénarios de données temporaires/cache, débit le plus élevé |

> Choisir un mode autre que synchrone (`FULL`) signifie accepter une perte de données possible dans les scénarios de crash/panne de courant.

## Déploiement en cluster

NovaDb prend en charge une architecture **un maître, plusieurs esclaves** avec réplication asynchrone des données via Binlog :

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

- Le nœud maître gère toutes les opérations d'écriture ; les nœuds esclaves fournissent des requêtes en lecture seule
- Réplication asynchrone via Binlog avec prise en charge de la reprise après interruption
- La couche applicative est responsable de la séparation lecture/écriture

## Feuille de route

| Version | Fonctionnalités prévues |
|---------|-------------------------|
| **v1.0** (Terminé) | Mode double embarqué+serveur, moteurs Nova/Flux/KV, SQL DDL/DML/SELECT/JOIN, transactions/MVCC, WAL/récupération, séparation chaud-froid, partitionnement, groupes de consommateurs MQ, ADO.NET Provider, synchronisation maître-esclave en cluster |
| **v1.1** | Fonctions SQL de niveau P0 (chaîne/numérique/date/conversion/conditionnelle ~30 fonctions) |
| **v1.2** | Lecture bloquante MQ, opérations KV Add/Inc, fonctions SQL de niveau P1 |
| **v1.3** | Messages différés MQ, file d'attente de lettres mortes |
| **v2.0** (Terminé) | Géocodage GeoPoint + type Vector (recherche vectorielle IA), extension GIS, recherche plein texte (BM25), NovaVectorStore (IVectorStore, multi-collection), vues matérialisées incrémentales, gestion Binlog, observabilité et outils de gestion |

## Positionnement

NovaDb ne poursuit pas la conformité complète à la norme SQL92, mais couvre le sous-ensemble de 80% couramment utilisé en affaires en échange des capacités différenciées suivantes :

| Différenciation | Description |
|-----------------|-------------|
| **Pur .NET géré** | Aucune dépendance native, déploiement via xcopy, surcharge de sérialisation nulle dans le même processus avec les applications .NET |
| **Mode double embarqué+serveur** | Embarqué pour le développement/débogage comme SQLite, service autonome pour la production comme MySQL, même API |
| **Dossier comme base de données** | Copier le dossier pour terminer la migration/sauvegarde, aucun dump/restore nécessaire |
| **Séparation d'index chaud-froid** | Table de 10M lignes interrogeant uniquement les points chauds utilise < 20 Mo de mémoire, données froides déchargées automatiquement vers MMF |
| **Intégration de quatre moteurs** | Un seul composant couvre les scénarios courants SQLite + TDengine + Redis, réduit le nombre de composants opérationnels |
| **Intégration native NewLife** | Adaptation directe avec XCode ORM + ADO.NET, aucun pilote tiers nécessaire |

## Carte de l'écosystème

NewLife est un écosystème en couches de projets open source versionnés indépendamment. La carte ci-dessous est reprise à l'identique par tous les dépôts NewLife ; chaque projet renvoie à son dépôt et à son paquet NuGet principal.

| Projet | Rôle | Téléchargements |
| --- | --- | --- |
| **Fondation** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | Journalisation, configuration, cache, réseau, sérialisation, ordonnancement et traçage APM. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | Intergiciel de données : tables uniques à l'échelle de dizaines de milliards, partitionnement automatique, séparation lecture/écriture et plus de 10 bases de données. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | Bibliothèque réseau hautes performances : 1,77 M pps / 14,5 Gbps sur une machine (TCP 1KB, 2026-10), 4 M connexions TCP persistantes. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | Communication protocolaire et framework RPC pour applications client/serveur et appareils IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | Installe des applications .NET comme démons système : services Windows et systemd Linux. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | Modèles de projet `dotnet new` pour console, web, API, service, données et IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **Données et messagerie** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | Client Redis avec une latence en microsecondes, des millions d'opérations/s et des files de messages intégrées. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | Client RocketMQ entièrement managé pour Apache RocketMQ et Alibaba Cloud Message Queue. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | Client et serveur MQTT, y compris la connectivité Alibaba Cloud IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | Base de données hybride en C# : embarquée ou en réseau, relationnelle, séries temporelles, file de messages et KV réunis. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | Pilote et couche d'assistance MySql construits sur la pile de données NewLife. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **Web, IA et distribué** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | Framework rapide de plateforme d'administration : permissions, SSO, serveur OAuth et échafaudage CRUD. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | Plateforme de services distribués : gestion des nœuds, APM, centre de configuration, registre et centre de publication. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | Plateforme de tâches distribuées et d'ordonnancement de données en temps réel avec partitionnement, reprises et backfill. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | Client unifié pour 46 fournisseurs de LLM, plus ChatAI, une application web de chat prête à l'emploi. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **Industriel et IoT** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | Bibliothèque standard IoT définissant les contrats de protocole et les abstractions d'appareils. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | Implémentation Modbus TCP/RTU/ASCII au-dessus de NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | Protocole pour automate Siemens au-dessus de NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | Protocole pour automate Omron au-dessus de NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | Protocole pour automate Mitsubishi Melsec au-dessus de NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | Client OPC pour l'accès aux données industrielles. | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | Pile de protocoles BACnet pour l'automatisation des bâtiments. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | Plateforme IoT open source légère avec gestion des passerelles et des appareils. | — |
| **Télématique embarquée** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | Protocole de positionnement de véhicules JT/T 808 : plus de 70 corps de message, 2011/2013/2019. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | Protocole plateforme à plateforme JT/T 809 pour les autorités de transport. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | Vidéo embarquée JT/T 1078 : analyse RTP, remuxage FLV/fMP4 et diffusion en direct. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **Composants métier** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | Un seul paquet pour Excel, Word, PPT, PDF et plus de 15 formats de documents. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | Audio multiplateforme : codecs, E/S d'appareils, DSP, conteneurs et streaming. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | Codecs vidéo, formats de conteneurs, traitement des images et streaming. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | Interface unifiée pour Baidu, AMap, Tencent et Tianditu avec géocodage et conversion de coordonnées. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | Géolocalisation IPv4 avec une base de données hors ligne mappée en mémoire et une API en ligne en secours. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **Outils et produits** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | Boîte à outils du développeur : réseau, port série, cryptographie, regex, Modbus et MQTT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | Système de gestion de code source Git auto-hébergé. | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | Installe automatiquement le runtime .NET correspondant lorsqu'il est lancé depuis un hôte plus ancien. | — |

> Les badges indiquent le paquet principal de chaque projet ; les paquets de thèmes, d'extensions et satellites sont répertoriés sur leurs pages NuGet.Les plus de 30 paquets non listés ici (FTP, LoRa, extensions Office, pilotes de protocoles, etc.) sont disponibles dans l'[organisation NewLifeX](https://github.com/NewLifeX) et sur [newlifex.com](https://newlifex.com).


## Équipe

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

NewLife a été fondée en 2002 et œuvre depuis plus de deux décennies dans l'écosystème .NET et IoT, maintient près de 100 projets open source (dont 34 principaux) et fournit des solutions matérielles et logicielles intégrées aux secteurs de l'énergie, de la logistique, de la santé, du contrôle industriel et d'Internet. Notre devise : « l'apprentissage ne connaît pas l'ancienneté — celui qui maîtrise devient maître ».

Les composants open source ont été téléchargés <!--nuget-total:start-->6.48M<!--nuget-total:end--> fois depuis NuGet (à l'échelle de l'écosystème, en date de 2026-10).

Site web : https://newlifex.com · Organisation open source : https://github.com/NewLifeX

<details>
<summary>Compte officiel WeChat : 智能大石头 (scannez pour suivre)</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>

