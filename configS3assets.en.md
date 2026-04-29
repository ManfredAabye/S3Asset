# S3AssetService Configuration

## What is an S3 Asset Server and Can I Run One Myself

An S3 asset server is an object store that implements the S3 protocol (Amazon Simple Storage Service).
It stores large, immutable data blocks (the actual asset contents such as textures, sounds, objects) under a
content-based key (SHA-256 hash), while metadata (asset ID, name, type, etc.) remains in MySQL.

Yes, you can run such a server yourself. The most common self-hosted solution is **MinIO**, a free,
S3-compatible object store that runs on Linux, Windows and in Docker. Alternatively, any other
S3-compatible service (e.g. AWS S3, Wasabi, Backblaze B2) can be used.

## What is Needed for an S3 Asset Server

**Minimum (local / test setup):**

- A machine or VM (Linux recommended, e.g. Ubuntu 22.04 LTS)
- [MinIO](https://min.io/) as an S3-compatible object store
- MySQL or MariaDB for asset metadata
- OpenSimulator with the S3AssetService plugin installed

**Minimum hardware for MinIO:**

| Resource | Minimum | Production Recommendation |
| --- | --- | --- |
| CPU | 2 cores | 4+ cores |
| RAM | 2 GB | 8+ GB |
| Storage | SSD recommended | NVMe, RAID or distributed storage |
| Network | 100 Mbit/s | 1 Gbit/s |

**MinIO Quick-Start on Ubuntu:**

```bash
# Download MinIO
wget https://dl.min.io/server/minio/release/linux-amd64/minio
chmod +x minio
sudo mv minio /usr/local/bin/

# Create data directory
sudo mkdir -p /data/minio

# Start as a service (simplest variant)
MINIO_ROOT_USER=minioadmin MINIO_ROOT_PASSWORD=minioadmin \
  minio server /data/minio --console-address ":9001"
```

MinIO is then available at:

- S3 API: `http://localhost:9000`
- Web console: `http://localhost:9001`

Create the `opensim-assets` bucket via the web console or using the `mc` client:

```bash
mc alias set local http://localhost:9000 minioadmin minioadmin
mc mb local/opensim-assets
```

## Configuration for One or More S3 Asset Servers

### Robust.ini (standard grid without Hypergrid)

In the `[AssetService]` section of `Robust.ini`:

```ini
[AssetService]

    ;; Choose one asset service (only one option should be enabled):
    ;LocalServiceModule = "OpenSim.Services.AssetService.dll:AssetService"
    ;LocalServiceModule = "OpenSim.Services.FSAssetService.dll:FSAssetConnector"
    LocalServiceModule = "OpenSim.Services.S3AssetService.dll:S3AssetConnector"

    ;; S3AssetService -- scalable asset service for very large installations.
    ;; Metadata in MySQL (256 KEY partitions), binary data in S3/MinIO.
    ;; Solves BLOB growth, FSAssets inode issues and SQLite limits simultaneously.
    ;; Console: 's3 show assets', 's3 import assets', 's3 import fsassets', 's3 export assets'
    S3Endpoint   = "http://minio:9000"
    S3Bucket     = "opensim-assets"
    S3AccessKey  = "minioadmin"
    S3SecretKey  = "minioadmin"
    S3Region     = "us-east-1"

    ;; S3AssetService requires StorageProvider + ConnectionString + Realm
    StorageProvider  = "OpenSim.Data.MySQL.dll"
    ConnectionString = "Data Source=localhost;Database=opensim;User ID=opensim;Password=YOURPASSWORD;Old Guids=true;SslMode=None;"
    Realm            = "s3assets"
    DaysBetweenAccessTimeUpdates = 30
    ShowConsoleStats = true

    DefaultAssetLoader = "OpenSim.Framework.AssetLoader.Filesystem.dll"
    AssetLoaderArgs = "./assets/AssetSets.xml"

    AllowRemoteDelete = false
    AllowRemoteDeleteAllTypes = false
```

### Robust.HG.ini (grid with Hypergrid support)

Use the same S3 block as above in the `[AssetService]` section.
Additionally, activate the HG-specific S3 service in the `[HGAssetService]` section:

```ini
[HGAssetService]
    ;; Choose one option (only one should be enabled):
    ;LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGAssetService"
    ;LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGFSAssetService"
    LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGS3AssetService"

    UserAccountsService = "OpenSim.Services.UserAccountService.dll:UserAccountService"

    ;; Public-facing service -- do not enforce authentication
    AuthType = None

    ;; HomeURI of your grid (must match [Hypergrid])
    ; HomeURI = "http://mygrid.example.com:8002"

    ;; Asset types that may be exported/imported.
    ;; Leave blank for no restrictions.
    ;; Example: block scripts:
    ; DisallowExport = "LSLText"
    ; DisallowImport = "LSLBytecode"
```

### Multiple S3 Instances (horizontal scaling)

When running more than one Robust instance sharing the same S3 bucket and MySQL database,
set `SecondaryInstance = true` in every secondary instance so that only one instance
runs background tasks (e.g. access time updates, statistics thread):

```ini
[AssetService]
    LocalServiceModule = "OpenSim.Services.S3AssetService.dll:S3AssetConnector"
    SecondaryInstance = true
    ; ... remaining S3 parameters as above
```

The bucket and MySQL database can be shared by all instances without further changes,
since S3 itself is stateless and scalable, and MySQL handles the serialisation of metadata.
