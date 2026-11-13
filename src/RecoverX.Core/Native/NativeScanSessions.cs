using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;

namespace RecoverX.Core.Native
{
    /// <summary>
    /// Shared lifetime handling for a native scan handle: one Run at a time, a cancel/pause token that lives exactly as long
    /// as the run, and Dispose that is safe while a scan is still running (the handle is destroyed when the run returns).
    /// </summary>
    internal abstract class NativeScanSessionBase : IScanSession
    {
        protected readonly object Gate = new object();
        private IntPtr _h, _token;
        private bool _running, _disposeRequested, _paused;

        protected NativeScanSessionBase(IntPtr handle) { _h = handle; }

        public abstract ScanMode Mode { get; }
        protected abstract int NativeRun(IntPtr handle, IntPtr token);
        protected abstract void NativeDestroy(IntPtr handle);
        protected abstract ScanProgress NativeProgress(IntPtr handle);
        public abstract IReadOnlyList<ScanEntry> GetResults();
        public abstract EngineStatus ReadFile(int index, long offset, byte[] buffer, int count, out int bytesRead);

        public EngineStatus Run(CancellationToken cancel)
        {
            IntPtr h;
            lock (Gate)
            {
                if (_h == IntPtr.Zero || _running) throw new InvalidOperationException("Scan session is not available.");
                _running = true; h = _h;
                _token = NativeMethods3.rx_cancel_create();
                if (_token != IntPtr.Zero && _paused) NativeMethods4.rx_cancel_set_paused(_token, 1);
            }
            try
            {
                using (cancel.Register(RequestCancel))
                {
                    if (cancel.IsCancellationRequested) RequestCancel();
                    return Map(NativeRun(h, _token));
                }
            }
            finally
            {
                lock (Gate)
                {
                    // The registration above is disposed (end of using), so nothing can touch the token any more.
                    if (_token != IntPtr.Zero) { NativeMethods3.rx_cancel_destroy(_token); _token = IntPtr.Zero; }
                    _running = false;
                    if (_disposeRequested && _h != IntPtr.Zero) { NativeDestroy(_h); _h = IntPtr.Zero; }
                }
            }
        }

        private void RequestCancel()
        {
            lock (Gate) { if (_token != IntPtr.Zero) NativeMethods3.rx_cancel_request(_token); }
        }

        public void SetPaused(bool paused)
        {
            lock (Gate)
            {
                _paused = paused;
                if (_token != IntPtr.Zero) NativeMethods4.rx_cancel_set_paused(_token, paused ? 1 : 0);
            }
        }

        public ScanProgress GetProgress()
        {
            lock (Gate)
            {
                if (_h == IntPtr.Zero) return new ScanProgress { Mode = Mode, Phase = ScanPhase.Done, IsDone = true };
                return NativeProgress(_h);
            }
        }

        public void Dispose()
        {
            lock (Gate)
            {
                if (_h == IntPtr.Zero) return;
                if (_running) { _disposeRequested = true; if (_token != IntPtr.Zero) NativeMethods3.rx_cancel_request(_token); return; }
                NativeDestroy(_h);
                _h = IntPtr.Zero;
            }
        }

        protected IntPtr Handle()
        {
            lock (Gate) { if (_h == IntPtr.Zero) throw new ObjectDisposedException(GetType().Name); return _h; }
        }

        protected static EngineStatus Map(int st) { return Enum.IsDefined(typeof(EngineStatus), st) ? (EngineStatus)st : EngineStatus.Internal; }

        protected static DateTime? FileTime(ulong ft, bool unspecified = false)
        {
            if (ft == 0 || ft > (ulong)DateTime.MaxValue.ToFileTimeUtc()) return null;
            try
            {
                DateTime t = DateTime.FromFileTimeUtc((long)ft);
                return unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Unspecified) : t;
            }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        protected static RecoveryCondition Cond(uint c)
        {
            return Enum.IsDefined(typeof(RecoveryCondition), (int)c) ? (RecoveryCondition)c : RecoveryCondition.Unknown;
        }
    }

    /// <summary>The native entry points of one Quick Scan family (NTFS, or FAT12/16/32 + exFAT). Same structures, different functions.</summary>
    internal sealed class QuickScanApi
    {
        public Func<IntPtr, IntPtr, int> Run;
        public Action<IntPtr> Destroy;
        public Func<IntPtr, RxScanProgress, RxScanProgress> Progress;
        public Func<IntPtr, uint> Count;
        public Func<IntPtr, uint, RxFileEntry[], uint, Tuple<int, uint>> Get;
        public Func<IntPtr, uint, ulong, byte[], uint, Tuple<int, uint>> Read;

        public static readonly QuickScanApi Ntfs = new QuickScanApi
        {
            Run = NativeMethods3.rx_ntfs_scan_run,
            Destroy = NativeMethods3.rx_ntfs_scan_destroy,
            Progress = (h, p) => { NativeMethods3.rx_ntfs_scan_progress(h, ref p); return p; },
            Count = NativeMethods3.rx_ntfs_scan_result_count,
            Get = (h, start, buf, want) => { uint got; int st = NativeMethods3.rx_ntfs_scan_get_results(h, start, buf, want, out got); return Tuple.Create(st, got); },
            Read = (h, i, off, buf, len) => { uint got; int st = NativeMethods3.rx_ntfs_read_file(h, i, off, buf, len, out got); return Tuple.Create(st, got); }
        };

        public static readonly QuickScanApi Fat = new QuickScanApi
        {
            Run = NativeMethods5.rx_fat_scan_run,
            Destroy = NativeMethods5.rx_fat_scan_destroy,
            Progress = (h, p) => { NativeMethods5.rx_fat_scan_progress(h, ref p); return p; },
            Count = NativeMethods5.rx_fat_scan_result_count,
            Get = (h, start, buf, want) => { uint got; int st = NativeMethods5.rx_fat_scan_get_results(h, start, buf, want, out got); return Tuple.Create(st, got); },
            Read = (h, i, off, buf, len) => { uint got; int st = NativeMethods5.rx_fat_scan_read_file(h, i, off, buf, len, out got); return Tuple.Create(st, got); }
        };
    }

    /// <summary>Quick Scan: deleted files from the file system's own records (NTFS file table, FAT/exFAT directories).</summary>
    internal sealed class NativeQuickScanSession : NativeScanSessionBase
    {
        private readonly QuickScanApi _api;
        public NativeQuickScanSession(IntPtr handle, QuickScanApi api) : base(handle) { _api = api; }
        public override ScanMode Mode { get { return ScanMode.Quick; } }
        protected override int NativeRun(IntPtr h, IntPtr token) { return _api.Run(h, token); }
        protected override void NativeDestroy(IntPtr h) { _api.Destroy(h); }

        protected override ScanProgress NativeProgress(IntPtr h)
        {
            var p = _api.Progress(h, new RxScanProgress { StructSize = (uint)Marshal.SizeOf(typeof(RxScanProgress)) });
            return new ScanProgress
            {
                Mode = ScanMode.Quick,
                Phase = Enum.IsDefined(typeof(ScanPhase), (int)p.Phase) ? (ScanPhase)p.Phase : ScanPhase.Opening,
                Total = p.RecordsTotal, Done = p.RecordsDone, Candidates = (int)p.Candidates, BadRecords = p.BadRecords,
                Truncated = (p.Flags & 1) != 0, IsDone = (p.Flags & 2) != 0, BitmapUnavailable = (p.Flags & 4) != 0,
                MftPartial = (p.Flags & 8) != 0, Paused = (p.Flags & 16) != 0
            };
        }

        public override IReadOnlyList<ScanEntry> GetResults()
        {
            IntPtr h = Handle();
            uint total = _api.Count(h);
            var list = new List<ScanEntry>((int)Math.Min(total, 1u << 20));
            const int page = 500;
            var buf = new RxFileEntry[page];
            for (uint start = 0; start < total; start += page)
            {
                uint want = Math.Min((uint)page, total - start);
                for (int i = 0; i < buf.Length; i++) buf[i].StructSize = (uint)Marshal.SizeOf(typeof(RxFileEntry));
                Tuple<int, uint> r = _api.Get(h, start, buf, want);
                if (r.Item1 != 0) throw new EngineException("Reading scan results failed.");
                for (uint i = 0; i < r.Item2; i++)
                {
                    RxFileEntry e = buf[i];
                    list.Add(new ScanEntry
                    {
                        Index = (int)(start + i),
                        Origin = EntryOrigin.FileTable,
                        MftNumber = (long)e.MftNumber,
                        Path = e.Path ?? string.Empty,
                        Size = e.Size > long.MaxValue ? long.MaxValue : (long)e.Size,
                        Created = FileTime(e.Created), Modified = FileTime(e.Modified), Accessed = FileTime(e.Accessed), RecordChanged = FileTime(e.RecordChanged),
                        Condition = Cond(e.Condition),
                        Header = Enum.IsDefined(typeof(HeaderCheck), (int)e.HeaderCheck) ? (HeaderCheck)e.HeaderCheck : HeaderCheck.NotChecked,
                        Flags = (EntryFlags)e.Flags,
                        ClustersTotal = e.ClustersTotal, ClustersInUse = e.ClustersInUse
                    });
                }
            }
            return list;
        }

        public override EngineStatus ReadFile(int index, long offset, byte[] buffer, int count, out int bytesRead)
        {
            bytesRead = 0;
            if (buffer == null || count < 0 || count > buffer.Length || index < 0 || offset < 0) return EngineStatus.InvalidArgument;
            Tuple<int, uint> r = _api.Read(Handle(), (uint)index, (ulong)offset, buffer, (uint)count);
            bytesRead = (int)r.Item2;
            return Map(r.Item1);
        }
    }

    /// <summary>Deep Scan: files found by their content signature.</summary>
    internal sealed class NativeDeepScanSession : NativeScanSessionBase
    {
        public NativeDeepScanSession(IntPtr handle) : base(handle) { }
        public override ScanMode Mode { get { return ScanMode.Deep; } }
        protected override int NativeRun(IntPtr h, IntPtr token) { return NativeMethods4.rx_carve_run(h, token); }
        protected override void NativeDestroy(IntPtr h) { NativeMethods4.rx_carve_destroy(h); }

        protected override ScanProgress NativeProgress(IntPtr h)
        {
            var p = new RxCarveProgress { StructSize = (uint)Marshal.SizeOf(typeof(RxCarveProgress)) };
            NativeMethods4.rx_carve_progress(h, ref p);
            return new ScanProgress
            {
                Mode = ScanMode.Deep, CountsBytes = true,
                Phase = Enum.IsDefined(typeof(ScanPhase), (int)p.Phase) ? (ScanPhase)p.Phase : ScanPhase.Opening,
                Total = p.BytesTotal > long.MaxValue ? long.MaxValue : (long)p.BytesTotal,
                Done = p.BytesDone > long.MaxValue ? long.MaxValue : (long)p.BytesDone,
                Candidates = (int)p.Found, ReadErrors = p.ReadErrors,
                Truncated = (p.Flags & 1) != 0, IsDone = (p.Flags & 2) != 0, BitmapUnavailable = (p.Flags & 4) != 0, Paused = (p.Flags & 16) != 0
            };
        }

        public override IReadOnlyList<ScanEntry> GetResults()
        {
            IntPtr h = Handle();
            uint total = NativeMethods4.rx_carve_result_count(h);
            var list = new List<ScanEntry>((int)Math.Min(total, 1u << 20));
            const int page = 1000;
            var buf = new RxCarveEntry[page];
            for (uint start = 0; start < total; start += page)
            {
                uint want = Math.Min((uint)page, total - start);
                for (int i = 0; i < buf.Length; i++) buf[i].StructSize = (uint)Marshal.SizeOf(typeof(RxCarveEntry));
                uint got;
                if (NativeMethods4.rx_carve_get_results(h, start, buf, want, out got) != 0) throw new EngineException("Reading Deep Scan results failed.");
                for (uint i = 0; i < got; i++)
                {
                    RxCarveEntry e = buf[i];
                    var ev = (CarveEvidence)e.Evidence;
                    bool local = (ev & CarveEvidence.TimeIsLocal) != 0;
                    list.Add(new ScanEntry
                    {
                        Index = (int)(start + i),
                        Origin = EntryOrigin.Signature,
                        VolumeOffset = e.Offset > long.MaxValue ? long.MaxValue : (long)e.Offset,
                        Path = CarvedNames.PathFor(e.Ext, (long)e.Offset),
                        Size = e.Size > long.MaxValue ? long.MaxValue : (long)e.Size,
                        Condition = Cond(e.Condition),
                        Evidence = ev,
                        ContentTime = FileTime(e.ContentTime, local),
                        ContentTimeIsLocal = local,
                        ClustersTotal = e.ClustersTotal, ClustersInUse = e.ClustersInUse,
                        Header = (ev & CarveEvidence.StructureOk) != 0 ? HeaderCheck.Match : HeaderCheck.NotChecked
                    });
                }
            }
            return list;
        }

        public override EngineStatus ReadFile(int index, long offset, byte[] buffer, int count, out int bytesRead)
        {
            bytesRead = 0;
            if (buffer == null || count < 0 || count > buffer.Length || index < 0 || offset < 0) return EngineStatus.InvalidArgument;
            uint got;
            int st = NativeMethods4.rx_carve_read_file(Handle(), (uint)index, (ulong)offset, buffer, (uint)count, out got);
            bytesRead = (int)got;
            return Map(st);
        }
    }

    /// <summary>Names for files found by Deep Scan, which have no file-system name: "\Deep Scan\JPG\f0000123456.jpg".</summary>
    public static class CarvedNames
    {
        public const string Folder = "Deep Scan";

        public static string PathFor(string ext, long volumeOffset)
        {
            string e = Sanitize(ext);
            long sector = volumeOffset / 512;
            return "\\" + Folder + "\\" + e.ToUpperInvariant() + "\\f" + sector.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + "." + e;
        }

        private static string Sanitize(string ext)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in ext ?? string.Empty) if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            return sb.Length == 0 ? "bin" : sb.ToString();
        }
    }
}
