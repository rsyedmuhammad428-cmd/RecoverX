using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;

namespace RecoverX.Core.Native
{
    /// <summary>Managed wrapper over one native scan handle. Safe against Dispose racing a running scan.</summary>
    internal sealed class NativeNtfsScanSession : INtfsScanSession
    {
        private readonly object _gate = new object();
        private IntPtr _h;
        private bool _running, _disposeRequested;

        public NativeNtfsScanSession(IntPtr handle) { _h = handle; }

        public EngineStatus Run(CancellationToken cancel)
        {
            IntPtr token = IntPtr.Zero, h;
            lock (_gate)
            {
                if (_h == IntPtr.Zero || _running) throw new InvalidOperationException("Scan session is not available.");
                _running = true; h = _h;
            }
            try
            {
                token = NativeMethods3.rx_cancel_create();
                using (cancel.Register(() => { if (token != IntPtr.Zero) NativeMethods3.rx_cancel_request(token); }))
                {
                    if (cancel.IsCancellationRequested) NativeMethods3.rx_cancel_request(token);
                    int st = NativeMethods3.rx_ntfs_scan_run(h, token);
                    return Map(st);
                }
            }
            finally
            {
                // Destroy the token only after the registration above has been disposed (end of the using block).
                if (token != IntPtr.Zero) NativeMethods3.rx_cancel_destroy(token);
                lock (_gate)
                {
                    _running = false;
                    if (_disposeRequested && _h != IntPtr.Zero) { NativeMethods3.rx_ntfs_scan_destroy(_h); _h = IntPtr.Zero; }
                }
            }
        }

        public ScanProgress GetProgress()
        {
            lock (_gate)
            {
                if (_h == IntPtr.Zero) return new ScanProgress { Phase = ScanPhase.Done, IsDone = true };
                var p = new RxScanProgress { StructSize = (uint)Marshal.SizeOf(typeof(RxScanProgress)) };
                NativeMethods3.rx_ntfs_scan_progress(_h, ref p);
                return new ScanProgress
                {
                    Phase = Enum.IsDefined(typeof(ScanPhase), (int)p.Phase) ? (ScanPhase)p.Phase : ScanPhase.Opening,
                    Total = p.RecordsTotal, Done = p.RecordsDone, Candidates = (int)p.Candidates, BadRecords = p.BadRecords,
                    Truncated = (p.Flags & 1) != 0, IsDone = (p.Flags & 2) != 0, BitmapUnavailable = (p.Flags & 4) != 0, MftPartial = (p.Flags & 8) != 0
                };
            }
        }

        public IReadOnlyList<ScanEntry> GetResults()
        {
            IntPtr h = Handle();
            uint total = NativeMethods3.rx_ntfs_scan_result_count(h);
            var list = new List<ScanEntry>((int)Math.Min(total, 1u << 20));
            const int page = 500;
            var buf = new RxFileEntry[page];
            for (uint start = 0; start < total; start += page)
            {
                uint want = Math.Min((uint)page, total - start);
                for (int i = 0; i < buf.Length; i++) buf[i].StructSize = (uint)Marshal.SizeOf(typeof(RxFileEntry));
                uint got;
                if (NativeMethods3.rx_ntfs_scan_get_results(h, start, buf, want, out got) != 0) throw new EngineException("Reading scan results failed.");
                for (uint i = 0; i < got; i++)
                {
                    RxFileEntry e = buf[i];
                    list.Add(new ScanEntry
                    {
                        Index = (int)(start + i),
                        MftNumber = (long)e.MftNumber,
                        Path = e.Path ?? string.Empty,
                        Size = e.Size > long.MaxValue ? long.MaxValue : (long)e.Size,
                        Created = FileTime(e.Created), Modified = FileTime(e.Modified), Accessed = FileTime(e.Accessed), RecordChanged = FileTime(e.RecordChanged),
                        Condition = Enum.IsDefined(typeof(RecoveryCondition), (int)e.Condition) ? (RecoveryCondition)e.Condition : RecoveryCondition.Unknown,
                        Header = Enum.IsDefined(typeof(HeaderCheck), (int)e.HeaderCheck) ? (HeaderCheck)e.HeaderCheck : HeaderCheck.NotChecked,
                        Flags = (EntryFlags)e.Flags,
                        ClustersTotal = e.ClustersTotal, ClustersInUse = e.ClustersInUse
                    });
                }
            }
            return list;
        }

        public EngineStatus ReadFile(int index, long offset, byte[] buffer, int count, out int bytesRead)
        {
            bytesRead = 0;
            if (buffer == null || count < 0 || count > buffer.Length || index < 0 || offset < 0) return EngineStatus.InvalidArgument;
            uint got;
            int st = NativeMethods3.rx_ntfs_read_file(Handle(), (uint)index, (ulong)offset, buffer, (uint)count, out got);
            bytesRead = (int)got;
            return Map(st);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_h == IntPtr.Zero) return;
                if (_running) { _disposeRequested = true; return; }     // the running scan destroys the handle when it ends
                NativeMethods3.rx_ntfs_scan_destroy(_h);
                _h = IntPtr.Zero;
            }
        }

        private IntPtr Handle()
        {
            lock (_gate) { if (_h == IntPtr.Zero) throw new ObjectDisposedException("NtfsScanSession"); return _h; }
        }

        private static EngineStatus Map(int st) { return Enum.IsDefined(typeof(EngineStatus), st) ? (EngineStatus)st : EngineStatus.Internal; }

        private static DateTime? FileTime(ulong ft)
        {
            if (ft == 0 || ft > (ulong)DateTime.MaxValue.ToFileTimeUtc()) return null;
            try { return DateTime.FromFileTimeUtc((long)ft); } catch (ArgumentOutOfRangeException) { return null; }
        }
    }
}
