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
🌐 [English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [한국어](README.ko.md) | [العربية](README.ar.md)
<!-- lang:end -->

> ⚠️ Esta traducción puede estar desactualizada; la versión de referencia es el [README en inglés](README.md).

Una base de datos híbrida de tamaño mediano a grande implementada en **C#**, ejecutándose en la **plataforma .NET** (compatible con .NET Framework 4.5 ~ .NET 10), compatible con modo dual integrado/servidor, integrando capacidades relacionales, series temporales, colas de mensajes y NoSQL (KV).

## Presentación del producto

`NewLife.NovaDb` (abreviado como `Nova`) es la infraestructura central del ecosistema NewLife, un motor de datos integrado para aplicaciones .NET. Al eliminar muchas características de nicho (como procedimientos almacenados/triggers/funciones de ventana), logra un mayor rendimiento de lectura/escritura y menores costos operativos; el volumen de datos es lógicamente ilimitado (limitado por disco y estrategias de particionamiento), y puede reemplazar SQLite/MySQL/Redis/TDengine en escenarios específicos.

### Características principales

- **Modos de implementación dual**:
  - **Modo integrado**: Se ejecuta como una biblioteca como SQLite, con datos almacenados en carpetas locales, configuración cero
  - **Modo servidor**: Proceso independiente + protocolo TCP, acceso de red como MySQL; admite implementación en clúster y replicación maestro-esclavo (un maestro, múltiples esclavos)
- **Carpeta como base de datos**: Copie la carpeta para completar la migración/copia de seguridad, no se necesita proceso dump/restore. Cada tabla tiene grupos de archivos independientes (`.data`/`.idx`/`.wal`).
- **Integración de cuatro motores**:
  - **Nova Engine** (relacional general): Índice SkipList + transacciones MVCC (Read Committed), admite CRUD, consultas SQL, JOIN
  - **Flux Engine** (series temporales + MQ): Particionamiento basado en tiempo Append Only, admite limpieza automática TTL, grupos de consumidores estilo Redis Stream + Pending + Ack; sellado de bloques columnares con codificación según el tipo (Delta/DeltaDelta/XOR/RLE/diccionario) y compresión de bloques LZ4, pre-agregación rollup con enrutamiento automático de consultas agregadas, agrupación por ventanas SESSION/STATE y reporte del ratio de compresión mediante `_sys.flux_compression`
  - **Modo KV** (almacén de clave-valor): Motor Bitcask independiente (índice en memoria + valores en disco), admite TTL, aislamiento multi-tabla
  - **ADO.NET Provider**: Reconoce automáticamente el modo integrado/servidor, integración nativa con XCode ORM
- **Separación dinámica de índices calientes-fríos**: Los datos calientes se cargan completamente en la memoria física (nodos SkipList), los datos fríos se descargan a MMF con solo directorio disperso retenido. Tabla de 10 millones de filas consultando solo las últimas 10,000 filas usa < 20 MB de memoria.
- **Código puramente gestionado**: Sin dependencias de componentes nativos (puro C#/.NET), fácil de implementar en plataformas y entornos restringidos.

### Motores de almacenamiento

| Motor | Estructura de datos | Casos de uso |
|-------|---------------------|--------------|
| **Nova Engine** | SkipList (Separación memoria+MMF caliente-frío) | CRUD general, tablas de configuración, pedidos comerciales, datos de usuario |
| **Flux Engine** | Particionamiento basado en tiempo (Append Only) | Sensores IoT, recopilación de registros, colas de mensajes internas, registros de auditoría |
| **Modo KV** | Bitcask (índice en memoria + valores en disco) | Bloqueos distribuidos, almacenamiento en caché, almacenamiento de sesiones, contadores, centro de configuración |

### Tipos de datos

| Categoría | Tipo SQL | Mapeo C# | Descripción |
|-----------|----------|----------|-------------|
| Boolean | `BOOL` | `Boolean` | 1 byte |
| Integer | `INT` / `LONG` | `Int32` / `Int64` | 4/8 bytes |
| Float | `DOUBLE` | `Double` | 8 bytes |
| Decimal | `DECIMAL` | `Decimal` | 128 bits, precisión unificada |
| String | `STRING(n)` / `STRING` | `String` | UTF-8, la longitud se puede especificar |
| Binary | `BINARY(n)` / `BLOB` | `Byte[]` | La longitud se puede especificar |
| DateTime | `DATETIME` | `DateTime` | Precisión hasta Ticks (100 nanosegundos) |
| GeoPoint | `GEOPOINT` | Estructura personalizada | Coordenadas latitud/longitud, con consultas de distancia/polígono |
| Vector | `VECTOR(n)` | `Single[]` | Búsqueda de vectores AI, con consultas KNN de vecino más cercano |

### Capacidades SQL

Subconjunto SQL estándar implementado, cubriendo aproximadamente el 60% de los escenarios comerciales comunes:

| Característica | Estado | Descripción |
|----------------|--------|-------------|
| DDL | ✅ | CREATE/DROP TABLE/INDEX/DATABASE, ALTER TABLE (ADD/MODIFY/DROP COLUMN, COMMENT), con IF NOT EXISTS, PRIMARY KEY, UNIQUE, ENGINE |
| DML | ✅ | INSERT (múltiples filas), UPDATE, DELETE, UPSERT (ON DUPLICATE KEY UPDATE), TRUNCATE TABLE |
| Consulta | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| Agregación | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN (Nested Loop), admite alias de tabla |
| Parametrización | ✅ | Marcadores de posición @param |
| Transacción | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| Funciones SQL | ✅ | Funciones de cadena/numérica/fecha/conversión/condicional/hash (más de 60 funciones) |
| Subconsulta | ✅ | Subconsultas IN/EXISTS |
| Búsqueda de texto completo | ✅ | Índice de texto completo con puntuación BM25, resultados Top-K para recuperación híbrida RAG |
| Avanzado | ❌ | Sin vistas/triggers/procedimientos almacenados/funciones de ventana |

---

## Guía de uso

### Instalación

Instale el paquete principal de NovaDb a través de NuGet:

```shell
dotnet add package NewLife.NovaDb
```

### Métodos de acceso

NovaDb proporciona dos métodos de acceso de cliente para diferentes escenarios:

| Método de acceso | Motor objetivo | Descripción |
|-----------------|----------------|-------------|
| **ADO.NET + SQL** | Nova (relacional), Flux (series temporales) | `DbConnection`/`DbCommand`/`DbDataReader` estándar, compatible con todos los ORMs |
| **NovaClient** | MQ (cola de mensajes), KV (almacén clave-valor) | Cliente RPC que proporciona APIs de publicación/consumo/confirmación de mensajes y lectura/escritura KV |

---

### 1. Base de datos relacional (ADO.NET + SQL)

El motor relacional (Nova Engine) se accede a través de la interfaz estándar ADO.NET. Un `Data Source` en la cadena de conexión indica modo integrado; un `Server` indica modo servidor.

#### 1.1 Modo integrado (Inicio rápido en 5 minutos)

El modo integrado no requiere un servicio independiente, ideal para aplicaciones de escritorio, dispositivos IoT y pruebas unitarias.

```csharp
using NewLife.NovaDb.Client;

// Crear conexión (modo integrado, carpeta como base de datos)
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

// Crear tabla
using var cmd = conn.CreateCommand();
cmd.CommandText = @"CREATE TABLE IF NOT EXISTS users (
    id   INT PRIMARY KEY AUTO_INCREMENT,
    name STRING(50) NOT NULL,
    age  INT DEFAULT 0,
    created DATETIME
)";
cmd.ExecuteNonQuery();

// Insertar datos
cmd.CommandText = "INSERT INTO users (name, age, created) VALUES ('Alice', 25, NOW())";
cmd.ExecuteNonQuery();

// Inserción por lotes
cmd.CommandText = @"INSERT INTO users (name, age) VALUES
    ('Bob', 30),
    ('Charlie', 28)";
cmd.ExecuteNonQuery();

// Consultar datos
cmd.CommandText = "SELECT * FROM users WHERE age >= 25 ORDER BY age";
using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"id={reader["id"]}, name={reader["name"]}, age={reader["age"]}");
}
```

#### 1.2 Modo servidor

El modo servidor proporciona acceso remoto a través de TCP, admitiendo múltiples conexiones de clientes concurrentes. Cada base de datos de la cadena de conexión (`Database=mydb`) se asigna a un directorio de motor independiente dentro de la carpeta de datos del servidor, lo que permite el enrutamiento de múltiples bases de datos en un solo servidor.

**Iniciar el servidor:**

```csharp
using NewLife.NovaDb.Server;

var svr = new NovaServer(3306) { DbPath = "./data" };
svr.Start();
Console.ReadLine();
svr.Stop("Manual shutdown");
```

**Conexión del cliente ADO.NET (API idéntica al modo integrado):**

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

#### 1.3 Consultas parametrizadas

Las consultas parametrizadas previenen la inyección SQL usando parámetros con nombre `@name`:

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

#### 1.4 Agregación y consultas escalares

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT COUNT(*) FROM users";
var count = Convert.ToInt32(cmd.ExecuteScalar());
Console.WriteLine($"Total users: {count}");

cmd.CommandText = "SELECT AVG(age) FROM users WHERE age > 0";
var avgAge = Convert.ToDouble(cmd.ExecuteScalar());
Console.WriteLine($"Average age: {avgAge:F1}");
```

#### 1.5 Transacciones

NovaDb implementa el aislamiento de transacciones basado en MVCC con un nivel de aislamiento predeterminado de Read Committed:

```csharp
using var conn = new NovaConnection { ConnectionString = "Data Source=./mydb" };
conn.Open();

using var tx = conn.BeginTransaction();
try
{
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;

    // Descontar stock
    cmd.CommandText = "UPDATE products SET stock = stock - 1 WHERE id = 1 AND stock > 0";
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) throw new InvalidOperationException("Insufficient stock");

    // Crear pedido
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

#### 1.7 Operaciones DDL

```sql
-- Crear/eliminar base de datos
CREATE DATABASE shop;
DROP DATABASE shop;

-- Modificar la estructura de la tabla
ALTER TABLE products ADD COLUMN category STRING;
ALTER TABLE products MODIFY COLUMN name STRING(200);
ALTER TABLE products DROP COLUMN category;

-- Gestión de índices
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

#### 1.9 Referencia de cadena de conexión

| Parámetro | Ejemplo | Descripción |
|-----------|---------|-------------|
| `Data Source` | `Data Source=./mydb` | Modo integrado, ruta de la carpeta de base de datos |
| `Server` | `Server=127.0.0.1` | Modo servidor, dirección del servidor |
| `Port` | `Port=3306` | Puerto del servidor (predeterminado 3306) |
| `Database` | `Database=mydb` | Nombre de la base de datos |
| `WalMode` | `WalMode=Full` | Modo WAL (Full/Normal/None) |
| `ReadOnly` | `ReadOnly=true` | Modo de solo lectura |

---

### 2. Base de datos de series temporales (ADO.NET + SQL)

El motor de series temporales (Flux Engine) también se accede a través de ADO.NET + SQL. Especifique `ENGINE=FLUX` al crear tablas.

#### 2.1 Crear una tabla de series temporales

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

#### 2.2 Escribir datos de series temporales

```csharp
// Inserción individual
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity)
    VALUES (NOW(), 'sensor-001', 23.5, 65.0)";
cmd.ExecuteNonQuery();

// Inserción por lotes
cmd.CommandText = @"INSERT INTO metrics (timestamp, device_id, temperature, humidity) VALUES
    ('2025-07-01 10:00:00', 'sensor-001', 22.1, 60.0),
    ('2025-07-01 10:01:00', 'sensor-001', 22.3, 61.0),
    ('2025-07-01 10:02:00', 'sensor-002', 25.0, 55.0)";
cmd.ExecuteNonQuery();
```

#### 2.3 Consulta por rango de tiempo

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

#### 2.4 Análisis de agregación

```csharp
// Temperatura promedio por dispositivo
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

#### 2.5 Política de retención de datos

Las tablas de series temporales admiten la limpieza automática de fragmentos expirados basada en TTL, reteniendo los datos por tiempo o por capacidad:

```csharp
// Configurar los parámetros de series temporales mediante DbOptions
var options = new DbOptions
{
    FluxPartitionHours = 1,        // Una partición por hora
    FluxDefaultTtlSeconds = 86400, // Retener los datos durante 24 horas
};
```

---

### 3. Cola de mensajes (NovaClient)

NovaDb implementa una cola de mensajes estilo Redis Stream basada en el motor de series temporales Flux. La cola de mensajes se accede a través de la interfaz RPC `NovaClient`.

#### 3.1 Conectar al servidor

```csharp
using NewLife.NovaDb.Client;

using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();
```

#### 3.2 Publicar mensajes

```csharp
// Ejecutar SQL por RPC para insertar un mensaje en la tabla Flux
var affected = await client.ExecuteAsync(
    "INSERT INTO order_events (timestamp, orderId, action, amount) " +
    "VALUES (NOW(), 10001, 'created', 299.00)");
Console.WriteLine($"Message published, affected rows: {affected}");
```

#### 3.3 Consumir mensajes

```csharp
// Leer mensajes (por rango de tiempo)
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

#### 3.5 Características principales de MQ

- **ID de mensaje**: Marca de tiempo + número de secuencia (autoincremento en el mismo milisegundo), ordenado globalmente
- **Grupo de consumidores**: `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **Confiabilidad**: At-Least-Once, entra en Pending después de leer, Ack después del éxito comercial
- **Retención de datos**: Admite TTL (elimina automáticamente fragmentos antiguos por tiempo/tamaño de archivo)
- **Mensajes retrasados**: Especifique la duración del retraso o la hora de entrega exacta
- **Cola de mensajes no entregados**: Entra automáticamente en DLQ después de superar el número máximo de reintentos

---

### 4. Almacén KV clave-valor (NovaClient)

El almacenamiento KV se accede a través de `NovaClient`. Especifique `ENGINE=KV` al crear tablas. Las tablas KV tienen un esquema fijo de `Key + Value + TTL`.

#### 4.1 Crear una tabla KV

```csharp
using var client = new NovaClient("tcp://127.0.0.1:3306");
client.Open();

// Crear una tabla KV (TTL predeterminado 7200 segundos = 2 horas)
await client.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS session_cache (
    Key STRING(200) PRIMARY KEY, Value BLOB, TTL DATETIME
) ENGINE=KV DEFAULT_TTL=7200");
```

#### 4.2 Leer/escribir datos

```csharp
// Escribir (semántica UPSERT)
await client.ExecuteAsync(
    "INSERT INTO session_cache (Key, Value, TTL) VALUES ('session:1001', 'user-data', " +
    "DATEADD(NOW(), 30, 'MINUTE')) ON DUPLICATE KEY UPDATE Value = 'user-data'");

// Leer
var result = await client.QueryAsync<IDictionary<String, Object>[]>(
    "SELECT Value FROM session_cache WHERE Key = 'session:1001' " +
    "AND (TTL IS NULL OR TTL > NOW())");

// Eliminar
await client.ExecuteAsync("DELETE FROM session_cache WHERE Key = 'session:1001'");
```

#### 4.3 Incremento atómico (contador)

```csharp
await client.ExecuteAsync(
    "INSERT INTO counters (Key, Value) VALUES ('page:views', 1) " +
    "ON DUPLICATE KEY UPDATE Value = Value + 1");
```

#### 4.4 Bloqueo distribuido

```csharp
// Intentar adquirir el bloqueo (la inserción solo tiene éxito cuando la clave no existe)
var locked = await client.ExecuteAsync(
    "INSERT INTO dist_lock (Key, Value, TTL) VALUES ('lock:order:123', 'worker-1', " +
    "DATEADD(NOW(), 30, 'SECOND'))");

if (locked > 0)
{
    try
    {
        // Bloqueo adquirido, ejecutar la lógica de negocio
    }
    finally
    {
        await client.ExecuteAsync("DELETE FROM dist_lock WHERE Key = 'lock:order:123'");
    }
}
```

#### 4.5 Resumen de capacidades KV

| Operación | Descripción |
|-----------|-------------|
| `Get` | Leer valor con verificación perezosa de TTL |
| `Set` | Establecer valor con TTL opcional |
| `Add` | Agregar solo cuando la clave no existe (bloqueo distribuido) |
| `Delete` | Eliminar clave |
| `Inc` | Incremento/decremento atómico (contador) |
| `TTL` | Auto-invisible al expirar, limpieza periódica en segundo plano |

---

## Seguridad de datos y modos WAL

NovaDb proporciona tres estrategias de persistencia WAL:

| Modo | Descripción | Casos de uso |
|------|-------------|--------------|
| `FULL` | Escritura síncrona en disco, descarga inmediata en cada commit | Escenarios financieros/comerciales, máxima seguridad de datos |
| `NORMAL` | Descarga asíncrona de 1s (predeterminado) | La mayoría de los escenarios comerciales, equilibra rendimiento y seguridad |
| `NONE` | Totalmente asíncrono, sin descarga proactiva | Escenarios de datos temporales/caché, máximo rendimiento |

> Elegir un modo distinto al síncrono (`FULL`) significa aceptar una posible pérdida de datos en escenarios de fallo/corte de energía.

## Implementación en clúster

NovaDb admite una arquitectura de **un maestro, múltiples esclavos** con replicación asíncrona de datos mediante Binlog:

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

- El nodo maestro gestiona todas las operaciones de escritura; los nodos esclavos proporcionan consultas de solo lectura
- Replicación asíncrona mediante Binlog con soporte de reanudación desde punto de interrupción
- La capa de aplicación es responsable de la separación lectura/escritura

## Hoja de ruta

| Versión | Características planificadas |
|---------|------------------------------|
| **v1.0** (Completado) | Modo dual integrado+servidor, motores Nova/Flux/KV, SQL DDL/DML/SELECT/JOIN, transacciones/MVCC, WAL/recuperación, separación caliente-frío, particionamiento, grupos de consumidores MQ, ADO.NET Provider, sincronización maestro-esclavo de clúster |
| **v1.1** (Completado) | Funciones SQL de nivel P0 (cadena/numérica/fecha/conversión/condicional ~30 funciones) |
| **v1.2** (Completado) | Lectura bloqueante MQ, operaciones KV Add/Inc, funciones SQL de nivel P1 |
| **v1.3** (Completado) | Mensajes retrasados MQ, cola de mensajes no entregados |
| **v2.0** (Completado) | Geocodificación GeoPoint + tipo Vector (búsqueda de vectores AI), extensión GIS, búsqueda de texto completo (BM25), NovaVectorStore (IVectorStore, multi-colección), vistas materializadas incrementales, gestión de Binlog, observabilidad y herramientas de gestión |
| **v2.1** (Completado) | Sellado columnar de series temporales + compresión de bloques (codificación numérica + LZ4), pre-agregación rollup con relleno histórico y enrutamiento automático de consultas, agrupación SESSION/STATE_WINDOW, reporte del ratio de compresión, soporte NativeAOT con CI multi-RID (win-x64/linux-x64/linux-arm64/loongarch64) |

## Posicionamiento

NovaDb no busca la conformidad completa con el estándar SQL92, sino que cubre el subconjunto del 80% comúnmente utilizado en negocios a cambio de las siguientes capacidades diferenciadas:

| Diferenciación | Descripción |
|----------------|-------------|
| **Puro .NET gestionado** | Sin dependencias nativas, implementación a través de xcopy, sobrecarga de serialización cero en el mismo proceso con aplicaciones .NET |
| **Modo dual integrado+servidor** | Integrado para desarrollo/depuración como SQLite, servicio independiente para producción como MySQL, misma API |
| **Carpeta como base de datos** | Copiar carpeta para completar migración/copia de seguridad, no se necesita dump/restore |
| **Separación de índices caliente-frío** | Tabla de 10M filas consultando solo puntos calientes usa < 20 MB de memoria, datos fríos descargados automáticamente a MMF |
| **Integración de cuatro motores** | Un solo componente cubre escenarios comunes de SQLite + TDengine + Redis, reduce el número de componentes operativos |
| **Integración nativa NewLife** | Adaptación directa con XCode ORM + ADO.NET, no se necesitan controladores de terceros |

## Mapa del ecosistema

NewLife es un ecosistema por capas de proyectos de código abierto con versionado independiente. El mapa siguiente se comparte literalmente en todos los repositorios de NewLife; cada proyecto enlaza a su repositorio y a su paquete principal de NuGet.

| Proyecto | Qué hace | Descargas |
| --- | --- | --- |
| **Fundación** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | Registro de logs, configuración, caché, red, serialización, programación de tareas y seguimiento APM. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | Middleware de datos: tablas únicas a escala de decenas de miles de millones, particionado automático, separación lectura/escritura y más de 10 bases de datos. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | Biblioteca de red de alto rendimiento: 1,77 M pps / 14,5 Gbps en una máquina (TCP 1KB, 2026-10), 4 M conexiones TCP persistentes. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | Framework de comunicación por protocolo y RPC para aplicaciones cliente/servidor y dispositivos IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | Instala aplicaciones .NET como demonios del sistema operativo: servicios de Windows y systemd de Linux. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | Plantillas de proyecto `dotnet new` para consola, web, API, servicio, datos e IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **Datos y mensajería** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | Cliente Redis con latencia de microsegundos, millones de operaciones por segundo y colas de mensajes integradas. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | Cliente RocketMQ totalmente administrado para Apache RocketMQ y Alibaba Cloud Message Queue. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | Cliente y servidor MQTT, incluida la conectividad con Alibaba Cloud IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | Base de datos híbrida en C#: embebida o en red, relacional, series temporales, cola de mensajes y KV en una sola. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | Controlador y capa de ayuda de MySql construidos sobre la pila de datos de NewLife. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **Web, IA y distribuido** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | Framework rápido para plataformas de administración: permisos, SSO, servidor OAuth y andamiaje CRUD. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | Plataforma distribuida de servicios: gestión de nodos, APM, centro de configuración, registro y centro de despliegues. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | Plataforma de tareas distribuidas y programación de datos en tiempo real con particionado, reintentos y relleno histórico. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | Cliente unificado para 46 proveedores de LLM más ChatAI, una aplicación web de chat lista para usar. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **Industrial e IoT** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | Biblioteca estándar de IoT que define contratos de protocolo y abstracciones de dispositivos. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | Implementación de Modbus TCP/RTU/ASCII sobre NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | Protocolo para PLC Siemens sobre NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | Protocolo para PLC Omron sobre NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | Protocolo para PLC Mitsubishi Melsec sobre NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | Cliente OPC para el acceso a datos industriales. | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | Pila de protocolo BACnet para la automatización de edificios. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | Plataforma IoT de código abierto y ligera con gestión de pasarelas y dispositivos. | — |
| **Telemática vehicular** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | Protocolo de posicionamiento vehicular JT/T 808: más de 70 cuerpos de mensaje, 2011/2013/2019. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | Protocolo plataforma a plataforma JT/T 809 para autoridades de transporte. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | Vídeo a bordo JT/T 1078: análisis RTP, remultiplexado FLV/fMP4 y transmisión en directo. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **Componentes de dominio** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | Un solo paquete para Excel, Word, PPT, PDF y más de 15 formatos de documento. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | Audio multiplataforma: códecs, E/S de dispositivos, DSP, contenedores y streaming. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | Códecs de vídeo, formatos de contenedor, procesamiento de fotogramas y streaming. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | Interfaz unificada sobre Baidu, AMap, Tencent y Tianditu con geocodificación y conversión de coordenadas. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | Geolocalización IPv4 con una base de datos offline mapeada en memoria y una API en línea de respaldo. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **Herramientas y productos** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | Caja de herramientas del desarrollador: red, puerto serie, criptografía, expresiones regulares, Modbus y MQTT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | Sistema de gestión de código fuente Git autoalojado. | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | Instala automáticamente el runtime de .NET correspondiente cuando se ejecuta desde un host más antiguo. | — |

> Las insignias muestran el paquete principal de cada proyecto; los paquetes de temas, extensiones y satélites figuran en sus páginas de NuGet. Los más de 30 paquetes no listados aquí (FTP, LoRa, extensiones de Office, controladores de protocolo, etc.) están disponibles en la [organización NewLifeX](https://github.com/NewLifeX) y en [newlifex.com](https://newlifex.com).


## Equipo

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

NewLife se fundó en 2002 y lleva más de dos décadas dedicado al ecosistema .NET e IoT, mantiene cerca de 100 proyectos de código abierto (34 de ellos principales) y ofrece soluciones integradas de software y hardware para los sectores de energía, logística, salud, control industrial e Internet. Nos guía el lema «en el aprendizaje no hay antigüedad: quien domina es maestro».

Los componentes de código abierto acumulan <!--nuget-total:start-->6.48M<!--nuget-total:end--> descargas en NuGet (todo el ecosistema, a fecha de 2026-10).

Sitio web: https://newlifex.com · Organización de código abierto: https://github.com/NewLifeX

<details>
<summary>Cuenta oficial de WeChat: 智能大石头 (escanea para seguir)</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>

