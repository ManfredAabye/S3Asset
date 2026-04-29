# Configuration du S3AssetService

## Qu'est-ce qu'un serveur S3 Asset et puis-je en exploiter un moi-même

Un serveur S3 Asset est un magasin d'objets qui implémente le protocole S3 (Amazon Simple Storage Service).
Il stocke de grands blocs de données immuables (le contenu réel des assets : textures, sons, objets) sous une
clé basée sur le contenu (hash SHA-256), tandis que les métadonnées (ID d'asset, nom, type, etc.) restent dans MySQL.

Oui, vous pouvez exploiter un tel serveur vous-même. La solution auto-hébergée la plus courante est **MinIO**, un
magasin d'objets gratuit et compatible S3, qui fonctionne sous Linux, Windows et dans Docker. Tout autre service
compatible S3 (p. ex. AWS S3, Wasabi, Backblaze B2) peut également être utilisé.

## Ce dont vous avez besoin pour un serveur S3 Asset

**Minimum (local / test) :**

- Une machine ou une VM (Linux recommandé, p. ex. Ubuntu 22.04 LTS)
- [MinIO](https://min.io/) comme magasin d'objets compatible S3
- MySQL ou MariaDB pour les métadonnées des assets
- OpenSimulator avec le plugin S3AssetService installé

**Matériel minimal pour MinIO :**

| Ressource | Minimum | Recommandation production |
| --- | --- | --- |
| CPU | 2 cœurs | 4+ cœurs |
| RAM | 2 Go | 8+ Go |
| Stockage | SSD recommandé | NVMe, RAID ou stockage distribué |
| Réseau | 100 Mbit/s | 1 Gbit/s |

**Démarrage rapide MinIO sous Ubuntu :**

```bash
# Télécharger MinIO
wget https://dl.min.io/server/minio/release/linux-amd64/minio
chmod +x minio
sudo mv minio /usr/local/bin/

# Créer le répertoire de données
sudo mkdir -p /data/minio

# Démarrer en tant que service (variante la plus simple)
MINIO_ROOT_USER=minioadmin MINIO_ROOT_PASSWORD=minioadmin \
  minio server /data/minio --console-address ":9001"
```

MinIO est ensuite accessible à :

- API S3 : `http://localhost:9000`
- Console web : `http://localhost:9001`

Créer le bucket `opensim-assets` via la console web ou avec le client `mc` :

```bash
mc alias set local http://localhost:9000 minioadmin minioadmin
mc mb local/opensim-assets
```

## Configuration pour un ou plusieurs serveurs S3 Asset

### Robust.ini (grid standard sans Hypergrid)

Dans la section `[AssetService]` de `Robust.ini` :

```ini
[AssetService]

    ;; Choisir un service d'assets (une seule option doit être activée) :
    ;LocalServiceModule = "OpenSim.Services.AssetService.dll:AssetService"
    ;LocalServiceModule = "OpenSim.Services.FSAssetService.dll:FSAssetConnector"
    LocalServiceModule = "OpenSim.Services.S3AssetService.dll:S3AssetConnector"

    ;; S3AssetService -- service d'assets évolutif pour les très grandes installations.
    ;; Métadonnées dans MySQL (256 partitions KEY), données binaires dans S3/MinIO.
    ;; Résout la croissance des BLOB, les problèmes d'inodes FSAssets et les limites SQLite simultanément.
    ;; Console : 's3 show assets', 's3 import assets', 's3 import fsassets', 's3 export assets'
    S3Endpoint   = "http://minio:9000"
    S3Bucket     = "opensim-assets"
    S3AccessKey  = "minioadmin"
    S3SecretKey  = "minioadmin"
    S3Region     = "us-east-1"

    ;; S3AssetService nécessite StorageProvider + ConnectionString + Realm
    StorageProvider  = "OpenSim.Data.MySQL.dll"
    ConnectionString = "Data Source=localhost;Database=opensim;User ID=opensim;Password=VOTREMOTDEPASSE;Old Guids=true;SslMode=None;"
    Realm            = "s3assets"
    DaysBetweenAccessTimeUpdates = 30
    ShowConsoleStats = true

    DefaultAssetLoader = "OpenSim.Framework.AssetLoader.Filesystem.dll"
    AssetLoaderArgs = "./assets/AssetSets.xml"

    AllowRemoteDelete = false
    AllowRemoteDeleteAllTypes = false
```

### Robust.HG.ini (grid avec support Hypergrid)

Utiliser le même bloc S3 que ci-dessus dans la section `[AssetService]`.
En plus, activer le service S3 spécifique HG dans la section `[HGAssetService]` :

```ini
[HGAssetService]
    ;; Choisir une option (une seule doit être activée) :
    ;LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGAssetService"
    ;LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGFSAssetService"
    LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGS3AssetService"

    UserAccountsService = "OpenSim.Services.UserAccountService.dll:UserAccountService"

    ;; Service public -- ne pas imposer l'authentification
    AuthType = None

    ;; HomeURI de votre grid (doit correspondre à [Hypergrid])
    ; HomeURI = "http://mygrid.example.com:8002"

    ;; Types d'assets pouvant être exportés/importés.
    ;; Laisser vide pour aucune restriction.
    ;; Exemple : bloquer les scripts :
    ; DisallowExport = "LSLText"
    ; DisallowImport = "LSLBytecode"
```

### Plusieurs instances S3 (mise à l'échelle horizontale)

Lorsque plusieurs instances Robust partagent le même bucket S3 et la même base de données MySQL,
définir `SecondaryInstance = true` dans chaque instance secondaire afin qu'une seule instance
exécute les tâches en arrière-plan (p. ex. mise à jour du temps d'accès, thread de statistiques) :

```ini
[AssetService]
    LocalServiceModule = "OpenSim.Services.S3AssetService.dll:S3AssetConnector"
    SecondaryInstance = true
    ; ... autres paramètres S3 comme ci-dessus
```

Le bucket et la base de données MySQL peuvent être partagés par toutes les instances sans modification supplémentaire,
car S3 lui-même est sans état et évolutif, et MySQL gère la sérialisation des métadonnées.
