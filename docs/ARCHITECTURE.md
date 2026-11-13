# RecoverX Architecture (1.0)

```
RecoverX.sln
 src/RecoverX.Core       models, interfaces, drive logic, scan/recovery/preview services, result query (search/filter/sort),
                         P/Invoke layer (Native/), SystemInfo.
 src/RecoverX.Database   SQLite (System.Data.SQLite.Core): settings, file_categories, scan_sessions, recovered_files; versioned migrations (v3).
 src/RecoverX.Desktop    WPF, MVVM, Microsoft.Extensions.DependencyInjection, Resources/Strings.resx (English), themes.
 src/RecoverX.Native     C++14 engine, CMake, C ABI (include/rx_engine.h, ABI 3) -> recoverx_engine.dll (x64 + x86).
 tests/CoreTests         MSTest (managed), incl. integration tests against the real engine DLL.
 tests/NativeTests       native_tests, ntfs_tests (real NTFS image), carve_tests (Deep Scan ground truth), abi_layout_check.
 tests/fixtures          ntfs_basic (real NTFS image + manifest), carve_basic (Deep Scan image + manifest) and their generators.
 scripts/                portable toolchain setup, build, verification, compatibility and VHD test scripts.
```

## Layering rules
- The UI never calls the native DLL directly: `Desktop -> Core (IEngineClient / services) -> NativeMethods -> recoverx_engine.dll`.
- The engine is optional at runtime. Without it the app falls back to Phase 1 drive detection and scans report "engine unavailable".
- Core and Database contain no display text: they return resource keys; the UI localizes them (`scripts/check-localization.py` enforces it).
- Long-running work (scan, recovery, preview reads, image decoding, DB reads) runs off the UI thread; progress is polled/reported back.

## Scan flow (Quick and Deep)
`DriveCardViewModel (Quick Scan | Deep Scan | Restore points)` -> `IScanLauncher.RequestScan(drive, mode)` -> `ScanViewModel` ->
`ScanService.RunAsync(mode)` -> Quick: `OpenNtfsScan` (`rx_ntfs_scan_*`) or `OpenFatScan` (`rx_fat_scan_*`) by file system; Deep:
`OpenDeepScan` (`rx_carve_*`); Restore points: `IShadowCopyService` (WMI `Win32_ShadowCopy`, managed, no native engine needed).
All return an `IScanSession` (`NativeQuickScanSession` over either Quick Scan API, `NativeDeepScanSession`, `SnapshotScanSession`): Run (cancellable),
SetPaused, GetProgress (thread-safe), GetResults, ReadFile. Results stay in the open native session; `RecoveryService` and `PreviewService`
read file data from it. Results become `ScanEntry` objects with an `Origin` (file table / signature), evidence, and a rating.

## Results page
`ScanFilter` (Settings: categories, minimum size) -> `ResultQuery.Apply` (search, type, rating, size, date range, sort; pure and unit-tested,
250,000 rows in well under a second) -> rows in a virtualized `DataGrid`. Row text is computed lazily (only visible rows are rendered).
Selection is kept per row across filter changes. Selecting a row loads its preview (cancellable; images decoded and scaled on a worker
thread; PDF summarized by `PdfInspector` without rendering).

## Ratings
Engine conditions (`RX_COND_*`) are evidence-based; `ConditionInfo.Rating` maps them to the three user-facing ratings:
VERIFIED/LIKELY -> **Excellent** (no sign of damage), PARTIAL -> **Partial**, LOST -> **Poor**; unsupported/empty/unknown -> Not recoverable.
The tooltip states exactly what was verified, and every place that shows ratings says they are estimates.

## Safety decisions
- The engine has **no write API**; recovery writes happen in managed code, after destination checks (not on the scanned volume, exists, space).
- Query-only handles for hardware info; read-only handle for volumes; manifest `asInvoker`; raw reads need the user to restart elevated.
- Engine entry points accept drive letters only. Test builds (and only test builds) define `RX_ALLOW_IMAGE_FILES` so disk images can stand
  in for volumes; the shipped DLL rejects any other path (checked by a managed integration test).
- Every exception is caught at the C ABI boundary. NTFS and Deep Scan parsers validate every offset and length (fuzzed).
- Database: sanitized settings, metadata only (never file contents), idempotent upgrades.

## Restore points (Previous Versions)
`ShadowCopyService` lists the drive's shadow copies (matched by volume GUID) and `SnapshotScanSession` walks them newest first with
direct Win32 calls (`FindFirstFileW`/`CreateFileW` on `\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\...`), reporting files whose path
no longer exists on the drive. Results flow through the same filters, preview and recovery as every other scan.

## Packaging
`installer/RecoverX.iss` (Inno Setup): Program Files (or per-user) install, Start Menu + optional desktop shortcut, uninstaller,
.NET Framework 4.8 check, Windows 7 SP1 minimum, 64-bit install mode on x64. `scripts/build-installer.ps1` stages a Release build and
compiles `installer/output/RecoverX-Setup-<version>.exe`.
