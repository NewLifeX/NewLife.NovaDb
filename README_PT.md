# NewLife.NovaDb

[![English](https://img.shields.io/badge/lang-English-blue.svg)](README.md) [![简体中文](https://img.shields.io/badge/lang-简体中文-red.svg)](README_CN.md) [![繁體中文](https://img.shields.io/badge/lang-繁體中文-orange.svg)](README_TW.md) [![Français](https://img.shields.io/badge/lang-Français-green.svg)](README_FR.md) [![Deutsch](https://img.shields.io/badge/lang-Deutsch-yellow.svg)](README_DE.md) [![日本語](https://img.shields.io/badge/lang-日本語-purple.svg)](README_JA.md) [![한국어](https://img.shields.io/badge/lang-한국어-brightgreen.svg)](README_KO.md) [![Русский](https://img.shields.io/badge/lang-Русский-lightgrey.svg)](README_RU.md) [![Español](https://img.shields.io/badge/lang-Español-yellow.svg)](README_ES.md) [![Português](https://img.shields.io/badge/lang-Português-blue.svg)](README_PT.md)

Um banco de dados híbrido de médio a grande porte implementado em **C#**, executando na **plataforma .NET** (suporta .NET Framework 4.5 ~ .NET 10), suportando modo duplo embarcado/servidor, integrando capacidades relacionais, séries temporais, fila de mensagens e NoSQL (KV).

## Apresentação do Produto

`NewLife.NovaDb` (abreviado como `Nova`) é a infraestrutura central do ecossistema NewLife, um motor de dados integrado para aplicações .NET. Ao remover muitos recursos de nicho (como procedimentos armazenados/triggers/funções de janela), alcança maior desempenho de leitura/escrita e menores custos operacionais; o volume de dados é logicamente ilimitado (restrito por disco e estratégias de particionamento), e pode substituir SQLite/MySQL/Redis/TDengine em cenários específicos.

### Recursos Principais

- **Modos de Implantação Duplos**:
  - **Modo Embarcado**: Executa como uma biblioteca como SQLite, com dados armazenados em pastas locais, configuração zero
  - **Modo Servidor**: Processo independente + protocolo TCP, acesso de rede como MySQL; suporta implantação em cluster e replicação master-slave (um master, vários slaves)
- **Pasta como Banco de Dados**: Copie a pasta para concluir a migração/backup, nenhum processo dump/restore necessário. Cada tabela tem grupos de arquivos independentes (`.data`/`.idx`/`.wal`).
- **Integração de Quatro Motores**:
  - **Nova Engine** (Relacional Geral): Índice SkipList + transações MVCC (Read Committed), suporta CRUD, consultas SQL, JOIN
  - **Flux Engine** (Séries Temporais + MQ): Particionamento baseado em tempo Append Only, suporta limpeza automática TTL, grupos de consumidores estilo Redis Stream + Pending + Ack
  - **Modo KV** (Visão Lógica): Reutiliza Nova Engine, API oculta detalhes SQL, cada linha contém `Key + Value + TTL`
  - **ADO.NET Provider**: Reconhece automaticamente o modo embarcado/servidor, integração nativa com XCode ORM
- **Separação Dinâmica de Índices Quente-Frio**: Dados quentes totalmente carregados na memória física (nós SkipList), dados frios descarregados para MMF com apenas diretório esparso retido. Tabela de 10 milhões de linhas consultando apenas as últimas 10.000 linhas usa < 20 MB de memória.
- **Código Puramente Gerenciado**: Sem dependências de componentes nativos (C#/.NET puro), fácil de implantar em plataformas e ambientes restritos.

### Motores de Armazenamento

| Motor | Estrutura de Dados | Casos de Uso |
|-------|-------------------|--------------|
| **Nova Engine** | SkipList (Separação Memória+MMF Quente-Frio) | CRUD geral, tabelas de configuração, pedidos comerciais, dados de usuário |
| **Flux Engine** | Particionamento baseado em tempo (Append Only) | Sensores IoT, coleta de logs, filas de mensagens internas, logs de auditoria |
| **Modo KV** | Visão Lógica da Tabela Nova | Bloqueios distribuídos, cache, armazenamento de sessão, contadores, centro de configuração |

### Tipos de Dados

| Categoria | Tipo SQL | Mapeamento C# | Descrição |
|-----------|----------|---------------|-----------|
| Boolean | `BOOL` | `Boolean` | 1 byte |
| Integer | `INT` / `LONG` | `Int32` / `Int64` | 4/8 bytes |
| Float | `DOUBLE` | `Double` | 8 bytes |
| Decimal | `DECIMAL` | `Decimal` | 128 bits, precisão unificada |
| String | `STRING(n)` / `STRING` | `String` | UTF-8, comprimento pode ser especificado |
| Binary | `BINARY(n)` / `BLOB` | `Byte[]` | Comprimento pode ser especificado |
| DateTime | `DATETIME` | `DateTime` | Precisão até Ticks (100 nanossegundos) |
| GeoPoint | `GEOPOINT` | Estrutura Personalizada | Coordenadas latitude/longitude (planejado) |
| Vector | `VECTOR(n)` | `Single[]` | Busca de vetores AI (planejado) |

### Capacidades SQL

Subconjunto SQL padrão implementado, cobrindo aproximadamente 60% dos cenários comerciais comuns:

| Recurso | Status | Descrição |
|---------|--------|-----------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX/DATABASE, ALTER TABLE (ADD/MODIFY/DROP COLUMN, COMMENT), com IF NOT EXISTS, PRIMARY KEY, UNIQUE, ENGINE |
| DML | ✅ | INSERT (várias linhas), UPDATE, DELETE, UPSERT (ON DUPLICATE KEY UPDATE), TRUNCATE TABLE |
| Consulta | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| Agregação | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN (Nested Loop), suporta aliases de tabela |
| Parametrização | ✅ | Placeholders @param |
| Transação | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| Funções SQL | ✅ | Funções de string/numérica/data/conversão/condicional/hash (60+ funções) |
| Subconsulta | ✅ | Subconsultas IN/EXISTS |
| Avançado | ❌ | Sem views/triggers/procedimentos armazenados/funções de janela |

---

## Guia de Uso

### Instalação

Instale o pacote principal do NovaDb via NuGet:

```shell
dotnet add package NewLife.NovaDb
```

### Métodos de Acesso

O NovaDb fornece dois métodos de acesso cliente para diferentes cenários:

| Método de Acesso | Motor Alvo | Descrição |
|-----------------|------------|-----------|
| **ADO.NET + SQL** | Nova (Relacional), Flux (Séries Temporais) | `DbConnection`/`DbCommand`/`DbDataReader` padrão, compatível com todos os ORMs |
| **NovaClient** | MQ (Fila de Mensagens), KV (Armazenamento Chave-Valor) | Cliente RPC que fornece APIs de publicação/consumo/confirmação de mensagens e leitura/escrita KV |

---

### 1. Banco de Dados Relacional (ADO.NET + SQL)

O motor relacional (Nova Engine) é acessado através da interface padrão ADO.NET. Um `Data Source` na string de conexão indica modo embarcado; um `Server` indica modo servidor.

#### 1.1 Modo Embarcado (Início Rápido em 5 Minutos)

O modo embarcado não requer serviço independente, ideal para aplicações desktop, dispositivos IoT e testes unitários.

```csharp
using NewLife.NovaDb.Client;

// Create connection (embedded mode, folder-as-database)
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

// Create table
using var cmd = conn.CreateCommand();
cmd.CommandText = @"CREATE TABLE IF NOT EXISTS users (
    id   INT PRIMARY KEY AUTO_INCREMENT,
    name STRING(50) NOT NULL,
    age  INT DEFAULT 0,
    created DATETIME
)";
cmd.ExecuteNonQuery();

// Insert data
cmd.CommandText = "INSERT INTO users (name, age, created) VALUES ('Alice', 25, NOW())";
cmd.ExecuteNonQuery();

// Batch insert
cmd.CommandText = @"INSERT INTO users (name, age) VALUES
    ('Bob', 30),
    ('Charlie', 28)";
cmd.ExecuteNonQuery();

// Query data
cmd.CommandText = "SELECT * FROM users WHERE age >= 25 ORDER BY age";
using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"id={reader["id"]}, name={reader["name"]}, age={reader["age"]}");
}
```

#### 1.2 Modo Servidor

O modo servidor fornece acesso remoto via TCP, suportando múltiplas conexões de clientes simultâneas.

**Iniciar o servidor:**

```csharp
using NewLife.NovaDb.Server;

var svr = new NovaServer(3306) { DbPath = "./data" };
svr.Start();
Console.ReadLine();
svr.Stop("Manual shutdown");
```

**Conexão do cliente ADO.NET (API idêntica ao modo embarcado):**

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

#### 1.3 Consultas Parametrizadas

Consultas parametrizadas previnem injeção SQL usando parâmetros nomeados `@name`:

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

#### 1.4 Transações

O NovaDb implementa isolamento de transações baseado em MVCC com nível de isolamento padrão Read Committed:

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

#### 1.5 Consultas JOIN

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

#### 1.6 Referência da String de Conexão

| Parâmetro | Exemplo | Descrição |
|-----------|---------|-----------|
| `Data Source` | `Data Source=./mydb` | Modo embarcado, caminho da pasta do banco de dados |
| `Server` | `Server=127.0.0.1` | Modo servidor, endereço do servidor |
| `Port` | `Port=3306` | Porta do servidor (padrão 3306) |
| `Database` | `Database=mydb` | Nome do banco de dados |
| `WalMode` | `WalMode=Full` | Modo WAL (Full/Normal/None) |
| `ReadOnly` | `ReadOnly=true` | Modo somente leitura |

---

### 2. Banco de Dados de Séries Temporais (ADO.NET + SQL)

O motor de séries temporais (Flux Engine) também é acessado via ADO.NET + SQL. Especifique `ENGINE=FLUX` ao criar tabelas.

#### 2.1 Criar uma Tabela de Séries Temporais

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

#### 2.2 Escrever Dados de Séries Temporais

```csharp
// Single insert
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity)
    VALUES (NOW(), 'sensor-001', 23.5, 65.0)";
cmd.ExecuteNonQuery();

// Batch insert
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity) VALUES
    ('2025-07-01 10:00:00', 'sensor-001', 22.1, 60.0),
    ('2025-07-01 10:01:00', 'sensor-001', 22.3, 61.0),
    ('2025-07-01 10:02:00', 'sensor-002', 25.0, 55.0)";
cmd.ExecuteNonQuery();
```

#### 2.3 Consulta por Intervalo de Tempo

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

#### 2.4 Análise de Agregação

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

### 3. Fila de Mensagens (NovaClient)

O NovaDb implementa uma fila de mensagens estilo Redis Stream baseada no motor de séries temporais Flux. A fila de mensagens é acessada via interface RPC `NovaClient`.

#### 3.1 Conectar ao Servidor

```csharp
using NewLife.NovaDb.Client;

using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();
```

#### 3.2 Publicar Mensagens

```csharp
var affected = await client.ExecuteAsync(
    "INSERT INTO order_events (timestamp, orderId, action, amount) " +
    "VALUES (NOW(), 10001, 'created', 299.00)");
Console.WriteLine($"Message published, affected rows: {affected}");
```

#### 3.3 Consumir Mensagens

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

#### 3.5 Recursos Principais do MQ

- **ID de Mensagem**: Marca de tempo + número de sequência (autoincremento no mesmo milissegundo), ordenado globalmente
- **Grupo de Consumidores**: `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **Confiabilidade**: At-Least-Once, entra em Pending após leitura, Ack após sucesso comercial
- **Retenção de Dados**: Suporta TTL (exclui automaticamente fragmentos antigos por tempo/tamanho de arquivo)
- **Mensagens Atrasadas**: Especifique duração de atraso ou tempo de entrega exato
- **Fila de Mensagens Mortas**: Entra automaticamente em DLQ após exceder contagem máxima de tentativas

---

### 4. Armazenamento KV Chave-Valor (NovaClient)

O armazenamento KV é acessado via `NovaClient`. Especifique `ENGINE=KV` ao criar tabelas. Tabelas KV possuem um esquema fixo de `Key + Value + TTL`.

#### 4.1 Criar uma Tabela KV

```csharp
using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();

await client.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS session_cache (
    Key STRING(200) PRIMARY KEY, Value BLOB, TTL DATETIME
) ENGINE=KV DEFAULT_TTL=7200");
```

#### 4.2 Ler/Escrever Dados

```csharp
// Write (UPSERT semantics)
await client.ExecuteAsync(
    "INSERT INTO session_cache (Key, Value, TTL) VALUES ('session:1001', 'user-data', " +
    "DATEADD(NOW(), 30, 'MINUTE')) ON DUPLICATE KEY UPDATE Value = 'user-data'");

// Read
var result = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT Value FROM session_cache WHERE Key = 'session:1001' " +
    "AND (TTL IS NULL OR TTL > NOW())");

// Delete
await client.ExecuteAsync("DELETE FROM session_cache WHERE Key = 'session:1001'");
```

#### 4.3 Incremento Atômico (Contador)

```csharp
await client.ExecuteAsync(
    "INSERT INTO counters (Key, Value) VALUES ('page:views', 1) " +
    "ON DUPLICATE KEY UPDATE Value = Value + 1");
```

#### 4.4 Visão Geral das Capacidades KV

| Operação | Descrição |
|----------|-----------|
| `Get` | Ler valor com verificação preguiçosa de TTL |
| `Set` | Definir valor com TTL opcional |
| `Add` | Adicionar apenas quando a Key não existe (bloqueio distribuído) |
| `Delete` | Excluir chave |
| `Inc` | Incremento/decremento atômico (contador) |
| `TTL` | Invisível automaticamente ao expirar, limpeza periódica em segundo plano |

---

## Segurança de Dados e Modos WAL

O NovaDb fornece três estratégias de persistência WAL:

| Modo | Descrição | Casos de Uso |
|------|-----------|--------------|
| `FULL` | Gravação síncrona em disco, descarga imediata em cada commit | Cenários financeiros/comerciais, máxima segurança de dados |
| `NORMAL` | Descarga assíncrona de 1s (padrão) | Maioria dos cenários comerciais, equilibra desempenho e segurança |
| `NONE` | Totalmente assíncrono, sem descarga proativa | Cenários de dados temporários/cache, máximo throughput |

> Escolher um modo diferente do síncrono (`FULL`) significa aceitar possível perda de dados em cenários de falha/queda de energia.

## Implantação em Cluster

O NovaDb suporta uma arquitetura **um master, vários slaves** com replicação assíncrona de dados via Binlog:

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

- O nó master lida com todas as operações de escrita; os nós slaves fornecem consultas somente leitura
- Replicação assíncrona via Binlog com suporte a retomada a partir do ponto de interrupção
- A camada de aplicação é responsável pela separação de leitura/escrita

## Roteiro

| Versão | Recursos Planejados |
|--------|---------------------|
| **v1.0** (Concluído) | Modo duplo embarcado+servidor, motores Nova/Flux/KV, SQL DDL/DML/SELECT/JOIN, transações/MVCC, WAL/recuperação, separação quente-frio, particionamento, grupos de consumidores MQ, ADO.NET Provider, sincronização master-slave de cluster |
| **v1.1** | Funções SQL de nível P0 (string/numérica/data/conversão/condicional ~30 funções) |
| **v1.2** | Leitura bloqueante MQ, operações KV Add/Inc, funções SQL de nível P1 |
| **v1.3** | Mensagens atrasadas MQ, fila de mensagens mortas |
| **v2.0** | Geocodificação GeoPoint + tipo Vector (busca de vetores AI), observabilidade e ferramentas de gerenciamento |

## Posicionamento

O NovaDb não busca conformidade completa com o padrão SQL92, mas cobre o subconjunto de 80% comumente usado em negócios em troca das seguintes capacidades diferenciadas:

| Diferenciação | Descrição |
|---------------|-----------|
| **Puro .NET Gerenciado** | Sem dependências nativas, implantação via xcopy, sobrecarga de serialização zero no mesmo processo com aplicações .NET |
| **Modo Duplo Embarcado+Servidor** | Embarcado para desenvolvimento/depuração como SQLite, serviço independente para produção como MySQL, mesma API |
| **Pasta como Banco de Dados** | Copiar pasta para concluir migração/backup, dump/restore não necessário |
| **Separação de Índices Quente-Frio** | Tabela de 10M linhas consultando apenas pontos quentes usa < 20 MB de memória, dados frios descarregados automaticamente para MMF |
| **Integração de Quatro Motores** | Componente único cobre cenários comuns de SQLite + TDengine + Redis, reduz o número de componentes operacionais |
| **Integração Nativa NewLife** | Adaptação direta com XCode ORM + ADO.NET, drivers de terceiros não necessários |
