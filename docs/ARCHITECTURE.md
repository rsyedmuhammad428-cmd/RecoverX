# RecoverX Architecture (through Phase 3)

```
RecoverX.sln
 src/RecoverX.Core       models, interfaces, drive logic, scan/recovery services, P/Invoke layer (Native/), SystemInfo.
 src/RecoverX.Database   SQLite (System.Data.SQLite.Core): settings, file_categories, scan_sessions, recovered_files; versioned migrations (v3).
 src/RecoverX.Desktop    WPF, MVVM, Microsoft.Extensions.DependencyInjection, Resources/Strings.resx (English), themes.
 src/RecoverX.Native     C++14 engine, CMake, C ABI (include/rx_engine.h) -> recoverx_engine.dll (x64 + x86).
 tests/CoreTests         MSTest (managed).   tests/NativeTests  C++ tests incl. real-NTFS fixture tests.   tests/fixtures  real NTFS image + ground truth.
 scripts/                build, verification, compatibility and VHD test scripts.
```

## Layering rules
- The UI never calls the native DLL directly: `Desktop -> Core (IEngineClient / services) -> NativeMethods -> recoverx_engine.dll`.
- The engine is optional at runtime. Without it the app falls back to Phase 1 drive detection and Quick Scan reports "engine unavailable".
- Core and Database contain no display text: they return resource keys; the UI localizes them.
- Long-running work (scan, recovery, DB reads) runs off the UI thread; progress is polled/reported back.

## Quick Scan / recovery flow
`DriveCardViewModel (Quick Scan)` -> `IScanLauncher` -> `ScanViewModel` -> `QuickScanService` -> `IEngineClient.OpenNtfsScan` -> native `rx_ntfs_scan_*`.
Results stay in the open native session; `RecoveryService` reads file data from that session (`rx_ntfs_read_file`), writes files, re-checks them, and records them via `IRecoveredFilesRepository`.

## Safety decisions
- The engine has **no write API**; recovery writes happen in managed code, after destination checks (not on the scanned volume, exists, enough space).
- Query-only handles for hardware info; read-only handle for volumes; manifest `asInvoker`; raw reads need the user to restart elevated after confirmation.
- Every exception is caught at the C ABI boundary. The NTFS parser validates every offset and length (fuzzed under ASan/UBSan).
- Evidence-based conditions: cluster-bitmap occupancy + file-header check; names are never trusted.
- Database: sanitized settings, metadata only (never file contents), idempotent upgrades.
