using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RecoverX.Core.Services
{
    /// <summary>
    /// Turns a path recovered from the disk (untrusted text) into a safe path under the recovery folder.
    /// Never allows ".." or rooted components, never overwrites an existing file, keeps names legal for Windows.
    /// </summary>
    public static class RecoveryPathBuilder
    {
        public const int MaxComponentLength = 100;
        public const int MaxFullPathLength = 240;
        private const string InvalidChars = "<>:\"/\\|?*";
        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        public static string SanitizeComponent(string name, int maxLength = MaxComponentLength)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? string.Empty)
                sb.Append(c < 32 || InvalidChars.IndexOf(c) >= 0 ? '_' : c);
            string s = sb.ToString().TrimEnd(' ', '.').TrimStart(' ');
            if (s.Length == 0 || s.Trim('.').Length == 0) return "_";

            int dot = s.IndexOf('.');
            string stem = dot >= 0 ? s.Substring(0, dot) : s;
            if (Reserved.Contains(stem)) s = "_" + s;

            if (s.Length > maxLength)
            {
                int ext = s.LastIndexOf('.');
                string extension = ext > 0 && s.Length - ext <= 20 ? s.Substring(ext) : string.Empty;
                s = s.Substring(0, Math.Max(1, maxLength - extension.Length)) + extension;
                s = s.TrimEnd(' ', '.');
                if (s.Length == 0) s = "_";
            }
            return s;
        }

        /// <summary>Relative path (folders + file name) for an engine path such as "\docs\a.txt".</summary>
        public static string[] Components(string enginePath, bool preserveFolders)
        {
            var parts = new List<string>();
            foreach (string p in (enginePath ?? string.Empty).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                parts.Add(SanitizeComponent(p));
            if (parts.Count == 0) parts.Add("_");
            if (!preserveFolders) return new[] { parts[parts.Count - 1] };
            return parts.ToArray();
        }

        /// <summary>
        /// Full destination path under <paramref name="sessionDirectory"/>. Falls back to a flat name when the folder
        /// structure would make the path too long, and appends " (2)", " (3)"... instead of ever overwriting.
        /// </summary>
        public static string BuildUniquePath(string sessionDirectory, string enginePath, bool preserveFolders, Func<string, bool> exists)
        {
            string[] parts = Components(enginePath, preserveFolders);
            string full = Join(sessionDirectory, parts);
            if (full.Length > MaxFullPathLength && parts.Length > 1) { parts = new[] { parts[parts.Length - 1] }; full = Join(sessionDirectory, parts); }
            if (full.Length > MaxFullPathLength)
            {
                int room = Math.Max(8, MaxFullPathLength - sessionDirectory.Length - 8);
                parts = new[] { SanitizeComponent(parts[0], room) };
                full = Join(sessionDirectory, parts);
            }
            if (!exists(full)) return full;

            string dir = Path.GetDirectoryName(full);
            string file = Path.GetFileName(full);
            int dot = file.LastIndexOf('.');
            string stem = dot > 0 ? file.Substring(0, dot) : file;
            string ext = dot > 0 ? file.Substring(dot) : string.Empty;
            for (int n = 2; n < 100000; n++)
            {
                string candidate = Path.Combine(dir, stem + " (" + n + ")" + ext);
                if (!exists(candidate)) return candidate;
            }
            throw new IOException("Could not find an unused file name for " + file);
        }

        private static string Join(string root, string[] parts)
        {
            string p = root;
            foreach (string part in parts) p = Path.Combine(p, part);
            return p;
        }
    }
}
