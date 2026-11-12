using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;

namespace RecoverX.Core.Services
{
    public interface IRecoveryService
    {
        Task<RecoverySummary> RecoverAsync(INtfsScanSession session, IList<ScanEntry> items, RecoveryRequest request,
                                           IProgress<RecoveryProgress> progress, CancellationToken cancel);
    }

    /// <summary>
    /// Copies deleted files out of a scan session into a new folder. The source drive is only ever read. Rules enforced here:
    /// destination must exist and must not be on the drive being recovered, free space is checked first, existing files are
    /// never overwritten, partial files are removed on failure, and every recovered file is re-checked after writing.
    /// </summary>
    public sealed class RecoveryService : IRecoveryService
    {
        private const int Chunk = 1 << 20;
        private readonly IEngineClient _engine;
        private readonly IDestinationValidator _validator;
        private readonly IRecoveredFilesRepository _repo;
        private readonly IAppLogger _log;
        private readonly Func<DateTime> _now;

        public RecoveryService(IEngineClient engine, IDestinationValidator validator, IRecoveredFilesRepository repo, IAppLogger log)
            : this(engine, validator, repo, log, () => DateTime.Now) { }

        public RecoveryService(IEngineClient engine, IDestinationValidator validator, IRecoveredFilesRepository repo, IAppLogger log, Func<DateTime> now)
        { _engine = engine; _validator = validator; _repo = repo; _log = log; _now = now; }

        public Task<RecoverySummary> RecoverAsync(INtfsScanSession session, IList<ScanEntry> items, RecoveryRequest req,
                                                  IProgress<RecoveryProgress> progress, CancellationToken cancel)
        {
            return Task.Run(() => Recover(session, items, req, progress, cancel));
        }

        private RecoverySummary Recover(INtfsScanSession session, IList<ScanEntry> items, RecoveryRequest req,
                                        IProgress<RecoveryProgress> progress, CancellationToken cancel)
        {
            var summary = new RecoverySummary();

            // ---- 1. destination safety ----
            if (string.IsNullOrWhiteSpace(req.DestinationDirectory)) { summary.Error = "Recover_Err_Dest"; return summary; }
            switch (_validator.Validate(req.SourceRoot, req.DestinationDirectory))
            {
                case DestinationVerdict.NotFound: summary.Error = "Recover_Err_DestInvalid"; return summary;
                case DestinationVerdict.SameVolume: summary.Error = "Recover_Err_SameVolume"; summary.ErrorDetail = (req.SourceRoot ?? "").TrimEnd('\\'); return summary;
                case DestinationVerdict.Unverifiable: summary.Error = "Recover_Err_Unverifiable"; return summary;
            }

            // ---- 2. free space ----
            long needed = items.Sum(i => i.Size);
            summary.NeededBytes = needed;
            long? available = FreeSpace(req.DestinationDirectory);
            if (available.HasValue)
            {
                summary.AvailableBytes = available.Value;
                if (available.Value < needed + (1 << 20)) { summary.Error = "Recover_Err_Space"; return summary; }
            }

            // ---- 3. a fresh folder for this recovery: nothing existing can be touched ----
            string letter = (req.SourceRoot ?? "X:").Substring(0, Math.Min(1, (req.SourceRoot ?? "X").Length)).ToUpperInvariant();
            string session_dir = Path.Combine(req.DestinationDirectory, "RecoverX_" + letter + "_" + _now().ToString("yyyyMMdd_HHmmss"));
            try
            {
                int n = 2; string baseDir = session_dir;
                while (Directory.Exists(session_dir)) session_dir = baseDir + "_" + (n++);
                Directory.CreateDirectory(session_dir);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            { summary.Error = "Recover_Err_Create"; summary.ErrorDetail = ex.Message; return summary; }
            summary.SessionDirectory = session_dir;

            // ---- 4. copy each file ----
            long bytesTotal = needed, bytesDone = 0;
            var buffer = new byte[Chunk];
            for (int i = 0; i < items.Count; i++)
            {
                ScanEntry e = items[i];
                if (cancel.IsCancellationRequested) { summary.Cancelled = true; break; }
                if (progress != null) progress.Report(new RecoveryProgress { FileNumber = i + 1, FileCount = items.Count, CurrentName = e.Name, BytesDone = bytesDone, BytesTotal = bytesTotal });

                RecoveryItemResult r = RecoverOne(session, e, req, session_dir, buffer, cancel, d =>
                {
                    if (progress != null) progress.Report(new RecoveryProgress { FileNumber = i + 1, FileCount = items.Count, CurrentName = e.Name, BytesDone = bytesDone + d, BytesTotal = bytesTotal });
                });
                summary.Items.Add(r);
                bytesDone += e.Size;
                if (r.Outcome == RecoveryOutcome.Cancelled) { summary.Cancelled = true; break; }
                Persist(r, req);
            }
            return summary;
        }

        private RecoveryItemResult RecoverOne(INtfsScanSession session, ScanEntry e, RecoveryRequest req, string dir, byte[] buffer,
                                              CancellationToken cancel, Action<long> fileProgress)
        {
            var result = new RecoveryItemResult { Entry = e };
            if (!e.CanAttemptRecovery) { result.Outcome = RecoveryOutcome.Unsupported; return result; }

            string path = null;
            FileStream fs = null;
            bool finished = false;
            try
            {
                path = RecoveryPathBuilder.BuildUniquePath(dir, e.Path, req.PreserveFolders, File.Exists);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);   // CreateNew: never overwrites

                long offset = 0;
                while (offset < e.Size)
                {
                    cancel.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(buffer.Length, e.Size - offset), got;
                    EngineStatus st = session.ReadFile(e.Index, offset, buffer, want, out got);
                    if (st == EngineStatus.Unsupported) { result.Outcome = RecoveryOutcome.Unsupported; return result; }
                    if (st != EngineStatus.Ok)
                    {
                        result.Outcome = RecoveryOutcome.Failed;
                        result.ReasonKey = st == EngineStatus.NotFound ? "Reason_RecordReused" : "Reason_ReadError";
                        result.ReasonDetail = st.ToString();
                        return result;
                    }
                    if (got <= 0) break;
                    fs.Write(buffer, 0, got);
                    offset += got;
                    fileProgress(offset);
                }
                fs.Flush();
                fs.Dispose(); fs = null;
                result.BytesWritten = offset;
                if (offset != e.Size)
                {
                    result.Outcome = RecoveryOutcome.Failed; result.ReasonKey = "Reason_ShortRead";
                    return result;
                }

                ApplyTimestamps(path, e);
                result.SavedPath = path;
                Classify(result, path);
                finished = true;
                return result;
            }
            catch (OperationCanceledException) { result.Outcome = RecoveryOutcome.Cancelled; return result; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is EngineException)
            {
                _log.Warn("Recovering file failed: " + ex.GetType().Name + ": " + ex.Message);
                result.Outcome = RecoveryOutcome.Failed; result.ReasonKey = "Reason_WriteError"; result.ReasonDetail = ex.Message;
                return result;
            }
            finally
            {
                if (fs != null) { try { fs.Dispose(); } catch (IOException) { } }
                // A file that did not complete must not be left behind looking like a recovered file.
                if (!finished && path != null && result.Outcome != RecoveryOutcome.Verified)
                {
                    try { if (File.Exists(path) && (result.Outcome == RecoveryOutcome.Failed || result.Outcome == RecoveryOutcome.Cancelled || result.Outcome == RecoveryOutcome.Unsupported)) File.Delete(path); }
                    catch (Exception) { }
                    result.SavedPath = null;
                }
            }
        }

        /// <summary>Decides how much to trust the written file, using the engine's evidence plus a re-check of the written bytes.</summary>
        private void Classify(RecoveryItemResult r, string path)
        {
            ScanEntry e = r.Entry;
            HeaderCheck header = HeaderCheck.NotChecked;
            if (e.Size > 0)
            {
                try
                {
                    var head = new byte[512];
                    int n;
                    using (var rf = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) n = rf.Read(head, 0, head.Length);
                    header = _engine.CheckHeader(e.Name, head, n);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is EngineException) { _log.Warn("Header re-check skipped: " + ex.Message); }
            }

            if (e.Condition == RecoveryCondition.Partial) { r.Outcome = RecoveryOutcome.Suspect; r.ReasonKey = "Reason_PartlyOverwritten"; }
            else if (e.Condition == RecoveryCondition.Lost) { r.Outcome = RecoveryOutcome.Suspect; r.ReasonKey = "Reason_Overwritten"; }
            else if (header == HeaderCheck.Mismatch) { r.Outcome = RecoveryOutcome.Suspect; r.ReasonKey = "Reason_HeaderMismatch"; }
            else if (header == HeaderCheck.Match) r.Outcome = RecoveryOutcome.Verified;
            else r.Outcome = RecoveryOutcome.Unverified;
        }

        private static void ApplyTimestamps(string path, ScanEntry e)
        {
            try
            {
                if (e.Created.HasValue) File.SetCreationTimeUtc(path, e.Created.Value);
                if (e.Modified.HasValue) File.SetLastWriteTimeUtc(path, e.Modified.Value);
                if (e.Accessed.HasValue) File.SetLastAccessTimeUtc(path, e.Accessed.Value);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentOutOfRangeException) { /* metadata is best effort */ }
        }

        private void Persist(RecoveryItemResult r, RecoveryRequest req)
        {
            if (r.SavedPath == null) return;
            if (r.Outcome != RecoveryOutcome.Verified && r.Outcome != RecoveryOutcome.Unverified && r.Outcome != RecoveryOutcome.Suspect) return;
            try
            {
                _repo.Add(new RecoveredFileRecord
                {
                    RecoveredUtc = DateTime.UtcNow, SourceDrive = (req.SourceRoot ?? "").TrimEnd('\\'), OriginalPath = r.Entry.Path,
                    SavedPath = r.SavedPath, Size = r.BytesWritten, Status = r.Outcome.ToString()
                });
            }
            catch (Exception ex) { _log.Warn("Could not record recovered file: " + ex.Message); }
        }

        private static long? FreeSpace(string directory)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(directory));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;   // network share: unknown
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException) { return null; }
        }
    }
}
