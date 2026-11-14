# RecoverX 1.0

**Your Data. Another Chance.** — Powered by RazaZaidi.AI

RecoverX is a Windows desktop application for recovering deleted files from hard disks, SSDs, USB sticks, memory cards and external
drives. It is a **WPF app (.NET Framework 4.8, C#)** on top of a **native C++ recovery engine** (`recoverx_engine.dll`). The engine
only ever **reads** the drive being recovered. It has no write API at all.

---

## Table of contents
1. [Features](#1-features)
2. [What recovery can and cannot do](#2-what-recovery-can-and-cannot-do)
3. [Technology stack](#3-technology-stack)
4. [Architecture](#4-architecture)
5. [Project structure](#5-project-structure)
6. [How it works](#6-how-it-works)
7. [Using the app](#7-using-the-app)
8. [Installation](#8-installation)
9. [Building from source](#9-building-from-source)
10. [Testing](#10-testing)
11. [Scripts reference](#11-scripts-reference)
12. [Settings, data and logs](#12-settings-data-and-logs)
13. [Safety and security](#13-safety-and-security)
14. [Windows compatibility](#14-windows-compatibility)
15. [Known limitations](#15-known-limitations)
16. [Troubleshooting](#16-troubleshooting)
17. [Further documentation](#17-further-documentation)
18. [License](#18-license)

---

## 1. Features

| Feature | What it does |
|---|---|
| **Quick Scan** | NTFS, FAT32, FAT16/12 and exFAT. Reads the file system's own records and finds deleted files **with their original names, folders and dates**. |
| **Deep Scan** | Works on any drive, including formatted ones. Searches raw data for photos, videos, documents, audio and archives by their content (signature carving). On NTFS/FAT/exFAT only free space is searched. File names cannot be recovered this way. |
| **Restore points** | NTFS. Finds files that still exist in Windows restore points (Volume Shadow Copies) but are gone from the drive. This is the one method that can help on SSDs with TRIM. |
| **Ratings** | Every file is rated **Excellent / Partial / Poor** from evidence checked on the drive: have the clusters been reused, is the structure intact, do the checksums match. Ratings are estimates. |
| **Search, filters, sort** | Search by name or path. Filter by file type, condition, size and date. Sort by name, size, date, condition or type. |
| **Preview** | Images (JPG, PNG, BMP, GIF), any text file, PDF (details and a text sample) and a hex view for everything else. It also tells you when the data was erased (all zeros). |
| **Safe recovery** | Never writes to the drive being recovered. Each recovery goes into a new folder, nothing is overwritten, and every file is re-checked after writing. |
| **History** | Scan history and a list of recovered files, stored in SQLite as metadata only (never file contents). |
| **Hardware info** | Detects bus type (USB, SATA, NVMe…), SSD vs HDD, removable media and TRIM status, and warns when TRIM makes recovery unlikely. |
| **Themes** | Light and Dark themes. |
| **Pause / resume / cancel** | Every scan type can be paused, resumed or cancelled. Results found so far are kept. |

---

## 2. What recovery can and cannot do

- **HDDs, USB sticks, memory cards, external disks:** deleted files usually come back intact if the drive was not used much afterwards.
- **SSDs with TRIM (most internal SSDs):** the drive erases deleted data within minutes. Only tiny files (under about 700 bytes, which are
  stored inside the NTFS file table) survive. RecoverX shows such files as *erased*. Restore points, cloud recycle bins and backups are the
  realistic options.
- **Overwritten, encrypted (BitLocker) or physically damaged drives:** overwritten data is gone, encrypted volumes must be unlocked first,
  and failing hardware needs a professional lab. Recovery is never guaranteed.

> **Tip:** stop using the drive as soon as you notice data loss. Every new file written to it can overwrite what you want back.

---

## 3. Technology stack

### Desktop application (managed)
| Item | Choice |
|---|---|
| Language | C# 7.3 |
| Runtime / target | .NET Framework 4.8 (`net48`), SDK-style projects |
| UI framework | WPF (Windows Presentation Foundation), XAML |
| UI pattern | MVVM (ViewModels + `RelayCommand`, `ViewModelBase` with `INotifyPropertyChanged`) |
| Dependency injection | `Microsoft.Extensions.DependencyInjection` 6.0.1 |
| Database | SQLite through `System.Data.SQLite.Core` 1.0.118 |
| Restore points | WMI (`System.Management`, `Win32_ShadowCopy`) and direct Win32 file APIs |
| Folder picker | WinForms `FolderBrowserDialog` (available on Windows 7) |
| Native interop | P/Invoke (`NativeMethods.cs`) to the C ABI of `recoverx_engine.dll` |
| Localization | `Resources/Strings.resx` (English), `{loc:Loc Key}` markup extension in XAML |
| Process bitness | AnyCPU. Loads the `x64\` or `x86\` engine to match the process. |

### Recovery engine (native)
| Item | Choice |
|---|---|
| Language | C++14 |
| Build system | CMake 3.15+ |
| Compilers | GCC 16.2 (WinLibs MinGW-w64, msvcrt runtime), verified. MSVC (Visual Studio 2022, `/MT`) supported but not yet run. |
| Output | `recoverx_engine.dll` for x64 and x86, plus the `rx_selftest.exe` diagnostic tool |
| ABI | Plain C, cdecl, UTF-16 strings, ABI version 3 (`include/rx_engine.h`) |
| Runtime dependencies | Only `KERNEL32.dll` and `msvcrt.dll`. No Visual C++ Redistributable, no Universal CRT. |
| Minimum OS API level | `_WIN32_WINNT=0x0601` (Windows 7) |
| Platform layers | `platform_win.cpp` (real volumes) and `platform_stub.cpp` (disk-image files, used for tests on Linux) |

### Testing and tooling
| Item | Choice |
|---|---|
| Managed tests | MSTest 3.1 (`Microsoft.NET.Test.Sdk` 17.8), run in both 64-bit and 32-bit processes |
| Native tests | Custom C++ test executables driven by CTest; optional AddressSanitizer + UBSan (`-DRX_SANITIZE=ON`) |
| Test fixtures | Python 3 generators; real NTFS/FAT32/exFAT disk images made by the Linux kernel / ntfs-3g drivers |
| Build scripts | PowerShell (Windows), POSIX shell (Linux verification) |
| Installer | Inno Setup 6.3+ (`installer/RecoverX.iss`) |

---

## 4. Architecture

```
 ┌──────────────────────────────────────────────────────────────────┐
 │ RecoverX.Desktop  (WPF, MVVM, DI)                                 │
 │   Views (XAML) ──binds──► ViewModels ──► services via interfaces   │
 └──────────────────────────────┬───────────────────────────────────┘
                                │ IScanService, IRecoveryService, IPreviewService,
                                │ IDriveService, IShadowCopyService, ...
 ┌──────────────────────────────▼───────────────────────────────────┐
 │ RecoverX.Core   models, services, result query, P/Invoke layer    │
 │                 NativeEngineClient ─► NativeMethods (P/Invoke)    │
 └───────────────┬───────────────────────────────┬──────────────────┘
                 │                               │
 ┌───────────────▼──────────────┐   ┌────────────▼─────────────────┐
 │ RecoverX.Database (SQLite)   │   │ recoverx_engine.dll (C++14)   │
 │ settings, categories,        │   │ read-only volume access,      │
 │ scan history, recovered list │   │ NTFS / FAT / exFAT Quick Scan,│
 └──────────────────────────────┘   │ Deep Scan carving, hardware   │
                                    └──────────────────────────────┘
```

**Layering rules**
- The UI never calls the native DLL directly: `Desktop → Core (IEngineClient / services) → NativeMethods → recoverx_engine.dll`.
- The engine is optional at runtime. Without it the app falls back to basic .NET drive detection, and scans report "engine unavailable".
- Core and Database contain no display text. They return resource keys and the UI localizes them (`scripts/check-localization.py` enforces this).
- Long-running work (scans, recovery, preview reads, image decoding, database reads) runs off the UI thread. Progress is polled and reported back.
- All services are registered as singletons in `App.xaml.cs` and injected into the ViewModels.

---

## 5. Project structure

```
RecoverX.sln                    Visual Studio / dotnet solution
Directory.Build.props           Shared .NET settings: net48, C# 7.3, version 1.0.1, company/product
CMakeLists.txt                  Top-level CMake: engine + native tests (+ optional sanitizers)
cmake/                          MinGW-w64 cross-compile toolchain files (x86_64, i686) for Linux hosts

src/
  RecoverX.Core/                Business logic (no UI)
    Interfaces/                 IEngineClient, IScanService, IRecoveryService, IDriveService, ...
    Models/                     AppSettings, DriveModel, ScanModels, ScanSession, FileCategory, HardwareModels, ...
    Native/                     NativeMethods (P/Invoke), NativeEngineClient, NativeScanSessions
    Services/                   ScanService, RecoveryService, PreviewService, ShadowCopyService, ResultQuery,
                                ScanFilter, DriveService, EnrichedDriveService, DriveClassifier, DestinationValidator,
                                RecoveryPathBuilder, FileCategorizer, FileLogger, AppPaths, SystemInfo
  RecoverX.Database/            SQLite: SqliteDatabase (schema + migrations), SettingsService,
                                ScanHistoryRepository, RecoveredFilesRepository, CategoryRepository
  RecoverX.Desktop/             WPF app
    App.xaml(.cs)               Startup: logger, database, DI container, theme, environment log
    MainWindow.xaml             Shell with the navigation sidebar
    Views/                      Dashboard, DriveCards, Drives, Scan, RecoveredFiles, History, Settings, About
    ViewModels/                 One ViewModel per view + MainViewModel (navigation)
    Services/                   Localizer, ThemeService, ScanLauncher, SystemServices (dialogs, elevation)
    Themes/                     Light.xaml, Dark.xaml, Styles.xaml
    Resources/Strings.resx      All UI text
    app.manifest                asInvoker, supported OS list (Windows 7 to 11)
  RecoverX.Native/              C++ engine
    include/rx_engine.h         Public C ABI
    src/api.cpp                 ABI entry points (every exception caught here)
    src/platform_win.cpp        Win32 volume I/O + storage IOCTLs
    src/platform_stub.cpp       Image-file "volumes" for tests / Linux
    src/fs_detect.cpp           Boot-sector file-system identification
    src/ntfs_format.*, ntfs_scan.cpp   NTFS parsing and Quick Scan
    src/fat_format.*, fat_scan.cpp     FAT12/16/32 + exFAT parsing and Quick Scan
    src/carve_format.*, carve_scan.cpp Deep Scan (signature carving)
    src/file_sig.cpp            File-type header checks
    src/dest_guard.cpp          Refuses a recovery destination on the scanned volume
    src/sha256.h                SHA-256 (tests and verification)
    src/selftest_main.cpp       rx_selftest.exe diagnostic tool

tests/
  CoreTests/                    MSTest managed tests, incl. integration tests against the real engine DLL
  NativeTests/                  native_tests, ntfs_tests, fat_tests, carve_tests, perf_tests, abi_layout_check
  fixtures/                     Disk-image fixtures + manifests (SHA-256 ground truth) and their generators
  mono/                         Helpers to run the managed tests under Mono on Linux

scripts/                        Build, toolchain setup, verification, compatibility and VHD test scripts
installer/                      Inno Setup script, icon, stage/ (Release build) and output/ (setup exe)
native-bin/                     Built engine DLLs (x64/, x86/), copied next to the exe at build time
docs/                           Detailed technical documentation
```

---

## 6. How it works

### 6.1 Startup
1. `App.xaml.cs` creates the file logger (`%LOCALAPPDATA%\RecoverX\logs`) and opens or creates the SQLite database, upgrading its schema if needed.
2. Scans left "running" by a previous crash are marked **Interrupted**.
3. `NativeEngineClient` loads `x64\recoverx_engine.dll` or `x86\recoverx_engine.dll` by full path. It checks the ABI version and the
   struct sizes. On a mismatch the engine is disabled instead of risking memory corruption.
4. Every service and ViewModel is registered in the DI container. The saved theme is applied and the OS version, process bitness and
   engine version are written to the log.

### 6.2 Drive detection
`DriveService` lists drives with .NET `DriveInfo`. `EnrichedDriveService` adds engine data on top: bus type, SSD/HDD (seek penalty),
removable flag, TRIM, vendor and product. The engine gets this through `IOCTL_STORAGE_QUERY_PROPERTY` on a query-only handle, so no
administrator rights are needed. Each query can fail on its own and is then shown as "unknown". Serial numbers are never read.

### 6.3 Scan flow
```
Drive card (Quick Scan | Deep Scan | Restore points)
  → IScanLauncher.RequestScan(drive, mode)
  → ScanViewModel
  → ScanService.RunAsync(mode)
       Quick, NTFS        → rx_ntfs_scan_*   (NativeQuickScanSession)
       Quick, FAT/exFAT   → rx_fat_scan_*    (NativeQuickScanSession)
       Deep               → rx_carve_*       (NativeDeepScanSession)
       Restore points     → ShadowCopyService (SnapshotScanSession, managed only)
  → IScanSession: Run (cancellable), SetPaused, GetProgress, GetResults, ReadFile
```
Results stay inside the open native session. Preview and recovery read file data from it. Each result becomes a `ScanEntry` with an
origin (file table or signature), its evidence and a rating.

### 6.4 Quick Scan — NTFS
- Opens the volume read-only and validates the boot sector, `$MFT` (falling back to the mirror) and `$Bitmap`.
- Walks every MFT record and applies the update-sequence fixups. It parses `$STANDARD_INFORMATION`, `$FILE_NAME` (Win32 name preferred)
  and `$DATA` (resident data, data runs, sparse and fragmented files).
- Rebuilds full paths from parent references and checks `$Bitmap` to see whether the file's clusters have been reused.
- Every time a file is read, its MFT record is re-read and re-validated. If the record has been reused since the scan, the read fails safely.

### 6.5 Quick Scan — FAT12/16/32 and exFAT
- Decides the FAT type by cluster count and identifies exFAT by its OEM name. Every BPB field is validated.
- Walks every directory breadth-first, including deleted folders whose first cluster is still intact.
- **Long names** are rebuilt from deleted LFN entries and accepted only if their checksum matches. The lost first byte of a deleted entry
  is found by trying every value against the checksum. exFAT entry sets are accepted only when the set checksum matches.
- **Data:** FAT clears a deleted file's cluster chain, so data is read contiguously from the first cluster. exFAT files flagged
  contiguous are exact, and an intact FAT chain is followed when it matches the file length exactly.

### 6.6 Deep Scan (signature carving)
- **Search area:** on NTFS, FAT and exFAT only free clusters are read, using the `$Bitmap`, the FAT table or the exFAT bitmap. Without a
  map (unknown or damaged file system) the whole volume is read. A setting can force the whole volume to be read on NTFS.
- **Candidates:** every 512-byte boundary is checked against known file headers. Unreadable regions are retried in 64 KB pieces and counted.
- **Parsers:** each format is walked and validated, never just "header to fixed size":

| Format | Extensions | How the end and integrity are checked |
|---|---|---|
| JPEG | jpg | Marker segments, entropy data to EOI; EXIF date |
| PNG | png | Every chunk CRC-32 up to IEND |
| GIF | gif | Blocks and sub-blocks to the trailer |
| BMP | bmp | Header consistency (width × height × depth = size) |
| PDF | pdf | Last `%%EOF` whose `startxref` points at a real xref |
| ZIP family | zip, docx, xlsx, pptx, odt, ods, odp, epub, jar, apk | Central directory and every local header verified; type from entry names |
| ISO BMFF | mp4, mov, m4a, 3gp, heic, avif, cr3 | Top-level box walk (moov/mvhd/mdat) |
| RIFF | wav, avi, webp | Chunk walk must end exactly at the RIFF size |
| MP3 | mp3 | ID3v2 + chain of ≥ 8 MPEG frames; tail confirmed |
| 7z | 7z | Start-header and next-header CRCs |
| EBML | mkv, webm | DocType + Segment, element walk |
| OLE2 | doc, xls, ppt, msg | FAT/DIFAT and directory chain |

### 6.7 Restore points (Volume Shadow Copies)
`ShadowCopyService` lists the drive's shadow copies through WMI `Win32_ShadowCopy`, matched by volume GUID. `SnapshotScanSession` walks
them newest first with `FindFirstFileW` / `CreateFileW` on `\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\...` and reports files whose
path no longer exists on the live drive. System, program and AppData folders are skipped.

### 6.8 Ratings
The engine reports an evidence-based condition. The app maps it to what the user sees:

| Engine condition | Meaning | Shown as |
|---|---|---|
| `VERIFIED` | Clusters free and header/structure/checksum confirmed | **Excellent** |
| `LIKELY` | Clusters free; content could not be fully checked | **Excellent** |
| `PARTIAL` | Some clusters reused, or the structure is truncated/corrupt | **Partial** |
| `LOST` | All clusters reused, or the header does not match | **Poor** |
| `UNSUPPORTED` / `EMPTY` / `UNKNOWN` | Cannot be recovered or checked | Not recoverable |

The tooltip says exactly what was verified.

### 6.9 Results, search and filters
`ScanFilter` applies the Settings (enabled file categories, minimum size). `ResultQuery.Apply` then applies search, type, rating, size,
date range and sort. It is pure and unit-tested, and filters 250,000 rows in well under a second. Rows are shown in a virtualized
`DataGrid` with lazily computed text, and selection is kept across filter changes.

File categories (editable in Settings):

| Category | Extensions |
|---|---|
| Photos | jpg, jpeg, png, bmp, gif, tif, tiff, webp, heic, avif, cr3 |
| Videos | mp4, avi, mov, mkv, wmv, flv, webm, m4v, 3gp |
| Documents | pdf, doc, docx, xls, xlsx, ppt, pptx, txt, rtf, odt, ods, odp, epub, msg |
| Audio | mp3, wav, flac, aac, ogg, m4a, wma |
| Archives | zip, rar, 7z, tar, gz |
| Other | everything else |

### 6.10 Preview
`PreviewService` reads the file from the scan session in a cancellable background task. Images are decoded and scaled on a worker thread,
and anything over 100 megapixels is refused. Text files are shown as text. PDFs are summarized (details and a text sample, with
decompression capped at 32 MB). Everything else gets a hex view. If the data is all zeros, the preview says it was **erased**
(typical after TRIM). Preview can be turned off, and a maximum size can be set, in Settings.

### 6.11 Recovery
1. `DestinationValidator` (and the engine's `rx_validate_destination`) refuse a destination on the drive being scanned. They also check
   that the folder exists and that there is enough free space.
2. A new folder is created for each recovery session, so nothing is ever overwritten.
3. `RecoveryPathBuilder` turns the recovered (untrusted) path into a safe one. It removes `..`, rooted paths, reserved names like `CON`
   and invalid characters, and keeps the original folder structure where possible.
4. `RecoveryService` reads the data from the engine and writes it in managed code. It then re-reads and re-checks the written file.
   A cancelled recovery removes the partial file.
5. Each recovered file is recorded in `recovered_files` (metadata only).

---

## 7. Using the app

| Page | Purpose |
|---|---|
| **Dashboard** | Overview, recent activity and quick start |
| **Recover Files** | The active scan: progress, pause/resume/cancel, results table, filters, preview, recover |
| **Drive Scan** | Drive cards with capacity, file system and hardware info; start Quick Scan, Deep Scan or Restore points |
| **Recovered Files** | Everything you have recovered, with its original and saved paths |
| **Scan History** | Past scans with drive, type, status and file count |
| **Settings** | Theme, default scan type, default recovery folder, file categories, minimum size, Deep Scan used-space option, preview, history size |
| **About** | Version, engine version, OS version |

**Step by step**
1. Open **Drive Scan**. Scanning needs administrator rights. If RecoverX is not elevated, click **Restart as administrator** and accept the
   UAC prompt.
2. On the drive's card choose **Quick Scan** first (fast, keeps names). If that does not find your files, try **Deep Scan**. On an NTFS
   system drive, also try **Restore points**.
3. Watch progress on **Recover Files**. You can pause, resume or cancel at any time, and results appear as they are found.
4. Narrow the results with search, type, rating, size and date filters. Select a file to preview it.
5. Tick the files you want and click **Recover**. Choose a folder on a **different drive**.
6. Open the recovered folder, or review it later under **Recovered Files**.

---

## 8. Installation

**Requirements**
- Windows 7 SP1, 8.1, 10 or 11, 32-bit or 64-bit (built and tested on Windows 10; Windows 7 SP1 and 11 are not yet tested).
- Microsoft .NET Framework 4.8. It is already included in Windows 10 (1903+) and Windows 11. On Windows 7 SP1, install it first. Setup checks for it.
- About 10 MB of disk space. No Visual C++ Redistributable is needed.

**Install**
1. Run `RecoverX-Setup-1.0.1.exe`.
2. Choose **Install for all users** (Program Files, needs admin, recommended) or **Install for me only**.
3. Read the **License Agreement** (MIT) and choose **I accept the agreement**.
4. Optionally tick **Create a desktop shortcut**.

The license is also installed as `LICENSE.txt` in the program folder and linked from the Start Menu.

Silent install: `RecoverX-Setup-1.0.1.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`. Add `/CURRENTUSER` for a per-user install,
`/TASKS=desktopicon` for the shortcut and `/LOG=path` for a log.

**Uninstall:** Settings → Apps → RecoverX → Uninstall, or run `unins000.exe /VERYSILENT` in the install folder. Your
`%LOCALAPPDATA%\RecoverX` folder (settings, history, logs) and recovered files are kept.

RecoverX installs no drivers or services and never connects to the internet. Full details are in [docs/INSTALLATION.md](docs/INSTALLATION.md).

---

## 9. Building from source

### 9.1 Prerequisites
Choose **one** of these toolchains:

**A. Portable toolchain (recommended, no admin rights, no Visual Studio)**
```powershell
scripts\setup-portable-tools.ps1
```
This downloads the .NET SDK 8, CMake 4.5.0 and WinLibs GCC 16.2 (x64 + x86, msvcrt) into a `tools\` folder **next to** the repository
folder (about 1 GB). Delete that folder to remove everything.

**B. Visual Studio 2022**
- The ".NET desktop development" and "Desktop development with C++" workloads
- The .NET Framework 4.8 Developer Pack, a Windows 10/11 SDK and CMake 3.15+

You also need **Python 3** for the localization check. Run `scripts\check-env.ps1` to see what is installed.

### 9.2 Full build and test
```powershell
scripts\build-all.ps1
```
This does the following:
1. Builds the native engine for x64 and x86 and runs all native test suites (`build-native.ps1`).
2. Copies the DLLs into `native-bin\x64` and `native-bin\x86`.
3. Runs the localization check (`check-localization.py`).
4. Runs `dotnet build RecoverX.sln -c Release`.
5. Runs the managed tests in a 64-bit and a 32-bit process.

Then run the app: `src\RecoverX.Desktop\bin\Release\net48\RecoverX.Desktop.exe`.

Useful options: `-SkipNative` (reuse the engine already in `native-bin\`) and `-Toolchain mingw|msvc`.

### 9.3 Building parts separately
```powershell
# Native engine only
scripts\build-native.ps1                      # auto-picks mingw if the portable tools exist, else msvc
scripts\build-native.ps1 -Toolchain msvc      # Visual Studio 2022 (optional -Toolset v142)
scripts\build-native.ps1 -Clean -SkipTests

# .NET solution only (the engine DLLs are copied from native-bin\ if present)
dotnet build RecoverX.sln -c Release
dotnet test tests\CoreTests\RecoverX.CoreTests.csproj -c Release
```
If `native-bin\x64\recoverx_engine.dll` is missing, the build prints a warning. The app still runs, using basic drive detection only.

You can also open `RecoverX.sln` in Visual Studio 2022 and set **RecoverX.Desktop** as the startup project.

### 9.4 Building the installer
Install Inno Setup 6.3+ and run:
```powershell
scripts\build-installer.ps1        # -Iscc <path to ISCC.exe>, -SkipNative
```
This builds a Release copy into `installer\stage\`, checks that the exe, both engine DLLs and both SQLite interop DLLs are present, and
compiles `installer\output\RecoverX-Setup-<version>.exe`. The version comes from `Directory.Build.props`.

### 9.5 Linux (engine development and CI)
```sh
scripts/verify-linux.sh
```
This builds the engine with `platform_stub.cpp` (disk images act as volumes) and runs the native tests. It also cross-compiles the
Windows code path with MinGW-w64 if available, runs AddressSanitizer + UBSan, and optionally runs the managed tests under Mono
(`verify-managed-mono.sh`).

### 9.6 Changing the version
Edit `<Version>` in `Directory.Build.props`. It is used by every .NET assembly and by the installer.

---

## 10. Testing

| Suite | Location | Checks | What it covers |
|---|---|---|---|
| Engine basics, ABI | `tests/NativeTests/native_tests.cpp`, `abi_layout_check.cpp` | 55 | Version handshake, struct layouts, file-system detection, destination guard |
| NTFS Quick Scan | `ntfs_tests.cpp` | 1,408 | 162 files byte-exact on a real NTFS image, independent oracle, cancellation, fuzz |
| Deep Scan | `carve_tests.cpp` | 5,495 | SHA-256 ground truth, free-space mode, planted/overlapping files, wiped tails, 3,000-case fuzz |
| FAT32 + exFAT | `fat_tests.cpp` | 1,044 | Images made by Linux kernel drivers; names, deleted folders, fragmentation; 600 corrupted volumes |
| Large volume | `perf_tests.cpp` (manual) | 3 files | 16 GB image, files beyond 4/8/16 GB: 779 MB/s, 20.5 MB peak memory |
| Managed | `tests/CoreTests` | 85 | Search/filters, ratings, preview, restore points, recovery, database, real-engine interop (64- and 32-bit) |

All suites pass on Windows 10 22H2 for x64 and x86. Details are in [docs/TEST_REPORT.md](docs/TEST_REPORT.md).

**Fixtures:** `tests/fixtures/*.manifest.tsv` list every file with its SHA-256. To regenerate them:
`make_carve_fixture.py` (any OS), `make_ntfs_fixture.py` (Linux + ntfs-3g), `make_fat_fixtures.sh` (Docker, privileged Debian container).

**Engine test builds** define `RX_ALLOW_IMAGE_FILES` so disk-image files can stand in for volumes. The shipped DLL accepts drive letters
only, and a managed test verifies this.

**On a real Windows machine (elevated, disposable):**
```powershell
scripts\Test-NtfsVhd.ps1          # creates a temporary VHD, formats NTFS, deletes known files, Quick + Deep Scan, compares bytes
scripts\Test-Compatibility.ps1    # read-only; writes compat-report-<PC>-<time>.txt
```

---

## 11. Scripts reference

| Script | Purpose |
|---|---|
| `setup-portable-tools.ps1` | Download a portable .NET SDK, CMake and GCC (x64 + x86) into `..\tools` |
| `check-env.ps1` | Read-only check of installed build tools |
| `build-all.ps1` | Engine + native tests + localization check + solution build + managed tests (x64 and x86) |
| `build-native.ps1` | Engine only, for x64 and x86, with tests; stages DLLs in `native-bin\` |
| `build-installer.ps1` | Release build + Inno Setup → `installer\output\RecoverX-Setup-<version>.exe` |
| `check-localization.py` | Fails if any resource key used in XAML or code is missing from `Strings.resx` |
| `Test-NtfsVhd.ps1` | Elevated end-to-end test on a disposable virtual disk |
| `Test-Compatibility.ps1` | Collects a compatibility report on the current Windows machine |
| `verify-linux.sh` | Linux build, tests, cross-compile and sanitizer run |
| `verify-managed-mono.sh` | Managed tests under Mono on Linux |

---

## 12. Settings, data and logs

All per-user data lives in `%LOCALAPPDATA%\RecoverX\`:

| Path | Content |
|---|---|
| `recoverx.db` | SQLite database (schema version 3, upgraded in place with `PRAGMA user_version`) |
| `logs\` | Text logs: startup environment, engine status, scan and recovery events, errors |

**Database tables**

| Table | Content |
|---|---|
| `settings` | Key/value app settings |
| `file_categories` | User-editable categories: extensions, enabled flag, order |
| `scan_sessions` | Each scan: start/end time, drive, file system, scan type, status, files found |
| `recovered_files` | Each recovered file: time, source drive, original path, saved path, size, status |

File contents are never stored.

**Settings** (validated and clamped on load, so bad stored data cannot break the app)

| Setting | Default | Range / values |
|---|---|---|
| Theme | Light | Light, Dark |
| Default scan type | Quick | Quick, Deep |
| Default recovery folder | (none) | Any folder not on the scanned drive |
| Minimum file size | 0 KB | 0 – 1,048,576 KB |
| Deep Scan includes used space | off | on/off (NTFS) |
| Preview enabled | on | on/off |
| Preview maximum size | 25 MB | 1 – 1024 MB |
| History entries kept | 200 | 10 – 10,000 |

---

## 13. Safety and security

| Concern | How it is handled |
|---|---|
| Writing to the source drive | Impossible through the engine: it has no write API and opens volumes with `GENERIC_READ`. Recovery writes only to the chosen folder, which must be on another drive. |
| Arbitrary device paths | Engine entry points accept drive letters only. `\\.\PhysicalDrive0` and file paths are refused. |
| Untrusted file names | Sanitized: reserved names, `..`, rooted paths and invalid characters. Covered by path-traversal tests. |
| Untrusted file content | Every parser bounds-checks every offset and length, and all are fuzzed. PDF inflation is capped at 32 MB, and images over 100 megapixels are refused. |
| Native crashes | Every exception is caught at the C ABI boundary. Struct sizes are verified at load. |
| DLL loading | The engine is loaded by full path from the app's `x64\` / `x86\` folder. The default install location is Program Files. |
| Elevation | The manifest uses `asInvoker`. Admin rights are requested only when the user asks (UAC). |
| Network / telemetry | None. |
| Privacy | Logs and database stay local. Metadata only. Drive serial numbers are never read. |

---

## 14. Windows compatibility

| | Windows 7 SP1 | Windows 10 22H2 | Windows 11 |
|---|---|---|---|
| Build + all tests (x64, x86) | not tested | **pass** | not tested |
| App start, engine load, SSD/TRIM detection | not tested | **pass** | not tested |
| Quick Scan of a real NTFS SSD (elevated) | not tested | done by the user: files listed and recovered; TRIM-erased data shown as such | not tested |
| Restore points, installer, USB hardware, display scaling | not tested | not tested | not tested |

How Windows 7 is targeted: `_WIN32_WINNT=0x0601`, only Windows 7 APIs are used, the msvcrt runtime with static libstdc++ means no UCRT
is needed, the manifest declares Windows 7 to 11, and the UI uses Segoe UI Symbol glyphs only. The full test matrix is in
[docs/WINDOWS_COMPATIBILITY.md](docs/WINDOWS_COMPATIBILITY.md).

---

## 15. Known limitations

- Deep Scan cannot recover names or folders. It assumes each file is stored in one piece, so fragmented files are rated Partial/Poor
  rather than reassembled. Formats without a reliable end marker (plain text, RAW photos, TIFF, RAR…) are not searched by Deep Scan.
- FAT32/FAT16 erase a deleted file's cluster list, so Quick Scan reads such files as one continuous piece (exFAT usually records this
  exactly). A deleted 8.3-only name loses its first letter, which is shown as `_`.
- NTFS compressed or encrypted (EFS) files, `$ATTRIBUTE_LIST` files and alternate data streams are not recovered.
- Restore-point search skips system, program and AppData folders, and only covers NTFS drives with System Protection turned on.
- PDF preview shows details and a text sample, not rendered pages.
- At most 250,000 results per scan.
- The UI is English only.

---

## 16. Troubleshooting

| Problem | What to do |
|---|---|
| "Access denied" or scan will not start | Use **Restart as administrator** on the Drive Scan page. |
| "Engine unavailable" | `x64\` / `x86\recoverx_engine.dll` is missing or the wrong version. Reinstall, or run `scripts\build-native.ps1` when building from source. The log says why. |
| Files found but preview says *erased* | The SSD trimmed the data. Try **Restore points**, backups or cloud recycle bins. |
| Recover button refuses the folder | The folder is on the drive being scanned, does not exist, or there is not enough space. Choose another drive. |
| BitLocker drive shows nothing | Unlock the drive in Windows first. |
| Build warning "Native engine not found" | Run `scripts\build-native.ps1` (or `build-all.ps1`) before building the solution. |
| Localization check fails | A key used in XAML or code is missing from `Strings.resx`. Add it. |
| App closed during a scan | That scan is shown as **Interrupted** in Scan History on the next start. Run it again. |

Logs: `%LOCALAPPDATA%\RecoverX\logs\`. For engine diagnostics, use `native-bin\x64\rx_selftest.exe`.

---

## 17. Further documentation

| Document | Content |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Layering, scan flow, results page, ratings, safety decisions, packaging |
| [docs/RECOVERY_ENGINE.md](docs/RECOVERY_ENGINE.md) | Full C ABI reference, status codes, NTFS / Deep Scan / FAT internals |
| [docs/INSTALLATION.md](docs/INSTALLATION.md) | Requirements, install, silent install, uninstall, building the installer |
| [docs/TEST_REPORT.md](docs/TEST_REPORT.md) | Test results and security review |
| [docs/WINDOWS_COMPATIBILITY.md](docs/WINDOWS_COMPATIBILITY.md) | Compatibility work and the test matrix |
| [docs/LOCALIZATION.md](docs/LOCALIZATION.md) | How UI text and resource keys work |

---

## 18. License

RecoverX is released under the **MIT License**. See [LICENSE](LICENSE).

You may use, copy, modify and distribute it, including commercially, as long as you keep the copyright notice. The software is
provided "as is", without warranty. In particular, data recovery is never guaranteed.

**Third-party components shipped with RecoverX**

| Component | License |
|---|---|
| SQLite / System.Data.SQLite | Public domain |
| Microsoft.Extensions.DependencyInjection (+ Microsoft.Bcl.AsyncInterfaces, System.Threading.Tasks.Extensions, System.Runtime.CompilerServices.Unsafe) | MIT |
| GCC runtime (libstdc++, libgcc), statically linked into the engine | GPL v3 with the GCC Runtime Library Exception, which allows use in software under any license |
| .NET Framework 4.8 | Microsoft .NET Framework license (part of Windows, not redistributed) |

---

© 2026 RazaZaidi.AI — RecoverX 1.0.1. Recovery is never guaranteed. Always recover to a different drive.
