# Windows compatibility - status and test plan

**Windows 10 22H2 (x64) has now been used for building, testing and running RecoverX (Phase 4). Windows 7 SP1 and Windows 11 have not been tested.
No compatibility is claimed for an OS until its column below is filled in from a real test.**

## What was done to target Windows 7 SP1 / 10 / 11
- Engine compiled with `_WIN32_WINNT=0x0601`; only APIs available on Windows 7 are used. Verified on the Phase 4 build: the x64 and x86
  `recoverx_engine.dll` import only `KERNEL32.dll` and `msvcrt.dll`, and every one of the 66 kernel32 imports exists on Windows 7.
- Built with GCC 16.2 (WinLibs, **msvcrt** runtime, static libstdc++/libgcc): no Visual C++ Redistributable and no Universal CRT (KB2999226)
  needed. The MSVC path (`/MT`, VS 2022) is still supported by CMake but has not been run.
- Storage structures are declared locally (no dependency on a newer SDK); queries fail independently and are reported as "unknown".
- Both x64 and x86 engines are built; the app loads the one matching the process (verified in 64-bit and 32-bit test processes).
- Manifest declares Windows 7, 8, 8.1, 10/11 and `asInvoker`.
- Real OS version via `RtlGetVersion`, shown on the About page and written to the log at start-up.
- UI uses no Windows 10/11-only controls or effects; glyphs come from Segoe UI Symbol (present on Windows 7). The folder picker is the
  WinForms `FolderBrowserDialog` (part of .NET Framework 4.8, available on Windows 7).

## Prerequisites on a target machine
Windows 7 needs **Service Pack 1** and **.NET Framework 4.8** (which itself needs recent Windows 7 updates; Microsoft ended Windows 7 support,
so installers may require manual update packages).

## Test matrix
| Check | Windows 7 SP1 | Windows 10 22H2 | Windows 11 |
|---|---|---|---|
| Solution builds (portable .NET SDK 8 + GCC 16.2), 0 warnings | n/a | **PASS** | NOT TESTED |
| Native test suites (55 + 1,408 + 5,495 + 1,044 checks), x64 and x86 | NOT TESTED | **PASS** | NOT TESTED |
| Managed tests (85), in a 64-bit and a 32-bit process, incl. P/Invoke layout checks against the real DLL | NOT TESTED | **PASS** | NOT TESTED |
| App starts | NOT TESTED | **PASS** (not elevated) | NOT TESTED |
| Native engine loads in the app | NOT TESTED | **PASS** (log: engine 0.4.0, x64 process) | NOT TESTED |
| SSD/HDD and TRIM detection | NOT TESTED | **PASS** (NVMe SSD, TRIM on, shown with warning) | NOT TESTED |
| USB vs internal correct | NOT TESTED | NOT TESTED (no USB drive attached) | NOT TESTED |
| Dark/Light themes legible | NOT TESTED | Dashboard + Drive Scan seen in both themes | NOT TESTED |
| Verify read access, not elevated -> clear denial | NOT TESTED | **PASS** (log: AccessDenied) | NOT TESTED |
| Quick Scan / Deep Scan of a real drive, elevated | NOT TESTED | User ran Quick Scan of an NTFS SSD: files listed, recovered, TRIM-erased data shown as such | NOT TESTED |
| 16 GB volume, offsets beyond 4 GB, x64 and x86 | NOT TESTED | **PASS** (sparse image, 725-779 MB/s, 20.5 MB peak) | NOT TESTED |
| Restore points listed (elevated) | NOT TESTED | NOT TESTED | NOT TESTED |
| Installer install / uninstall | NOT TESTED | NOT TESTED (Inno Setup not available on the build machine) | NOT TESTED |
| Scan while not elevated shows a clear message + "Restart as administrator" | NOT TESTED | NOT TESTED in the UI (engine returns AccessDenied, verified) | NOT TESTED |
| `Test-NtfsVhd.ps1` (elevated, disposable virtual disk) | NOT TESTED | NOT TESTED | NOT TESTED |
| Recover files to a different drive; files open correctly | NOT TESTED | NOT TESTED in the UI (recovery service tested with real file I/O) | NOT TESTED |
| Results table stays responsive with 250,000 rows | NOT TESTED | NOT TESTED in the UI (filtering 250,000 results: < 3 s in tests) | NOT TESTED |
| Preview of JPG/PNG/BMP/GIF/TXT/PDF in the app | NOT TESTED | NOT TESTED in the UI (preview service tested) | NOT TESTED |
| 125% / 150% display scaling | NOT TESTED | NOT TESTED | NOT TESTED |

Known risk areas: raw volume reads on Windows 7 (volume handle sharing, BitLocker volumes); NTFS volumes created by Windows itself (the engine
was validated on volumes created by the Linux NTFS driver and on images); Deep Scan duration on large HDDs; WPF rendering on Windows 7 without
Aero; the `System.Data.SQLite` native interop folders (`x64\`, `x86\`) next to the exe.
