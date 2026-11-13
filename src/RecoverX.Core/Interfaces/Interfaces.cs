using System;
using System.Collections.Generic;
using RecoverX.Core.Models;

namespace RecoverX.Core.Interfaces
{
    public interface IDriveService
    {
        /// <summary>Enumerates mounted fixed and removable volumes. Never throws for an inaccessible drive.</summary>
        IReadOnlyList<DriveModel> GetDrives();
    }

    public interface ISettingsService
    {
        AppSettings Load();
        void Save(AppSettings settings);
    }

    public interface ICategoryRepository
    {
        IList<FileCategory> GetAll();
        void Save(IEnumerable<FileCategory> categories);
    }

    public interface IScanHistoryRepository
    {
        IReadOnlyList<ScanSession> GetRecent(int limit);
        ScanStats GetStats();
        long AddSession(ScanSession session);   // used from Phase 3 onward
        int Prune(int keepNewest);              // deletes all but the newest N; returns rows deleted
        int ClearAll();                         // returns rows deleted
        void UpdateSession(long id, DateTime finishedUtc, string status, long filesFound);
        int MarkInterrupted();                  // sessions left in "Running" by a crash or kill; returns rows changed
    }

    public interface IRecoveredFilesRepository
    {
        long Add(RecoveredFileRecord record);
        IReadOnlyList<RecoveredFileRecord> GetRecent(int limit);
        long Count();
        int ClearAll();
    }

    public interface IAppLogger
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message, Exception ex = null);
    }

    /// <summary>Wrapper over the native C++ engine. Every member is safe to call when the engine is missing.</summary>
    public interface IEngineClient
    {
        bool IsAvailable { get; }
        string UnavailableReason { get; }
        string Version { get; }
        string LoadedPath { get; }
        /// <summary>Hardware details for all fixed/removable lettered volumes. Throws EngineException on engine failure.</summary>
        IReadOnlyList<VolumeHardwareInfo> EnumerateVolumes();
        /// <summary>Opens the volume read-only, reads sector 0 and identifies the file system. Needs administrator rights.</summary>
        VolumeProbeResult ProbeVolume(string root);
        DestinationVerdict ValidateDestination(string sourceRoot, string destinationDirectory);
        /// <summary>Opens a read-only NTFS Quick Scan session on a drive (needs administrator rights on a real drive).</summary>
        ScanOpenResult OpenNtfsScan(string root, ScanOptions options);
        /// <summary>Opens a read-only Deep Scan (signature search) session on any drive (needs administrator rights on a real drive).</summary>
        ScanOpenResult OpenDeepScan(string root, DeepScanOptions options);
        /// <summary>Opens a read-only FAT12/16/32 or exFAT Quick Scan session (needs administrator rights on a real drive).</summary>
        ScanOpenResult OpenFatScan(string root, ScanOptions options);
        /// <summary>Checks a file's first bytes against the type its name claims. NotChecked if the engine is unavailable.</summary>
        HeaderCheck CheckHeader(string fileName, byte[] data, int length);
    }

    public sealed class ScanOpenResult
    {
        public EngineStatus Status { get; set; }
        public IScanSession Session { get; set; }      // non-null only when Status is Ok
        public string Detail { get; set; }
    }

    /// <summary>A Quick or Deep Scan in progress or finished. Disposing it closes the drive handle.</summary>
    public interface IScanSession : IDisposable
    {
        ScanMode Mode { get; }
        /// <summary>Pauses or resumes a running scan (the engine stops at its next check point). Safe to call at any time.</summary>
        void SetPaused(bool paused);
        /// <summary>Thread-safe; may be called while Run is executing on another thread.</summary>
        ScanProgress GetProgress();
        /// <summary>Blocking. Cancelled when the token is cancelled (results so far stay readable).</summary>
        EngineStatus Run(System.Threading.CancellationToken cancel);
        IReadOnlyList<ScanEntry> GetResults();
        /// <summary>Reads file content (not metadata). Read-only; the caller writes the recovered file.</summary>
        EngineStatus ReadFile(int index, long offset, byte[] buffer, int count, out int bytesRead);
    }

    public interface IDestinationValidator
    {
        DestinationVerdict Validate(string sourceRoot, string destinationDirectory);
    }

    public interface ILocalizer
    {
        string Get(string key);
        string Format(string key, params object[] args);
        System.Globalization.CultureInfo Culture { get; }
    }

    public sealed class EngineException : Exception
    {
        public EngineException(string message, Exception inner = null) : base(message, inner) { }
    }
}
