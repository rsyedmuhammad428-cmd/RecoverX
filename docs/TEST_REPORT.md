# RecoverX 1.0 - test report (Phase 6: quality and security)

Machine: Windows 10 22H2 x64, not elevated. Toolchain: GCC 16.2 (msvcrt) x64 + x86, .NET SDK 8.0.428 targeting .NET Framework 4.8.
All suites run for both architectures. "Image" = a disk image file standing in for a volume (test builds only; the shipped DLL accepts
drive letters only, which a managed test verifies).

## Results
| Area (prompt checklist) | How it was tested | Result |
|---|---|---|
| Deleted JPG / PNG / PDF / DOCX / MP4 recovery | NTFS, FAT32 and exFAT images made by real drivers (ntfs-3g, Linux vfat/exfat); SHA-256 recorded before deletion | Byte-identical |
| Recycle Bin / permanent deletion | Real Windows SSD (user's own scan, read-only): Recycle Bin entries ($R/$I) found; data erased by TRIM reported as such | Correct, incl. "erased" preview |
| USB / memory card file systems | FAT32 and exFAT images; Deep Scan with FAT/exFAT free-space maps | 34 of 34 deleted files found by name each; 32 / 33 byte-exact (the rest correctly Partial/Poor) |
| Quick Scan / Deep Scan | ntfs_tests, fat_tests, carve_tests | 1,408 + 1,044 + 5,495 checks pass |
| Cancelled scan | Cancel before/while running and while paused, all scan types | Returns promptly, results so far kept |
| Interrupted recovery | Managed tests: cancellation mid-file removes the partial file; crashed scans marked "Interrupted" at next start | Pass |
| Recovery to a different drive / invalid destination | Managed tests with real file I/O | Same-drive and missing destinations refused; never overwrites |
| Inaccessible drive | Real drives without elevation (AccessDenied), missing letters (NotFound), non-drive paths (InvalidArgument) through the real DLL | Clear status, no crash |
| SSD limitations | Real TRIM SSD scan by the user; preview distinguishes "erased (all zeros)" | Reported honestly |
| Overwritten files | NTFS and FAT fixtures with reused clusters; Deep Scan files planted across allocated clusters | Rated Poor/Partial, never Excellent (except when checksums prove the data) |
| Fragmented files | NTFS (followed exactly), FAT32 (Partial), exFAT (FAT chain followed: exact), Deep Scan (Partial) | As expected |
| Corrupted media | Mutation fuzzing: NTFS 400, FAT32 300, exFAT 300 corrupted volumes; 3,000 corrupted Deep Scan candidates; truncated images | No crash, hang or out-of-range result |
| Large storage devices | 16 GB sparse image, files at 5 GB, 9 GB and the last MB | All found intact at exact 64-bit offsets, x64 and x86 |
| Performance | Same 16 GB image, whole-volume Deep Scan | 779 MB/s (x64), 725 MB/s (x86) - faster than an HDD can deliver; on NTFS/FAT/exFAT only free space is read |
| Memory | Peak working set during the 16 GB scan | 20.5 MB; filtering 250,000 results in the UI layer < 3 s |
| Engine never writes | SHA-256 of every image before/after every scan and read | Unchanged |

## Security review
| Item | Status |
|---|---|
| Source drive written | Impossible through the engine: no write API, volumes opened `GENERIC_READ`; recovery writes only to the chosen folder |
| Arbitrary device paths | Engine accepts drive letters only (verified through the real DLL: `\\.\PhysicalDrive0` and file paths refused) |
| Untrusted file names | Sanitized (reserved names, `..`, rooted paths, invalid characters); path traversal tests |
| Untrusted file content | All parsers bound-check every length and offset (fuzzed); PDF inflation capped at 32 MB; image preview refuses > 100 megapixels (decompression bombs) |
| DLL loading | Engine loaded by full path from the app's `x64\`/`x86\` folder; default install in Program Files (not writable by normal users) |
| Elevation | App manifest `asInvoker`; administrator rights requested only on user action (UAC) |
| Network / telemetry | None. Logs and database stay in `%LOCALAPPDATA%\RecoverX`; no file contents are stored |

## Not tested (needs action outside this environment)
- Elevated scans of real drives, and `scripts\Test-NtfsVhd.ps1` on a Windows-formatted virtual disk (Quick + Deep Scan, byte comparison).
- Listing real restore points (WMI, needs elevation). The comparison logic is tested with ordinary folders standing in for a snapshot.
- The installer: Inno Setup could not be installed on the build machine. The staged Release build was verified to start (engine 1.0.0, icon).
- Windows 7 SP1 and Windows 11, display scaling, USB/SD hardware.
