# NewLife.NovaDb

[![English](https://img.shields.io/badge/lang-English-blue.svg)](README.md) [![简体中文](https://img.shields.io/badge/lang-简体中文-red.svg)](README_CN.md) [![繁體中文](https://img.shields.io/badge/lang-繁體中文-orange.svg)](README_TW.md) [![Français](https://img.shields.io/badge/lang-Français-green.svg)](README_FR.md) [![Deutsch](https://img.shields.io/badge/lang-Deutsch-yellow.svg)](README_DE.md) [![日本語](https://img.shields.io/badge/lang-日本語-purple.svg)](README_JA.md) [![한국어](https://img.shields.io/badge/lang-한국어-brightgreen.svg)](README_KO.md) [![Русский](https://img.shields.io/badge/lang-Русский-lightgrey.svg)](README_RU.md) [![Español](https://img.shields.io/badge/lang-Español-yellow.svg)](README_ES.md) [![Português](https://img.shields.io/badge/lang-Português-blue.svg)](README_PT.md)

Um banco de dados híbrido de médio a grande porte implementado em **C#**, executando na **plataforma .NET** (suporta .NET Framework 4.5 ~ .NET 10), suportando modo duplo embarcado/servidor, integrando capacidades relacionais, séries temporais, fila de mensagens e NoSQL (KV).

## Apresentação do produto

`NewLife.NovaDb` (abreviado como `Nova`) é a infraestrutura central do ecossistema NewLife, um motor de dados integrado para aplicações .NET. Ao remover muitos recursos de nicho (como procedimentos armazenados/triggers/funções de janela), alcança maior desempenho de leitura/escrita e menores custos operacionais; o volume de dados é logicamente ilimitado (restrito por disco e estratégias de particionamento), e pode substituir SQLite/MySQL/Redis/TDengine em cenários específicos.

### Recursos principais

- **Modos de implantação duplos**:
  - **Modo embarcado**: Executa como uma biblioteca como SQLite, com dados armazenados em pastas locais, configuração zero
  - **Modo servidor**: Processo independente + protocolo TCP, acesso de rede como MySQL; suporta implantação em cluster e replicação master-slave (um master, vários slaves)
- **Pasta como banco de dados**: Copie a pasta para concluir a migração/backup, nenhum processo dump/restore necessário. Cada tabela tem grupos de arquivos independentes (`.data`/`.idx`/`.wal`).
- **Integração de quatro motores**:
  - **Nova Engine** (relacional geral): Índice SkipList + transações MVCC (Read Committed), suporta CRUD, consultas SQL, JOIN
  - **Flux Engine** (séries temporais + MQ): Particionamento baseado em tempo Append Only, suporta limpeza automática TTL, grupos de consumidores estilo Redis Stream + Pending + Ack
  - **Modo KV** (visão lógica): Reutiliza Nova Engine, API oculta detalhes SQL, cada linha contém `Key + Value + TTL`
  - **ADO.NET Provider**: Reconhece automaticamente o modo embarcado/servidor, integração nativa com XCode ORM
- **Separação dinâmica de índices quente-frio**: Dados quentes totalmente carregados na memória física (nós SkipList), dados frios descarregados para MMF com apenas diretório esparso retido. Tabela de 10 milhões de linhas consultando apenas as últimas 10.000 linhas usa < 20 MB de memória.
- **Código puramente gerenciado**: Sem dependências de componentes nativos (C#/.NET puro), fácil de implantar em plataformas e ambientes restritos.

### Motores de armazenamento

| Motor | Estrutura de dados | Casos de uso |
|-------|-------------------|--------------|
| **Nova Engine** | SkipList (Separação memória+MMF quente-frio) | CRUD geral, tabelas de configuração, pedidos comerciais, dados de usuário |
| **Flux Engine** | Particionamento baseado em tempo (Append Only) | Sensores IoT, coleta de logs, filas de mensagens internas, logs de auditoria |
| **Modo KV** | Visão lógica da tabela Nova | Bloqueios distribuídos, cache, armazenamento de sessão, contadores, centro de configuração |

### Tipos de dados

| Categoria | Tipo SQL | Mapeamento C# | Descrição |
|-----------|----------|---------------|-----------|
| Boolean | `BOOL` | `Boolean` | 1 byte |
| Integer | `INT` / `LONG` | `Int32` / `Int64` | 4/8 bytes |
| Float | `DOUBLE` | `Double` | 8 bytes |
| Decimal | `DECIMAL` | `Decimal` | 128 bits, precisão unificada |
| String | `STRING(n)` / `STRING` | `String` | UTF-8, comprimento pode ser especificado |
| Binary | `BINARY(n)` / `BLOB` | `Byte[]` | Comprimento pode ser especificado |
| DateTime | `DATETIME` | `DateTime` | Precisão até Ticks (100 nanossegundos) |
| GeoPoint | `GEOPOINT` | Estrutura personalizada | Coordenadas latitude/longitude (planejado) |
| Vector | `VECTOR(n)` | `Single[]` | Busca de vetores AI (planejado) |

### Capacidades SQL

Subconjunto SQL padrão implementado, cobrindo aproximadamente 60% dos cenários comerciais comuns:

| Recurso | Status | Descrição |
|---------|--------|-----------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX, com IF NOT EXISTS, PRIMARY KEY, UNIQUE |
| DML | ✅ | INSERT (várias linhas), UPDATE, DELETE, UPSERT (ON DUPLICATE KEY UPDATE), TRUNCATE TABLE |
| Consulta | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| Agregação | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN (Nested Loop), suporta aliases de tabela |
| Parametrização | ✅ | Espaços reservados @param |
| Transação | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| Funções SQL | ⚠️ | Funções de string/numérica/data/conversão/condicional (planejado) |
| Subconsulta | ⚠️ | IN/EXISTS (planejado) |
| Avançado | ❌ | Sem views/triggers/procedimentos armazenados/funções de janela |

### Capacidades MQ (Flux Engine)

Baseado no modelo de grupo de consumidores do Redis Stream:

- **ID de mensagem**: Marca de tempo + número de sequência (autoincremento no mesmo milissegundo), ordenado globalmente
- **Grupo de consumidores**: `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **Confiabilidade**: At-Least-Once, entra em Pending após leitura, Ack após sucesso comercial
- **Retenção de dados**: Suporta TTL (exclui automaticamente fragmentos antigos por tempo/tamanho de arquivo)
- **Mensagens atrasadas**: Especifique `DelayTime`/`DeliverAt` (planejado)
- **Fila de mensagens não entregues**: Entra automaticamente em DLQ em caso de falha no consumo (planejado)
- **Leitura bloqueante**: Sondagem longa + tempo limite (planejado)

### Capacidades KV

- `Get(key)` / `Set(key, value, ttl)` / `Delete(key)` / `Exists(key)`
- Exclusão preguiçosa (verificar expiração na leitura) + limpeza em segundo plano (`CleanupExpired()`)
- `Add(key, value, ttl)`: Adicionar apenas quando a chave não existe (planejado)
- `Inc(key, delta, ttl)`: Incremento atômico (planejado)

## Segurança de dados e modos WAL

NovaDb fornece três estratégias de persistência WAL:

| Modo | Descrição | Casos de uso |
|------|-----------|--------------|
| `FULL` | Gravação síncrona em disco, descarga imediata em cada commit | Cenários financeiros/comerciais, máxima segurança de dados |
| `NORMAL` | Descarga assíncrona de 1s (padrão) | Maioria dos cenários comerciais, equilibra desempenho e segurança |
| `NONE` | Totalmente assíncrono, sem descarga proativa | Cenários de dados temporários/cache, máximo throughput |

> Escolher um modo diferente do síncrono (`FULL`) significa aceitar possível perda de dados em cenários de falha/queda de energia.

## Roteiro

| Versão | Recursos planejados |
|--------|---------------------|
| **v1.0** (Concluído) | Modo duplo embarcado+servidor, motores Nova/Flux/KV, SQL DDL/DML/SELECT/JOIN, transações/MVCC, WAL/recuperação, separação quente-frio, particionamento, grupos de consumidores MQ, ADO.NET Provider, sincronização master-slave de cluster |
| **v1.1** | Funções SQL de nível P0 (string/numérica/data/conversão/condicional ~30 funções) |
| **v1.2** | Leitura bloqueante MQ, operações KV Add/Inc, funções SQL de nível P1 |
| **v1.3** | Mensagens atrasadas MQ, fila de mensagens não entregues |
| **v2.0** | Geocodificação GeoPoint + tipo Vector (busca de vetores AI), observabilidade e ferramentas de gerenciamento |

## Posicionamento

NovaDb não busca conformidade completa com o padrão SQL92, mas cobre o subconjunto de 80% comumente usado em negócios em troca das seguintes capacidades diferenciadas:

| Diferenciação | Descrição |
|---------------|-----------|
| **Puro .NET gerenciado** | Sem dependências nativas, implantação via xcopy, sobrecarga de serialização zero no mesmo processo com aplicações .NET |
| **Modo duplo embarcado+servidor** | Embarcado para desenvolvimento/depuração como SQLite, serviço independente para produção como MySQL, mesma API |
| **Pasta como banco de dados** | Copiar pasta para concluir migração/backup, dump/restore não necessário |
| **Separação de índices quente-frio** | Tabela de 10M linhas consultando apenas pontos quentes usa < 20 MB de memória, dados frios descarregados automaticamente para MMF |
| **Integração de quatro motores** | Componente único cobre cenários comuns de SQLite + TDengine + Redis, reduz o número de componentes operacionais |
| **Integração nativa NewLife** | Adaptação direta com XCode ORM + ADO.NET, drivers de terceiros não necessários |
