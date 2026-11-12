using System;

namespace RecoverX.Core.Models
{
    public enum DriveKind { Fixed, Removable, Other }

    /// <summary>
    /// A mounted volume. Basic fields come from System.IO.DriveInfo (Phase 1); hardware fields come from the native
    /// engine's Windows storage queries (Phase 2) and stay at their "unknown" defaults when the engine is unavailable.
    /// Display text lives in the UI layer so it can be localized; this class holds data only.
    /// </summary>
    public sealed class DriveModel
    {
        public string Letter { get; set; }          // e.g. "C:"
        public string RootPath { get; set; }        // e.g. "C:\"
        public string VolumeName { get; set; }
        public string FileSystem { get; set; }      // NTFS, FAT32, exFAT, ...
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }
        public DriveKind Kind { get; set; }
        public bool IsReady { get; set; }
        public bool IsSystemDrive { get; set; }

        // Phase 2 hardware details
        public BusType Bus { get; set; }
        public MediaType Media { get; set; }
        public bool? RemovableMedia { get; set; }
        public bool? TrimEnabled { get; set; }
        public string Vendor { get; set; }
        public string Product { get; set; }
        public DetectionSource Source { get; set; }

        public long UsedBytes { get { return Math.Max(0, TotalBytes - FreeBytes); } }
        public double UsedPercent { get { return TotalBytes > 0 ? 100.0 * UsedBytes / TotalBytes : 0; } }

        /// <summary>"Vendor Product" when the device reported them, otherwise empty.</summary>
        public string DeviceName
        {
            get { return ((Vendor ?? string.Empty) + " " + (Product ?? string.Empty)).Trim(); }
        }

        /// <summary>True if the file system is one RecoverX plans to support. Does NOT mean recovery is implemented yet.</summary>
        public bool IsPlannedFileSystem
        {
            get
            {
                return string.Equals(FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(FileSystem, "FAT32", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(FileSystem, "exFAT", StringComparison.OrdinalIgnoreCase);
            }
        }

        public string TotalDisplay { get { return ByteFormatter.Format(TotalBytes); } }
        public string UsedDisplay { get { return ByteFormatter.Format(UsedBytes); } }
        public string FreeDisplay { get { return ByteFormatter.Format(FreeBytes); } }
    }
}
