using System;
using System.Runtime.InteropServices;

namespace RecoverX.Core.Native
{
    // Mirrors include/rx_engine.h (ABI version 1). Sizes are verified at load time against rx_sizeof_* so a
    // mismatched DLL is rejected instead of corrupting memory.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct RxVolumeInfo
    {
        public uint StructSize;
        public uint AbiVersion;
        public uint BusType;
        public uint MediaType;
        public int RemovableMedia;
        public int TrimEnabled;
        public uint Flags;
        public uint Win32Error;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)] public string Root;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 40)] public string Vendor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 48)] public string Product;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RxProbeResult
    {
        public uint StructSize;
        public uint FsId;
        public uint BytesPerSector;
        public uint Reserved;
        public ulong TotalBytes;
        public uint BytesRead;
        public uint Win32Error;
    }

    internal static class NativeMethods
    {
        public const string DllName = "recoverx_engine.dll";
        public const uint ExpectedAbi = 1;
        public const uint FlagDeviceQueried = 1, FlagSeekQueried = 2, FlagTrimQueried = 4, FlagOpenFailed = 8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryW(string path);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint rx_get_abi_version();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr rx_get_version_string();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint rx_sizeof_volume_info();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint rx_sizeof_probe_result();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int rx_enumerate_volumes([In, Out] RxVolumeInfo[] items, uint capacity, out uint count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int rx_probe_volume([MarshalAs(UnmanagedType.LPWStr)] string root, ref RxProbeResult result);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int rx_validate_destination([MarshalAs(UnmanagedType.LPWStr)] string sourceRoot,
                                                         [MarshalAs(UnmanagedType.LPWStr)] string destDir, out int verdict);
    }
}

namespace RecoverX.Core.Native
{
    // Phase 3 structures. Sizes: RxScanOptions 16, RxScanProgress 32, RxFileEntry 1120 (verified at load and in tests).
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct RxScanOptions { public uint StructSize, Flags, MaxResults, Reserved; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct RxScanProgress { public uint StructSize, Phase, RecordsTotal, RecordsDone, Candidates, Flags, Status, BadRecords; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal struct RxFileEntry
    {
        public uint StructSize, Flags, Condition, HeaderCheck;
        public ulong MftNumber, Size, Created, Modified, Accessed, RecordChanged;
        public uint ClustersTotal, ClustersInUse, RecordSeq, Reserved;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 520)] public string Path;
    }

    internal static class NativeMethods3
    {
        private const string Dll = NativeMethods.DllName;
        private const System.Runtime.InteropServices.CallingConvention Cc = System.Runtime.InteropServices.CallingConvention.Cdecl;

        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = Cc, ExactSpelling = true)] public static extern uint rx_sizeof_file_entry();
        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = Cc, ExactSpelling = true)] public static extern uint rx_sizeof_scan_progress();
        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = Cc, ExactSpelling = true)] public static extern uint rx_sizeof_scan_options();

        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = Cc, CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
        public static extern int rx_ntfs_scan_create([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string root, ref RxScanOptions options, out System.IntPtr scan);
        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = Cc, ExactSpelling = true)] public static extern int rx_ntfs_scan_run(System.IntPtr scan, System.IntPtr cancel);
        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = Cc, ExactSpelling = true)] public static extern int rx_ntfs_scan_progress(System.IntPtr scan, ref RxScanProgress progress);
        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = Cc, ExactSpelling = true)] public static extern uint rx_ntfs_scan_result_count(System.IntPtr scan);
        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = Cc, ExactSpelling = true)]
        public static extern int rx_ntfs_scan_get_results(System.IntPtr scan, uint start, [System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] RxFileEntry[] items, uint capacity, out uint count);
        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = Cc, ExactSpelling = true)]
        public static extern int rx_ntfs_read_file(System.IntPtr scan, uint index, ulong offset, [System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] byte[] buffer, uint length, out uint bytesRead);
        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = Cc, ExactSpelling = true)] public static extern void rx_ntfs_scan_destroy(System.IntPtr scan);

        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = Cc, ExactSpelling = true)] public static extern System.IntPtr rx_cancel_create();
        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = Cc, ExactSpelling = true)] public static extern void rx_cancel_request(System.IntPtr token);
        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = Cc, ExactSpelling = true)] public static extern void rx_cancel_destroy(System.IntPtr token);

        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = Cc, CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
        public static extern int rx_check_header([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string name,
                                                 [System.Runtime.InteropServices.In] byte[] data, uint length, out uint result);
    }
}
