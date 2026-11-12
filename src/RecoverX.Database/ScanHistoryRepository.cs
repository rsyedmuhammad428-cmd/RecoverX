using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;

namespace RecoverX.Database
{
    public sealed class ScanHistoryRepository : IScanHistoryRepository
    {
        private readonly SqliteDatabase _db;

        public ScanHistoryRepository(SqliteDatabase db) { _db = db; }

        public long AddSession(ScanSession s)
        {
            using (SQLiteConnection c = _db.Open())
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO scan_sessions(started_utc, finished_utc, drive_letter, volume_name, file_system, scan_type, status, files_found, resumable) " +
                    "VALUES(@s, @f, @d, @v, @fs, @t, @st, @n, @r); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@s", s.StartedUtc.ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@f", s.FinishedUtc.HasValue ? (object)s.FinishedUtc.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
                cmd.Parameters.AddWithValue("@d", s.DriveLetter);
                cmd.Parameters.AddWithValue("@v", (object)s.VolumeName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@fs", (object)s.FileSystem ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@t", s.ScanType);
                cmd.Parameters.AddWithValue("@st", s.Status);
                cmd.Parameters.AddWithValue("@n", s.FilesFound);
                cmd.Parameters.AddWithValue("@r", s.Resumable ? 1 : 0);
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        public IReadOnlyList<ScanSession> GetRecent(int limit)
        {
            var list = new List<ScanSession>();
            using (SQLiteConnection c = _db.Open())
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id, started_utc, finished_utc, drive_letter, volume_name, file_system, scan_type, status, files_found, resumable " +
                    "FROM scan_sessions ORDER BY started_utc DESC LIMIT @lim";
                cmd.Parameters.AddWithValue("@lim", limit);
                using (SQLiteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new ScanSession
                        {
                            Id = r.GetInt64(0),
                            StartedUtc = ParseUtc(r.GetString(1)),
                            FinishedUtc = r.IsDBNull(2) ? (DateTime?)null : ParseUtc(r.GetString(2)),
                            DriveLetter = r.GetString(3),
                            VolumeName = r.IsDBNull(4) ? null : r.GetString(4),
                            FileSystem = r.IsDBNull(5) ? null : r.GetString(5),
                            ScanType = r.GetString(6),
                            Status = r.GetString(7),
                            FilesFound = r.GetInt64(8),
                            Resumable = r.GetInt64(9) != 0
                        });
                    }
                }
            }
            return list;
        }

        public ScanStats GetStats()
        {
            using (SQLiteConnection c = _db.Open())
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT COUNT(*), COALESCE(SUM(CASE WHEN status='Completed' THEN 1 ELSE 0 END),0), COALESCE(SUM(files_found),0) FROM scan_sessions";
                using (SQLiteDataReader r = cmd.ExecuteReader())
                {
                    r.Read();
                    return new ScanStats
                    {
                        TotalScans = r.GetInt64(0),
                        CompletedScans = r.GetInt64(1),
                        TotalFilesFound = r.GetInt64(2)
                    };
                }
            }
        }

        public void UpdateSession(long id, DateTime finishedUtc, string status, long filesFound)
        {
            using (SQLiteConnection c = _db.Open())
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "UPDATE scan_sessions SET finished_utc=@f, status=@s, files_found=@n WHERE id=@i";
                cmd.Parameters.AddWithValue("@f", finishedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@s", status);
                cmd.Parameters.AddWithValue("@n", filesFound);
                cmd.Parameters.AddWithValue("@i", id);
                cmd.ExecuteNonQuery();
            }
        }

        public int MarkInterrupted()
        {
            using (SQLiteConnection c = _db.Open())
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "UPDATE scan_sessions SET status='Interrupted' WHERE status='Running'";
                return cmd.ExecuteNonQuery();
            }
        }

        public int Prune(int keepNewest)
        {
            if (keepNewest < 0) keepNewest = 0;
            using (SQLiteConnection c = _db.Open())
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    "DELETE FROM scan_sessions WHERE id NOT IN (SELECT id FROM scan_sessions ORDER BY started_utc DESC, id DESC LIMIT @n)";
                cmd.Parameters.AddWithValue("@n", keepNewest);
                return cmd.ExecuteNonQuery();
            }
        }

        public int ClearAll()
        {
            using (SQLiteConnection c = _db.Open())
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM scan_sessions";
                return cmd.ExecuteNonQuery();
            }
        }

        private static DateTime ParseUtc(string s)
        {
            return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }
    }
}
