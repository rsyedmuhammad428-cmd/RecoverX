using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace RecoverX.Core.Services
{
    /// <summary>
    /// Honest environment diagnostics. Environment.OSVersion lies on Windows 8.1+ for apps without a matching manifest,
    /// so the real version comes from RtlGetVersion.
    /// </summary>
    public static class SystemInfo
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfoEx
        {
            public int Size; public int Major; public int Minor; public int Build; public int PlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string CsdVersion;
            public ushort ServicePackMajor; public ushort ServicePackMinor; public ushort SuiteMask; public byte ProductType; public byte Reserved;
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref OsVersionInfoEx info);

        public static string OsDescription()
        {
            try
            {
                var v = new OsVersionInfoEx();
                v.Size = Marshal.SizeOf(typeof(OsVersionInfoEx));
                if (RtlGetVersion(ref v) == 0)
                {
                    string name = Name(v.Major, v.Minor, v.Build);
                    string sp = v.ServicePackMajor > 0 ? " SP" + v.ServicePackMajor : string.Empty;
                    return string.Format("{0}{1} ({2}.{3}.{4}, {5})", name, sp, v.Major, v.Minor, v.Build,
                        Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit");
                }
            }
            catch (Exception) { /* fall back below */ }
            return Environment.OSVersion.VersionString;
        }

        private static string Name(int major, int minor, int build)
        {
            if (major == 6 && minor == 1) return "Windows 7";
            if (major == 6 && minor == 2) return "Windows 8";
            if (major == 6 && minor == 3) return "Windows 8.1";
            if (major == 10) return build >= 22000 ? "Windows 11" : "Windows 10";
            return "Windows " + major + "." + minor;
        }

        /// <summary>Installed .NET Framework 4.x as a friendly string, e.g. "4.8 (528040)".</summary>
        public static string DotNetFramework()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    object r = key == null ? null : key.GetValue("Release");
                    if (r is int)
                    {
                        int rel = (int)r;
                        string v = rel >= 528040 ? "4.8" : rel >= 461808 ? "4.7.2" : rel >= 461308 ? "4.7.1" : rel >= 460798 ? "4.7"
                                 : rel >= 394802 ? "4.6.2" : "older than 4.6.2";
                        return v + " (" + rel + ")";
                    }
                }
            }
            catch (Exception) { }
            return Environment.Version.ToString();
        }

        public static bool Is64BitProcess { get { return Environment.Is64BitProcess; } }

        public static bool IsAdministrator()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception) { return false; }
        }
    }
}
