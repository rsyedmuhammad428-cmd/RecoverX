using System;

namespace RecoverX.Core.Models
{
    /// <summary>One row of scan history. Only real sessions recorded by the app are ever stored.</summary>
    public sealed class ScanSession
    {
        public long Id { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public string DriveLetter { get; set; }
        public string VolumeName { get; set; }
        public string FileSystem { get; set; }
        public string ScanType { get; set; }   // "Quick" | "Deep"
        public string Status { get; set; }     // "Completed" | "Cancelled" | "Failed" | "Interrupted"
        public long FilesFound { get; set; }
        public bool Resumable { get; set; }    // only true when scan state can be safely reconstructed
    }

    public sealed class ScanStats
    {
        public long TotalScans { get; set; }
        public long CompletedScans { get; set; }
        public long TotalFilesFound { get; set; }
    }
}
