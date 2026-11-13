using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;

namespace RecoverX.Core.Native
{
    /// <summary>
    /// Loads recoverx_engine.dll from "x64\" or "x86\" next to the executable (matching the process bitness) and wraps
    /// the C ABI. Loading is lazy and failure-tolerant: a missing or incompatible DLL makes IsAvailable false and the
    /// app keeps working with Phase 1 detection.
    /// </summary>
    public sealed class NativeEngineClient : IEngineClient
    {
        private readonly IAppLogger _log;
        private readonly object _gate = new object();
        private bool _initialized, _ok;
        private string _reason = "not loaded", _path = string.Empty, _version = string.Empty;

        public NativeEngineClient(IAppLogger log) { _log = log; }

        public bool IsAvailable { get { EnsureLoaded(); return _ok; } }
        public string UnavailableReason { get { EnsureLoaded(); return _reason; } }
        public string Version { get { EnsureLoaded(); return _version; } }
        public string LoadedPath { get { EnsureLoaded(); return _path; } }

        private void EnsureLoaded()
        {
            lock (_gate)
            {
                if (_initialized) return;
                _initialized = true;
                try { Load(); }
                catch (Exception ex)
                {
                    _ok = false;
                    _reason = ex.GetType().Name + ": " + ex.Message;
                    _log.Warn("Native engine unavailable: " + _reason);
                }
            }
        }

        private void Load()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            string[] candidates =
            {
                Path.Combine(Path.Combine(baseDir, arch), NativeMethods.DllName),
                Path.Combine(baseDir, NativeMethods.DllName)
            };
            string found = null;
            foreach (string c in candidates) if (File.Exists(c)) { found = c; break; }
            if (found == null) { _reason = "recoverx_engine.dll not found (looked in " + Path.Combine(baseDir, arch) + ")"; _log.Warn(_reason); return; }

            // Load by full path first; the later DllImport by bare name then binds to this already-loaded module.
            if (NativeMethods.LoadLibraryW(found) == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                _reason = "LoadLibrary failed for " + found + ": " + new Win32Exception(err).Message + " (Win32 " + err + ")";
                _log.Warn(_reason);
                return;
            }
            uint abi = NativeMethods.rx_get_abi_version();
            if (abi != NativeMethods.ExpectedAbi) { _reason = "ABI mismatch: engine " + abi + ", expected " + NativeMethods.ExpectedAbi; _log.Warn(_reason); return; }
            if (NativeMethods.rx_sizeof_volume_info() != (uint)Marshal.SizeOf(typeof(RxVolumeInfo))
                || NativeMethods.rx_sizeof_probe_result() != (uint)Marshal.SizeOf(typeof(RxProbeResult))
                || NativeMethods3.rx_sizeof_file_entry() != (uint)Marshal.SizeOf(typeof(RxFileEntry))
                || NativeMethods3.rx_sizeof_scan_progress() != (uint)Marshal.SizeOf(typeof(RxScanProgress))
                || NativeMethods3.rx_sizeof_scan_options() != (uint)Marshal.SizeOf(typeof(RxScanOptions))
                || NativeMethods4.rx_sizeof_carve_entry() != (uint)Marshal.SizeOf(typeof(RxCarveEntry))
                || NativeMethods4.rx_sizeof_carve_progress() != (uint)Marshal.SizeOf(typeof(RxCarveProgress))
                || NativeMethods4.rx_sizeof_carve_options() != (uint)Marshal.SizeOf(typeof(RxCarveOptions)))
            { _reason = "Struct layout mismatch between engine and managed code"; _log.Warn(_reason); return; }

            _version = Marshal.PtrToStringUni(NativeMethods.rx_get_version_string()) ?? string.Empty;
            _path = found;
            _ok = true;
            _reason = string.Empty;
            _log.Info("Native engine " + _version + " loaded from " + found);
        }

        public IReadOnlyList<VolumeHardwareInfo> EnumerateVolumes()
        {
            Require();
            try
            {
                var items = new RxVolumeInfo[26];
                for (int i = 0; i < items.Length; i++) items[i].StructSize = (uint)Marshal.SizeOf(typeof(RxVolumeInfo));
                uint count;
                int st = NativeMethods.rx_enumerate_volumes(items, (uint)items.Length, out count);
                if (st != 0) throw new EngineException("rx_enumerate_volumes returned status " + st);
                var list = new List<VolumeHardwareInfo>();
                for (int i = 0; i < count && i < items.Length; i++)
                {
                    RxVolumeInfo x = items[i];
                    list.Add(new VolumeHardwareInfo
                    {
                        Letter = (x.Root ?? string.Empty).TrimEnd('\\'),
                        Bus = Enum.IsDefined(typeof(BusType), (int)x.BusType) ? (BusType)x.BusType : BusType.Unknown,
                        Media = x.MediaType == 1 ? MediaType.Hdd : x.MediaType == 2 ? MediaType.Ssd : MediaType.Unknown,
                        RemovableMedia = x.RemovableMedia < 0 ? (bool?)null : x.RemovableMedia != 0,
                        TrimEnabled = x.TrimEnabled < 0 ? (bool?)null : x.TrimEnabled != 0,
                        Vendor = x.Vendor, Product = x.Product,
                        DeviceQueried = (x.Flags & NativeMethods.FlagDeviceQueried) != 0,
                        MediaQueried = (x.Flags & NativeMethods.FlagSeekQueried) != 0,
                        Win32Error = x.Win32Error
                    });
                }
                return list;
            }
            catch (Exception ex) when (IsInterop(ex)) { throw Wrap(ex); }
        }

        public VolumeProbeResult ProbeVolume(string root)
        {
            if (!IsAvailable) return new VolumeProbeResult { Status = EngineStatus.Unavailable };
            try
            {
                var r = new RxProbeResult { StructSize = (uint)Marshal.SizeOf(typeof(RxProbeResult)) };
                int st = NativeMethods.rx_probe_volume(NormalizeRoot(root), ref r);
                return new VolumeProbeResult
                {
                    Status = Enum.IsDefined(typeof(EngineStatus), st) ? (EngineStatus)st : EngineStatus.Internal,
                    FileSystem = Enum.IsDefined(typeof(FileSystemId), (int)r.FsId) ? (FileSystemId)r.FsId : FileSystemId.Unknown,
                    SectorSize = r.BytesPerSector, TotalBytes = r.TotalBytes, Win32Error = r.Win32Error
                };
            }
            catch (Exception ex) when (IsInterop(ex)) { throw Wrap(ex); }
        }

        public DestinationVerdict ValidateDestination(string sourceRoot, string destinationDirectory)
        {
            Require();
            try
            {
                int verdict;
                int st = NativeMethods.rx_validate_destination(NormalizeRoot(sourceRoot), destinationDirectory, out verdict);
                if (st != 0) throw new EngineException("rx_validate_destination returned status " + st);
                return Enum.IsDefined(typeof(DestinationVerdict), verdict) ? (DestinationVerdict)verdict : DestinationVerdict.Unverifiable;
            }
            catch (Exception ex) when (IsInterop(ex)) { throw Wrap(ex); }
        }

        public ScanOpenResult OpenNtfsScan(string root, ScanOptions options)
        {
            if (!IsAvailable) return new ScanOpenResult { Status = EngineStatus.Unavailable, Detail = _reason };
            try
            {
                var o = new RxScanOptions { StructSize = (uint)Marshal.SizeOf(typeof(RxScanOptions)), Flags = options.ValidateContent ? 1u : 0u, MaxResults = (uint)Math.Max(0, options.MaxResults) };
                IntPtr h;
                int st = NativeMethods3.rx_ntfs_scan_create(NormalizeRoot(root), ref o, out h);
                var status = Enum.IsDefined(typeof(EngineStatus), st) ? (EngineStatus)st : EngineStatus.Internal;
                if (status != EngineStatus.Ok) return new ScanOpenResult { Status = status, Detail = "rx_ntfs_scan_create returned " + st };
                return new ScanOpenResult { Status = EngineStatus.Ok, Session = new NativeQuickScanSession(h, QuickScanApi.Ntfs) };
            }
            catch (Exception ex) when (IsInterop(ex))
            {
                _log.Error("Native engine call failed", ex);
                return new ScanOpenResult { Status = EngineStatus.Internal, Detail = ex.Message };
            }
        }

        public ScanOpenResult OpenFatScan(string root, ScanOptions options)
        {
            if (!IsAvailable) return new ScanOpenResult { Status = EngineStatus.Unavailable, Detail = _reason };
            try
            {
                var o = new RxScanOptions { StructSize = (uint)Marshal.SizeOf(typeof(RxScanOptions)), Flags = options.ValidateContent ? 1u : 0u, MaxResults = (uint)Math.Max(0, options.MaxResults) };
                IntPtr h;
                int st = NativeMethods5.rx_fat_scan_create(NormalizeRoot(root), ref o, out h);
                var status = Enum.IsDefined(typeof(EngineStatus), st) ? (EngineStatus)st : EngineStatus.Internal;
                if (status != EngineStatus.Ok) return new ScanOpenResult { Status = status, Detail = "rx_fat_scan_create returned " + st };
                return new ScanOpenResult { Status = EngineStatus.Ok, Session = new NativeQuickScanSession(h, QuickScanApi.Fat) };
            }
            catch (Exception ex) when (IsInterop(ex))
            {
                _log.Error("Native engine call failed", ex);
                return new ScanOpenResult { Status = EngineStatus.Internal, Detail = ex.Message };
            }
        }

        public ScanOpenResult OpenDeepScan(string root, DeepScanOptions options)
        {
            if (!IsAvailable) return new ScanOpenResult { Status = EngineStatus.Unavailable, Detail = _reason };
            try
            {
                var o = new RxCarveOptions
                {
                    StructSize = (uint)Marshal.SizeOf(typeof(RxCarveOptions)),
                    Flags = options != null && options.IncludeUsedSpace ? NativeMethods4.CarveAllSpace : 0u,
                    TypeMask = 0,
                    MaxResults = (uint)Math.Max(0, options == null ? 0 : options.MaxResults)
                };
                IntPtr h;
                int st = NativeMethods4.rx_carve_create(NormalizeRoot(root), ref o, out h);
                var status = Enum.IsDefined(typeof(EngineStatus), st) ? (EngineStatus)st : EngineStatus.Internal;
                if (status != EngineStatus.Ok) return new ScanOpenResult { Status = status, Detail = "rx_carve_create returned " + st };
                return new ScanOpenResult { Status = EngineStatus.Ok, Session = new NativeDeepScanSession(h) };
            }
            catch (Exception ex) when (IsInterop(ex))
            {
                _log.Error("Native engine call failed", ex);
                return new ScanOpenResult { Status = EngineStatus.Internal, Detail = ex.Message };
            }
        }

        public HeaderCheck CheckHeader(string fileName, byte[] data, int length)
        {
            if (!IsAvailable || data == null || length <= 0) return HeaderCheck.NotChecked;
            try
            {
                uint r;
                int st = NativeMethods3.rx_check_header(fileName ?? string.Empty, data, (uint)Math.Min(length, data.Length), out r);
                return st == 0 && Enum.IsDefined(typeof(HeaderCheck), (int)r) ? (HeaderCheck)r : HeaderCheck.NotChecked;
            }
            catch (Exception ex) when (IsInterop(ex)) { _log.Error("Native engine call failed", ex); return HeaderCheck.NotChecked; }
        }

        private void Require() { if (!IsAvailable) throw new EngineException("Native engine unavailable: " + _reason); }

        private static string NormalizeRoot(string root)
        {
            string r = (root ?? string.Empty).Trim();
            return r.Length == 2 && r[1] == ':' ? r + "\\" : r;
        }

        private static bool IsInterop(Exception ex)
        {
            return ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException
                || ex is SEHException || ex is MarshalDirectiveException || ex is AccessViolationException;
        }

        private EngineException Wrap(Exception ex)
        {
            _log.Error("Native engine call failed", ex);
            return new EngineException("Native engine call failed: " + ex.Message, ex);
        }
    }
}
