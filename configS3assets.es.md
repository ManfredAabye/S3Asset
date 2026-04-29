# Configuración del S3AssetService

## ¿Qué es un servidor S3 Asset y puedo operarlo yo mismo?

Un servidor S3 Asset es un almacén de objetos que implementa el protocolo S3 (Amazon Simple Storage Service).
Almacena bloques de datos grandes e inmutables (el contenido real de los assets: texturas, sonidos, objetos) bajo una
clave basada en el contenido (hash SHA-256), mientras que los metadatos (ID de asset, nombre, tipo, etc.) permanecen en MySQL.

Sí, puede operar dicho servidor usted mismo. La solución de alojamiento propio más común es **MinIO**, un
almacén de objetos gratuito y compatible con S3, que funciona en Linux, Windows y en Docker. Alternativamente,
se puede usar cualquier otro servicio compatible con S3 (p. ej. AWS S3, Wasabi, Backblaze B2).

## ¿Qué se necesita para un servidor S3 Asset?

**Mínimo (local / pruebas):**

- Una máquina o VM (Linux recomendado, p. ej. Ubuntu 22.04 LTS)
- [MinIO](https://min.io/) como almacén de objetos compatible con S3
- MySQL o MariaDB para los metadatos de los assets
- OpenSimulator con el plugin S3AssetService instalado

**Hardware mínimo para MinIO:**

| Recurso | Mínimo | Recomendación producción |
| --- | --- | --- |
| CPU | 2 núcleos | 4+ núcleos |
| RAM | 2 GB | 8+ GB |
| Almacenamiento | SSD recomendado | NVMe, RAID o almacenamiento distribuido |
| Red | 100 Mbit/s | 1 Gbit/s |

**Inicio rápido de MinIO en Ubuntu:**

```bash
# Descargar MinIO
wget https://dl.min.io/server/minio/release/linux-amd64/minio
chmod +x minio
sudo mv minio /usr/local/bin/

# Crear directorio de datos
sudo mkdir -p /data/minio

# Iniciar como servicio (variante más sencilla)
MINIO_ROOT_USER=minioadmin MINIO_ROOT_PASSWORD=minioadmin \
  minio server /data/minio --console-address ":9001"
```

MinIO estará disponible en:

- API S3: `http://localhost:9000`
- Consola web: `http://localhost:9001`

Crear el bucket `opensim-assets` mediante la consola web o con el cliente `mc`:

```bash
mc alias set local http://localhost:9000 minioadmin minioadmin
mc mb local/opensim-assets
```

## Configuración para uno o varios servidores S3 Asset

### Robust.ini (grid estándar sin Hypergrid)

En la sección `[AssetService]` de `Robust.ini`:

```ini
[AssetService]

    ;; Elegir un servicio de assets (solo una opción debe estar activa):
    ;LocalServiceModule = "OpenSim.Services.AssetService.dll:AssetService"
    ;LocalServiceModule = "OpenSim.Services.FSAssetService.dll:FSAssetConnector"
    LocalServiceModule = "OpenSim.Services.S3AssetService.dll:S3AssetConnector"

    ;; S3AssetService -- servicio de assets escalable para instalaciones muy grandes.
    ;; Metadatos en MySQL (256 particiones KEY), datos binarios en S3/MinIO.
    ;; Resuelve el crecimiento de BLOB, los problemas de inodos de FSAssets y los límites de SQLite simultáneamente.
    ;; Consola: 's3 show assets', 's3 import assets', 's3 import fsassets', 's3 export assets'
    S3Endpoint   = "http://minio:9000"
    S3Bucket     = "opensim-assets"
    S3AccessKey  = "minioadmin"
    S3SecretKey  = "minioadmin"
    S3Region     = "us-east-1"

    ;; S3AssetService requiere StorageProvider + ConnectionString + Realm
    StorageProvider  = "OpenSim.Data.MySQL.dll"
    ConnectionString = "Data Source=localhost;Database=opensim;User ID=opensim;Password=TUCONTRASEÑA;Old Guids=true;SslMode=None;"
    Realm            = "s3assets"
    DaysBetweenAccessTimeUpdates = 30
    ShowConsoleStats = true

    DefaultAssetLoader = "OpenSim.Framework.AssetLoader.Filesystem.dll"
    AssetLoaderArgs = "./assets/AssetSets.xml"

    AllowRemoteDelete = false
    AllowRemoteDeleteAllTypes = false
```

### Robust.HG.ini (grid con soporte Hypergrid)

Usar el mismo bloque S3 que arriba en la sección `[AssetService]`.
Adicionalmente, activar el servicio S3 específico de HG en la sección `[HGAssetService]`:

```ini
[HGAssetService]
    ;; Elegir una opción (solo una debe estar activa):
    ;LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGAssetService"
    ;LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGFSAssetService"
    LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGS3AssetService"

    UserAccountsService = "OpenSim.Services.UserAccountService.dll:UserAccountService"

    ;; Servicio público -- no forzar autenticación
    AuthType = None

    ;; HomeURI de su grid (debe coincidir con [Hypergrid])
    ; HomeURI = "http://mygrid.example.com:8002"

    ;; Tipos de assets que se pueden exportar/importar.
    ;; Dejar en blanco para sin restricciones.
    ;; Ejemplo: bloquear scripts:
    ; DisallowExport = "LSLText"
    ; DisallowImport = "LSLBytecode"
```

### Múltiples instancias S3 (escalado horizontal)

Cuando más de una instancia de Robust comparte el mismo bucket S3 y la misma base de datos MySQL,
establecer `SecondaryInstance = true` en cada instancia secundaria para que solo una instancia
ejecute tareas en segundo plano (p. ej. actualización del tiempo de acceso, hilo de estadísticas):

```ini
[AssetService]
    LocalServiceModule = "OpenSim.Services.S3AssetService.dll:S3AssetConnector"
    SecondaryInstance = true
    ; ... demás parámetros S3 como arriba
```

El bucket y la base de datos MySQL pueden ser compartidos por todas las instancias sin cambios adicionales,
ya que S3 en sí mismo es sin estado y escalable, y MySQL gestiona la serialización de los metadatos.
