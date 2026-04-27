# Service d'Assets S3 et S3 HG Asset

## Etat d'Implementation

| Composant | Fichier | Statut |
| --- | --- | --- |
| Migration MySQL (256 partitions) | `OpenSim/Data/MySQL/Resources/S3AssetStore.migrations` | Termine |
| Plugin de metadonnees | `OpenSim/Data/MySQL/MySQLS3AssetData.cs` | Termine |
| Service d'assets (grid normal) | `OpenSim/Services/S3AssetService/S3AssetConnector.cs` | Termine |
| Service d'assets (Hypergrid) | `OpenSim/Services/HypergridService/HGS3AssetService.cs` | Termine |
| AssemblyInfo | `OpenSim/Services/S3AssetService/Properties/AssemblyInfo.cs` | Termine |
| Interface (IFSAssetDataPlugin) | `OpenSim/Data/IFSAssetData.cs` | Etendu |
| Stubs MySQLFSAssetData | `OpenSim/Data/MySQL/MySQLFSAssetData.cs` | Termine |
| Stubs PGSQLFSAssetData | `OpenSim/Data/PGSQL/PGSQLFSAssetData.cs` | Termine |
| prebuild.xml | `prebuild.xml` | Termine |
| HypergridService.csproj | `OpenSim/Services/HypergridService/OpenSim.Services.HypergridService.csproj` | Termine |
| Robust.ini.example | `bin/Robust.ini.example` | Termine |
| Robust.HG.ini.example | `bin/Robust.HG.ini.example` | Termine |
| Build | `OpenSim.sln` | **Reussi (0 erreur, 4 avertissements CS9193)** |

Les 4 avertissements CS9193 concernent des parametres `ref readonly` dans `KeyframeMotion.cs`, `SceneObjectGroup.cs`, `Scene.Inventory.cs` et `BunchOfCaps.cs`, et sont independants de cette implementation.

Binaires generes: `bin/OpenSim.Services.S3AssetService.dll`, `bin/OpenSim.Services.HypergridService.dll`

Cette configuration decrit un service d'assets OpenSimulator qui stocke les metadonnees dans MySQL et les donnees binaires dans un stockage objet compatible S3.

Ce n'est pas un changement cosmetique mais une necessite d'exploitation: les tables d'assets classiques avec BLOB deviennent problematiques a grande echelle, FSAssets cree des quantites extremes de fichiers, et SQLite ne passe plus a l'echelle pour cette charge. La separation metadonnees SQL / objets S3 supprime ces goulets d'etranglement.

## Liste des Fichiers et Chemins pour Implementer dans d'Autres Repositories

### Nouveaux fichiers (a creer)

| Chemin | Description |
| --- | --- |
| `OpenSim/Data/MySQL/Resources/S3AssetStore.migrations` | Script de migration MySQL qui cree la table `s3assets` avec 256 partitions KEY |
| `OpenSim/Data/MySQL/MySQLS3AssetData.cs` | Implemente `IFSAssetDataPlugin` pour MySQL: lecture/ecriture metadonnees, verification batch, logique de migration |
| `OpenSim/Services/S3AssetService/S3AssetConnector.cs` | `IAssetService` complet pour grids normaux: client S3 (AWS SigV4), dedup SHA-256, commandes console, fallback |
| `OpenSim/Services/S3AssetService/Properties/AssemblyInfo.cs` | Metadonnees d'assembly pour le projet S3AssetService |
| `OpenSim/Services/HypergridService/HGS3AssetService.cs` | Wrapper Hypergrid: herite de `S3AssetConnector`, verifie les permissions d'assets et adapte CreatorID/SOP XML |

### Fichiers modifies (a adapter)

| Chemin | Modification |
| --- | --- |
| `OpenSim/Data/IFSAssetData.cs` | Deux nouvelles methodes d'interface: `ImportFromFSAssets(...)` et `ExportToLegacy(...)` |
| `OpenSim/Data/MySQL/MySQLFSAssetData.cs` | Stubs pour les deux nouvelles methodes d'interface (levent `NotImplementedException`) |
| `OpenSim/Data/PGSQL/PGSQLFSAssetData.cs` | Stubs pour les deux nouvelles methodes d'interface (levent `NotImplementedException`) |
| `prebuild.xml` | Ajout d'un nouvel element `<Project>` pour `OpenSim.Services.S3AssetService`; ajout de la reference `OpenSim.Services.S3AssetService` dans le bloc HypergridService |
| `OpenSim/Services/HypergridService/OpenSim.Services.HypergridService.csproj` | Ajout de `<ProjectReference>` vers `S3AssetService.csproj` |
| `bin/Robust.ini.example` | Ajout de `S3AssetConnector` comme troisieme option de `LocalServiceModule`; bloc de configuration S3 complet commente |
| `bin/Robust.HG.ini.example` | Ajout de `HGS3AssetService` comme troisieme option de `LocalServiceModule` dans `[HGAssetService]` |

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

L'architecture comporte deux couches:

1. MySQL stocke uniquement les metadonnees: ID d'asset, nom, type, hash, date de creation et dernier acces.
2. S3 ou MinIO stocke le bloc binaire sous forme d'objet, adresse par le hash SHA-256.

Cela apporte plusieurs proprietes:

- Pas de masses BLOB dans la table SQL.
- Pas de milliards de fichiers unitaires dans les repertoires du systeme de fichiers.
- Deduplication automatique, car un contenu identique produit le meme hash.
- Meilleure scalabilite horizontale pour de tres grands volumes d'assets.

## Pourquoi cette Structure est Pertinente

Dans les grandes installations OpenSim, le stockage d'assets devient vite un goulot d'etranglement.

- Une table SQL classique avec BLOB surcharge stockage, buffer pool, sauvegardes et replication.
- FSAssets deplace le probleme vers le systeme de fichiers et produit des volumes de fichiers ingarables.
- SQLite est pratique pour de petites installations, mais pas pour des charges d'assets massives et permanentes.

L'approche S3 separe volontairement:

- les donnees relationnelles dans MySQL,
- les donnees binaires volumineuses et immuables dans un stockage objet.

C'est la separation moderne generalement adoptee pour les stockages adressables par contenu a grande echelle.

## Commandes Console en Exploitation

Apres le demarrage, les commandes suivantes sont disponibles dans la console Robust:

| Commande | Fonction |
| --- | --- |
| `s3 show assets` | Affiche reads, writes, dedup hits, misses et estimation du nombre de lignes DB |
| `s3 import assets <conn> <table> [start count]` | Migre les assets depuis la table MySQL classique `assets` (avec BLOB) vers S3 |
| `s3 import fsassets <fsbase> <conn> <table>` | Migre les assets depuis l'arborescence FSAssets + table de hash MySQL vers S3 |
| `s3 export assets <conn> <table>` | Rollback: exporte tous les assets S3 vers une table classique `assets` |

Ces commandes s'executent de maniere synchrone dans la console et journalisent la progression tous les 10 000 elements.

## Activer le Service

Dans `Robust.ini` ou `Robust.HG.ini` dans la section `[AssetService]`:

```ini
;; Commenter la ligne actuelle:
;LocalServiceModule = "OpenSim.Services.AssetService.dll:AssetService"

;; Activer cette ligne:
LocalServiceModule = "OpenSim.Services.S3AssetService.dll:S3AssetConnector"
```

Ensuite, definir les parametres S3 (voir l'exemple ci-dessous).

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

## Signification des Parametres

`StorageProvider`
Definit le provider de base de donnees pour les metadonnees. Ici: MySQL.

`ConnectionString`
Chaine de connexion a la base MySQL qui contient la table de metadonnees d'assets.

`Realm`
Nom de table ou espace logique de stockage pour les metadonnees. Ici: `s3assets`.

`S3Endpoint`
URL du point d'acces compatible S3. Peut etre AWS S3, MinIO ou un autre service compatible.

`S3Bucket`
Bucket qui stocke les donnees binaires des assets.

`S3AccessKey` et `S3SecretKey`
Identifiants du stockage objet. En production, ils ne doivent pas etre en dur dans des fichiers largement visibles.

`S3Region`
Region compatible AWS pour la generation de signature. Avec MinIO, `us-east-1` est souvent utilise.

`DaysBetweenAccessTimeUpdates`
Limite la frequence de mise a jour de `access_time` en base, pour reduire les ecritures inutiles sur les assets souvent lus.

## Flux de Donnees a l'Ecriture

Lors de l'enregistrement d'un asset:

1. Le contenu est hashe.
2. Le systeme verifie si cet objet hash existe deja dans S3.
3. Sinon, le bloc binaire est envoye dans le bucket.
4. Les metadonnees sont inserees ou mises a jour dans MySQL.

Le hash sert de cle objet stable. Un contenu identique n'est donc stocke qu'une seule fois.

## Flux de Donnees a la Lecture

Lors de la lecture d'un asset:

1. Les metadonnees sont chargees depuis MySQL via l'ID d'asset.
2. Le hash de contenu est lu depuis les metadonnees.
3. L'objet correspondant est charge depuis S3 via ce hash.
4. Le temps d'acces peut etre mis a jour selon `DaysBetweenAccessTimeUpdates`.

## Notes de Scalabilite

Cette solution est prevue pour de tres grands volumes, en respectant quelques regles:

- La table MySQL de metadonnees doit etre partitionnee.
- Le bucket doit reposer sur un stockage capable de supporter un grand nombre d'objets sur la duree.
- Les sauvegardes metadonnees et objets doivent etre separees mais coordonnees.
- Les suppressions ne doivent pas toujours effacer physiquement les objets immediatement quand la deduplication est activee.

Important: la couche S3 passe mieux a l'echelle sur le nombre d'objets que les arborescences de fichiers classiques.

## Notes d'Exploitation

- MinIO est souvent le choix le plus pratique pour installations locales, clusters prives et tests.
- En production, utiliser TLS, identifiants separes et une strategie lifecycle de bucket.
- MySQL doit etre dimensionne pour le volume de metadonnees attendu, notamment buffer pool InnoDB, redo log et disposition stockage.
- Le stockage objet peut etre separe de la machine de simulation, ce qui ameliore la repartition de charge.

## Limites de cette Configuration

Les points suivants ne sont pas encore implementes et peuvent etre ajoutes selon les besoins:

- **Garbage collection des objets S3**: `Delete()` supprime seulement les metadonnees. Les objets hash sans reference en base restent dans S3. Un job offline dedie est requis en production pour comparer les hashes `s3assets` avec le contenu du bucket.
- **Monitoring**: Pas d'endpoint Prometheus/Grafana pour l'instant. Les statistiques sont disponibles via la console (`s3 show assets`) et le thread de log toutes les 60 secondes.
- **Strategie de backup**: Le dump MySQL et le backup du bucket S3 doivent etre coordonnes. Si la metadonnee existe mais pas l'objet bucket, la lecture renverra une charge vide.

## Migration vers S3

Une migration est necessaire lorsqu'un serveur existant passe d'un backend d'assets classique a S3. Trois situations de depart existent.

### Depart: table MySQL `assets` (classique, avec BLOB)

La table `assets` contient le binaire directement dans la colonne `data`. La migration lit les lignes, ecrit le contenu dans S3, puis enregistre les metadonnees dans `s3assets`.

Preparation:

```sql
-- Lecture par batches recommandee, pas de SELECT * en une fois
SELECT id, name, description, assetType, data, create_time, access_time
FROM assets
ORDER BY id
LIMIT 10000 OFFSET 0;
```

Procedure:

1. Arreter OpenSim ou passer en mode maintenance lecture seule.
2. Lancer le script de migration et lire les assets par lots.
3. Pour chaque ligne: calcul SHA-256 sur `data`, PUT vers S3, insertion metadonnees dans `s3assets`.
4. A la fin, basculer la configuration vers S3AssetConnector.
5. Optionnel: supprimer ou archiver l'ancienne table `assets` apres validation.

L'appel integre `Import()` dans `MySQLS3AssetData.cs` supporte directement ce cas.

### Depart: FSAssets (systeme de fichiers + table de hash MySQL)

FSAssets stocke les donnees binaires en fichiers `.gz` et maintient une table de hash en MySQL. La migration parcourt `fsassets`, lit chaque fichier compresse, le decompresse, puis l'ecrit vers S3.

Procedure:

1. Garder l'arborescence FSAssets accessible.
2. Lire toutes les entrees de la table MySQL `fsassets`.
3. Pour chaque entree: calcul du chemin depuis le hash, lecture `.gz`, decompression, ecriture S3, insertion metadonnees dans `s3assets`.
4. Basculer la configuration.

Important: de nombreux deploiements FSAssets ont de centaines de milliers a des centaines de millions de fichiers. La migration doit inclure traitement par lots, checkpoints et reprise apres interruption.

### Depart: SQLite

Les bases d'assets SQLite peuvent etre traitees comme une table MySQL `assets` classique avec BLOB. Un outil comme `System.Data.SQLite` lit les lignes; le reste est identique.

---

## Migration retour depuis S3

Une migration retour est necessaire si l'exploitation S3 est abandonnee ou en cas de rollback vers une architecture classique. Elle est plus lourde que la migration aller, car le binaire doit etre reintegre en table relationnelle.

### Cible: table MySQL `assets` (avec BLOB)

1. Lire toutes les entrees de `s3assets`.
2. Pour chaque entree: charger l'objet S3 via le hash et l'ecrire dans `assets.data`.
3. Reprendre les metadonnees (nom, type, dates, flags) depuis `s3assets`.
4. Revenir a la configuration AssetService classique.

```sql
-- Creer la table cible si absente
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

Ensuite, pour chaque asset en code applicatif:

```text
hash = s3assets.hash WHERE id = ?
data = S3.Get(hash)
INSERT INTO assets (id, name, description, ..., data) VALUES (...)
```

### Cible: FSAssets

1. Lire toutes les entrees de `s3assets`.
2. Pour chaque entree: charger l'objet depuis S3, le compresser en gzip, puis l'ecrire dans l'arborescence FSAssets.
3. Inserer l'entree correspondante dans la table MySQL `fsassets`.

Remarque: cette methode regonfle FSAssets avec de tres grands volumes de fichiers et reproduit les memes problemes de scalabilite que la migration vers S3 corrige.

### Notes generales pour migration retour

- Toujours valider en environnement de staging.
- Le nombre d'assets source et cible doit correspondre apres migration.
- Une verification SHA-256 a la lecture S3 et a l'ecriture cible evite des corruptions silencieuses.
- OpenSim doit etre arrete pendant une migration retour complete, sinon de nouveaux assets peuvent arriver uniquement dans S3 et pas dans la cible.

---

## Conclusion Courte

Pour les grandes installations OpenSim, cette architecture est nettement plus adaptee que les tables SQL chargees en BLOB, FSAssets ou le stockage SQLite. Elle separe proprement metadonnees et contenu, reduit les doublons et evite les limites de passage a l'echelle des stockages fichiers ou BLOB.
