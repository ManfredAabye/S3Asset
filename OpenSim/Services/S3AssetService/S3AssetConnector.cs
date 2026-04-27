/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

/*
 * S3AssetConnector – skalierbarer Asset-Service für OpenSimulator
 * ================================================================
 *
 * Architektur:
 *   Metadaten  → MySQL (PARTITION BY KEY, 256 Partitionen)
 *               Tabelle enthält NUR UUID, Name, Typ, Hash, Timestamps.
 *               Skaliert auf 25+ Milliarden Einträge ohne BLOB.
 *
 *   Binärdaten → S3-kompatibler Object-Store (MinIO, AWS S3, Ceph RGW …)
 *               Objekt-Key = SHA-256-Hash des Inhalts → automatische
 *               Deduplizierung: identische Texturen/Meshes werden nur
 *               einmal gespeichert, egal wie oft referenziert.
 *               Kein Inode-Problem, keine Dateisystem-Grenzen.
 *
 * Konfiguration (Robust.ini / OpenSim.ini):
 *   [AssetService]
 *   StorageProvider    = "OpenSim.Data.MySQL.dll"
 *   ConnectionString   = "Data Source=localhost;..."
 *   Realm              = "s3assets"
 *   S3Endpoint         = "http://minio:9000"
 *   S3Bucket           = "opensim-assets"
 *   S3AccessKey        = "minioadmin"
 *   S3SecretKey        = "minioadmin"
 *   S3Region           = "us-east-1"
 *   ; 0 = access_time bei jedem Lesezugriff aktualisieren (teuer)
 *   ; N = nur wenn access_time älter als N Tage ist
 *   DaysBetweenAccessTimeUpdates = 30
 *
 * Skalierbarkeits-Hinweise:
 *   - MinIO im Distributed-Mode (4+ Knoten) übersteht Festplattenausfälle
 *     und skaliert horizontal auf Petabytes.
 *   - MySQL Partitioning: ALTER TABLE ... COALESCE/ADD PARTITION erlaubt
 *     nachträgliche Partition-Anpassung ohne Downtime (Online-DDL).
 *   - Bei >50 Mrd. Einträgen: Sharding via ProxySQL oder Wechsel auf
 *     ScyllaDB mit identischer IFSAssetDataPlugin-Implementierung.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using System.IO.Compression;
using OpenSim.Framework.Console;
using OpenSim.Framework.Serialization.External;
using OpenSim.Services.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.S3AssetService
{
    public class S3AssetConnector : ServiceBase, IAssetService
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private IFSAssetDataPlugin m_meta;
        private S3Client           m_s3;
        private IAssetService      m_fallback;
        private bool               m_showStats = true;

        // Statistik-Zähler (threadsafe via Interlocked)
        private long m_reads;
        private long m_writes;
        private long m_dedupHits;
        private long m_misses;

        // ------------------------------------------------------------------ Konstruktor

        public S3AssetConnector(IConfigSource config)
            : this(config, "AssetService") { }

        public S3AssetConnector(IConfigSource config, string configName)
            : base(config)
        {
            IConfig cfg = config.Configs[configName];
            if (cfg == null)
                throw new Exception($"[S3ASSETS]: Section [{configName}] not found in config");

            // Metadaten-Plugin (MySQLS3AssetData oder kompatibler Ersatz)
            string dllName  = cfg.GetString("StorageProvider", string.Empty);
            string connStr  = cfg.GetString("ConnectionString", string.Empty);
            string realm    = cfg.GetString("Realm", "s3assets");
            int    skipDays = cfg.GetInt("DaysBetweenAccessTimeUpdates", 30);

            IConfig dbCfg = config.Configs["DatabaseService"];
            if (dbCfg != null)
            {
                if (string.IsNullOrEmpty(dllName))   dllName = dbCfg.GetString("StorageProvider", string.Empty);
                if (string.IsNullOrEmpty(connStr))    connStr  = dbCfg.GetString("ConnectionString", string.Empty);
            }

            if (string.IsNullOrEmpty(dllName))   throw new Exception("[S3ASSETS]: StorageProvider not configured");
            if (string.IsNullOrEmpty(connStr))    throw new Exception("[S3ASSETS]: ConnectionString not configured");

            m_meta = LoadPlugin<IFSAssetDataPlugin>(dllName);
            if (m_meta == null)
                throw new Exception($"[S3ASSETS]: Cannot load IFSAssetDataPlugin from {dllName}");

            m_meta.Initialise(connStr, realm, skipDays);

            // S3-Client
            string endpoint  = cfg.GetString("S3Endpoint",   string.Empty);
            string bucket    = cfg.GetString("S3Bucket",     "opensim-assets");
            string accessKey = cfg.GetString("S3AccessKey",  string.Empty);
            string secretKey = cfg.GetString("S3SecretKey",  string.Empty);
            string region    = cfg.GetString("S3Region",     "us-east-1");

            if (string.IsNullOrEmpty(endpoint))
                throw new Exception("[S3ASSETS]: S3Endpoint not configured");

            m_s3 = new S3Client(endpoint, bucket, accessKey, secretKey, region);

            // Sicherstellen dass der Bucket existiert
            m_s3.EnsureBucket();

            // Optionaler Fallback-Service
            string fallbackStr = cfg.GetString("FallbackService", string.Empty);
            if (!string.IsNullOrEmpty(fallbackStr))
            {
                object[] args = new object[] { config };
                m_fallback = LoadPlugin<IAssetService>(fallbackStr, args);
                if (m_fallback != null)
                    m_log.Info("[S3ASSETS]: Fallback service loaded");
                else
                    m_log.Error("[S3ASSETS]: Failed to load fallback service");
            }

            m_showStats = cfg.GetBoolean("ShowConsoleStats", true);

            if (m_showStats)
            {
                Thread statsThread = new Thread(StatsLoop) { IsBackground = true, Name = "S3AssetStats" };
                statsThread.Start();
            }

            m_log.InfoFormat("[S3ASSETS]: S3AssetConnector started. Endpoint={0} Bucket={1}", endpoint, bucket);

            // Console-Befehle registrieren (nur wenn Console verfügbar)
            if (MainConsole.Instance != null)
            {
                MainConsole.Instance.Commands.AddCommand("s3", false,
                    "s3 show assets", "s3 show assets",
                    "Zeigt Asset-Statistiken (Reads/Writes/Dedup/Misses und DB-Count)",
                    HandleShowStats);

                MainConsole.Instance.Commands.AddCommand("s3", false,
                    "s3 import assets",
                    "s3 import assets <conn> <table> [<start> <count>]",
                    "Importiert Assets aus einer klassischen MySQL-assets-Tabelle (BLOB) nach S3+MySQL",
                    HandleImportFromAssets);

                MainConsole.Instance.Commands.AddCommand("s3", false,
                    "s3 import fsassets",
                    "s3 import fsassets <fsbase> <conn> <table>",
                    "Importiert Assets aus einem FSAssets-Verzeichnis (+ MySQL-Hash-Tabelle) nach S3",
                    HandleImportFromFSAssets);

                MainConsole.Instance.Commands.AddCommand("s3", false,
                    "s3 export assets",
                    "s3 export assets <conn> <table>",
                    "Exportiert alle Assets aus S3 zurück in eine klassische MySQL-assets-Tabelle (Rollback)",
                    HandleExportToLegacy);
            }
        }

        // ------------------------------------------------------------------ IAssetService

        public virtual AssetBase Get(string id)
        {
            string hash;
            AssetMetadata meta = m_meta.Get(id, out hash);

            if (meta == null)
            {
                if (m_fallback != null)
                {
                    AssetBase fb = m_fallback.Get(id);
                    if (fb != null)
                    {
                        Store(fb);
                        return fb;
                    }
                }
                Interlocked.Increment(ref m_misses);
                return null;
            }

            byte[] data = m_s3.Get(hash);
            if (data == null || data.Length == 0)
            {
                // S3-Objekt fehlt – Fallback versuchen
                if (m_fallback != null)
                {
                    AssetBase fb = m_fallback.Get(id);
                    if (fb != null)
                    {
                        Store(fb);
                        return fb;
                    }
                }
                Interlocked.Increment(ref m_misses);
                return null;
            }

            Interlocked.Increment(ref m_reads);

            AssetBase asset    = new AssetBase();
            asset.Metadata     = meta;
            asset.Data         = SanitizeIfObject(meta.Type, data);
            return asset;
        }

        public AssetBase Get(string id, string foreignAssetService, bool storeOnLocal)
        {
            return Get(id);
        }

        public virtual AssetMetadata GetMetadata(string id)
        {
            return m_meta.Get(id, out string _);
        }

        public virtual byte[] GetData(string id)
        {
            AssetMetadata meta = m_meta.Get(id, out string hash);
            if (meta == null) return null;
            return m_s3.Get(hash);
        }

        public AssetBase GetCached(string id)
        {
            // Kein In-Memory-Cache in diesem Service – direkter DB-Aufruf
            return Get(id);
        }

        public bool Get(string id, object sender, AssetRetrieved handler)
        {
            AssetBase asset = Get(id);
            handler(id, sender, asset);
            return true;
        }

        public void Get(string id, string foreignService, bool storeOnLocal,
                        SimpleAssetRetrieved callback)
        {
            callback(Get(id));
        }

        public bool[] AssetsExist(string[] ids)
        {
            UUID[] uuids = Array.ConvertAll(ids, s => new UUID(s));
            return m_meta.AssetsExist(uuids);
        }

        public virtual string Store(AssetBase asset)
        {
            if (asset.Data == null || asset.Data.Length == 0)
                return asset.ID;

            // Erzwungene Längenkürzung für Metadaten
            if (asset.Name.Length > AssetBase.MAX_ASSET_NAME)
                asset.Name = asset.Name.Substring(0, AssetBase.MAX_ASSET_NAME);
            if (asset.Description.Length > AssetBase.MAX_ASSET_DESC)
                asset.Description = asset.Description.Substring(0, AssetBase.MAX_ASSET_DESC);

            // Sanitize LSL-Objekte
            byte[] data = SanitizeIfObject(asset.Type, asset.Data);

            // SHA-256 des Inhalts = S3-Objekt-Key (Content-Addressable)
            string hash = ComputeSha256(data);

            // Binärdaten nur schreiben wenn noch nicht vorhanden (Deduplizierung)
            if (!m_s3.Exists(hash))
            {
                m_s3.Put(hash, data);
                Interlocked.Increment(ref m_writes);
            }
            else
            {
                Interlocked.Increment(ref m_dedupHits);
            }

            // Asset-ID sicherstellen
            if (string.IsNullOrEmpty(asset.ID))
            {
                if (asset.FullID.IsZero())
                    asset.FullID = UUID.Random();
                asset.ID = asset.FullID.ToString();
            }
            else if (asset.FullID.IsZero())
            {
                if (!UUID.TryParse(asset.ID, out UUID parsed))
                    parsed = UUID.Random();
                asset.FullID = parsed;
            }

            // Nur Metadaten in MySQL
            if (!m_meta.Store(asset.Metadata, hash))
            {
                if (asset.Metadata.Type == -2)
                    return asset.ID;
                return UUID.Zero.ToString();
            }

            return asset.ID;
        }

        public bool UpdateContent(string id, byte[] data)
        {
            AssetMetadata meta = m_meta.Get(id, out string _);
            if (meta == null) return false;

            string newHash = ComputeSha256(data);
            if (!m_s3.Exists(newHash))
                m_s3.Put(newHash, data);

            return m_meta.Store(meta, newHash);
        }

        public virtual bool Delete(string id)
        {
            // Binärdaten werden NICHT gelöscht: Hash könnte von anderen Assets
            // referenziert sein (Deduplizierung). Nur Metadaten-Eintrag entfernen.
            // Für Garbage-Collection des S3-Stores ist ein separater offline Job
            // nötig, der Hashes ohne Metadaten-Referenz aufräumt.
            return m_meta.Delete(id);
        }

        // ------------------------------------------------------------------ Hilfsmethoden

        private static string ComputeSha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static byte[] SanitizeIfObject(sbyte type, byte[] data)
        {
            if (type == (sbyte)AssetType.Object && data != null)
            {
                string xml = ExternalRepresentationUtils.SanitizeXml(
                    Utils.BytesToString(data));
                return Utils.StringToBytes(xml);
            }
            return data;
        }

        private void StatsLoop()
        {
            while (true)
            {
                Thread.Sleep(60000);
                m_log.InfoFormat(
                    "[S3ASSETS]: reads={0}  writes={1}  dedup_hits={2}  misses={3}  db_count~={4}",
                    Interlocked.Read(ref m_reads),
                    Interlocked.Read(ref m_writes),
                    Interlocked.Read(ref m_dedupHits),
                    Interlocked.Read(ref m_misses),
                    m_meta.Count());
            }
        }

        // ------------------------------------------------------------------ Konsolen-Handler

        private void HandleShowStats(string module, string[] args)
        {
            MainConsole.Instance.Output("S3Asset reads      : {0}", Interlocked.Read(ref m_reads));
            MainConsole.Instance.Output("S3Asset writes     : {0}", Interlocked.Read(ref m_writes));
            MainConsole.Instance.Output("S3Asset dedup hits : {0}", Interlocked.Read(ref m_dedupHits));
            MainConsole.Instance.Output("S3Asset misses     : {0}", Interlocked.Read(ref m_misses));
            MainConsole.Instance.Output("S3Asset DB count~  : {0}", m_meta.Count());
        }

        // s3 import assets <conn> <table> [<start> <count>]
        private void HandleImportFromAssets(string module, string[] args)
        {
            if (args.Length < 4)
            {
                MainConsole.Instance.Output("Syntax: s3 import assets <conn> <table> [<start> <count>]");
                return;
            }
            string srcConn  = args[3];
            string srcTable = args[4];
            int start = args.Length > 5 ? int.Parse(args[5]) : 0;
            int count = args.Length > 6 ? int.Parse(args[6]) : int.MaxValue;

            MainConsole.Instance.Output("[S3ASSETS]: Starte Import aus {0}.{1} ab {2}", srcConn, srcTable, start);
            m_meta.Import(srcConn, srcTable, start, count, false, (asset, force) =>
            {
                if (asset.Data != null && asset.Data.Length > 0)
                    return Store(asset);
                return asset.ID;
            });
            MainConsole.Instance.Output("[S3ASSETS]: Import abgeschlossen.");
        }

        // s3 import fsassets <fsbase> <conn> <table>
        private void HandleImportFromFSAssets(string module, string[] args)
        {
            if (args.Length < 5)
            {
                MainConsole.Instance.Output("Syntax: s3 import fsassets <fsbase> <conn> <table>");
                return;
            }
            string fsBase   = args[3];
            string srcConn  = args[4];
            string srcTable = args[5];

            if (!Directory.Exists(fsBase))
            {
                MainConsole.Instance.Output("[S3ASSETS]: FSBase-Verzeichnis nicht gefunden: {0}", fsBase);
                return;
            }

            MainConsole.Instance.Output("[S3ASSETS]: Starte FSAssets-Import aus {0} / {1}.{2}", fsBase, srcConn, srcTable);
            m_meta.ImportFromFSAssets(fsBase, srcConn, srcTable, (asset, force) =>
            {
                if (asset.Data != null && asset.Data.Length > 0)
                    return Store(asset);
                return asset.ID;
            });
            MainConsole.Instance.Output("[S3ASSETS]: FSAssets-Import abgeschlossen.");
        }

        // s3 export assets <conn> <table>
        private void HandleExportToLegacy(string module, string[] args)
        {
            if (args.Length < 4)
            {
                MainConsole.Instance.Output("Syntax: s3 export assets <conn> <table>");
                return;
            }
            string dstConn  = args[3];
            string dstTable = args[4];

            MainConsole.Instance.Output("[S3ASSETS]: Starte Export nach {0}.{1}", dstConn, dstTable);
            m_meta.ExportToLegacy(dstConn, dstTable, hash => m_s3.Get(hash));
            MainConsole.Instance.Output("[S3ASSETS]: Export abgeschlossen.");
        }
    }

    // ======================================================================
    // S3Client – minimaler HTTP-Client mit AWS Signature Version 4
    // Unterstützt MinIO, AWS S3, Ceph RGW und jeden S3-kompatiblen Store.
    // Keine externen Abhängigkeiten – nur System.Net.Http (BCL).
    // ======================================================================

    internal sealed class S3Client
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(typeof(S3Client));

        private readonly string      m_endpoint;   // z.B. "http://minio:9000"
        private readonly string      m_bucket;
        private readonly string      m_accessKey;
        private readonly string      m_secretKey;
        private readonly string      m_region;
        private readonly HttpClient  m_http;

        private static readonly byte[] EmptyPayloadHash =
            HexBytes(SHA256.Create().ComputeHash(Array.Empty<byte>()));

        internal S3Client(string endpoint, string bucket,
                          string accessKey, string secretKey, string region)
        {
            m_endpoint  = endpoint.TrimEnd('/');
            m_bucket    = bucket;
            m_accessKey = accessKey;
            m_secretKey = secretKey;
            m_region    = region;

            // ServicePointManager-Einstellungen für hohe Parallelität
            ServicePointManager.DefaultConnectionLimit = 256;
            ServicePointManager.Expect100Continue      = false;

            m_http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(120)
            };
        }

        // ---- Öffentliche Methoden ----------------------------------------

        internal void EnsureBucket()
        {
            // HEAD-Request auf Bucket-Ebene; legt Bucket an falls nötig.
            try
            {
                string url = $"{m_endpoint}/{m_bucket}";
                using (HttpRequestMessage req = BuildRequest(HttpMethod.Head, url, null))
                {
                    HttpResponseMessage resp = m_http.SendAsync(req).GetAwaiter().GetResult();
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        using (HttpRequestMessage put = BuildRequest(HttpMethod.Put, url, null))
                        {
                            HttpResponseMessage cr = m_http.SendAsync(put).GetAwaiter().GetResult();
                            if (!cr.IsSuccessStatusCode && cr.StatusCode != HttpStatusCode.Conflict)
                                m_log.WarnFormat("[S3CLIENT]: CreateBucket returned {0}", cr.StatusCode);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[S3CLIENT]: EnsureBucket failed: {0}", ex.Message);
            }
        }

        internal bool Exists(string hash)
        {
            try
            {
                string url = ObjectUrl(hash);
                using (HttpRequestMessage req = BuildRequest(HttpMethod.Head, url, null))
                {
                    HttpResponseMessage resp = m_http.SendAsync(req).GetAwaiter().GetResult();
                    return resp.IsSuccessStatusCode;
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[S3CLIENT]: Exists({0}) error: {1}", hash, ex.Message);
                return false;
            }
        }

        internal void Put(string hash, byte[] data)
        {
            try
            {
                string url = ObjectUrl(hash);
                using (HttpRequestMessage req = BuildRequest(HttpMethod.Put, url, data))
                {
                    req.Content = new ByteArrayContent(data);
                    req.Content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                    HttpResponseMessage resp = m_http.SendAsync(req).GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode)
                        m_log.ErrorFormat("[S3CLIENT]: PUT {0} failed: {1}", hash, resp.StatusCode);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[S3CLIENT]: Put({0}) exception: {1}", hash, ex.Message);
            }
        }

        internal byte[] Get(string hash)
        {
            try
            {
                string url = ObjectUrl(hash);
                using (HttpRequestMessage req = BuildRequest(HttpMethod.Get, url, null))
                {
                    HttpResponseMessage resp = m_http.SendAsync(req).GetAwaiter().GetResult();
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                        return null;
                    if (!resp.IsSuccessStatusCode)
                    {
                        m_log.WarnFormat("[S3CLIENT]: GET {0} failed: {1}", hash, resp.StatusCode);
                        return null;
                    }
                    return resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[S3CLIENT]: Get({0}) exception: {1}", hash, ex.Message);
                return null;
            }
        }

        // ---- AWS Signature Version 4 ------------------------------------

        private HttpRequestMessage BuildRequest(HttpMethod method, string url, byte[] payload)
        {
            DateTime now     = DateTime.UtcNow;
            string   dateStr = now.ToString("yyyyMMdd");
            string   amzDate = now.ToString("yyyyMMddTHHmmssZ");

            Uri uri = new Uri(url);

            byte[] body        = payload ?? Array.Empty<byte>();
            string payloadHash = ToHex(Sha256(body));

            // Kanonische Header (müssen sortiert sein)
            var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["host"]                 = uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port),
                ["x-amz-content-sha256"] = payloadHash,
                ["x-amz-date"]           = amzDate,
            };

            string canonHeaders  = BuildCanonicalHeaders(headers);
            string signedHeaders  = string.Join(";", headers.Keys);

            string canonRequest = string.Join("\n",
                method.Method.ToUpper(),
                uri.AbsolutePath,
                uri.Query.TrimStart('?'),
                canonHeaders,
                signedHeaders,
                payloadHash);

            string credScope  = $"{dateStr}/{m_region}/s3/aws4_request";
            string strToSign  = string.Join("\n",
                "AWS4-HMAC-SHA256",
                amzDate,
                credScope,
                ToHex(Sha256(Encoding.UTF8.GetBytes(canonRequest))));

            byte[] signingKey = GetSigningKey(dateStr);
            string signature  = ToHex(HmacSha256(signingKey, strToSign));

            string authHeader =
                $"AWS4-HMAC-SHA256 Credential={m_accessKey}/{credScope}, " +
                $"SignedHeaders={signedHeaders}, Signature={signature}";

            HttpRequestMessage req = new HttpRequestMessage(method, url);
            foreach (var h in headers)
            {
                // Host wird automatisch gesetzt; x-amz-* manuell
                if (h.Key == "host") continue;
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            req.Headers.TryAddWithoutValidation("Authorization", authHeader);
            return req;
        }

        private byte[] GetSigningKey(string dateStamp)
        {
            byte[] kDate    = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + m_secretKey), dateStamp);
            byte[] kRegion  = HmacSha256(kDate,    m_region);
            byte[] kService = HmacSha256(kRegion,  "s3");
            return HmacSha256(kService, "aws4_request");
        }

        private static string BuildCanonicalHeaders(SortedDictionary<string, string> headers)
        {
            var sb = new StringBuilder();
            foreach (var kv in headers)
                sb.Append(kv.Key.ToLowerInvariant()).Append(':').Append(kv.Value.Trim()).Append('\n');
            return sb.ToString();
        }

        // ---- Krypto-Helfer ----------------------------------------------

        private static byte[] Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
                return sha.ComputeHash(data);
        }

        private static byte[] HmacSha256(byte[] key, string data)
        {
            using (HMACSHA256 hmac = new HMACSHA256(key))
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        private static string ToHex(byte[] bytes) =>
            BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        private static byte[] HexBytes(byte[] b) => b; // Alias für Lesbarkeit

        private string ObjectUrl(string hash) =>
            $"{m_endpoint}/{m_bucket}/{hash}";
    }
}
