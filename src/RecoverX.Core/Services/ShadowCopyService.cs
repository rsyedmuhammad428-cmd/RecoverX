using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;

namespace RecoverX.Core.Services
{
    /// <summary>One Windows restore point (Volume Shadow Copy) of a drive.</summary>
    public sealed class SnapshotSource
    {
        /// <summary>Folder that holds the snapshot's copy of the drive root, e.g. \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3\</summary>
        public string Root { get; set; }
        public DateTime? CreatedUtc { get; set; }
    }

    public interface IShadowCopyService
    {
        /// <summary>
        /// Lists the drive's restore points and returns a session that, when run, reports files present in a restore point but
        /// missing from the drive now. Needs administrator rights. NotFound when the drive has no restore points.
        /// </summary>
        ScanOpenResult OpenSnapshotScan(DriveModel drive);
    }

    /// <summary>Windows Volume Shadow Copies through WMI (Win32_ShadowCopy, available on Windows 7 and later). Read-only.</summary>
    public sealed class ShadowCopyService : IShadowCopyService
    {
        private readonly IAppLogger _log;
        public ShadowCopyService(IAppLogger log) { _log = log; }

        public ScanOpenResult OpenSnapshotScan(DriveModel drive)
        {
            if (drive == null || string.IsNullOrEmpty(drive.RootPath)) return new ScanOpenResult { Status = EngineStatus.InvalidArgument };
            if (!SystemInfo.IsAdministrator()) return new ScanOpenResult { Status = EngineStatus.AccessDenied };
            List<SnapshotSource> snaps;
            try { snaps = List(drive.RootPath); }
            catch (UnauthorizedAccessException) { return new ScanOpenResult { Status = EngineStatus.AccessDenied }; }
            catch (ManagementException ex)
            {
                _log.Warn("Listing restore points failed: " + ex.Message);
                return new ScanOpenResult { Status = ex.ErrorCode == ManagementStatus.AccessDenied ? EngineStatus.AccessDenied : EngineStatus.IoError, Detail = ex.Message };
            }
            catch (COMException ex) { _log.Warn("Listing restore points failed: " + ex.Message); return new ScanOpenResult { Status = EngineStatus.IoError, Detail = ex.Message }; }
            _log.Info("Restore points of " + drive.Letter + ": " + snaps.Count);
            if (snaps.Count == 0) return new ScanOpenResult { Status = EngineStatus.NotFound };
            return new ScanOpenResult { Status = EngineStatus.Ok, Session = new SnapshotScanSession(drive.RootPath, snaps) };
        }

        private static List<SnapshotSource> List(string root)
        {
            string volume = VolumeGuid(root);
            var result = new List<SnapshotSource>();
            using (var searcher = new ManagementObjectSearcher("SELECT DeviceObject, InstallDate, VolumeName FROM Win32_ShadowCopy"))
            using (ManagementObjectCollection items = searcher.Get())
            {
                foreach (ManagementBaseObject o in items)
                {
                    string vol = o["VolumeName"] as string, dev = o["DeviceObject"] as string, date = o["InstallDate"] as string;
                    if (string.IsNullOrEmpty(dev) || volume == null || !string.Equals(vol, volume, StringComparison.OrdinalIgnoreCase)) continue;
                    DateTime? created = null;
                    try { if (!string.IsNullOrEmpty(date)) created = ManagementDateTimeConverter.ToDateTime(date).ToUniversalTime(); } catch (ArgumentException) { }
                    result.Add(new SnapshotSource { Root = dev.TrimEnd('\\') + "\\", CreatedUtc = created });
                }
            }
            return result.OrderByDescending(s => s.CreatedUtc ?? DateTime.MinValue).ToList();
        }

        private static string VolumeGuid(string root)
        {
            var sb = new StringBuilder(64);
            string r = root.EndsWith("\\", StringComparison.Ordinal) ? root : root + "\\";
            return GetVolumeNameForVolumeMountPointW(r, sb, (uint)sb.Capacity) ? sb.ToString() : null;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, uint size);
    }

    /// <summary>
    /// Compares restore points (newest first) with the live drive and reports files that exist in a restore point but not on the
    /// drive any more. Data is read straight from the snapshot, which Windows keeps read-only, so a reported file is an exact copy
    /// of the file as it was when the restore point was taken. Folders full of system or cache data are skipped.
    /// </summary>
    public sealed class SnapshotScanSession : IScanSession
    {
        private static readonly HashSet<string> SkipAnywhere = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "System Volume Information", "$WinREAgent", "$Windows.~BT", "$Windows.~WS", "AppData", "node_modules", ".git" };
        private static readonly HashSet<string> SkipAtRoot = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Recovery", "PerfLogs", "MSOCache", "Config.Msi" };

        private readonly string _live;
        private readonly IList<SnapshotSource> _sources;
        private readonly Func<string, bool> _existsLive;
        private readonly List<ScanEntry> _results = new List<ScanEntry>();
        private readonly List<string> _paths = new List<string>();
        private readonly ManualResetEventSlim _resume = new ManualResetEventSlim(true);
        private readonly object _readGate = new object();
        private readonly int _max;
        private int _total, _done, _candidates, _errors;
        private volatile bool _paused, _finished, _truncated;
        private FileStream _open;
        private int _openIndex = -1;

        public SnapshotScanSession(string liveRoot, IList<SnapshotSource> sources, Func<string, bool> existsLive = null, int maxResults = 250000)
        {
            _live = (liveRoot ?? string.Empty).TrimEnd('\\') + "\\";
            _sources = sources ?? new List<SnapshotSource>();
            _existsLive = existsLive ?? (p => File.Exists(p) || Directory.Exists(p));
            _max = maxResults;
        }

        public ScanMode Mode { get { return ScanMode.PreviousVersions; } }
        public int SnapshotCount { get { return _sources.Count; } }
        public void SetPaused(bool paused) { _paused = paused; if (paused) _resume.Reset(); else _resume.Set(); }

        public ScanProgress GetProgress()
        {
            return new ScanProgress
            {
                Mode = ScanMode.PreviousVersions, Phase = _finished ? ScanPhase.Done : ScanPhase.Scanning,
                Total = Volatile.Read(ref _total), Done = Volatile.Read(ref _done), Candidates = Volatile.Read(ref _candidates),
                BadRecords = Volatile.Read(ref _errors), Paused = _paused, IsDone = _finished, Truncated = _truncated
            };
        }

        public EngineStatus Run(CancellationToken cancel)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (SnapshotSource src in _sources)
                {
                    var stack = new Stack<string>();
                    stack.Push(string.Empty);
                    Interlocked.Increment(ref _total);
                    while (stack.Count > 0)
                    {
                        _resume.Wait(cancel);
                        cancel.ThrowIfCancellationRequested();
                        string rel = stack.Pop();
                        Interlocked.Increment(ref _done);
                        // A whole folder that is gone from the drive: everything below it is deleted (no per-file check needed).
                        bool folderGone = rel.Length > 0 && !_existsLive(_live + rel);
                        List<RawFs.Item> items;
                        try { items = RawFs.List(src.Root + rel); }
                        catch (Win32Exception) { Interlocked.Increment(ref _errors); continue; }
                        foreach (RawFs.Item it in items)
                        {
                            string childRel = rel.Length == 0 ? it.Name : rel + "\\" + it.Name;
                            if (it.IsDirectory)
                            {
                                if (it.IsReparsePoint || SkipAnywhere.Contains(it.Name) || (rel.Length == 0 && SkipAtRoot.Contains(it.Name))) continue;
                                stack.Push(childRel);
                                Interlocked.Increment(ref _total);
                                continue;
                            }
                            if (!seen.Add(childRel)) continue;                         // a newer restore point already reported it
                            if (!folderGone && _existsLive(_live + childRel)) continue;
                            if (_results.Count >= _max) { _truncated = true; return EngineStatus.Ok; }
                            lock (_readGate)
                            {
                                _results.Add(new ScanEntry
                                {
                                    Index = _results.Count, Origin = EntryOrigin.Snapshot, Path = "\\" + childRel, Size = it.Size,
                                    Created = it.CreatedUtc, Modified = it.ModifiedUtc, Accessed = it.AccessedUtc, SnapshotTime = src.CreatedUtc,
                                    Condition = RecoveryCondition.Verified, Header = HeaderCheck.NotChecked
                                });
                                _paths.Add(src.Root + childRel);
                            }
                            Interlocked.Increment(ref _candidates);
                        }
                    }
                }
                return EngineStatus.Ok;
            }
            catch (OperationCanceledException) { return EngineStatus.Cancelled; }
            finally { _finished = true; }
        }

        public IReadOnlyList<ScanEntry> GetResults() { lock (_readGate) return _results.ToList(); }

        public EngineStatus ReadFile(int index, long offset, byte[] buffer, int count, out int bytesRead)
        {
            bytesRead = 0;
            if (buffer == null || count < 0 || count > buffer.Length || offset < 0) return EngineStatus.InvalidArgument;
            lock (_readGate)
            {
                if (index < 0 || index >= _paths.Count) return EngineStatus.InvalidArgument;
                try
                {
                    if (_openIndex != index)
                    {
                        if (_open != null) { _open.Dispose(); _open = null; }
                        _open = RawFs.OpenRead(_paths[index]);
                        _openIndex = index;
                    }
                    _open.Seek(offset, SeekOrigin.Begin);
                    int n;
                    while (bytesRead < count && (n = _open.Read(buffer, bytesRead, count - bytesRead)) > 0) bytesRead += n;
                    return EngineStatus.Ok;
                }
                catch (Win32Exception ex) { _openIndex = -1; return ex.NativeErrorCode == 5 ? EngineStatus.AccessDenied : ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3 ? EngineStatus.NotFound : EngineStatus.IoError; }
                catch (IOException) { _openIndex = -1; return EngineStatus.IoError; }
                catch (UnauthorizedAccessException) { _openIndex = -1; return EngineStatus.AccessDenied; }
            }
        }

        public void Dispose()
        {
            lock (_readGate) { if (_open != null) { _open.Dispose(); _open = null; } _openIndex = -1; }
        }
    }

    /// <summary>
    /// Direct Win32 directory listing and read-only open. Used for restore-point paths (\\?\GLOBALROOT\Device\...), which the
    /// .NET Framework path APIs do not handle reliably on every Windows version. Long paths are fine with the \\?\ prefix.
    /// </summary>
    internal static class RawFs
    {
        public sealed class Item
        {
            public string Name; public bool IsDirectory, IsReparsePoint; public long Size;
            public DateTime? CreatedUtc, ModifiedUtc, AccessedUtc;
        }

        public static List<Item> List(string dir)
        {
            var list = new List<Item>();
            string pattern = Prefixed(dir.TrimEnd('\\')) + "\\*";
            WIN32_FIND_DATAW fd;
            IntPtr h = FindFirstFileW(pattern, out fd);
            if (h == new IntPtr(-1))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 2 || err == 18) return list;                    // empty / no files
                throw new Win32Exception(err);
            }
            try
            {
                do
                {
                    if (fd.cFileName == "." || fd.cFileName == "..") continue;
                    list.Add(new Item
                    {
                        Name = fd.cFileName,
                        IsDirectory = (fd.dwFileAttributes & 0x10) != 0,
                        IsReparsePoint = (fd.dwFileAttributes & 0x400) != 0,
                        Size = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow,
                        CreatedUtc = Time(fd.ftCreationTime), ModifiedUtc = Time(fd.ftLastWriteTime), AccessedUtc = Time(fd.ftLastAccessTime)
                    });
                } while (FindNextFileW(h, out fd));
            }
            finally { FindClose(h); }
            return list;
        }

        public static FileStream OpenRead(string path)
        {
            SafeFileHandle h = CreateFileW(Prefixed(path), 0x80000000 /*GENERIC_READ*/, 1 | 2 /*share read+write*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0x08000000 /*SEQUENTIAL_SCAN*/, IntPtr.Zero);
            if (h.IsInvalid) { int err = Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(err); }
            return new FileStream(h, FileAccess.Read, 1 << 16);
        }

        private static string Prefixed(string p)
        {
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) return p;
            if (p.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + p.Substring(2);
            return @"\\?\" + p;
        }

        private static DateTime? Time(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        {
            long v = ((long)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
            if (v <= 0) return null;
            try { return DateTime.FromFileTimeUtc(v); } catch (ArgumentOutOfRangeException) { return null; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string fileName, out WIN32_FIND_DATAW data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr h, out WIN32_FIND_DATAW data);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    }
}
