using System;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using RecoverX.Core.Models;

namespace RecoverX.Database
{
    /// <summary>
    /// Owns the SQLite file. Stores settings, file categories and scan metadata only - recovered user files are never
    /// stored here. The schema is versioned with PRAGMA user_version so Phase 1 databases upgrade in place.
    /// </summary>
    public sealed class SqliteDatabase
    {
        public const int CurrentSchemaVersion = 3;

        // Version 1 (Phase 1): unchanged, still CREATE IF NOT EXISTS so an existing Phase 1 file is left intact.
        private const string SchemaV1 = @"
CREATE TABLE IF NOT EXISTS settings (
    key   TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS scan_sessions (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    started_utc  TEXT    NOT NULL,
    finished_utc TEXT,
    drive_letter TEXT    NOT NULL,
    volume_name  TEXT,
    file_system  TEXT,
    scan_type    TEXT    NOT NULL,
    status       TEXT    NOT NULL,
    files_found  INTEGER NOT NULL DEFAULT 0,
    resumable    INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_scan_sessions_started ON scan_sessions(started_utc DESC);
";

        // Version 2 (Phase 2): user-editable file categories.
        private const string SchemaV2 = @"
CREATE TABLE IF NOT EXISTS file_categories (
    id         TEXT PRIMARY KEY NOT NULL,
    extensions TEXT    NOT NULL,
    enabled    INTEGER NOT NULL DEFAULT 1,
    sort_order INTEGER NOT NULL DEFAULT 0
);
";

        // Version 3 (Phase 3): files the user has recovered (metadata only - never the file contents).
        private const string SchemaV3 = @"
CREATE TABLE IF NOT EXISTS recovered_files (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    recovered_utc TEXT    NOT NULL,
    source_drive  TEXT    NOT NULL,
    original_path TEXT    NOT NULL,
    saved_path    TEXT    NOT NULL,
    size          INTEGER NOT NULL DEFAULT 0,
    status        TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_recovered_files_time ON recovered_files(recovered_utc DESC);
";

        private readonly string _connectionString;

        public SqliteDatabase(string databasePath)
        {
            string dir = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var b = new SQLiteConnectionStringBuilder
            {
                DataSource = databasePath,
                Version = 3,
                ForeignKeys = true
            };
            _connectionString = b.ToString();
        }

        public SQLiteConnection Open()
        {
            var c = new SQLiteConnection(_connectionString);
            c.Open();
            return c;
        }

        /// <summary>Creates a new database or upgrades an older one. Safe to call on every start.</summary>
        public void EnsureCreated()
        {
            using (SQLiteConnection c = Open())
            {
                int version = GetVersion(c);
                if (version >= CurrentSchemaVersion) return;

                using (SQLiteTransaction tx = c.BeginTransaction())
                {
                    if (version < 1) Exec(c, SchemaV1);
                    if (version < 2)
                    {
                        Exec(c, SchemaV2);
                        SeedCategories(c);
                    }
                    if (version < 3) Exec(c, SchemaV3);
                    Exec(c, "PRAGMA user_version = " + CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
                    tx.Commit();
                }
            }
        }

        public int SchemaVersion()
        {
            using (SQLiteConnection c = Open()) return GetVersion(c);
        }

        private static int GetVersion(SQLiteConnection c)
        {
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version";
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        private static void Exec(SQLiteConnection c, string sql)
        {
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        private static void SeedCategories(SQLiteConnection c)
        {
            foreach (FileCategory cat in DefaultCategories.Create())
            {
                using (SQLiteCommand cmd = c.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR IGNORE INTO file_categories(id, extensions, enabled, sort_order) VALUES(@i, @e, @n, @o)";
                    cmd.Parameters.AddWithValue("@i", cat.Id);
                    cmd.Parameters.AddWithValue("@e", string.Join(",", cat.Extensions));
                    cmd.Parameters.AddWithValue("@n", cat.Enabled ? 1 : 0);
                    cmd.Parameters.AddWithValue("@o", cat.SortOrder);
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }
}
