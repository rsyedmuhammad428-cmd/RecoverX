using System;
using System.Collections.Generic;

namespace RecoverX.Core.Models
{
    /// <summary>Evidence-based recovery condition (mirrors RX_COND_* in rx_engine.h). Never inferred from the file name alone.</summary>
    public enum RecoveryCondition
    {
        Unknown = 0, Verified = 1, Likely = 2, Partial = 3, Lost = 4, Unsupported = 5, Empty = 6
    }

    public enum HeaderCheck { NotChecked = 0, Match = 1, Mismatch = 2, NoSignature = 3 }

    public enum ScanPhase { Opening = 1, Scanning = 2, Validating = 3, Paths = 4, Done = 5 }

    /// <summary>Quick Scan reads the NTFS file table; Deep Scan searches the raw data for file signatures (any file system).</summary>
    /// <summary>PreviousVersions: files found in Windows restore points (Volume Shadow Copies) that are gone from the drive.</summary>
    public enum ScanMode { Quick = 0, Deep = 1, PreviousVersions = 2 }

    /// <summary>How a result was found.</summary>
    public enum EntryOrigin { FileTable = 0, Signature = 1, Snapshot = 2 }

    /// <summary>What Deep Scan actually verified for a carved file (mirrors RX_EV_* in rx_engine.h).</summary>
    [Flags]
    public enum CarveEvidence
    {
        None = 0, EndFound = 1, StructureOk = 2, ChecksumOk = 4, TailConfirmed = 8, Truncated = 16, Corrupt = 32,
        InUse = 64, AllocationUnknown = 128, TimeIsLocal = 256
    }

    /// <summary>
    /// The three user-facing recovery ratings. Excellent = no sign of damage was found (and, where the format allows it, the
    /// structure was verified); Partial = some data is overwritten or broken; Poor = the data is overwritten or unusable.
    /// Always an estimate: it never guarantees the recovered file will open.
    /// </summary>
    public enum RecoveryRating { Excellent = 0, Partial = 1, Poor = 2, NotRecoverable = 3 }

    [Flags]
    public enum EntryFlags
    {
        None = 0, Resident = 1, PathUncertain = 2, Compressed = 4, Encrypted = 8, Sparse = 16,
        AttributeList = 32, PathTruncated = 64, HasStreams = 128, BadRuns = 256,
        NameGuessed = 512,      // FAT: first letter of a short name lost on deletion (shown as '_')
        ChainFollowed = 1024    // exFAT: located through a FAT chain left intact
    }

    /// <summary>One file found by Quick Scan (file table) or Deep Scan (signature).</summary>
    public sealed class ScanEntry
    {
        public int Index { get; set; }                  // position in the scan session (used to read the file's data)
        public EntryOrigin Origin { get; set; }
        public long MftNumber { get; set; }
        public long VolumeOffset { get; set; }          // Deep Scan: byte offset of the file on the volume
        public CarveEvidence Evidence { get; set; }     // Deep Scan: what was verified
        /// <summary>Date recorded inside the file (photo EXIF / video header). Deep Scan only; null if none.</summary>
        public DateTime? ContentTime { get; set; }
        /// <summary>True when ContentTime is a camera wall-clock time without time zone (EXIF), so it must not be converted.</summary>
        public bool ContentTimeIsLocal { get; set; }
        /// <summary>Previous Versions: when the restore point holding this copy was taken (UTC).</summary>
        public DateTime? SnapshotTime { get; set; }
        public string Path { get; set; }                // "\folder\name.ext"
        public long Size { get; set; }
        public DateTime? Created { get; set; }
        public DateTime? Modified { get; set; }
        public DateTime? Accessed { get; set; }
        public DateTime? RecordChanged { get; set; }    // last change of the MFT record; often near the deletion time
        public RecoveryCondition Condition { get; set; }
        public HeaderCheck Header { get; set; }
        public EntryFlags Flags { get; set; }
        public long ClustersTotal { get; set; }
        public long ClustersInUse { get; set; }

        public bool IsCarved { get { return Origin == EntryOrigin.Signature; } }

        /// <summary>Best available date for display, filtering and sorting (local time): modified time, else the content time.</summary>
        public DateTime? DisplayDate
        {
            get
            {
                if (Modified.HasValue) return Modified.Value.ToLocalTime();
                if (ContentTime.HasValue) return ContentTimeIsLocal ? ContentTime.Value : ContentTime.Value.ToLocalTime();
                return null;
            }
        }

        public string Extension
        {
            get
            {
                string n = Name;
                int i = n.LastIndexOf('.');
                return i >= 0 && i < n.Length - 1 ? n.Substring(i + 1).ToLowerInvariant() : string.Empty;
            }
        }

        public RecoveryRating Rating { get { return Services.ConditionInfo.Rating(Condition); } }

        public string Name
        {
            get
            {
                string p = Path ?? string.Empty;
                int i = p.LastIndexOf('\\');
                return i >= 0 ? p.Substring(i + 1) : p;
            }
        }

        /// <summary>Conditions for which recovery is expected to give usable data.</summary>
        public bool IsLikelyRecoverable
        {
            get { return Condition == RecoveryCondition.Verified || Condition == RecoveryCondition.Likely; }
        }

        /// <summary>Whether the engine can read this file's data at all (compressed/encrypted data cannot be read yet).</summary>
        public bool CanAttemptRecovery { get { return Condition != RecoveryCondition.Unsupported; } }
    }

    public sealed class ScanProgress
    {
        public ScanMode Mode { get; set; }
        public ScanPhase Phase { get; set; }
        /// <summary>Deep Scan counts bytes (Total/Done); Quick Scan counts file-table records.</summary>
        public bool CountsBytes { get; set; }
        public bool Paused { get; set; }
        public long ReadErrors { get; set; }
        public long Total { get; set; }
        public long Done { get; set; }
        public int Candidates { get; set; }
        public bool Truncated { get; set; }
        public bool BitmapUnavailable { get; set; }
        public bool MftPartial { get; set; }
        public long BadRecords { get; set; }
        public bool IsDone { get; set; }
        public double Fraction { get { return Total > 0 ? Math.Min(1.0, (double)Done / Total) : 0; } }
    }

    public sealed class ScanOptions
    {
        public bool ValidateContent { get; set; } = true;
        public int MaxResults { get; set; } = 250000;
    }

    public sealed class DeepScanOptions
    {
        /// <summary>Also search space used by existing files (NTFS). Off by default: existing files are not "lost".</summary>
        public bool IncludeUsedSpace { get; set; }
        public int MaxResults { get; set; } = 250000;
    }

    public enum RecoveryOutcome { Failed = 0, Verified, Unverified, Suspect, Unsupported, Cancelled }   // default must never read as success

    /// <summary>Result of recovering one file.</summary>
    public sealed class RecoveryItemResult
    {
        public ScanEntry Entry { get; set; }
        public RecoveryOutcome Outcome { get; set; }
        public string SavedPath { get; set; }
        public long BytesWritten { get; set; }
        /// <summary>Resource key explaining a Suspect/Failed outcome (e.g. "Reason_HeaderMismatch"); null otherwise.</summary>
        public string ReasonKey { get; set; }
        public string ReasonDetail { get; set; }
    }

    public sealed class RecoveryRequest
    {
        public string SourceRoot { get; set; }          // "E:\" - used for the destination safety check and folder naming
        public string DestinationDirectory { get; set; }
        public bool PreserveFolders { get; set; } = true;
    }

    public sealed class RecoveryProgress
    {
        public int FileNumber { get; set; }
        public int FileCount { get; set; }
        public string CurrentName { get; set; }
        public long BytesDone { get; set; }
        public long BytesTotal { get; set; }
    }

    public sealed class RecoverySummary
    {
        public string Error { get; set; }               // null on success; otherwise a resource key
        public string ErrorDetail { get; set; }
        public long NeededBytes { get; set; }
        public long AvailableBytes { get; set; }
        public string SessionDirectory { get; set; }
        public List<RecoveryItemResult> Items { get; } = new List<RecoveryItemResult>();
        public bool Cancelled { get; set; }
        public int Succeeded { get { int n = 0; foreach (var i in Items) if (i.Outcome == RecoveryOutcome.Verified || i.Outcome == RecoveryOutcome.Unverified) n++; return n; } }
        public int NeedAttention { get { int n = 0; foreach (var i in Items) if (i.Outcome == RecoveryOutcome.Suspect) n++; return n; } }
        public int Failed { get { int n = 0; foreach (var i in Items) if (i.Outcome == RecoveryOutcome.Failed || i.Outcome == RecoveryOutcome.Unsupported) n++; return n; } }
    }

    /// <summary>A row of the persisted "Recovered Files" list.</summary>
    public sealed class RecoveredFileRecord
    {
        public long Id { get; set; }
        public DateTime RecoveredUtc { get; set; }
        public string SourceDrive { get; set; }
        public string OriginalPath { get; set; }
        public string SavedPath { get; set; }
        public long Size { get; set; }
        public string Status { get; set; }              // "Verified" | "Unverified" | "Suspect"
    }
}
