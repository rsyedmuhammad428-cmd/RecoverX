using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;

namespace RecoverX.Database
{
    public sealed class SettingsService : ISettingsService
    {
        private readonly SqliteDatabase _db;

        public SettingsService(SqliteDatabase db) { _db = db; }

        public AppSettings Load()
        {
            var values = new Dictionary<string, string>();
            using (SQLiteConnection c = _db.Open())
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT key, value FROM settings";
                using (SQLiteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read()) values[r.GetString(0)] = r.GetString(1);
                }
            }

            var s = new AppSettings();
            string v;
            if (values.TryGetValue("theme", out v)) s.Theme = v;
            if (values.TryGetValue("language", out v)) s.Language = v;
            if (values.TryGetValue("default_destination", out v)) s.DefaultRecoveryDestination = v;
            if (values.TryGetValue("scan_type_default", out v)) s.DefaultScanType = v;
            if (values.TryGetValue("min_file_size_kb", out v)) s.MinFileSizeKb = ParseInt(v, s.MinFileSizeKb);
            if (values.TryGetValue("deep_scan_all_space", out v)) s.DeepScanIncludeUsedSpace = v == "1";
            if (values.TryGetValue("preview_enabled", out v)) s.PreviewEnabled = v == "1";
            if (values.TryGetValue("preview_max_mb", out v)) s.PreviewMaxSizeMb = ParseInt(v, s.PreviewMaxSizeMb);
            if (values.TryGetValue("history_max_entries", out v)) s.HistoryMaxEntries = ParseInt(v, s.HistoryMaxEntries);
            s.Sanitize();   // bad or hand-edited values can never escape into the app
            return s;
        }

        public void Save(AppSettings settings)
        {
            settings.Sanitize();
            using (SQLiteConnection c = _db.Open())
            using (SQLiteTransaction tx = c.BeginTransaction())
            {
                Upsert(c, "theme", settings.Theme);
                Upsert(c, "language", settings.Language);
                Upsert(c, "default_destination", settings.DefaultRecoveryDestination);
                Upsert(c, "scan_type_default", settings.DefaultScanType);
                Upsert(c, "min_file_size_kb", settings.MinFileSizeKb.ToString(CultureInfo.InvariantCulture));
                Upsert(c, "deep_scan_all_space", settings.DeepScanIncludeUsedSpace ? "1" : "0");
                Upsert(c, "preview_enabled", settings.PreviewEnabled ? "1" : "0");
                Upsert(c, "preview_max_mb", settings.PreviewMaxSizeMb.ToString(CultureInfo.InvariantCulture));
                Upsert(c, "history_max_entries", settings.HistoryMaxEntries.ToString(CultureInfo.InvariantCulture));
                tx.Commit();
            }
        }

        private static int ParseInt(string s, int fallback)
        {
            int n;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : fallback;
        }

        private static void Upsert(SQLiteConnection c, string key, string value)
        {
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "INSERT OR REPLACE INTO settings(key, value) VALUES(@k, @v)";
                cmd.Parameters.AddWithValue("@k", key);
                cmd.Parameters.AddWithValue("@v", value ?? string.Empty);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
