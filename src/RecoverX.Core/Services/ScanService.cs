using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;

namespace RecoverX.Core.Services
{
    public sealed class ScanOutcome
    {
        public ScanMode Mode { get; set; }
        public EngineStatus Status { get; set; }
        public string Detail { get; set; }
        public IScanSession Session { get; set; }              // kept open so files can be previewed/recovered; the caller disposes it
        public IReadOnlyList<ScanEntry> Entries { get; set; } = new List<ScanEntry>();
        public ScanProgress FinalProgress { get; set; }
        public bool Cancelled { get { return Status == EngineStatus.Cancelled; } }
        public bool HasResults { get { return Session != null; } }
    }

    public interface IScanService
    {
        /// <summary>
        /// Runs a Quick or Deep Scan off the UI thread and records it in the history. `onSessionOpened` receives the live
        /// session as soon as it exists so the caller can pause/resume it.
        /// </summary>
        Task<ScanOutcome> RunAsync(DriveModel drive, ScanMode mode, AppSettings settings, IProgress<ScanProgress> progress,
                                   CancellationToken cancel, Action<IScanSession> onSessionOpened = null);
    }

    /// <summary>Opens, runs and records scans. Quick Scan needs NTFS, FAT12/16/32 or exFAT; Deep Scan works on any readable drive.</summary>
    public sealed class ScanService : IScanService
    {
        private readonly IEngineClient _engine;
        private readonly IScanHistoryRepository _history;
        private readonly IAppLogger _log;
        private readonly IShadowCopyService _shadow;

        public ScanService(IEngineClient engine, IScanHistoryRepository history, IAppLogger log, IShadowCopyService shadow = null)
        { _engine = engine; _history = history; _log = log; _shadow = shadow; }

        /// <summary>Windows keeps restore points (shadow copies) for NTFS volumes only.</summary>
        public static bool SupportsPreviousVersions(DriveModel d) { return IsNtfs(d); }

        /// <summary>Quick Scan reads the file system's own records: NTFS, FAT12/16/32 ("FAT", "FAT32") and exFAT.</summary>
        public static bool SupportsQuickScan(DriveModel d)
        {
            return d != null && (IsNtfs(d) || IsFatFamily(d));
        }

        public static bool IsNtfs(DriveModel d) { return d != null && string.Equals(d.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase); }

        public static bool IsFatFamily(DriveModel d)
        {
            string fs = d == null ? null : d.FileSystem;
            return string.Equals(fs, "FAT32", StringComparison.OrdinalIgnoreCase) || string.Equals(fs, "exFAT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fs, "FAT", StringComparison.OrdinalIgnoreCase) || string.Equals(fs, "FAT16", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fs, "FAT12", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<ScanOutcome> RunAsync(DriveModel drive, ScanMode mode, AppSettings settings, IProgress<ScanProgress> progress,
                                                CancellationToken cancel, Action<IScanSession> onSessionOpened = null)
        {
            if (mode == ScanMode.Quick && !SupportsQuickScan(drive))
                return new ScanOutcome { Mode = mode, Status = EngineStatus.NotNtfs };
            if (mode == ScanMode.PreviousVersions && (_shadow == null || !SupportsPreviousVersions(drive)))
                return new ScanOutcome { Mode = mode, Status = EngineStatus.Unsupported };
            if (mode != ScanMode.PreviousVersions && !_engine.IsAvailable)
                return new ScanOutcome { Mode = mode, Status = EngineStatus.Unavailable, Detail = _engine.UnavailableReason };

            ScanOpenResult open;
            try
            {
                open = await Task.Run(() =>
                {
                    switch (mode)
                    {
                        case ScanMode.Quick: return IsNtfs(drive) ? _engine.OpenNtfsScan(drive.RootPath, new ScanOptions()) : _engine.OpenFatScan(drive.RootPath, new ScanOptions());
                        case ScanMode.Deep: return _engine.OpenDeepScan(drive.RootPath, new DeepScanOptions { IncludeUsedSpace = settings != null && settings.DeepScanIncludeUsedSpace });
                        default: return _shadow.OpenSnapshotScan(drive);
                    }
                });
            }
            catch (EngineException ex) { return new ScanOutcome { Mode = mode, Status = EngineStatus.Internal, Detail = ex.Message }; }
            if (open.Status != EngineStatus.Ok) return new ScanOutcome { Mode = mode, Status = open.Status, Detail = open.Detail };

            IScanSession session = open.Session;
            if (onSessionOpened != null) onSessionOpened(session);
            long historyId = 0;
            try
            {
                historyId = _history.AddSession(new ScanSession
                {
                    StartedUtc = DateTime.UtcNow, DriveLetter = drive.Letter, VolumeName = drive.VolumeName, FileSystem = drive.FileSystem,
                    ScanType = mode == ScanMode.Quick ? AppSettings.QuickScan : mode == ScanMode.Deep ? AppSettings.DeepScan : AppSettings.SnapshotScan, Status = "Running"
                });
            }
            catch (Exception ex) { _log.Warn("Could not record scan start: " + ex.Message); }

            EngineStatus status;
            try
            {
                Task<EngineStatus> run = Task.Run(() => session.Run(cancel));
                while (!run.IsCompleted)
                {
                    if (progress != null) progress.Report(session.GetProgress());
                    await Task.WhenAny(run, Task.Delay(150));
                }
                status = await run;
            }
            catch (Exception ex)
            {
                _log.Error(mode + " Scan failed", ex);
                Finish(historyId, "Failed", 0);
                session.Dispose();
                return new ScanOutcome { Mode = mode, Status = EngineStatus.Internal, Detail = ex.Message };
            }

            ScanProgress final = session.GetProgress();
            if (progress != null) progress.Report(final);

            if (status != EngineStatus.Ok && status != EngineStatus.Cancelled)
            {
                Finish(historyId, "Failed", 0);
                session.Dispose();
                return new ScanOutcome { Mode = mode, Status = status, FinalProgress = final };
            }

            IReadOnlyList<ScanEntry> entries;
            try { entries = await Task.Run(() => session.GetResults()); }
            catch (Exception ex)
            {
                _log.Error("Reading scan results failed", ex);
                Finish(historyId, "Failed", 0);
                session.Dispose();
                return new ScanOutcome { Mode = mode, Status = EngineStatus.Internal, Detail = ex.Message };
            }
            Finish(historyId, status == EngineStatus.Cancelled ? "Cancelled" : "Completed", entries.Count);
            _log.Info(mode + " Scan of " + drive.Letter + " " + status + ": " + entries.Count + " files"
                      + (mode == ScanMode.Deep ? ", " + final.ReadErrors + " unreadable regions" : ", " + final.BadRecords + " unreadable records/folders"));
            return new ScanOutcome { Mode = mode, Status = status, Session = session, Entries = entries, FinalProgress = final };
        }

        private void Finish(long id, string status, long found)
        {
            if (id == 0) return;
            try { _history.UpdateSession(id, DateTime.UtcNow, status, found); }
            catch (Exception ex) { _log.Warn("Could not record scan result: " + ex.Message); }
        }
    }
}
