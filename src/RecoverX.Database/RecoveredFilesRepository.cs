using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;

namespace RecoverX.Database
{
    public sealed class RecoveredFilesRepository : IRecoveredFilesRepository
    {
        private readonly SqliteDatabase _db;
        public RecoveredFilesRepository(SqliteDatabase db) { _db = db; }

        public long Add(RecoveredFileRecord r)
        {
            using (SQLiteConnection c = _db.Open())
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO recovered_files(recovered_utc, source_drive, original_path, saved_path, size, status) VALUES(@t,@d,@o,@s,@z,@st); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@t", r.RecoveredUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@d", r.SourceDrive ?? string.Empty);
                cmd.Parameters.AddWithValue("@o", r.OriginalPath ?? string.Empty);
                cmd.Parameters.AddWithValue("@s", r.SavedPath ?? string.Empty);
                cmd.Parameters.AddWithValue("@z", r.Size);
                cmd.Parameters.AddWithValue("@st", r.Status ?? string.Empty);
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        public IReadOnlyList<RecoveredFileRecord> GetRecent(int limit)
        {
            var list = new List<RecoveredFileRecord>();
            using (SQLiteConnection c = _db.Open())
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT id, recovered_utc, source_drive, original_path, saved_path, size, status FROM recovered_files ORDER BY recovered_utc DESC, id DESC LIMIT @n";
                cmd.Parameters.AddWithValue("@n", limit);
                using (SQLiteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new RecoveredFileRecord
                        {
                            Id = r.GetInt64(0),
                            RecoveredUtc = DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
                            SourceDrive = r.GetString(2), OriginalPath = r.GetString(3), SavedPath = r.GetString(4),
                            Size = r.GetInt64(5), Status = r.GetString(6)
                        });
                }
            }
            return list;
        }

        public long Count()
        {
            using (SQLiteConnection c = _db.Open())
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM recovered_files";
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        public int ClearAll()
        {
            using (SQLiteConnection c = _db.Open())
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM recovered_files";
                return cmd.ExecuteNonQuery();
            }
        }
    }
}
