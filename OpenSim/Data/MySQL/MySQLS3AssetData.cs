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

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using log4net;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    /// <summary>
    /// MySQL-Metadaten-Plugin für den S3AssetConnector.
    /// Speichert ausschließlich Asset-Metadaten (kein BLOB).
    /// Die Tabelle ist KEY-partitioniert (256 Partitionen) und
    /// skaliert auf 25 Milliarden Einträge.
    /// Binärdaten werden ausschließlich in S3/MinIO über den Hash referenziert.
    /// </summary>
    public class MySQLS3AssetData : IFSAssetDataPlugin
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private string m_connectionString;
        private string m_table;
        private int m_daysBetweenAccessTimeUpdates = 0;

        protected virtual Assembly Assembly => GetType().Assembly;

        // ------------------------------------------------------------------ IPlugin

        public string Version => "1.0.0.0";
        public string Name    => "MySQL S3Asset metadata engine";

        public void Initialise()
        {
            throw new NotImplementedException();
        }

        public void Initialise(string connect, string realm, int skipAccessTimeDays)
        {
            m_connectionString = connect;
            m_table            = realm;
            m_daysBetweenAccessTimeUpdates = skipAccessTimeDays;

            try
            {
                using (MySqlConnection conn = new MySqlConnection(m_connectionString))
                {
                    conn.Open();
                    Migration m = new Migration(conn, Assembly, "S3AssetStore");
                    m.Update();
                }
            }
            catch (MySqlException ex)
            {
                m_log.ErrorFormat("[S3ASSETMETA]: Cannot connect to database: {0}", ex.Message);
            }
        }

        public void Dispose() { }

        // ------------------------------------------------------------------ IFSAssetDataPlugin

        public AssetMetadata Get(string id, out string hash)
        {
            hash = string.Empty;
            try
            {
                using (MySqlConnection conn = new MySqlConnection(m_connectionString))
                {
                    conn.Open();
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            $"SELECT id, name, description, type, hash, create_time, asset_flags, access_time " +
                            $"FROM `{m_table}` WHERE id = ?id";
                        cmd.Parameters.AddWithValue("?id", id);

                        using (IDataReader reader = cmd.ExecuteReader())
                        {
                            if (!reader.Read())
                                return null;

                            hash = reader["hash"].ToString();

                            AssetMetadata meta = new AssetMetadata();
                            meta.ID          = id;
                            meta.FullID      = new UUID(id);
                            meta.Name        = reader["name"].ToString();
                            meta.Description = reader["description"].ToString();
                            meta.Type        = (sbyte)Convert.ToInt32(reader["type"]);
                            meta.ContentType = SLUtil.SLAssetTypeToContentType(meta.Type);
                            meta.CreationDate = Util.ToDateTime(Convert.ToInt32(reader["create_time"]));
                            meta.Flags       = (AssetFlags)Convert.ToInt32(reader["asset_flags"]);

                            UpdateAccessTime(id, Convert.ToInt32(reader["access_time"]));
                            return meta;
                        }
                    }
                }
            }
            catch (MySqlException ex)
            {
                m_log.ErrorFormat("[S3ASSETMETA]: Get({0}) failed: {1}", id, ex.Message);
                return null;
            }
        }

        public bool Store(AssetMetadata metadata, string hash)
        {
            try
            {
                using (MySqlConnection conn = new MySqlConnection(m_connectionString))
                {
                    conn.Open();
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
                        int now = Util.ToUnixTime(metadata.CreationDate);
                        cmd.CommandText =
                            $"INSERT INTO `{m_table}` " +
                            "(id, name, description, type, hash, create_time, access_time, asset_flags) " +
                            "VALUES (?id, ?name, ?desc, ?type, ?hash, ?create, ?access, ?flags) " +
                            "ON DUPLICATE KEY UPDATE " +
                            "hash=VALUES(hash), access_time=VALUES(access_time)";

                        cmd.Parameters.AddWithValue("?id",     metadata.ID);
                        cmd.Parameters.AddWithValue("?name",   metadata.Name.Length > 64
                                                                ? metadata.Name.Substring(0, 64)
                                                                : metadata.Name);
                        cmd.Parameters.AddWithValue("?desc",   metadata.Description.Length > 64
                                                                ? metadata.Description.Substring(0, 64)
                                                                : metadata.Description);
                        cmd.Parameters.AddWithValue("?type",   (int)metadata.Type);
                        cmd.Parameters.AddWithValue("?hash",   hash);
                        cmd.Parameters.AddWithValue("?create", now);
                        cmd.Parameters.AddWithValue("?access", now);
                        cmd.Parameters.AddWithValue("?flags",  (int)metadata.Flags);

                        cmd.ExecuteNonQuery();
                    }
                }
                return true;
            }
            catch (MySqlException ex)
            {
                m_log.ErrorFormat("[S3ASSETMETA]: Store({0}) failed: {1}", metadata.ID, ex.Message);
                return false;
            }
        }

        public bool Delete(string id)
        {
            try
            {
                using (MySqlConnection conn = new MySqlConnection(m_connectionString))
                {
                    conn.Open();
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"DELETE FROM `{m_table}` WHERE id = ?id";
                        cmd.Parameters.AddWithValue("?id", id);
                        cmd.ExecuteNonQuery();
                    }
                }
                return true;
            }
            catch (MySqlException ex)
            {
                m_log.ErrorFormat("[S3ASSETMETA]: Delete({0}) failed: {1}", id, ex.Message);
                return false;
            }
        }

        public bool[] AssetsExist(UUID[] uuids)
        {
            bool[] result = new bool[uuids.Length];
            if (uuids.Length == 0)
                return result;

            // Batch-Abfrage in Blöcken von 1000 für optimale Performance
            const int batchSize = 1000;
            var idIndex = new Dictionary<string, int>(uuids.Length);
            for (int i = 0; i < uuids.Length; i++)
                idIndex[uuids[i].ToString()] = i;

            try
            {
                using (MySqlConnection conn = new MySqlConnection(m_connectionString))
                {
                    conn.Open();
                    for (int offset = 0; offset < uuids.Length; offset += batchSize)
                    {
                        int count = Math.Min(batchSize, uuids.Length - offset);
                        var paramNames = new List<string>(count);
                        using (MySqlCommand cmd = conn.CreateCommand())
                        {
                            for (int j = 0; j < count; j++)
                            {
                                string pname = "?p" + j;
                                paramNames.Add(pname);
                                cmd.Parameters.AddWithValue(pname, uuids[offset + j].ToString());
                            }
                            cmd.CommandText =
                                $"SELECT id FROM `{m_table}` WHERE id IN ({string.Join(",", paramNames)})";

                            using (IDataReader reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string foundId = reader.GetString(0);
                                    if (idIndex.TryGetValue(foundId, out int idx))
                                        result[idx] = true;
                                }
                            }
                        }
                    }
                }
            }
            catch (MySqlException ex)
            {
                m_log.ErrorFormat("[S3ASSETMETA]: AssetsExist() failed: {0}", ex.Message);
            }
            return result;
        }

        public int Count()
        {
            try
            {
                using (MySqlConnection conn = new MySqlConnection(m_connectionString))
                {
                    conn.Open();
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
                        // TABLE_ROWS ist eine schnelle Schätzung aus information_schema –
                        // bei 25 Mrd. Zeilen ist ein COUNT(*) nicht akzeptabel.
                        cmd.CommandText =
                            "SELECT IFNULL(SUM(TABLE_ROWS), 0) FROM information_schema.PARTITIONS " +
                            $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{m_table}'";
                        object val = cmd.ExecuteScalar();
                        return Convert.ToInt32(val);
                    }
                }
            }
            catch (MySqlException ex)
            {
                m_log.ErrorFormat("[S3ASSETMETA]: Count() failed: {0}", ex.Message);
                return 0;
            }
        }

        public void Import(string conn, string table, int start, int count, bool force,
                           FSStoreDelegate store)
        {
            // Import aus legacy assets-Tabelle: holt Metadaten + Binärdaten,
            // schreibt Binärdaten über store() nach S3, Metadaten in diese Tabelle.
            m_log.InfoFormat("[S3ASSETMETA]: Import from {0}.{1} start={2} count={3}", conn, table, start, count);
            try
            {
                using (MySqlConnection srcConn = new MySqlConnection(conn))
                {
                    srcConn.Open();
                    using (MySqlCommand cmd = srcConn.CreateCommand())
                    {
                        cmd.CommandText =
                            $"SELECT id, name, description, assetType, local, temporary, data, " +
                            $"create_time, access_time, asset_flags, CreatorID " +
                            $"FROM `{table}` LIMIT {start},{count}";

                        using (IDataReader reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                AssetBase asset = new AssetBase();
                                asset.ID          = reader["id"].ToString();
                                asset.Name        = reader["name"].ToString();
                                asset.Description = reader["description"].ToString();
                                asset.Type        = (sbyte)Convert.ToInt32(reader["assetType"]);
                                asset.Local       = Convert.ToBoolean(reader["local"]);
                                asset.Temporary   = Convert.ToBoolean(reader["temporary"]);
                                asset.Data        = (byte[])reader["data"];
                                asset.Flags       = (AssetFlags)Convert.ToInt32(reader["asset_flags"]);

                                store(asset, force);
                            }
                        }
                    }
                }
            }
            catch (MySqlException ex)
            {
                m_log.ErrorFormat("[S3ASSETMETA]: Import failed: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Importiert Assets aus einem FSAssets-Verzeichnisbaum nach S3+MySQL.
        /// Liest die Hash-Metadaten aus der fsassets-MySQL-Tabelle,
        /// liest die komprimierten .gz-Dateien aus dem Dateisystem,
        /// entpackt sie und ruft den store-Delegate auf.
        /// </summary>
        public void ImportFromFSAssets(string fsBase, string srcConn, string hashTable,
                                       FSStoreDelegate store)
        {
            m_log.InfoFormat("[S3ASSETMETA]: ImportFromFSAssets fsBase={0} src={1}.{2}",
                fsBase, srcConn, hashTable);
            int processed = 0;
            int errors = 0;
            try
            {
                using (MySqlConnection conn = new MySqlConnection(srcConn))
                {
                    conn.Open();
                    // fsassets-Tabelle: Spalten id, name, description, asset_type, hash, ...  
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            $"SELECT id, name, description, asset_type, local, temporary, " +
                            $"hash, create_time, access_time, asset_flags " +
                            $"FROM `{hashTable}`";

                        using (IDataReader reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string id   = reader["id"].ToString();
                                string hash = reader["hash"].ToString();

                                // Pfad rekonstruieren: gleiche Logik wie FSAssetService.HashToPath
                                string relPath = hash.Length >= 10
                                    ? Path.Combine(hash.Substring(0, 2),
                                        Path.Combine(hash.Substring(2, 2),
                                        Path.Combine(hash.Substring(4, 2),
                                        hash.Substring(6, 4))))
                                    : "junkyard";

                                string diskFile = Path.Combine(fsBase, relPath, hash);
                                byte[] data = null;

                                // .gz-Version bevorzugen
                                if (File.Exists(diskFile + ".gz"))
                                {
                                    try
                                    {
                                        using (FileStream fs = File.OpenRead(diskFile + ".gz"))
                                        using (GZipStream gz = new GZipStream(fs, CompressionMode.Decompress))
                                        using (MemoryStream ms = new MemoryStream())
                                        {
                                            gz.CopyTo(ms);
                                            data = ms.ToArray();
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        m_log.WarnFormat("[S3ASSETMETA]: Lesen von {0}.gz fehlgeschlagen: {1}",
                                            diskFile, ex.Message);
                                        errors++;
                                        continue;
                                    }
                                }
                                else if (File.Exists(diskFile))
                                {
                                    data = File.ReadAllBytes(diskFile);
                                }
                                else
                                {
                                    m_log.WarnFormat("[S3ASSETMETA]: Datei nicht gefunden für hash={0}, id={1}",
                                        hash, id);
                                    errors++;
                                    continue;
                                }

                                AssetBase asset = new AssetBase();
                                asset.ID          = id;
                                asset.Name        = reader["name"].ToString();
                                asset.Description = reader["description"].ToString();
                                asset.Type        = (sbyte)Convert.ToInt32(reader["asset_type"]);
                                asset.Local       = Convert.ToBoolean(reader["local"]);
                                asset.Temporary   = Convert.ToBoolean(reader["temporary"]);
                                asset.Data        = data;
                                asset.Flags       = (AssetFlags)Convert.ToInt32(reader["asset_flags"]);

                                store(asset, false);
                                processed++;

                                if (processed % 10000 == 0)
                                    m_log.InfoFormat("[S3ASSETMETA]: ImportFromFSAssets {0} verarbeitet, {1} Fehler",
                                        processed, errors);
                            }
                        }
                    }
                }
            }
            catch (MySqlException ex)
            {
                m_log.ErrorFormat("[S3ASSETMETA]: ImportFromFSAssets fehlgeschlagen: {0}", ex.Message);
            }
            m_log.InfoFormat("[S3ASSETMETA]: ImportFromFSAssets abgeschlossen: {0} Assets, {1} Fehler",
                processed, errors);
        }

        /// <summary>
        /// Exportiert alle Assets aus s3assets zurück in eine klassische MySQL-assets-Tabelle.
        /// Dient dem Rollback auf den klassischen Asset-Service.
        /// getDataFromS3: Delegate der den Binärinhalt per Hash aus S3 lädt.
        /// </summary>
        public void ExportToLegacy(string targetConn, string targetTable,
                                   Func<string, byte[]> getDataFromS3)
        {
            m_log.InfoFormat("[S3ASSETMETA]: ExportToLegacy nach {0}.{1}", targetConn, targetTable);
            int exported = 0;
            int errors   = 0;

            // Zieltabelle anlegen falls nicht vorhanden
            const string createSql =
                "CREATE TABLE IF NOT EXISTS `{0}` (" +
                "  `id`          CHAR(36)      NOT NULL PRIMARY KEY," +
                "  `name`        VARCHAR(64)   NOT NULL DEFAULT ''," +
                "  `description` VARCHAR(64)   NOT NULL DEFAULT ''," +
                "  `assetType`   TINYINT       NOT NULL," +
                "  `local`       TINYINT       NOT NULL," +
                "  `temporary`   TINYINT       NOT NULL," +
                "  `data`        LONGBLOB      NOT NULL," +
                "  `create_time` INT           NOT NULL DEFAULT 0," +
                "  `access_time` INT           NOT NULL DEFAULT 0," +
                "  `asset_flags` INT           NOT NULL DEFAULT 0" +
                ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4";

            try
            {
                using (MySqlConnection dst = new MySqlConnection(targetConn))
                {
                    dst.Open();
                    using (MySqlCommand createCmd = dst.CreateCommand())
                    {
                        createCmd.CommandText = string.Format(createSql, targetTable);
                        createCmd.ExecuteNonQuery();
                    }

                    // Alle Metadaten aus s3assets lesen
                    using (MySqlConnection src = new MySqlConnection(m_connectionString))
                    {
                        src.Open();
                        using (MySqlCommand readCmd = src.CreateCommand())
                        {
                            readCmd.CommandText =
                                $"SELECT id, name, description, type, hash, create_time, access_time, asset_flags " +
                                $"FROM `{m_table}`";

                            using (IDataReader reader = readCmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string id   = reader["id"].ToString();
                                    string hash = reader["hash"].ToString();
                                    int    ctime = Convert.ToInt32(reader["create_time"]);
                                    int    atime = Convert.ToInt32(reader["access_time"]);
                                    int    flags = Convert.ToInt32(reader["asset_flags"]);
                                    sbyte  type  = (sbyte)Convert.ToInt32(reader["type"]);
                                    string name  = reader["name"].ToString();
                                    string desc  = reader["description"].ToString();

                                    byte[] data = null;
                                    try { data = getDataFromS3(hash); }
                                    catch (Exception ex)
                                    {
                                        m_log.WarnFormat("[S3ASSETMETA]: S3.Get({0}) für id={1}: {2}",
                                            hash, id, ex.Message);
                                        errors++;
                                        continue;
                                    }

                                    if (data == null)
                                    {
                                        m_log.WarnFormat("[S3ASSETMETA]: S3-Objekt nicht gefunden hash={0} id={1}",
                                            hash, id);
                                        errors++;
                                        continue;
                                    }

                                    try
                                    {
                                        using (MySqlCommand ins = dst.CreateCommand())
                                        {
                                            ins.CommandText =
                                                $"INSERT IGNORE INTO `{targetTable}` " +
                                                "(id, name, description, assetType, local, temporary, " +
                                                " data, create_time, access_time, asset_flags) " +
                                                "VALUES (?id, ?name, ?desc, ?type, 0, 0, " +
                                                "        ?data, ?ctime, ?atime, ?flags)";

                                            ins.Parameters.AddWithValue("?id",    id);
                                            ins.Parameters.AddWithValue("?name",  name);
                                            ins.Parameters.AddWithValue("?desc",  desc);
                                            ins.Parameters.AddWithValue("?type",  (int)type);
                                            ins.Parameters.AddWithValue("?data",  data);
                                            ins.Parameters.AddWithValue("?ctime", ctime);
                                            ins.Parameters.AddWithValue("?atime", atime);
                                            ins.Parameters.AddWithValue("?flags", flags);
                                            ins.ExecuteNonQuery();
                                        }
                                    }
                                    catch (MySqlException ex)
                                    {
                                        m_log.WarnFormat("[S3ASSETMETA]: INSERT id={0} fehlgeschlagen: {1}",
                                            id, ex.Message);
                                        errors++;
                                        continue;
                                    }

                                    exported++;
                                    if (exported % 10000 == 0)
                                        m_log.InfoFormat("[S3ASSETMETA]: ExportToLegacy {0} exportiert, {1} Fehler",
                                            exported, errors);
                                }
                            }
                        }
                    }
                }
            }
            catch (MySqlException ex)
            {
                m_log.ErrorFormat("[S3ASSETMETA]: ExportToLegacy fehlgeschlagen: {0}", ex.Message);
            }
            m_log.InfoFormat("[S3ASSETMETA]: ExportToLegacy abgeschlossen: {0} Assets, {1} Fehler",
                exported, errors);
        }

        // ------------------------------------------------------------------ Privat

        private void UpdateAccessTime(string id, int dbAccessTime)
        {
            if (m_daysBetweenAccessTimeUpdates <= 0)
                return;

            int nowUnix = Util.UnixTimeSinceEpoch();
            if ((nowUnix - dbAccessTime) < m_daysBetweenAccessTimeUpdates * 86400)
                return;

            try
            {
                using (MySqlConnection conn = new MySqlConnection(m_connectionString))
                {
                    conn.Open();
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            $"UPDATE `{m_table}` SET access_time = ?now WHERE id = ?id";
                        cmd.Parameters.AddWithValue("?now", nowUnix);
                        cmd.Parameters.AddWithValue("?id",  id);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (MySqlException ex)
            {
                m_log.WarnFormat("[S3ASSETMETA]: UpdateAccessTime({0}) failed: {1}", id, ex.Message);
            }
        }
    }
}
