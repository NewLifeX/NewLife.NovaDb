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
🌐 [English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Русский](README.ru.md) | [한국어](README.ko.md) | [العربية](README.ar.md)
<!-- lang:end -->

> ⚠️ Esta tradução pode estar desatualizada; a referência é o [README em inglês](README.md).

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
  - **Flux Engine** (Séries Temporais + MQ): Particionamento baseado em tempo Append Only, suporta limpeza automática TTL, grupos de consumidores estilo Redis Stream + Pending + Ack; selagem colunar em blocos com codificação sensível ao tipo (Delta/DeltaDelta/XOR/RLE/dicionário) mais compressão de bloco LZ4, pré-agregação rollup com roteamento automático de consultas agregadas, agrupamento em janelas SESSION/STATE e relatório da taxa de compressão via `_sys.flux_compression`
  - **Modo KV** (armazenamento chave-valor): Motor Bitcask independente (índice em memória + valores em disco), suporta TTL, isolamento multi-tabela
  - **ADO.NET Provider**: Reconhece automaticamente o modo embarcado/servidor, integração nativa com XCode ORM
- **Separação Dinâmica de Índices Quente-Frio**: Dados quentes totalmente carregados na memória física (nós SkipList), dados frios descarregados para MMF com apenas diretório esparso retido. Tabela de 10 milhões de linhas consultando apenas as últimas 10.000 linhas usa < 20 MB de memória.
- **Código Puramente Gerenciado**: Sem dependências de componentes nativos (C#/.NET puro), fácil de implantar em plataformas e ambientes restritos.

### Motores de Armazenamento

| Motor | Estrutura de Dados | Casos de Uso |
|-------|-------------------|--------------|
| **Nova Engine** | SkipList (Separação Memória+MMF Quente-Frio) | CRUD geral, tabelas de configuração, pedidos comerciais, dados de usuário |
| **Flux Engine** | Particionamento baseado em tempo (Append Only) | Sensores IoT, coleta de logs, filas de mensagens internas, logs de auditoria |
| **Modo KV** | Bitcask (índice em memória + valores em disco) | Bloqueios distribuídos, cache, armazenamento de sessão, contadores, centro de configuração |

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
| GeoPoint | `GEOPOINT` | Estrutura Personalizada | Coordenadas latitude/longitude, com consultas por distância/polígono |
| Vector | `VECTOR(n)` | `Single[]` | Busca de vetores AI, com consultas KNN de vizinhos mais próximos |

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
| Pesquisa de texto completo | ✅ | Índice de texto completo com pontuação BM25, resultados Top-K para recuperação híbrida RAG |
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

O modo servidor fornece acesso remoto via TCP, suportando múltiplas conexões de clientes simultâneas. Cada banco de dados na string de conexão (`Database=mydb`) é mapeado para um diretório de motor independente sob a pasta de dados do servidor, permitindo o roteamento de múltiplos bancos em um único servidor.

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

#### 1.4 Agregação e Consultas Escalares

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT COUNT(*) FROM users";
var count = Convert.ToInt32(cmd.ExecuteScalar());
Console.WriteLine($"Total users: {count}");

cmd.CommandText = "SELECT AVG(age) FROM users WHERE age > 0";
var avgAge = Convert.ToDouble(cmd.ExecuteScalar());
Console.WriteLine($"Average age: {avgAge:F1}");
```

#### 1.5 Transações

O NovaDb implementa isolamento de transações baseado em MVCC com nível de isolamento padrão Read Committed:

```csharp
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

using var tx = conn.BeginTransaction();
try
{
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;

    // Deduzir estoque
    cmd.CommandText = "UPDATE products SET stock = stock - 1 WHERE id = 1 AND stock > 0";
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) throw new InvalidOperationException("Insufficient stock");

    // Criar pedido
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

#### 1.6 Consultas JOIN

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

#### 1.7 Operações DDL

```sql
-- Criar/excluir banco de dados
CREATE DATABASE shop;
DROP DATABASE shop;

-- Alterar a estrutura da tabela
ALTER TABLE products ADD COLUMN category STRING;
ALTER TABLE products MODIFY COLUMN name STRING(200);
ALTER TABLE products DROP COLUMN category;

-- Gerenciamento de índices
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

#### 1.9 Referência da String de Conexão

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
// Temperatura média por dispositivo
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

#### 2.5 Política de Retenção de Dados

Tabelas de séries temporais suportam limpeza automática de fragmentos expirados baseada em TTL, retendo dados por tempo ou capacidade:

```csharp
// Configurar parâmetros de séries temporais via DbOptions
var options = new DbOptions
{
    FluxPartitionHours = 1,        // Uma partição por hora
    FluxDefaultTtlSeconds = 86400, // Reter dados por 24 horas
};
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
// Executar SQL via RPC para inserir uma mensagem na tabela Flux
var affected = await client.ExecuteAsync(
    "INSERT INTO order_events (timestamp, orderId, action, amount) " +
    "VALUES (NOW(), 10001, 'created', 299.00)");
Console.WriteLine($"Message published, affected rows: {affected}");
```

#### 3.3 Consumir Mensagens

```csharp
// Ler mensagens (por intervalo de tempo)
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

// Criar uma tabela KV (TTL padrão 7200 segundos = 2 horas)
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

#### 4.4 Bloqueio Distribuído

```csharp
// Tentar adquirir o bloqueio (a inserção só é bem-sucedida quando a chave não existe)
var locked = await client.ExecuteAsync(
    "INSERT INTO dist_lock (Key, Value, TTL) VALUES ('lock:order:123', 'worker-1', " +
    "DATEADD(NOW(), 30, 'SECOND'))");

if (locked > 0)
{
    try
    {
        // Bloqueio adquirido, executar a lógica de negócio
    }
    finally
    {
        await client.ExecuteAsync("DELETE FROM dist_lock WHERE Key = 'lock:order:123'");
    }
}
```

#### 4.5 Visão Geral das Capacidades KV

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
| **v1.1** (Concluído) | Funções SQL de nível P0 (string/numérica/data/conversão/condicional ~30 funções) |
| **v1.2** (Concluído) | Leitura bloqueante MQ, operações KV Add/Inc, funções SQL de nível P1 |
| **v1.3** (Concluído) | Mensagens atrasadas MQ, fila de mensagens mortas |
| **v2.0** (Concluído) | Geocodificação GeoPoint + tipo Vector (busca de vetores AI), extensão GIS, pesquisa de texto completo (BM25), NovaVectorStore (IVectorStore, multi-coleção), visualizações materializadas incrementais, gerenciamento de Binlog, observabilidade e ferramentas de gerenciamento |
| **v2.1** (Concluído) | Selagem colunar e compressão de blocos em séries temporais (codificação numérica + LZ4), pré-agregação rollup com backfill e roteamento automático de consultas, agrupamento SESSION/STATE_WINDOW, relatório da taxa de compressão, suporte a NativeAOT com CI multi-RID (win-x64/linux-x64/linux-arm64/loongarch64) |

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

## Mapa do ecossistema

NewLife é um ecossistema em camadas de projetos open source versionados de forma independente. O mapa abaixo é compartilhado literalmente por todos os repositórios NewLife; cada projeto tem links para o seu repositório e para o seu pacote principal no NuGet.

| Projeto | O que faz | Downloads |
| --- | --- | --- |
| **Base** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | Log, configuração, cache, rede, serialização, agendamento e rastreamento APM. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | Middleware de dados: tabelas únicas na escala de dezenas de bilhões de linhas, particionamento automático, separação de leitura e escrita, mais de 10 bancos de dados. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | Biblioteca de rede de alto desempenho: 1,77M pps / 14,5 Gbps em uma máquina (TCP 1KB, 2026-10), 4M de conexões TCP persistentes. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | Framework de comunicação por protocolo e RPC para aplicações cliente/servidor e dispositivos IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | Instala aplicações .NET como daemons do sistema operacional: serviços do Windows e systemd do Linux. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | Modelos de projeto `dotnet new` para console, web, API, serviço, dados e IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **Dados e mensagens** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | Cliente Redis com latência em microssegundos, milhões de operações por segundo e filas de mensagens integradas. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | Cliente RocketMQ totalmente gerenciado para Apache RocketMQ e Alibaba Cloud Message Queue. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | Cliente e servidor MQTT, incluindo conectividade com o Alibaba Cloud IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | Banco de dados híbrido em C#: embarcado ou em rede, relacional, séries temporais, fila de mensagens e KV em um só. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | Driver MySql e camada auxiliar construídos sobre a pilha de dados NewLife. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **Web, IA e distribuído** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | Framework de plataforma administrativa rápida: permissões, SSO, servidor OAuth e scaffolding CRUD. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | Plataforma de serviços distribuídos: gestão de nós, APM, centro de configuração, registro e centro de publicação. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | Plataforma distribuída de tarefas e agendamento de dados em tempo real, com particionamento, repetição e reprocessamento. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | Cliente unificado para 46 provedores de LLM, além do ChatAI, uma aplicação web de chat pronta para uso. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **Industrial e IoT** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | Biblioteca padrão de IoT que define contratos de protocolo e abstrações de dispositivos. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | Implementação Modbus TCP/RTU/ASCII sobre NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | Protocolo Siemens PLC sobre NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | Protocolo Omron PLC sobre NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | Protocolo Mitsubishi Melsec PLC sobre NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | Cliente OPC para acesso a dados industriais. | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | Pilha do protocolo de automação predial BACnet. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | Plataforma IoT de código aberto leve, com gestão de gateways e dispositivos. | — |
| **Telemática veicular** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | Protocolo de posicionamento veicular JT/T 808: mais de 70 corpos de mensagem, 2011/2013/2019. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | Protocolo plataforma-a-plataforma JT/T 809 para autoridades de transporte. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | Vídeo veicular JT/T 1078: análise RTP, remux para FLV/fMP4 e transmissão ao vivo. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **Componentes de domínio** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | Um pacote para Excel, Word, PPT, PDF e mais de 15 formatos de documento. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | Áudio multiplataforma: codecs, E/S de dispositivos, DSP, contêineres e streaming. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | Codecs de vídeo, formatos de contêiner, processamento de frames e streaming. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | Interface unificada sobre Baidu, AMap, Tencent e Tianditu, com geocodificação e conversão de coordenadas. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | Geolocalização IPv4 com banco de dados offline mapeado em memória e API online como alternativa. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **Ferramentas e produtos** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | Caixa de ferramentas do desenvolvedor: rede, porta serial, criptografia, regex, Modbus e MQTT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | Sistema auto-hospedado de gerenciamento de código-fonte Git. | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | Instala automaticamente o runtime .NET correspondente quando iniciado em um host mais antigo. | — |

> Os badges mostram o pacote principal de cada projeto; pacotes de tema, extensão e satélites estão listados nas respectivas páginas do NuGet. Os mais de 30 pacotes não listados aqui (FTP, LoRa, extensões do Office, drivers de protocolo e outros) estão disponíveis na [organização NewLifeX](https://github.com/NewLifeX) e em [newlifex.com](https://newlifex.com).


## Equipe

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

Fundada em 2002, a equipe NewLife dedica-se há mais de duas décadas ao ecossistema .NET e de IoT, mantém cerca de 100 projetos de código aberto (34 deles principais) e oferece soluções integradas de software e hardware para os setores de energia, logística, saúde, controle industrial e internet. Seguimos o lema "não há hierarquia no aprendizado — o domínio conquista o respeito".

Os componentes de código aberto já foram baixados <!--nuget-total:start-->6.48M<!--nuget-total:end--> vezes no NuGet (todo o ecossistema, até 2026-10).

Website: https://newlifex.com · Organização de código aberto: https://github.com/NewLifeX

<details>
<summary>Conta oficial no WeChat: 智能大石头 (escaneie para seguir)</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>

