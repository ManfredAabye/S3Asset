# Servicio de Assets S3 y S3 HG Asset

## Estado de Implementacion

| Componente | Archivo | Estado |
| --- | --- | --- |
| Migracion MySQL (256 particiones) | `OpenSim/Data/MySQL/Resources/S3AssetStore.migrations` | Terminado |
| Plugin de metadatos | `OpenSim/Data/MySQL/MySQLS3AssetData.cs` | Terminado |
| Servicio de assets (grid normal) | `OpenSim/Services/S3AssetService/S3AssetConnector.cs` | Terminado |
| Servicio de assets (Hypergrid) | `OpenSim/Services/HypergridService/HGS3AssetService.cs` | Terminado |
| AssemblyInfo | `OpenSim/Services/S3AssetService/Properties/AssemblyInfo.cs` | Terminado |
| Interfaz (IFSAssetDataPlugin) | `OpenSim/Data/IFSAssetData.cs` | Ampliado |
| Stubs MySQLFSAssetData | `OpenSim/Data/MySQL/MySQLFSAssetData.cs` | Terminado |
| Stubs PGSQLFSAssetData | `OpenSim/Data/PGSQL/PGSQLFSAssetData.cs` | Terminado |
| prebuild.xml | `prebuild.xml` | Terminado |
| HypergridService.csproj | `OpenSim/Services/HypergridService/OpenSim.Services.HypergridService.csproj` | Terminado |
| Robust.ini.example | `bin/Robust.ini.example` | Terminado |
| Robust.HG.ini.example | `bin/Robust.HG.ini.example` | Terminado |
| Build | `OpenSim.sln` | **Correcto (0 errores, 4 advertencias CS9193)** |

Las 4 advertencias CS9193 se refieren a parametros `ref readonly` en `KeyframeMotion.cs`, `SceneObjectGroup.cs`, `Scene.Inventory.cs` y `BunchOfCaps.cs`, y son independientes de esta implementacion.

Binarios generados: `bin/OpenSim.Services.S3AssetService.dll`, `bin/OpenSim.Services.HypergridService.dll`

Esta configuracion describe un servicio de assets para OpenSimulator que guarda metadatos en MySQL y los datos binarios en un almacenamiento de objetos compatible con S3.

No es un cambio cosmetico, sino operativo: las tablas clasicas con BLOB crecen hasta volverse problematicas, FSAssets genera cantidades extremas de archivos, y SQLite no escala bien para esta carga. Separar metadatos SQL y objetos S3 elimina estos cuellos de botella.

## Lista de Archivos con Rutas para Implementar en Otros Repositorios

### Archivos nuevos (deben crearse)

| Ruta | Descripcion |
| --- | --- |
| `OpenSim/Data/MySQL/Resources/S3AssetStore.migrations` | Script de migracion MySQL que crea la tabla `s3assets` con 256 particiones KEY |
| `OpenSim/Data/MySQL/MySQLS3AssetData.cs` | Implementa `IFSAssetDataPlugin` para MySQL: lectura/escritura de metadatos, verificacion por lotes, logica de migracion |
| `OpenSim/Services/S3AssetService/S3AssetConnector.cs` | `IAssetService` completo para grids normales: cliente S3 (AWS SigV4), deduplicacion SHA-256, comandos de consola, fallback |
| `OpenSim/Services/S3AssetService/Properties/AssemblyInfo.cs` | Metadatos de ensamblado para el proyecto S3AssetService |
| `OpenSim/Services/HypergridService/HGS3AssetService.cs` | Wrapper de Hypergrid: hereda de `S3AssetConnector`, valida permisos y ajusta CreatorID/SOP XML |

### Archivos modificados (deben ajustarse)

| Ruta | Cambio |
| --- | --- |
| `OpenSim/Data/IFSAssetData.cs` | Dos metodos nuevos en la interfaz: `ImportFromFSAssets(...)` y `ExportToLegacy(...)` |
| `OpenSim/Data/MySQL/MySQLFSAssetData.cs` | Stubs para los dos metodos nuevos (lanzan `NotImplementedException`) |
| `OpenSim/Data/PGSQL/PGSQLFSAssetData.cs` | Stubs para los dos metodos nuevos (lanzan `NotImplementedException`) |
| `prebuild.xml` | Nuevo elemento `<Project>` para `OpenSim.Services.S3AssetService`; agrega referencia `OpenSim.Services.S3AssetService` en HypergridService |
| `OpenSim/Services/HypergridService/OpenSim.Services.HypergridService.csproj` | Agrega `<ProjectReference>` a `S3AssetService.csproj` |
| `bin/Robust.ini.example` | Agrega `S3AssetConnector` como tercera opcion de `LocalServiceModule`; bloque completo S3 comentado |
| `bin/Robust.HG.ini.example` | Agrega `HGS3AssetService` como tercera opcion de `LocalServiceModule` en `[HGAssetService]` |

```bash
S3Asset\
├── prebuild.xml
├── s3assets.md
├── bin\
│   ├── Robust.ini.example
│   └── Robust.HG.ini.example
└── OpenSim\
    ├── Data\
    │   ├── IFSAssetData.cs
    │   ├── MySQL\  (MySQLS3AssetData.cs, MySQLFSAssetData.cs, Resources\S3AssetStore.migrations)
    │   └── PGSQL\  (PGSQLFSAssetData.cs)
    └── Services\
        ├── HypergridService\  (HGS3AssetService.cs)
        └── S3AssetService\    (S3AssetConnector.cs, Properties\AssemblyInfo.cs)
```

## Arquitectura

La estructura tiene dos capas:

1. MySQL guarda solo metadatos: ID de asset, nombre, tipo, hash, fecha de creacion y ultimo acceso.
2. S3 o MinIO guarda el bloque binario como objeto, direccionado por hash SHA-256.

Esto aporta varias propiedades:

- Sin grandes masas BLOB dentro de la tabla SQL.
- Sin miles de millones de archivos individuales en directorios del sistema.
- Deduplicacion automatica, porque contenido identico produce el mismo hash.
- Mejor escalado horizontal para inventarios de assets muy grandes.

## Por que esta Estructura tiene Sentido

En instalaciones grandes de OpenSim, el almacenamiento de assets se vuelve rapido un cuello de botella.

- Una tabla SQL clasica con BLOB impacta almacenamiento, buffer pool, backups y replicacion.
- FSAssets solo traslada el problema al sistema de archivos y produce volumenes inmanejables.
- SQLite es practico para instalaciones pequenas, pero no para cargas enormes y permanentes.

La variante S3 separa de forma deliberada:

- datos relacionales en MySQL,
- binarios grandes e inmutables en almacenamiento de objetos.

Esta es la separacion moderna usada en sistemas de almacenamiento direccionado por contenido a gran escala.

## Comandos de Consola en Produccion

Despues del arranque, estan disponibles estos comandos en la consola Robust:

| Comando | Funcion |
| --- | --- |
| `s3 show assets` | Muestra reads, writes, dedup hits, misses y estimacion de filas en DB |
| `s3 import assets <conn> <table> [start count]` | Migra assets desde tabla MySQL clasica `assets` (con BLOB) hacia S3 |
| `s3 import fsassets <fsbase> <conn> <table>` | Migra assets desde arbol FSAssets + tabla hash MySQL hacia S3 |
| `s3 export assets <conn> <table>` | Rollback: exporta todos los assets en S3 hacia una tabla clasica `assets` |

Estos comandos se ejecutan de forma sincrona y registran progreso cada 10.000 entradas.

## Activar el Servicio

En `Robust.ini` o `Robust.HG.ini` en la seccion `[AssetService]`:

```ini
;; Comentar la linea actual:
;LocalServiceModule = "OpenSim.Services.AssetService.dll:AssetService"

;; Activar esta linea:
LocalServiceModule = "OpenSim.Services.S3AssetService.dll:S3AssetConnector"
```

Luego configurar los parametros S3 (ver ejemplo abajo).

```ini
[AssetService]
DefaultAssetLoader = ""
StorageProvider = "OpenSim.Data.MySQL.dll"
ConnectionString = "Data Source=localhost;Database=opensim;User ID=opensim;Password=YOURPASSWORD;"
Realm = "s3assets"
S3Endpoint = "http://minio:9000"
S3Bucket = "opensim-assets"
S3AccessKey = "minioadmin"
S3SecretKey = "minioadmin"
S3Region = "us-east-1"
DaysBetweenAccessTimeUpdates = 30
```

## Significado de los Parametros

`StorageProvider`
Define que proveedor de base de datos se usa para metadatos. En este caso, MySQL.

`ConnectionString`
Cadena de conexion a la base MySQL donde vive la tabla de metadatos.

`Realm`
Nombre de tabla o ambito logico de almacenamiento para metadatos. Aqui se usa `s3assets`.

`S3Endpoint`
URL del endpoint compatible con S3. Puede ser AWS S3, MinIO u otro servicio compatible.

`S3Bucket`
Bucket donde se guardan los datos binarios.

`S3AccessKey` y `S3SecretKey`
Credenciales del almacenamiento de objetos. En produccion no deberian quedar en duro en archivos visibles.

`S3Region`
Region compatible AWS usada para firma. En MinIO suele usarse igualmente `us-east-1`.

`DaysBetweenAccessTimeUpdates`
Limita cuantas veces se actualiza `access_time` en base de datos, reduciendo escritura innecesaria en assets leidos con frecuencia.

## Flujo de Datos al Guardar

Al guardar un asset:

1. Se calcula hash del contenido.
2. Se verifica si ese hash ya existe en S3.
3. Si no existe, se sube el bloque al bucket.
4. Se insertan o actualizan metadatos en MySQL.

El hash funciona como clave estable del objeto. Contenido identico se guarda una sola vez.

## Flujo de Datos al Leer

Al leer un asset:

1. Se cargan metadatos por ID desde MySQL.
2. Se toma el hash de contenido desde metadatos.
3. Se carga el objeto en S3 usando ese hash.
4. Opcionalmente se actualiza el tiempo de acceso segun `DaysBetweenAccessTimeUpdates`.

## Notas de Escalado

Esta solucion esta pensada para volumenes muy grandes, siguiendo algunas reglas:

- La tabla de metadatos MySQL debe estar particionada.
- El bucket debe residir en almacenamiento capaz de sostener grandes cantidades de objetos.
- Los backups de metadatos y objetos deben ser separados pero coordinados.
- Los borrados no deben eliminar fisicamente todos los objetos de inmediato cuando hay deduplicacion.

Importante: la capa S3 escala conteo de objetos mucho mejor que los arboles de directorios clasicos.

## Notas Operativas

- MinIO suele ser la opcion mas practica para instalaciones locales, clusters privados y pruebas.
- En produccion se recomienda TLS, credenciales separadas y politicas de ciclo de vida del bucket.
- MySQL debe dimensionarse para el volumen esperado de metadatos (InnoDB buffer pool, redo log y layout de almacenamiento).
- El object store no tiene que estar en la misma maquina que la simulacion, lo cual mejora distribucion de carga.

## Limites de esta Configuracion

Lo siguiente aun no esta implementado y puede agregarse segun necesidad:

- **Garbage collection de objetos S3**: `Delete()` elimina solo metadatos. Los hashes sin referencia permanecen en S3. En produccion se requiere un job offline que compare hashes de `s3assets` con el contenido del bucket.
- **Monitoring**: Todavia no hay endpoint Prometheus/Grafana. Estadisticas disponibles solo por consola (`s3 show assets`) y por hilo de log cada 60 segundos.
- **Estrategia de backup**: El dump de MySQL y backup del bucket S3 deben coordinarse. Si hay metadato pero falta el objeto, la lectura devolvera bloque vacio.

## Migracion hacia S3

La migracion es necesaria al pasar de un backend clasico de assets a S3. Hay tres escenarios de origen.

### Origen: tabla MySQL `assets` (clasica, con BLOB)

La tabla `assets` guarda el binario en la columna `data`. La migracion lee filas, sube contenido a S3 y guarda metadatos en `s3assets`.

Preparacion:

```sql
-- Recomendado leer por lotes, no SELECT * de una vez
SELECT id, name, description, assetType, data, create_time, access_time
FROM assets
ORDER BY id
LIMIT 10000 OFFSET 0;
```

Proceso:

1. Detener OpenSim o pasar a modo de mantenimiento read-only.
2. Iniciar script de migracion y leer assets por lotes.
3. Por fila: calcular SHA-256 sobre `data`, hacer PUT a S3, escribir metadatos en `s3assets`.
4. Al terminar, cambiar configuracion a S3AssetConnector.
5. Opcional: borrar o archivar la tabla `assets` antigua tras validar.

La llamada integrada `Import()` en `MySQLS3AssetData.cs` soporta este camino directamente.

### Origen: FSAssets (sistema de archivos + tabla hash MySQL)

FSAssets guarda binarios como archivos `.gz` y mantiene tabla hash en MySQL. La migracion recorre `fsassets`, lee y descomprime cada archivo y lo sube a S3.

Proceso:

1. Mantener accesible la estructura de directorios FSAssets.
2. Leer todas las entradas de la tabla MySQL `fsassets`.
3. Por entrada: derivar ruta por hash, leer `.gz`, descomprimir, subir a S3, insertar metadatos en `s3assets`.
4. Cambiar configuracion.

Importante: muchas instalaciones FSAssets tienen desde cientos de miles hasta cientos de millones de archivos. La migracion necesita proceso por lotes, checkpoints y soporte de reanudacion.

### Origen: SQLite

Las bases de assets SQLite pueden tratarse igual que la tabla MySQL `assets` clasica con BLOB. Con una herramienta como `System.Data.SQLite` se leen filas y el resto es identico.

---

## Migracion de Vuelta desde S3

La migracion inversa es necesaria si se abandona S3 o se requiere rollback a una arquitectura clasica. Es mas costosa que la migracion de ida, porque el binario debe volver a incrustarse en filas relacionales.

### Destino: tabla MySQL `assets` (con BLOB)

1. Leer todas las entradas de `s3assets`.
2. Por entrada: cargar objeto desde S3 por hash y escribirlo en `assets.data`.
3. Copiar metadatos (nombre, tipo, timestamps y flags) desde `s3assets`.
4. Volver la configuracion al AssetService clasico.

```sql
-- Crear tabla destino si no existe
CREATE TABLE IF NOT EXISTS assets (
    id        CHAR(36)     NOT NULL PRIMARY KEY,
    name      VARCHAR(64)  NOT NULL,
    description VARCHAR(64) NOT NULL,
    assetType TINYINT      NOT NULL,
    local     TINYINT      NOT NULL,
    temporary TINYINT      NOT NULL,
    data      LONGBLOB     NOT NULL,
    create_time INT        DEFAULT 0,
    access_time INT        DEFAULT 0,
    asset_flags INT        NOT NULL DEFAULT 0
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

Luego, para cada asset en codigo de aplicacion:

```text
hash = s3assets.hash WHERE id = ?
data = S3.Get(hash)
INSERT INTO assets (id, name, description, ..., data) VALUES (...)
```

### Destino: FSAssets

1. Leer todas las entradas de `s3assets`.
2. Por entrada: cargar objeto de S3, comprimir con gzip y guardarlo como archivo en el arbol FSAssets.
3. Insertar la entrada correspondiente en la tabla MySQL `fsassets`.

Nota: esto vuelve a llenar FSAssets con enormes cantidades de archivos y reproduce los mismos problemas de escalado que la migracion a S3 corrige.

### Notas generales para migracion inversa

- Validar siempre la migracion inversa en staging.
- El conteo de assets de origen y destino debe coincidir al finalizar.
- Verificacion SHA-256 al leer de S3 y escribir en destino evita corrupcion silenciosa.
- OpenSim debe estar detenido durante una migracion completa de vuelta; si no, nuevos assets pueden quedar solo en S3 y no en el backend destino.

---

## Conclusion Breve

Para instalaciones grandes de OpenSim, esta arquitectura es claramente mas adecuada que tablas SQL con BLOB, FSAssets o almacenamiento basado en SQLite. Separa metadatos y contenido de forma limpia, reduce duplicados y evita los cuellos de botella tipicos de escalado en almacenamiento por archivos o BLOB.
