# S3 Asset und S3 HG Asset Service

## Implementierungsstatus

| Komponente | Datei | Status |
| --- | --- | --- |
| MySQL Migration (256 Partitionen) | `OpenSim/Data/MySQL/Resources/S3AssetStore.migrations` | Fertig |
| Metadaten-Plugin | `OpenSim/Data/MySQL/MySQLS3AssetData.cs` | Fertig |
| Asset-Service (normales Grid) | `OpenSim/Services/S3AssetService/S3AssetConnector.cs` | Fertig |
| Asset-Service (Hypergrid) | `OpenSim/Services/HypergridService/HGS3AssetService.cs` | Fertig |
| AssemblyInfo | `OpenSim/Services/S3AssetService/Properties/AssemblyInfo.cs` | Fertig |
| Interface (IFSAssetDataPlugin) | `OpenSim/Data/IFSAssetData.cs` | Erweitert |
| MySQLFSAssetData Stubs | `OpenSim/Data/MySQL/MySQLFSAssetData.cs` | Fertig |
| PGSQLFSAssetData Stubs | `OpenSim/Data/PGSQL/PGSQLFSAssetData.cs` | Fertig |
| prebuild.xml | `prebuild.xml` | Fertig |
| HypergridService.csproj | `OpenSim/Services/HypergridService/OpenSim.Services.HypergridService.csproj` | Fertig |
| Robust.ini.example | `bin/Robust.ini.example` | Fertig |
| Robust.HG.ini.example | `bin/Robust.HG.ini.example` | Fertig |
| Build | `OpenSim.sln` | **Erfolgreich (0 Fehler, 4 CS9193-Warnungen)** |

Die 4 CS9193-Warnungen betreffen `ref readonly`-Parameter in `KeyframeMotion.cs`, `SceneObjectGroup.cs`, `Scene.Inventory.cs` und `BunchOfCaps.cs` und sind unabhaengig von dieser Implementierung.

Erzeugte Binaries: `bin/OpenSim.Services.S3AssetService.dll`, `bin/OpenSim.Services.HypergridService.dll`

Diese Konfiguration beschreibt einen Asset-Service fuer OpenSimulator, der Metadaten in MySQL und die eigentlichen Binardaten in einem S3-kompatiblen Objektspeicher ablegt.

Das Ziel ist nicht kosmetisch, sondern betrieblich notwendig: klassische Asset-Tabellen mit BLOB-Inhalt wachsen im Realbetrieb in problematische Groessen, FSAssets erzeugt extreme Mengen einzelner Dateien, und SQLite skaliert fuer solche Lasten nicht mehr sinnvoll. Die Trennung in SQL-Metadaten und S3-Objekte beseitigt genau diese Engpaesse.

## Dateiliste mit Pfaden zum implementieren in anderen Repositories

### Neue Dateien (muessen angelegt werden)

| Pfad | Beschreibung |
| --- | --- |
| `OpenSim/Data/MySQL/Resources/S3AssetStore.migrations` | MySQL-Migrationsskript, erstellt `s3assets`-Tabelle mit 256 KEY-Partitionen |
| `OpenSim/Data/MySQL/MySQLS3AssetData.cs` | Implementiert `IFSAssetDataPlugin` fuer MySQL: Metadaten lesen/schreiben, Batch-Pruefung, Migrationslogik |
| `OpenSim/Services/S3AssetService/S3AssetConnector.cs` | Vollstaendiger `IAssetService` fuer normale Grids: S3-Client (AWS SigV4), SHA-256-Dedup, Konsolen-Befehle, Fallback |
| `OpenSim/Services/S3AssetService/Properties/AssemblyInfo.cs` | Assembly-Metadaten fuer das S3AssetService-Projekt |
| `OpenSim/Services/HypergridService/HGS3AssetService.cs` | Hypergrid-Wrapper: erbt von `S3AssetConnector`, prueft Asset-Berechtigungen und passt CreatorID/SOP-XML an |

### Geaenderte Dateien (muessen angepasst werden)

| Pfad | Aenderung |
| --- | --- |
| `OpenSim/Data/IFSAssetData.cs` | Zwei neue Interface-Methoden: `ImportFromFSAssets(...)` und `ExportToLegacy(...)` |
| `OpenSim/Data/MySQL/MySQLFSAssetData.cs` | Stubs fuer die zwei neuen Interface-Methoden (werfen `NotImplementedException`) |
| `OpenSim/Data/PGSQL/PGSQLFSAssetData.cs` | Stubs fuer die zwei neuen Interface-Methoden (werfen `NotImplementedException`) |
| `prebuild.xml` | Neues `<Project>`-Element fuer `OpenSim.Services.S3AssetService` eingefuegt; `OpenSim.Services.S3AssetService`-Referenz im HypergridService-Block ergaenzt |
| `OpenSim/Services/HypergridService/OpenSim.Services.HypergridService.csproj` | `<ProjectReference>` auf `S3AssetService.csproj` ergaenzt |
| `bin/Robust.ini.example` | `S3AssetConnector` als dritte Option bei `LocalServiceModule`; vollstaendiger S3-Konfigurationsblock (auskommentiert) |
| `bin/Robust.HG.ini.example` | `HGS3AssetService` als dritte Option bei `LocalServiceModule` im `[HGAssetService]`-Block |

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

## Architektur

Der Aufbau besteht aus zwei Schichten:

1. MySQL speichert nur Metadaten wie Asset-ID, Name, Typ, Hash, Erstellungszeit und letzte Zugriffszeit.
2. S3 oder MinIO speichert den eigentlichen Datenblock als Objekt, adressiert ueber den SHA-256-Hash.

Dadurch ergeben sich mehrere Eigenschaften:

- Keine BLOB-Massen in der SQL-Tabelle.
- Keine Milliarden Einzeldateien in Dateisystem-Verzeichnissen.
- Automatische Deduplizierung, weil identischer Inhalt denselben Hash erzeugt.
- Bessere horizontale Skalierung fuer sehr grosse Asset-Bestaende.

## Warum diese Struktur sinnvoll ist

Bei grossen OpenSim-Installationen wird die Asset-Haltung schnell zum Flaschenhals.

- Eine klassische SQL-Asset-Tabelle mit BLOB-Inhalt belastet Storage, Buffer Pool, Backup-Zeiten und Replikation.
- FSAssets verlagert das Problem nur auf das Dateisystem und erzeugt dort untragbar viele Dateien und Verzeichniseintraege.
- SQLite ist fuer kleine Installationen praktisch, aber nicht fuer sehr grosse, daueraktive Asset-Last mit Milliarden Eintraegen.

Die S3-Variante trennt daher bewusst:

- relationale Daten in MySQL,
- grosse, unveraenderliche Binaerdaten in Objektspeicher.

Das ist die moderne und in grossen Systemen uebliche Speichertrennung fuer Content-Addressable Storage.

## Konsolen-Befehle im laufenden Betrieb

Nach dem Start sind folgende Befehle in der Robust-Konsole verfuegbar:

| Befehl | Funktion |
| --- | --- |
| `s3 show assets` | Zeigt Reads, Writes, Dedup-Hits, Misses und geschaetzte DB-Zeilenzahl |
| `s3 import assets <conn> <table> [start count]` | Migriert Assets aus klassischer MySQL-`assets`-Tabelle (mit BLOB) nach S3 |
| `s3 import fsassets <fsbase> <conn> <table>` | Migriert Assets aus FSAssets-Verzeichnisbaum + MySQL-Hashtabelle nach S3 |
| `s3 export assets <conn> <table>` | Rollback: Exportiert alle S3-Assets zurueck in eine klassische `assets`-Tabelle |

Diese Befehle laufen synchron in der Konsole und protokollieren alle 10.000 Eintraege den Fortschritt im Log.

## Service aktivieren

In `Robust.ini` oder `Robust.HG.ini` im Abschnitt `[AssetService]`:

```ini
;; Aktuelle Zeile auskommentieren:
;LocalServiceModule = "OpenSim.Services.AssetService.dll:AssetService"

;; Diese Zeile aktivieren:
LocalServiceModule = "OpenSim.Services.S3AssetService.dll:S3AssetConnector"
```

Dann die S3-Parameter setzen (siehe Konfigurationsbeispiel unten).

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

## Bedeutung der Parameter

`StorageProvider`
Legt fest, welcher Datenbank-Provider fuer die Metadaten verwendet wird. In diesem Fall ist das der MySQL-Provider.

`ConnectionString`
Verbindungszeichenfolge zur MySQL-Datenbank, in der die Tabelle fuer die Asset-Metadaten liegt.

`Realm`
Name der Tabelle oder des logischen Speicherbereichs fuer die Metadaten. Hier wird `s3assets` verwendet.

`S3Endpoint`
URL des S3-kompatiblen Storage-Endpunkts. Das kann AWS S3, MinIO oder ein anderer kompatibler Dienst sein.

`S3Bucket`
Bucket, in dem die Binardaten der Assets gespeichert werden.

`S3AccessKey` und `S3SecretKey`
Zugangsdaten fuer den Objektspeicher. Diese sollten in produktiven Umgebungen nicht hartkodiert in allgemein sichtbaren Dateien liegen.

`S3Region`
AWS-kompatible Region fuer die Signaturerzeugung. Bei MinIO wird haeufig trotzdem `us-east-1` verwendet.

`DaysBetweenAccessTimeUpdates`
Begrenzt, wie oft `access_time` in der Datenbank aktualisiert wird. Das verhindert unnoetige Schreiblast bei haeufig gelesenen Assets.

## Datenfluss beim Speichern

Beim Speichern eines Assets laeuft der Prozess logisch so ab:

1. Der Inhalt wird gehasht.
2. Es wird geprueft, ob ein Objekt mit diesem Hash bereits in S3 vorhanden ist.
3. Falls nein, wird der Datenblock in den Bucket geschrieben.
4. Die Metadaten werden in MySQL gespeichert oder aktualisiert.

Der Hash dient dabei als stabiler Objektschluessel. Mehrfach identischer Inhalt fuehrt deshalb nicht zu mehrfach gespeicherten Binaerdaten.

## Datenfluss beim Lesen

Beim Lesen eines Assets passiert das Gegenteil:

1. Die Metadaten werden ueber die Asset-ID aus MySQL geladen.
2. Aus den Metadaten wird der Inhalts-Hash gelesen.
3. Das passende Objekt wird ueber den Hash aus S3 geladen.
4. Optional wird die Zugriffszeit aktualisiert, aber nur gemaess `DaysBetweenAccessTimeUpdates`.

## Skalierungshinweise

Diese Loesung ist fuer sehr grosse Datenmengen ausgelegt, wenn ein paar Grundregeln eingehalten werden:

- Die MySQL-Tabelle fuer Metadaten sollte partitioniert sein.
- Der Bucket sollte auf Storage liegen, der grosse Objektzahlen dauerhaft tragen kann.
- Backups der Metadaten und der Objekte muessen getrennt, aber abgestimmt geplant werden.
- Loeschungen sollten nicht sofort physisch jedes Objekt entfernen, wenn Deduplizierung genutzt wird.

Wichtig ist dabei: die S3-Ebene skaliert Objektmengen deutlich besser als klassische Dateisystem-Verzeichnisbaeume.

## Betriebspraktische Hinweise

- MinIO ist fuer lokale Installationen, private Cluster und Tests oft die praktikabelste Wahl.
- Fuer Produktion sollten TLS, getrennte Credentials und ein eigenes Bucket-Lifecycle-Konzept verwendet werden.
- MySQL sollte fuer den erwarteten Metadatenbestand dimensioniert werden, insbesondere InnoDB Buffer Pool, Redo Log und Storage-Layout.
- Der Objektstore muss nicht dieselbe Maschine wie die Sim laufen, was die Lastverteilung deutlich verbessert.

## Grenzen dieser Konfiguration

Folgendes ist noch nicht implementiert und muss bei Bedarf ergaenzt werden:

- **Garbage Collection fuer S3-Objekte**: `Delete()` entfernt nur Metadaten. Hashes ohne Metadaten-Referenz bleiben in S3 bestehen. Fuer Produktionsbetrieb wird ein separater Offline-Job benoetigt, der `s3assets`-Hashes mit Bucket-Inhalten abgleicht.
- **Monitoring**: Kein Prometheus/Grafana-Endpunkt. Die Statistiken sind nur ueber die Konsole (`s3 show assets`) und den Log-Thread (alle 60 Sekunden) abrufbar.
- **Backup-Strategie**: MySQL-Dump und S3-Bucket-Backup muessen aufeinander abgestimmt sein. Ein Asset das in MySQL vorhanden, aber im Bucket fehlt, wuerde beim Lesen einen leeren Datenblock liefern.

## Migration zu S3

Eine Migration ist noetig, wenn ein bestehender Server von einer der klassischen Asset-Varianten auf S3 umgestellt wird. Dabei gibt es drei Ausgangszustaende:

### Ausgang: MySQL-Tabelle `assets` (klassisch, mit BLOB)

Die Tabelle `assets` enthaelt den Binaerinhalt direkt in der Spalte `data`. Ein Migrationslauf liest alle Zeilen aus dieser Tabelle, schreibt den Inhalt als Objekt in S3 und legt die Metadaten in `s3assets` ab.

Vorbereitung:

```sql
-- Schrittweises Lesen empfohlen, nicht SELECT * auf einmal
SELECT id, name, description, assetType, data, create_time, access_time
FROM assets
ORDER BY id
LIMIT 10000 OFFSET 0;
```

Ablauf:

1. OpenSim stoppen oder in einen Read-only-Wartungsmodus bringen.
2. Migrationsscript startet und liest Asset-Zeilen in Batches.
3. Pro Zeile: SHA-256 ueber `data` berechnen, Inhalt per PUT in S3 schreiben, Metadaten in `s3assets` eintragen.
4. Nach Abschluss: Konfiguration auf S3AssetConnector umstellen.
5. Optional: Alte `assets`-Tabelle nach Validierung loeschen oder archivieren.

Der eingebaute `Import()`-Aufruf in `MySQLS3AssetData.cs` unterstuetzt diesen Weg direkt und liest aus der alten Tabelle in die neue Struktur.

### Ausgang: FSAssets (Dateisystem + MySQL-Hash-Tabelle)

FSAssets legt die Binaerdaten als `.gz`-Dateien im Dateisystem ab und fuehrt eine Hashtabelle in MySQL. Ein Migrationslauf geht die `fsassets`-Tabelle durch, liest die zugehoerige komprimierte Datei, dekomprimiert sie und schreibt den Inhalt nach S3.

Ablauf:

1. FSAssets-Verzeichnisstruktur erreichbar halten.
2. Alle Eintraege aus der `fsassets`-MySQL-Tabelle lesen.
3. Pro Eintrag: Dateipfad aus Hash berechnen, `.gz` einlesen, entpacken, nach S3 schreiben, Metadaten in `s3assets` eintragen.
4. Konfiguration umstellen.

Wichtig: Viele FSAssets-Installationen haben Hunderttausende bis Hunderte Millionen Dateien. Die Migration braucht Batchverarbeitung, Checkpoint-Logik und ein Resume-Feature fuer den Fall von Unterbrechungen.

### Ausgang: SQLite

SQLite-Asset-Datenbanken koennen direkt wie die klassische MySQL-`assets`-Tabelle behandelt werden. Ein geeignetes Tool wie `System.Data.SQLite` liest die Zeilen aus, der Rest ist identisch.

---

## Migration zurueck von S3

Eine Rueckmigration ist dann noetig, wenn der S3-Betrieb aufgegeben wird oder ein Rollback auf ein klassisches Setup notwendig ist. Sie ist aufwendiger als die Hinmigration, weil der Binaerinhalt wieder in die relationale Tabelle eingebettet werden muss.

### Ziel: MySQL-Tabelle `assets` (mit BLOB)

1. Alle Eintraege aus `s3assets` lesen.
2. Pro Eintrag: Hash-Objekt aus S3 laden, als `data`-BLOB in `assets` schreiben.
3. Metadaten wie Name, Typ, Erstellungszeit und Flags aus `s3assets` uebernehmen.
4. Konfiguration auf klassischen AssetService zurueckstellen.

```sql
-- Zieltabelle anlegen falls nicht vorhanden
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

Danach fuer jedes Asset per Anwendungscode:

```text
hash = s3assets.hash WHERE id = ?
data = S3.Get(hash)
INSERT INTO assets (id, name, description, ..., data) VALUES (...)
```

### Ziel: FSAssets

1. Alle Eintraege aus `s3assets` lesen.
2. Pro Eintrag: Objekt aus S3 laden, mit gzip komprimieren, als Datei im FSAssets-Verzeichnisbaum ablegen.
3. Eintrag in der `fsassets`-MySQL-Tabelle anlegen.

Hinweis: Der Verzeichnisbaum von FSAssets wird dabei wieder mit sehr vielen Dateien gefuellt. Dieses Vorgehen funktioniert technisch, reproduziert aber genau die Skalierungsprobleme, die durch die Migration zu S3 geloest wurden.

### Rueckmigration allgemeine Hinweise

- Rueckmigrationen sollten stets auf einer Staging-Umgebung validiert werden.
- Asset-Count in Quell- und Zielsystem muessen nach Abschluss uebereinstimmen.
- SHA-256-Pruefung beim Einlesen aus S3 und beim Schreiben ins Ziel vermeidet stille Datenfehler.
- OpenSim sollte waehrend einer vollstaendigen Rueckmigration gestoppt sein, da neue Assets sonst nur in S3 ankommen, nicht aber in der Zieltabelle.

---

## Kurzfazit

Diese Architektur ist fuer grosse OpenSim-Installationen deutlich geeigneter als BLOB-lastige SQL-Tabellen, FSAssets oder SQLite-basierte Asset-Haltung. Sie trennt Metadaten und Inhaltsdaten sauber, reduziert Duplikate und vermeidet die typischen Skalierungsprobleme klassischer Datei- oder BLOB-Speicherung.
