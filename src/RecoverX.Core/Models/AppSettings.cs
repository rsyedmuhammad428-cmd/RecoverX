using System;

namespace RecoverX.Core.Models
{
    public sealed class AppSettings
    {
        public const string LightTheme = "Light";
        public const string DarkTheme = "Dark";
        public const string QuickScan = "Quick";
        public const string DeepScan = "Deep";
        public const string SnapshotScan = "Snapshot";

        public string Theme { get; set; } = LightTheme;
        public string Language { get; set; } = "en";
        public string DefaultRecoveryDestination { get; set; } = string.Empty;

        // Scan preferences
        public string DefaultScanType { get; set; } = QuickScan;
        public int MinFileSizeKb { get; set; } = 0;
        /// <summary>Deep Scan on NTFS normally reads only free space; this also reads space used by existing files.</summary>
        public bool DeepScanIncludeUsedSpace { get; set; }

        // Preview settings
        public bool PreviewEnabled { get; set; } = true;
        public int PreviewMaxSizeMb { get; set; } = 25;

        // Scan history retention
        public int HistoryMaxEntries { get; set; } = 200;

        public const int MaxMinFileSizeKb = 1048576;
        public const int MaxPreviewMb = 1024;
        public const int MinHistoryEntries = 10;
        public const int MaxHistoryEntries = 10000;

        /// <summary>Forces every value into its valid range so bad stored data can never reach the rest of the app.</summary>
        public void Sanitize()
        {
            if (Theme != LightTheme && Theme != DarkTheme) Theme = LightTheme;
            if (DefaultScanType != QuickScan && DefaultScanType != DeepScan) DefaultScanType = QuickScan;
            if (string.IsNullOrWhiteSpace(Language)) Language = "en";
            if (DefaultRecoveryDestination == null) DefaultRecoveryDestination = string.Empty;
            MinFileSizeKb = Math.Max(0, Math.Min(MaxMinFileSizeKb, MinFileSizeKb));
            PreviewMaxSizeMb = Math.Max(1, Math.Min(MaxPreviewMb, PreviewMaxSizeMb));
            HistoryMaxEntries = Math.Max(MinHistoryEntries, Math.Min(MaxHistoryEntries, HistoryMaxEntries));
        }
    }
}
