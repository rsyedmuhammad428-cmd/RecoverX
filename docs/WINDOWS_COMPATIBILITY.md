# Windows compatibility - status and test plan

**Nothing below has been tested on Windows. No compatibility is claimed.**

## What was done to target Windows 7 SP1 / 10 / 11
- Engine compiled with `_WIN32_WINNT=0x0601`; only APIs available on Windows 7 are used (the 32-bit DLL's imports were inspected; all are old kernel32 functions).
- Storage structures are declared locally (no dependency on a newer SDK); queries fail independently and are reported as "unknown" instead of failing the app.
- Static C runtime (`/MT`): no Visual C++ Redistributable needed. Unverified on a clean Windows 7 SP1 with the MSVC 2022 toolset.
- Both x64 and x86 engines are built; the app loads the one matching the process.
- Manifest declares Windows 7, 8, 8.1, 10/11 and `asInvoker`.
- Real OS version via `RtlGetVersion` (not the compatibility-shimmed `Environment.OSVersion`), shown on the About page and written to the log at start-up.
- UI uses no Windows 10/11-only controls or effects; glyphs come from Segoe UI Symbol (present on Windows 7).

## Prerequisites on a target machine
Windows 7 needs **Service Pack 1** and **.NET Framework 4.8** (which itself needs recent Windows 7 updates; Microsoft ended Windows 7 support, so installers may require manual update packages).

## Test matrix (fill in after running `scripts\Test-Compatibility.ps1` on each machine)
| Check | Windows 7 SP1 | Windows 10 | Windows 11 |
|---|---|---|---|
| App starts | NOT TESTED | NOT TESTED | NOT TESTED |
| Native engine loads (About page) | NOT TESTED | NOT TESTED | NOT TESTED |
| SSD/HDD detection correct | NOT TESTED | NOT TESTED | NOT TESTED |
| USB vs internal correct | NOT TESTED | NOT TESTED | NOT TESTED |
| Dark/Light themes legible | NOT TESTED | NOT TESTED | NOT TESTED |
| Verify read access (not elevated -> clear denial) | NOT TESTED | NOT TESTED | NOT TESTED |
| Verify read access (elevated) | NOT TESTED | NOT TESTED | NOT TESTED |
| Settings persist across restart | NOT TESTED | NOT TESTED | NOT TESTED |
| 125% / 150% display scaling | NOT TESTED | NOT TESTED | NOT TESTED |
| `Test-NtfsVhd.ps1` passes (scan + SHA-256 recovery on a virtual NTFS disk) | NOT TESTED | NOT TESTED | NOT TESTED |
| Quick Scan of a real NTFS drive, elevated | NOT TESTED | NOT TESTED | NOT TESTED |
| Recover files to a different drive; files open correctly | NOT TESTED | NOT TESTED | NOT TESTED |
| Quick Scan while not elevated shows a clear message + "Restart as administrator" | NOT TESTED | NOT TESTED | NOT TESTED |
| Results list stays responsive with thousands of rows | NOT TESTED | NOT TESTED | NOT TESTED |

Phase 3 specific risks: raw volume reads on Windows 7 vs 10/11 (volume handle sharing, BitLocker volumes), NTFS volumes created by Windows (the engine was validated on volumes created by the Linux NTFS driver), the 16 MB read cap and 4 MB MFT chunking on real hardware, and performance on HDDs.

Known risk areas to look at first: DLL load failure on Windows 7 (see README for the `-Toolset v141` fallback), NVMe/USB-bridge drives reporting "Unknown",
WPF rendering on Windows 7 without Aero, and the `System.Data.SQLite` native interop folders (`x64\`, `x86\`) next to the exe.
