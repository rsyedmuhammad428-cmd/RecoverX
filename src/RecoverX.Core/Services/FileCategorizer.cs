using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RecoverX.Core.Models;

namespace RecoverX.Core.Services
{
    public static class FileCategorizer
    {
        private static readonly Regex ValidExt = new Regex("^[a-z0-9]{1,10}$", RegexOptions.Compiled);

        /// <summary>Returns the category id for a file name or bare extension; unknown extensions map to "Other".</summary>
        public static string Classify(string fileNameOrExtension, IEnumerable<FileCategory> categories)
        {
            string ext = Normalize(Path.GetExtension(fileNameOrExtension ?? string.Empty));
            if (ext.Length == 0) ext = Normalize(fileNameOrExtension);
            foreach (var c in categories)
                if (!c.IsCatchAll && c.Extensions.Any(e => e == ext)) return c.Id;
            return DefaultCategories.OtherId;
        }

        /// <summary>Whether files of this name should be reported, according to the enabled categories.</summary>
        public static bool IsWanted(string fileName, IList<FileCategory> categories)
        {
            string id = Classify(fileName, categories);
            var cat = categories.FirstOrDefault(c => c.Id == id);
            return cat != null && cat.Enabled;
        }

        /// <summary>Parses "jpg, .PNG ;gif" into normalized, de-duplicated extensions. False if anything is invalid.</summary>
        public static bool TryParseExtensions(string text, out List<string> extensions)
        {
            extensions = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (string raw in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string e = Normalize(raw);
                if (!ValidExt.IsMatch(e)) { extensions = new List<string>(); return false; }
                if (!extensions.Contains(e)) extensions.Add(e);
            }
            return extensions.Count > 0;
        }

        /// <summary>Finds an extension of `candidate` that another category already owns, or null.</summary>
        public static string FindConflict(FileCategory candidate, IEnumerable<FileCategory> all, out string ownerId)
        {
            ownerId = null;
            foreach (var other in all)
            {
                if (other.Id == candidate.Id || other.IsCatchAll) continue;
                string hit = candidate.Extensions.FirstOrDefault(e => other.Extensions.Contains(e));
                if (hit != null) { ownerId = other.Id; return hit; }
            }
            return null;
        }

        private static string Normalize(string ext)
        {
            return (ext ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        }
    }
}
