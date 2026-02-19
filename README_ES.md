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
| DDL | ✅ | CREATE/DROP TABLE/INDEX, con IF NOT EXISTS, PRIMARY KEY, UNIQUE |
| DML | ✅ | INSERT (múltiples filas), UPDATE, DELETE, UPSERT (ON DUPLICATE KEY UPDATE), TRUNCATE TABLE |
| Consulta | ✅ | SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT/OFFSET |
| Agregación | ✅ | COUNT/SUM/AVG/MIN/MAX |
| JOIN | ✅ | INNER/LEFT/RIGHT JOIN (Nested Loop), admite alias de tabla |
| Parametrización | ✅ | Marcadores de posición @param |
| Transacción | ✅ | MVCC, Read Committed, COMMIT/ROLLBACK |
| Funciones SQL | ⚠️ | Funciones de cadena/numérica/fecha/conversión/condicional (planificado) |
| Subconsulta | ⚠️ | IN/EXISTS (planificado) |
| Avanzado | ❌ | Sin vistas/triggers/procedimientos almacenados/funciones de ventana |

### Capacidades MQ (Flux Engine)

Basado en el modelo de grupo de consumidores de Redis Stream:

- **ID de mensaje**: Marca de tiempo + número de secuencia (autoincremento en el mismo milisegundo), ordenado globalmente
- **Grupo de consumidores**: `Topic/Stream` + `ConsumerGroup` + `Consumer` + `Pending`
- **Confiabilidad**: At-Least-Once, entra en Pending después de leer, Ack después del éxito comercial
- **Retención de datos**: Admite TTL (elimina automáticamente fragmentos antiguos por tiempo/tamaño de archivo)
- **Mensajes retrasados**: Especifique `DelayTime`/`DeliverAt` (planificado)
- **Cola de mensajes no entregados**: Entra automáticamente en DLQ en caso de fallo de consumo (planificado)
- **Lectura bloqueante**: Sondeo largo + tiempo de espera (planificado)

### Capacidades KV

- `Get(key)` / `Set(key, value, ttl)` / `Delete(key)` / `Exists(key)`
- Eliminación perezosa (verificar expiración al leer) + limpieza en segundo plano (`CleanupExpired()`)
- `Add(key, value, ttl)`: Agregar solo cuando la clave no existe (planificado)
- `Inc(key, delta, ttl)`: Incremento atómico (planificado)

## Seguridad de datos y modos WAL

NovaDb proporciona tres estrategias de persistencia WAL:

| Modo | Descripción | Casos de uso |
|------|-------------|--------------|
| `FULL` | Escritura síncrona en disco, descarga inmediata en cada commit | Escenarios financieros/comerciales, máxima seguridad de datos |
| `NORMAL` | Descarga asíncrona de 1s (predeterminado) | La mayoría de los escenarios comerciales, equilibra rendimiento y seguridad |
| `NONE` | Totalmente asíncrono, sin descarga proactiva | Escenarios de datos temporales/caché, máximo rendimiento |

> Elegir un modo distinto al síncrono (`FULL`) significa aceptar una posible pérdida de datos en escenarios de fallo/corte de energía.

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
