# NewLife.NovaDb

[![English](https://img.shields.io/badge/lang-English-blue.svg)](README.md) [![简体中文](https://img.shields.io/badge/lang-简体中文-red.svg)](README_CN.md) [![繁體中文](https://img.shields.io/badge/lang-繁體中文-orange.svg)](README_TW.md) [![Français](https://img.shields.io/badge/lang-Français-green.svg)](README_FR.md) [![Deutsch](https://img.shields.io/badge/lang-Deutsch-yellow.svg)](README_DE.md) [![日本語](https://img.shields.io/badge/lang-日本語-purple.svg)](README_JA.md) [![한국어](https://img.shields.io/badge/lang-한국어-brightgreen.svg)](README_KO.md) [![Русский](https://img.shields.io/badge/lang-Русский-lightgrey.svg)](README_RU.md) [![Español](https://img.shields.io/badge/lang-Español-yellow.svg)](README_ES.md) [![Português](https://img.shields.io/badge/lang-Português-blue.svg)](README_PT.md)

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
  - **Flux Engine** (series temporales + MQ): Particionamiento basado en tiempo Append Only, admite limpieza automática TTL, grupos de consumidores estilo Redis Stream + Pending + Ack
  - **Modo KV** (vista lógica): Reutiliza Nova Engine, la API oculta los detalles SQL, cada fila contiene `Key + Value + TTL`
  - **ADO.NET Provider**: Reconoce automáticamente el modo integrado/servidor, integración nativa con XCode ORM
- **Separación dinámica de índices calientes-fríos**: Los datos calientes se cargan completamente en la memoria física (nodos SkipList), los datos fríos se descargan a MMF con solo directorio disperso retenido. Tabla de 10 millones de filas consultando solo las últimas 10,000 filas usa < 20 MB de memoria.
- **Código puramente gestionado**: Sin dependencias de componentes nativos (puro C#/.NET), fácil de implementar en plataformas y entornos restringidos.

### Motores de almacenamiento

| Motor | Estructura de datos | Casos de uso |
|-------|---------------------|--------------|
| **Nova Engine** | SkipList (Separación memoria+MMF caliente-frío) | CRUD general, tablas de configuración, pedidos comerciales, datos de usuario |
| **Flux Engine** | Particionamiento basado en tiempo (Append Only) | Sensores IoT, recopilación de registros, colas de mensajes internas, registros de auditoría |
| **Modo KV** | Vista lógica de tabla Nova | Bloqueos distribuidos, almacenamiento en caché, almacenamiento de sesiones, contadores, centro de configuración |

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
| GeoPoint | `GEOPOINT` | Estructura personalizada | Coordenadas latitud/longitud (planificado) |
| Vector | `VECTOR(n)` | `Single[]` | Búsqueda de vectores AI (planificado) |

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

El modo servidor proporciona acceso remoto a través de TCP, admitiendo múltiples conexiones de clientes concurrentes.

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

#### 1.4 Transacciones

NovaDb implementa el aislamiento de transacciones basado en MVCC con un nivel de aislamiento predeterminado de Read Committed:

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

#### 1.6 Referencia de cadena de conexión

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
var affected = await client.ExecuteAsync(
    "INSERT INTO order_events (timestamp, orderId, action, amount) " +
    "VALUES (NOW(), 10001, 'created', 299.00)");
Console.WriteLine($"Message published, affected rows: {affected}");
```

#### 3.3 Consumir mensajes

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

#### 4.4 Resumen de capacidades KV

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
| **v1.1** | Funciones SQL de nivel P0 (cadena/numérica/fecha/conversión/condicional ~30 funciones) |
| **v1.2** | Lectura bloqueante MQ, operaciones KV Add/Inc, funciones SQL de nivel P1 |
| **v1.3** | Mensajes retrasados MQ, cola de mensajes no entregados |
| **v2.0** | Geocodificación GeoPoint + tipo Vector (búsqueda de vectores AI), observabilidad y herramientas de gestión |

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
