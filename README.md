# RecoverX - Phase 3: NTFS Quick Scan and File Recovery

**Your Data. Another Chance.** - Powered by RazaZaidi.AI

Phase 3 adds the first real recovery feature: scan an NTFS drive for deleted files and copy recoverable ones to a safe place.
All earlier features are preserved (dashboard, drive detection with SSD/HDD/USB details, settings, scan history, themes, SQLite, logging).
Deep Scan, FAT32/exFAT, previews and search/filters are **not** implemented yet. The app is English only.

## Testing status - read this first

**Nothing in this phase has been built with Visual Studio or run on Windows 7, 10 or 11.** The sandbox that produced it was Linux.
No Windows compatibility is claimed. What *was* tested is the recovery engine against **real NTFS volumes**:

| Area | Result | How it was checked |
|---|---|---|
| NTFS parsing + deleted-file detection | **Passed** | A 64 MB volume created with `mkntfs` and populated/deleted through the real NTFS driver (ntfs-3g): 520 files created; 263 deleted-file records remain on disk (some freed records were reused by later files, as on a real disk) |
| Recovered content | **Passed**: 162 files recovered byte-for-byte | SHA-256 of engine output vs hashes recorded *before* deletion. Covers resident data, multi-MB files, Unicode names, files in deleted folders, a 9-level deep path, alternate streams, a genuinely fragmented file (>= 10 runs), 150 small files |
| Independent oracle | **Passed**: 0 disagreements over 263 records | `ntfsundelete` (a separate NTFS implementation): same files, same sizes, same "% recoverable" |
| Overwritten data | **Passed** | Forced real cluster reuse: file fully overwritten (0% free) and half overwritten (50%) are reported as such and read back different from the original |
| Compressed file | **Passed** | Reported as "not supported yet", never returned as garbage |
| Engine never writes | **Passed** | SHA-256 of the image identical before and after scanning + reading every file |
| Hostile input | **Passed** | 600 random corruptions of the boot sector and MFT under AddressSanitizer + UBSan + leak detection: no crash, hang or memory error. Truncated and non-NTFS images handled |
| Cancellation, progress from another thread, result cap | **Passed** | native tests |
| Managed logic (path safety, filtering, scan runner, recovery service, v3 database upgrade) | **Passed**: 59 tests | Run under Mono with a real SQLite engine and real file I/O: path traversal blocked, never overwrites, partial files removed on failure/cancel, short reads detected, same-drive destination refused, free-space check |
| Windows x64/x86 engine builds | **Compile** | MinGW cross-compile; struct sizes and offsets asserted at compile time against the C# structs |
| Windows x64 test binary | **Passed** under Wine (smoke test only) | Wine is not Windows; x86 not run |
| MSVC / Visual Studio build, WPF UI (start-up, layout, bindings, dark theme), real P/Invoke calls, `System.Data.SQLite` | **Not tested** | needs Windows |
| Scanning a real Windows-formatted NTFS volume, raw reads, elevation, any real hardware | **Not tested** | run `scripts\Test-NtfsVhd.ps1` (see below) |
| PowerShell scripts | **Not tested** | needs Windows PowerShell |
| Windows 7 SP1 / 10 / 11 | **Not tested** | `scripts\Test-Compatibility.ps1`, `scripts\Test-NtfsVhd.ps1` |

## What Quick Scan does
1. Reads the NTFS boot sector and the Master File Table (`$MFT`, including a fragmented MFT; falls back to the mirror copy if record 0 is damaged).
2. Finds file records that are marked deleted and rebuilds their folder paths (through deleted folders too; flags paths it cannot trust).
3. Gathers **evidence** for each file instead of trusting the name:
   - the cluster bitmap (`$Bitmap`): are the file's clusters still unused, partly reused, or fully reused?
   - the file header: does the first data match the file type its extension claims (JPG, PNG, PDF, ZIP/Office, MP4, MP3, 7z, ... and plain text)?
4. Reports a condition per file: **Looks intact**, **Probably recoverable**, **Partly overwritten**, **Overwritten or damaged**, **Empty file**, **Not supported yet**.
   A name alone never counts as "recoverable".

## Recovery safety rules (enforced in code and tested)
- The engine only **reads**; it has no write function. The recovery service writes, to a folder you choose.
- The destination must exist and **must not be on the drive being recovered**; free space is checked first.
- Files go into a **new** folder `RecoverX_<drive>_<date>_<time>` inside it. Existing files are never overwritten (`CreateNew`; duplicates get ` (2)`).
- File names from the disk are untrusted: invalid characters, reserved names (`CON`, `NUL`...), `..` and rooted paths are neutralized; very deep paths are flattened.
- Each file is re-checked after writing. Results are labelled **verified** (header matches), **recovered, type not verifiable**, or **recovered but possibly damaged**
  (header mismatch, or the scan said partly/fully overwritten). A failed or cancelled file is removed, never left half-written.
- Original created/modified/accessed times are restored; names and (optionally) folder structure are kept. Security descriptors (permissions) are not.
- Scanning needs administrator rights; the app asks for confirmation before scanning and before recovering, and offers "Restart as administrator".

## Your Settings now take effect
File categories (enabled / extensions) and the minimum file size filter the scan results. Scan history is recorded (Running -> Completed / Cancelled / Failed;
sessions left "Running" by a crash become "Interrupted"). Recovered files are listed on the new **Recovered Files** page (SQLite schema v3; Phase 1/2 databases upgrade in place).

## Prerequisites (Windows build machine)
Visual Studio 2022 with **.NET desktop development** and **Desktop development with C++**; .NET Framework 4.8 Developer Pack; CMake 3.15+; .NET SDK.
`powershell -File scripts\check-env.ps1` lists anything missing. To *run* on a target PC: Windows 7 SP1 or later and .NET Framework 4.8.

## Build and test commands
```powershell
scripts\build-native.ps1        # engine x64 + x86 (+ native tests), stages native-bin\x64 and \x86
scripts\build-all.ps1           # native + dotnet build + managed tests
scripts\Test-NtfsVhd.ps1        # ELEVATED: end-to-end test on a disposable NTFS virtual disk (creates, deletes, scans, compares SHA-256)
scripts\Test-Compatibility.ps1  # environment report for each Windows version
src\RecoverX.Desktop\bin\Release\net48\RecoverX.Desktop.exe
```
Without Windows: `sh scripts/verify-linux.sh` (native + sanitizer + ground-truth checks) and `sh scripts/verify-managed-mono.sh` (managed tests under Mono).
Regenerating the NTFS fixture needs Linux + root + ntfs-3g: `sudo python3 tests/fixtures/make_ntfs_fixture.py OUTDIR`.

## Known limitations (be aware before relying on results)
- **Quick Scan only finds files whose MFT record still exists.** Deleted files whose record was reused or wiped need Deep Scan (next phase).
- Not supported yet: **compressed and encrypted (EFS) files**; files whose attributes live in other MFT records (`$ATTRIBUTE_LIST`: very fragmented or heavily hard-linked files); alternate data streams (only the main stream is recovered).
- NTFS does not store a deletion time; the dates shown are the file's own timestamps.
- A drive that is **BitLocker-encrypted**, or whose boot sector is damaged, reports "cannot be read as NTFS".
- On SSDs with TRIM, "looks intact" can still be wrong if the drive erased the blocks after the scan; recovery is never guaranteed.
- The cluster bitmap says a cluster is *in use*; it cannot prove that an unused cluster still holds the original data, which is why the header check and the post-recovery check exist, and why unverifiable files are labelled as such.
- At most 250,000 deleted files are listed per scan (a notice is shown) and the list displays the first 20,000; search and filters come in a later phase.
- Performance on very large or slow drives has not been measured. Scanning reads the whole MFT and validates each candidate with a small read, which can take a while on an HDD.
- Only drive-letter volumes are supported (no mounted folders or images).

## Next: Phase 4 - Deep Scan (signature carving), previews, search and filters
