# Konfiguration S3AssetService

## Was ist ein S3Asset Server und kann ich selbst einen betreiben

Ein S3-Asset-Server ist ein Objektspeicher, der das S3-Protokoll (Amazon Simple Storage Service) implementiert.
Er speichert grosse, unveraenderliche Datenbloecke (die eigentlichen Asset-Inhalte wie Texturen, Sounds, Objekte) unter einem
inhaltsbasierten Schluessel (SHA-256-Hash), waehrend die Metadaten (Asset-ID, Name, Typ usw.) weiterhin in MySQL liegen.

Ja, du kannst einen solchen Server selbst betreiben. Die gaengigste selbstgehostete Loesung ist **MinIO**, ein freier,
S3-kompatibler Objektspeicher, der unter Linux, Windows und in Docker lauft. Alternativ kann auch jeder andere
S3-kompatibler Dienst (z.B. AWS S3, Wasabi, Backblaze B2) verwendet werden.

## Was wird fuer einen S3-Asset-Server benoetigt

**Minimal (lokal / Testbetrieb):**

- Ein Rechner oder eine VM (Linux empfohlen, z.B. Ubuntu 22.04 LTS)
- [MinIO](https://min.io/) als S3-kompatibler Objektspeicher
- MySQL oder MariaDB fuer die Asset-Metadaten
- OpenSimulator mit eingespieltem S3AssetService-Plugin

**Minimale Hardware fuer MinIO:**

| Ressource | Minimum | Empfehlung Produktion |
| --- | --- | --- |
| CPU | 2 Kerne | 4+ Kerne |
| RAM | 2 GB | 8+ GB |
| Storage | SSD empfohlen | NVMe, RAID oder verteiltes Storage |
| Netzwerk | 100 Mbit/s | 1 Gbit/s |

**MinIO Quick-Start unter Ubuntu:**

```bash
# MinIO herunterladen
wget https://dl.min.io/server/minio/release/linux-amd64/minio
chmod +x minio
sudo mv minio /usr/local/bin/

# Datenverzeichnis anlegen
sudo mkdir -p /data/minio

# Als Service starten (einfachste Variante)
MINIO_ROOT_USER=minioadmin MINIO_ROOT_PASSWORD=minioadmin \
  minio server /data/minio --console-address ":9001"
```

Danach ist MinIO erreichbar unter:

- S3-API: `http://localhost:9000`
- Web-Konsole: `http://localhost:9001`

Bucket `opensim-assets` ueber die Web-Konsole oder per `mc`-Client anlegen:

```bash
mc alias set local http://localhost:9000 minioadmin minioadmin
mc mb local/opensim-assets
```

## Konfiguration bei einem oder mehreren S3Asset-Servern

### Robust.ini (normales Grid ohne Hypergrid)

Im Abschnitt `[AssetService]` in `Robust.ini`:

```ini
[AssetService]

    ;; Aktive Option auswaehlen (nur eine aktivieren):
    ;LocalServiceModule = "OpenSim.Services.AssetService.dll:AssetService"
    ;LocalServiceModule = "OpenSim.Services.FSAssetService.dll:FSAssetConnector"
    LocalServiceModule = "OpenSim.Services.S3AssetService.dll:S3AssetConnector"

    ;; S3AssetService -- skalierbarer Asset-Service fuer sehr grosse Installationen.
    ;; Metadaten in MySQL (256 KEY-Partitionen), Binaerdaten in S3/MinIO.
    ;; Loest BLOB-Wachstum, FSAssets-Inode-Probleme und SQLite-Limits gleichzeitig.
    ;; Konsole: 's3 show assets', 's3 import assets', 's3 import fsassets', 's3 export assets'
    S3Endpoint   = "http://minio:9000"
    S3Bucket     = "opensim-assets"
    S3AccessKey  = "minioadmin"
    S3SecretKey  = "minioadmin"
    S3Region     = "us-east-1"

    ;; Bei S3AssetService: StorageProvider + ConnectionString + Realm benoetigt
    StorageProvider  = "OpenSim.Data.MySQL.dll"
    ConnectionString = "Data Source=localhost;Database=opensim;User ID=opensim;Password=DEINPASSWORT;Old Guids=true;SslMode=None;"
    Realm            = "s3assets"
    DaysBetweenAccessTimeUpdates = 30
    ShowConsoleStats = true

    DefaultAssetLoader = "OpenSim.Framework.AssetLoader.Filesystem.dll"
    AssetLoaderArgs = "./assets/AssetSets.xml"

    AllowRemoteDelete = false
    AllowRemoteDeleteAllTypes = false
```

### Robust.HG.ini (Grid mit Hypergrid-Unterstuetzung)

Im Abschnitt `[AssetService]` denselben S3-Block wie oben verwenden.
Zusaetzlich im Abschnitt `[HGAssetService]` den HG-spezifischen S3-Service aktivieren:

```ini
[HGAssetService]
    ;; Aktive Option auswaehlen (nur eine aktivieren):
    ;LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGAssetService"
    ;LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGFSAssetService"
    LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGS3AssetService"

    UserAccountsService = "OpenSim.Services.UserAccountService.dll:UserAccountService"

    ;; Oeffentlicher Dienst -- Authentifizierung nicht erzwingen
    AuthType = None

    ;; HomeURI des eigenen Grids (muss mit [Hypergrid] uebereinstimmen)
    ; HomeURI = "http://mygrid.example.com:8002"

    ;; Asset-Typen, die exportiert/importiert werden duerfen.
    ;; Leer lassen = keine Einschraenkung.
    ;; Beispiel: Skripte sperren:
    ; DisallowExport = "LSLText"
    ; DisallowImport = "LSLBytecode"
```

### Mehrere S3-Instanzen (horizontale Skalierung)

Bei mehr als einer Robust-Instanz, die denselben S3-Bucket und dieselbe MySQL-Datenbank verwenden,
muss in jeder sekundaeren Instanz `SecondaryInstance = true` gesetzt werden, damit nur eine Instanz
Hintergrundaufgaben (z.B. Zugriffszeitaktualisierung, Statistik-Thread) ausfuehrt:

```ini
[AssetService]
    LocalServiceModule = "OpenSim.Services.S3AssetService.dll:S3AssetConnector"
    SecondaryInstance = true
    ; ... restliche S3-Parameter wie oben
```

Bucket und MySQL-Datenbank koennen ohne weitere Aenderungen von allen Instanzen gemeinsam genutzt werden,
da S3 selbst zustandslos und skalierbar ist und MySQL die Serialisierung der Metadaten uebernimmt.
