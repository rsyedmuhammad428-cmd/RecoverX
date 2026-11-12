using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;

namespace RecoverX.Database
{
    public sealed class CategoryRepository : ICategoryRepository
    {
        private readonly SqliteDatabase _db;

        public CategoryRepository(SqliteDatabase db) { _db = db; }

        public IList<FileCategory> GetAll()
        {
            var list = new List<FileCategory>();
            using (SQLiteConnection c = _db.Open())
            using (SQLiteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT id, extensions, enabled, sort_order FROM file_categories ORDER BY sort_order, id";
                using (SQLiteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new FileCategory
                        {
                            Id = r.GetString(0),
                            Extensions = r.GetString(1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
                            Enabled = r.GetInt64(2) != 0,
                            SortOrder = (int)r.GetInt64(3)
                        });
                    }
                }
            }
            if (list.Count == 0) return DefaultCategories.Create();   // table emptied by hand: fall back to defaults
            return list;
        }

        public void Save(IEnumerable<FileCategory> categories)
        {
            using (SQLiteConnection c = _db.Open())
            using (SQLiteTransaction tx = c.BeginTransaction())
            {
                foreach (FileCategory cat in categories)
                {
                    using (SQLiteCommand cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "INSERT OR REPLACE INTO file_categories(id, extensions, enabled, sort_order) VALUES(@i, @e, @n, @o)";
                        cmd.Parameters.AddWithValue("@i", cat.Id);
                        cmd.Parameters.AddWithValue("@e", string.Join(",", cat.Extensions));
                        cmd.Parameters.AddWithValue("@n", cat.Enabled ? 1 : 0);
                        cmd.Parameters.AddWithValue("@o", cat.SortOrder);
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
        }
    }
}
