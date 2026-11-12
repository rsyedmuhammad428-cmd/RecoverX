using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;

namespace RecoverX.Core.Services
{
    public sealed class QuickScanOutcome
    {
        public EngineStatus Status { get; set; }
        public string Detail { get; set; }
        public INtfsScanSession Session { get; set; }          // kept open so files can be recovered; the caller disposes it
        public IReadOnlyList<ScanEntry> Entries { get; set; } = new List<ScanEntry>();
        public ScanProgress FinalProgress { get; set; }
        public bool Cancelled { get { return Status == EngineStatus.Cancelled; } }
        public bool HasResults { get { return Session != null; } }
    }

    public interface IQuickScanService
    {
        Task<QuickScanOutcome> RunAsync(DriveModel drive, AppSettings settings, IProgress<ScanProgress> progress, CancellationToken cancel);
    }

    /// <summary>Runs an NTFS Quick Scan off the UI thread, reports progress, and records the scan in the history.</summary>
    public sealed class QuickScanService : IQuickScanService
    {
        private readonly IEngineClient _engine;
        private readonly IScanHistoryRepository _history;
        private readonly IAppLogger _log;

        public QuickScanService(IEngineClient engine, IScanHistoryRepository history, IAppLogger log)
        { _engine = engine; _history = history; _log = log; }

        public async Task<QuickScanOutcome> RunAsync(DriveModel drive, AppSettings settings, IProgress<ScanProgress> progress, CancellationToken cancel)
        {
            if (!string.Equals(drive.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
                return new QuickScanOutcome { Status = EngineStatus.NotNtfs };
            if (!_engine.IsAvailable)
                return new QuickScanOutcome { Status = EngineStatus.Unavailable, Detail = _engine.UnavailableReason };

            NtfsScanOpenResult open;
            try { open = await Task.Run(() => _engine.OpenNtfsScan(drive.RootPath, new ScanOptions())); }
            catch (EngineException ex) { return new QuickScanOutcome { Status = EngineStatus.Internal, Detail = ex.Message }; }
            if (open.Status != EngineStatus.Ok) return new QuickScanOutcome { Status = open.Status, Detail = open.Detail };

            INtfsScanSession session = open.Session;
            long historyId = 0;
            try
            {
                historyId = _history.AddSession(new ScanSession
                {
                    StartedUtc = DateTime.UtcNow, DriveLetter = drive.Letter, VolumeName = drive.VolumeName,
                    FileSystem = drive.FileSystem, ScanType = AppSettings.QuickScan, Status = "Running"
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
                _log.Error("Quick Scan failed", ex);
                Finish(historyId, "Failed", 0);
                session.Dispose();
                return new QuickScanOutcome { Status = EngineStatus.Internal, Detail = ex.Message };
            }

            ScanProgress final = session.GetProgress();
            if (progress != null) progress.Report(final);

            if (status != EngineStatus.Ok && status != EngineStatus.Cancelled)
            {
                Finish(historyId, "Failed", 0);
                session.Dispose();
                return new QuickScanOutcome { Status = status, FinalProgress = final };
            }

            IReadOnlyList<ScanEntry> entries;
            try { entries = await Task.Run(() => session.GetResults()); }
            catch (Exception ex)
            {
                _log.Error("Reading scan results failed", ex);
                Finish(historyId, "Failed", 0);
                session.Dispose();
                return new QuickScanOutcome { Status = EngineStatus.Internal, Detail = ex.Message };
            }
            Finish(historyId, status == EngineStatus.Cancelled ? "Cancelled" : "Completed", entries.Count);
            _log.Info("Quick Scan of " + drive.Letter + " " + status + ": " + entries.Count + " deleted files, " + final.BadRecords + " bad records");
            return new QuickScanOutcome { Status = status, Session = session, Entries = entries, FinalProgress = final };
        }

        private void Finish(long id, string status, long found)
        {
            if (id == 0) return;
            try { _history.UpdateSession(id, DateTime.UtcNow, status, found); }
            catch (Exception ex) { _log.Warn("Could not record scan result: " + ex.Message); }
        }
    }
}
