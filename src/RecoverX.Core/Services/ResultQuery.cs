using System;
using System.Collections.Generic;
using System.Linq;
using RecoverX.Core.Models;

namespace RecoverX.Core.Services
{
    public enum ResultSort { Name = 0, Size = 1, Date = 2, Rating = 3, Type = 4 }

    public enum SizeBucket { Any = 0, Under1Mb = 1, From1To100Mb = 2, Over100Mb = 3 }

    public enum RatingFilter { All = 0, Excellent = 1, Partial = 2, Poor = 3, Recoverable = 4 }

    /// <summary>Search, filter and sort settings for the results list. Pure data; see Apply.</summary>
    public sealed class ResultQuery
    {
        public string Search { get; set; } = string.Empty;
        /// <summary>Category id (FileCategory.Id) or null for all categories.</summary>
        public string CategoryId { get; set; }
        public RatingFilter Rating { get; set; } = RatingFilter.All;
        public SizeBucket Size { get; set; } = SizeBucket.Any;
        /// <summary>Inclusive local-date range. Files without a known date are excluded when a range is set.</summary>
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public ResultSort SortBy { get; set; } = ResultSort.Name;
        public bool Descending { get; set; }

        public const long OneMb = 1024L * 1024L;

        /// <summary>Applies search, filters and sort. Never modifies the input; stable for equal keys.</summary>
        public static List<ScanEntry> Apply(IEnumerable<ScanEntry> source, ResultQuery q, IList<FileCategory> categories)
        {
            if (source == null) return new List<ScanEntry>();
            q = q ?? new ResultQuery();
            string search = (q.Search ?? string.Empty).Trim();
            DateTime? from = q.From.HasValue ? q.From.Value.Date : (DateTime?)null;
            DateTime? toExclusive = q.To.HasValue ? q.To.Value.Date.AddDays(1) : (DateTime?)null;

            IEnumerable<ScanEntry> e = source;
            if (search.Length > 0)
                e = e.Where(x => (x.Path ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrEmpty(q.CategoryId) && categories != null)
                e = e.Where(x => FileCategorizer.Classify(x.Name, categories) == q.CategoryId);
            switch (q.Rating)
            {
                case RatingFilter.Excellent: e = e.Where(x => x.Rating == RecoveryRating.Excellent); break;
                case RatingFilter.Partial: e = e.Where(x => x.Rating == RecoveryRating.Partial); break;
                case RatingFilter.Poor: e = e.Where(x => x.Rating == RecoveryRating.Poor); break;
                case RatingFilter.Recoverable: e = e.Where(x => x.Rating == RecoveryRating.Excellent && x.Size > 0); break;
            }
            switch (q.Size)
            {
                case SizeBucket.Under1Mb: e = e.Where(x => x.Size < OneMb); break;
                case SizeBucket.From1To100Mb: e = e.Where(x => x.Size >= OneMb && x.Size <= 100 * OneMb); break;
                case SizeBucket.Over100Mb: e = e.Where(x => x.Size > 100 * OneMb); break;
            }
            if (from.HasValue || toExclusive.HasValue)
                e = e.Where(x =>
                {
                    DateTime? d = x.DisplayDate;
                    if (!d.HasValue) return false;
                    return (!from.HasValue || d.Value >= from.Value) && (!toExclusive.HasValue || d.Value < toExclusive.Value);
                });

            List<ScanEntry> list = e.ToList();
            Comparison<ScanEntry> cmp = Comparer(q.SortBy);
            // List.Sort is not stable; break ties by the session index so the order is deterministic.
            list.Sort((a, b) =>
            {
                int c = cmp(a, b);
                if (q.Descending) c = -c;
                return c != 0 ? c : a.Index.CompareTo(b.Index);
            });
            return list;
        }

        private static Comparison<ScanEntry> Comparer(ResultSort s)
        {
            switch (s)
            {
                case ResultSort.Size: return (a, b) => a.Size.CompareTo(b.Size);
                case ResultSort.Date: return (a, b) => Nullable.Compare(a.DisplayDate, b.DisplayDate);
                case ResultSort.Rating: return (a, b) => a.Rating.CompareTo(b.Rating);
                case ResultSort.Type: return (a, b) => string.CompareOrdinal(a.Extension, b.Extension);
                default: return (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
