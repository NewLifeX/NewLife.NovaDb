# NewLife.NovaDb

[![English](https://img.shields.io/badge/lang-English-blue.svg)](README.md) [![简体中文](https://img.shields.io/badge/lang-简体中文-red.svg)](README_CN.md) [![繁體中文](https://img.shields.io/badge/lang-繁體中文-orange.svg)](README_TW.md) [![Français](https://img.shields.io/badge/lang-Français-green.svg)](README_FR.md) [![Deutsch](https://img.shields.io/badge/lang-Deutsch-yellow.svg)](README_DE.md) [![日本語](https://img.shields.io/badge/lang-日本語-purple.svg)](README_JA.md) [![한국어](https://img.shields.io/badge/lang-한국어-brightgreen.svg)](README_KO.md) [![Русский](https://img.shields.io/badge/lang-Русский-lightgrey.svg)](README_RU.md) [![Español](https://img.shields.io/badge/lang-Español-yellow.svg)](README_ES.md) [![Português](https://img.shields.io/badge/lang-Português-blue.svg)](README_PT.md)

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
  - **Mode KV** (vue logique) : Réutilise Nova Engine, l'API masque les détails SQL, chaque ligne contient `Key + Value + TTL`
  - **ADO.NET Provider** : Reconnaît automatiquement le mode embarqué/serveur, intégration native avec XCode ORM
- **Séparation d'index chaud-froid dynamique** : Les données chaudes sont entièrement chargées dans la mémoire physique (nœuds SkipList), les données froides sont déchargées vers MMF avec seulement un répertoire clairsemé conservé. Une table de 10 millions de lignes interrogeant uniquement les 10 000 dernières lignes utilise < 20 Mo de mémoire.
- **Code purement géré** : Aucune dépendance de composant natif (pur C#/.NET), facile à déployer sur plusieurs plateformes et dans des environnements restreints.

### Moteurs de stockage

| Moteur | Structure de données | Cas d'utilisation |
|--------|----------------------|-------------------|
| **Nova Engine** | SkipList (Séparation mémoire+MMF chaud-froid) | CRUD général, tables de configuration, commandes commerciales, données utilisateur |
| **Flux Engine** | Partitionnement basé sur le temps (Append Only) | Capteurs IoT, collecte de journaux, files d'attente de messages internes, journaux d'audit |
| **Mode KV** | Vue logique de table Nova | Verrous distribués, mise en cache, stockage de session, compteurs, centre de configuration |

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
| Vecteur | `VECTOR(n)` | `Single[]` | Recherche vectorielle AI (prévu) |

### Capacités SQL

Sous-ensemble SQL standard implémenté, couvrant environ 60% des scénarios commerciaux courants :

| Fonctionnalité | Statut | Description |
|----------------|--------|-------------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX, avec IF NOT EXISTS, PRIMARY KEY, UNIQUE |
| DML | ✅ | INSERT (plusieurs lignes), UPDATE, DELETE, UPSERT (ON DUPLICATE KEY UPDATE), TRUNCATE TABLE |
| Requête | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| Agrégation | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN (Nested Loop), prend en charge les alias de table |
| Paramétrage | ✅ | Espaces réservés @param |
| Transaction | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| Fonctions SQL | ⚠️ | Fonctions de chaîne/numérique/date/conversion/conditionnelle (prévu) |
| Sous-requête | ⚠️ | IN/EXISTS (prévu) |
| Avancé | ❌ | Pas de vues/déclencheurs/procédures stockées/fonctions de fenêtre |

### Capacités MQ (Flux Engine)

Basé sur le modèle de groupe de consommateurs de Redis Stream :

- **ID de message** : Horodatage + numéro de séquence (auto-incrément dans la même milliseconde), ordonné globalement
- **Groupe de consommateurs** : `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **Fiabilité** : At-Least-Once, entre dans Pending après lecture, Ack après succès commercial
- **Rétention des données** : Prend en charge TTL (supprime automatiquement les anciens fragments par temps/taille de fichier)
- **Messages différés** : Spécifier `DelayTime`/`DeliverAt` (prévu)
- **File d'attente de lettres mortes** : Entre automatiquement dans DLQ en cas d'échec de consommation (prévu)
- **Lecture bloquante** : Sondage long + délai d'attente (prévu)

### Capacités KV

- `Get(key)` / `Set(key, value, ttl)` / `Delete(key)` / `Exists(key)`
- Suppression paresseuse (vérifier l'expiration lors de la lecture) + nettoyage en arrière-plan (`CleanupExpired()`)
- `Add(key, value, ttl)` : Ajouter uniquement lorsque la clé n'existe pas (prévu)
- `Inc(key, delta, ttl)` : Incrément atomique (prévu)

## Sécurité des données et modes WAL

NovaDb fournit trois stratégies de persistance WAL :

| Mode | Description | Cas d'utilisation |
|------|-------------|-------------------|
| `FULL` | Écriture disque synchrone, flush immédiatement à chaque commit | Scénarios financiers/de trading, sécurité des données la plus forte |
| `NORMAL` | Flush asynchrone 1s (par défaut) | La plupart des scénarios commerciaux, équilibre performance et sécurité |
| `NONE` | Entièrement asynchrone, pas de flush proactif | Scénarios de données temporaires/cache, débit le plus élevé |

> Choisir un mode autre que synchrone (`FULL`) signifie accepter une perte de données possible dans les scénarios de crash/panne de courant.

## Feuille de route

| Version | Fonctionnalités prévues |
|---------|-------------------------|
| **v1.0** (Terminé) | Mode double embarqué+serveur, moteurs Nova/Flux/KV, SQL DDL/DML/SELECT/JOIN, transactions/MVCC, WAL/récupération, séparation chaud-froid, partitionnement, groupes de consommateurs MQ, ADO.NET Provider, synchronisation maître-esclave en cluster |
| **v1.1** | Fonctions SQL de niveau P0 (chaîne/numérique/date/conversion/conditionnelle ~30 fonctions) |
| **v1.2** | Lecture bloquante MQ, opérations KV Add/Inc, fonctions SQL de niveau P1 |
| **v1.3** | Messages différés MQ, file d'attente de lettres mortes |
| **v2.0** | Géocodage GeoPoint + type Vector (recherche vectorielle AI), observabilité et outils de gestion |

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
