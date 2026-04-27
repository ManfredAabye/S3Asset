# S3 Asset and S3 HG Asset Service

## Implementation Status

| Component | File | Status |
| --- | --- | --- |
| MySQL migration (256 partitions) | `OpenSim/Data/MySQL/Resources/S3AssetStore.migrations` | Done |
| Metadata plugin | `OpenSim/Data/MySQL/MySQLS3AssetData.cs` | Done |
| Asset service (regular grid) | `OpenSim/Services/S3AssetService/S3AssetConnector.cs` | Done |
| Asset service (Hypergrid) | `OpenSim/Services/HypergridService/HGS3AssetService.cs` | Done |
| AssemblyInfo | `OpenSim/Services/S3AssetService/Properties/AssemblyInfo.cs` | Done |
| Interface (IFSAssetDataPlugin) | `OpenSim/Data/IFSAssetData.cs` | Extended |
| MySQLFSAssetData stubs | `OpenSim/Data/MySQL/MySQLFSAssetData.cs` | Done |
| PGSQLFSAssetData stubs | `OpenSim/Data/PGSQL/PGSQLFSAssetData.cs` | Done |
| prebuild.xml | `prebuild.xml` | Done |
| HypergridService.csproj | `OpenSim/Services/HypergridService/OpenSim.Services.HypergridService.csproj` | Done |
| Robust.ini.example | `bin/Robust.ini.example` | Done |
| Robust.HG.ini.example | `bin/Robust.HG.ini.example` | Done |
| Build | `OpenSim.sln` | **Successful (0 errors, 4 CS9193 warnings)** |

The 4 CS9193 warnings are about `ref readonly` parameters in `KeyframeMotion.cs`, `SceneObjectGroup.cs`, `Scene.Inventory.cs`, and `BunchOfCaps.cs`, and are independent of this implementation.

Generated binaries: `bin/OpenSim.Services.S3AssetService.dll`, `bin/OpenSim.Services.HypergridService.dll`

This configuration describes an OpenSimulator asset service that stores metadata in MySQL and the actual binary payload in an S3-compatible object store.

This is not a cosmetic change, but an operational requirement: classic asset tables with inline BLOB payloads become problematic at scale, FSAssets creates extreme file counts, and SQLite does not scale for this kind of load. Splitting SQL metadata and S3 objects removes these bottlenecks.

## File List with Paths for Implementation in Other Repositories

### New files (must be created)

| Path | Description |
| --- | --- |
| `OpenSim/Data/MySQL/Resources/S3AssetStore.migrations` | MySQL migration script creating the `s3assets` table with 256 KEY partitions |
| `OpenSim/Data/MySQL/MySQLS3AssetData.cs` | Implements `IFSAssetDataPlugin` for MySQL: metadata read/write, batch existence checks, migration logic |
| `OpenSim/Services/S3AssetService/S3AssetConnector.cs` | Full `IAssetService` for regular grids: S3 client (AWS SigV4), SHA-256 dedup, console commands, fallback |
| `OpenSim/Services/S3AssetService/Properties/AssemblyInfo.cs` | Assembly metadata for the S3AssetService project |
| `OpenSim/Services/HypergridService/HGS3AssetService.cs` | Hypergrid wrapper: inherits from `S3AssetConnector`, checks asset permissions, rewrites CreatorID/SOP XML |

### Modified files (must be patched)

| Path | Change |
| --- | --- |
| `OpenSim/Data/IFSAssetData.cs` | Two new interface methods: `ImportFromFSAssets(...)` and `ExportToLegacy(...)` |
| `OpenSim/Data/MySQL/MySQLFSAssetData.cs` | Stubs for the two new interface methods (throw `NotImplementedException`) |
| `OpenSim/Data/PGSQL/PGSQLFSAssetData.cs` | Stubs for the two new interface methods (throw `NotImplementedException`) |
| `prebuild.xml` | New `<Project>` entry for `OpenSim.Services.S3AssetService`; adds `OpenSim.Services.S3AssetService` reference in HypergridService block |
| `OpenSim/Services/HypergridService/OpenSim.Services.HypergridService.csproj` | Adds `<ProjectReference>` to `S3AssetService.csproj` |
| `bin/Robust.ini.example` | Adds `S3AssetConnector` as third `LocalServiceModule` option; full commented S3 config block |
| `bin/Robust.HG.ini.example` | Adds `HGS3AssetService` as third `LocalServiceModule` option in `[HGAssetService]` |

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

## Architecture

The design has two layers:

1. MySQL stores metadata only: asset ID, name, type, hash, creation time, and last access time.
2. S3 or MinIO stores the binary payload as objects addressed by SHA-256 hash.

This gives several key properties:

- No massive BLOB payloads inside SQL tables.
- No billions of individual files in filesystem directories.
- Automatic deduplication because identical content produces identical hashes.
- Better horizontal scalability for very large asset inventories.

## Why This Structure Makes Sense

In large OpenSim installations, asset storage quickly becomes a bottleneck.

- A classic SQL asset table with BLOB data stresses storage, buffer pool, backups, and replication.
- FSAssets only shifts the problem to the filesystem and produces unmanageable file and directory counts.
- SQLite is practical for small installations but not for very large, always-active asset workloads with billions of entries.

The S3 approach deliberately separates:

- relational metadata in MySQL,
- large immutable binary content in object storage.

This is the modern storage split typically used for content-addressable systems at scale.

## Console Commands During Operation

After startup, the following commands are available in the Robust console:

| Command | Function |
| --- | --- |
| `s3 show assets` | Shows reads, writes, dedup hits, misses, and estimated DB row count |
| `s3 import assets <conn> <table> [start count]` | Migrates assets from classic MySQL `assets` table (with BLOB) to S3 |
| `s3 import fsassets <fsbase> <conn> <table>` | Migrates assets from FSAssets directory tree + MySQL hash table to S3 |
| `s3 export assets <conn> <table>` | Rollback: exports all S3-backed assets to a classic `assets` table |

These commands run synchronously in the console and log progress every 10,000 entries.

## Enable the Service

In `Robust.ini` or `Robust.HG.ini` in section `[AssetService]`:

```ini
;; Comment the current line:
;LocalServiceModule = "OpenSim.Services.AssetService.dll:AssetService"

;; Enable this line:
LocalServiceModule = "OpenSim.Services.S3AssetService.dll:S3AssetConnector"
```

Then configure the S3 parameters (see example below).

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

## Meaning of Parameters

`StorageProvider`
Defines which database provider is used for metadata. Here it is MySQL.

`ConnectionString`
Connection string to the MySQL database that stores asset metadata.

`Realm`
Table name or logical storage realm for metadata. This setup uses `s3assets`.

`S3Endpoint`
URL of the S3-compatible storage endpoint. This can be AWS S3, MinIO, or another compatible service.

`S3Bucket`
Bucket that stores asset binary payloads.

`S3AccessKey` and `S3SecretKey`
Credentials for object storage. In production, these should not be hardcoded in broadly visible files.

`S3Region`
AWS-compatible region used for signature generation. With MinIO, `us-east-1` is commonly used.

`DaysBetweenAccessTimeUpdates`
Limits how often `access_time` is updated in the database, reducing unnecessary write pressure for frequently read assets.

## Write Data Flow

When storing an asset, the process is:

1. Hash the payload.
2. Check whether that hash already exists in S3.
3. If not, upload the payload to the bucket.
4. Store or update metadata in MySQL.

The hash acts as a stable object key. Identical content is therefore stored once.

## Read Data Flow

When reading an asset:

1. Load metadata by asset ID from MySQL.
2. Read the content hash from metadata.
3. Load the object from S3 by that hash.
4. Optionally update access time according to `DaysBetweenAccessTimeUpdates`.

## Scaling Notes

This design is built for very large datasets if a few rules are followed:

- The MySQL metadata table should be partitioned.
- The bucket should run on storage that can sustain large object counts.
- Metadata and object backups should be separate, but coordinated.
- Deletes should not always physically remove objects immediately when deduplication is used.

Important: S3-style object storage scales object counts much better than classic filesystem directory trees.

## Operational Notes

- MinIO is often the most practical choice for local setups, private clusters, and tests.
- Production should use TLS, separate credentials, and a bucket lifecycle concept.
- MySQL should be sized for expected metadata volume, especially InnoDB buffer pool, redo log, and storage layout.
- The object store does not have to run on the same machine as the simulator, which improves load distribution.

## Limitations of This Configuration

The following parts are still not implemented and should be added as needed:

- **S3 object garbage collection**: `Delete()` removes metadata only. Hash objects without metadata references remain in S3. A separate offline job is needed in production to compare `s3assets` hashes against bucket contents.
- **Monitoring**: No Prometheus/Grafana endpoint yet. Metrics are available only through console (`s3 show assets`) and the 60-second log thread.
- **Backup strategy**: MySQL dump and S3 bucket backup must be coordinated. If metadata exists but the bucket object is missing, reads return empty payloads.

## Migration to S3

Migration is required when moving an existing server from a classic asset backend to S3. There are three source scenarios.

### Source: MySQL `assets` table (classic, with BLOB)

The `assets` table stores binary data directly in column `data`. Migration reads rows, uploads payloads to S3 objects, and stores metadata in `s3assets`.

Preparation:

```sql
-- Read in batches, not SELECT * at once
SELECT id, name, description, assetType, data, create_time, access_time
FROM assets
ORDER BY id
LIMIT 10000 OFFSET 0;
```

Procedure:

1. Stop OpenSim or switch to read-only maintenance mode.
2. Start migration script and read asset rows in batches.
3. Per row: compute SHA-256 over `data`, PUT to S3, write metadata to `s3assets`.
4. After completion, switch config to S3AssetConnector.
5. Optionally delete or archive old `assets` table after validation.

The built-in `Import()` call in `MySQLS3AssetData.cs` directly supports this path.

### Source: FSAssets (filesystem + MySQL hash table)

FSAssets stores payloads as `.gz` files in the filesystem and tracks hashes in MySQL. Migration iterates the `fsassets` table, reads and decompresses each file, then writes payload to S3.

Procedure:

1. Keep FSAssets directory structure accessible.
2. Read all entries from MySQL `fsassets` table.
3. Per entry: derive path from hash, read `.gz`, decompress, upload to S3, insert metadata in `s3assets`.
4. Switch configuration.

Important: many FSAssets deployments have hundreds of thousands to hundreds of millions of files. Migration needs batch processing, checkpoint logic, and resume support.

### Source: SQLite

SQLite asset databases can be treated the same way as classic MySQL `assets` with BLOB data. Use a suitable tool such as `System.Data.SQLite` to read rows; the rest is identical.

---

## Migration Back from S3

A reverse migration is needed if S3 operation is discontinued or if rollback to a classic setup is required. It is more expensive than forward migration because binary payloads must be embedded back into relational rows.

### Target: MySQL `assets` table (with BLOB)

1. Read all entries from `s3assets`.
2. Per entry: fetch object from S3 by hash and write it into `assets.data`.
3. Copy metadata fields such as name, type, timestamps, and flags from `s3assets`.
4. Switch config back to classic AssetService.

```sql
-- Create target table if missing
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

Then for each asset in application code:

```text
hash = s3assets.hash WHERE id = ?
data = S3.Get(hash)
INSERT INTO assets (id, name, description, ..., data) VALUES (...)
```

### Target: FSAssets

1. Read all entries from `s3assets`.
2. Per entry: fetch object from S3, gzip it, store as file in FSAssets directory tree.
3. Insert matching entry in MySQL `fsassets` table.

Note: this repopulates FSAssets with very large file counts and therefore reproduces the same scaling issues that S3 migration solves.

### General notes for reverse migration

- Always validate reverse migration on staging first.
- Asset count in source and target must match after migration.
- SHA-256 verification when reading from S3 and writing to target avoids silent corruption.
- OpenSim should be stopped during full reverse migration. Otherwise, new assets may be written only to S3 and not to the target backend.

---

## Short Conclusion

For large OpenSim installations, this architecture is significantly more suitable than BLOB-heavy SQL tables, FSAssets, or SQLite-based asset storage. It cleanly separates metadata from content, reduces duplicates, and avoids the typical scaling bottlenecks of file or BLOB storage.
