# Native engine C ABI (version 3)

Header: `src/RecoverX.Native/include/rx_engine.h`. Output: `recoverx_engine.dll` (x64 and x86), cdecl, UTF-16 strings, static CRT.
C# binding: `src/RecoverX.Core/Native/NativeMethods.cs` (struct sizes verified at load; a mismatch disables the engine instead of risking memory corruption).

| Function | Purpose |
|---|---|
| `rx_get_abi_version`, `rx_get_version_string`, `rx_sizeof_volume_info`, `rx_sizeof_probe_result` | version and layout handshake |
| `rx_enumerate_volumes`, `rx_query_volume` | hardware details (bus, SSD/HDD, removable, TRIM, vendor, product) for fixed/removable lettered volumes; no admin needed |
| `rx_volume_open / get_geometry / read / close` | read-only volume access (admin needed); any offset/length, sector alignment handled internally, reads clamped at end of volume |
| `rx_probe_volume` | open + read sector 0 + identify file system + close |
| `rx_identify_filesystem` | pure boot-sector classifier: NTFS, exFAT, FAT32, FAT16, FAT12 |
| `rx_validate_destination` | refuses a recovery destination on the volume being scanned |
| `rx_cancel_*` | cooperative cancellation tokens; `rx_cancel_set_paused` / `rx_cancel_is_paused` (ABI 2) pause any running scan at its next check point |
| `rx_ntfs_scan_create` | open a drive read-only, validate NTFS boot sector, `$MFT` (mirror fallback) and `$Bitmap` |
| `rx_ntfs_scan_run` | blocking Quick Scan; cancellable; progress readable from another thread via `rx_ntfs_scan_progress` |
| `rx_ntfs_scan_result_count / get_results` | deleted files: path, size, timestamps, condition, header check, clusters in use, flags (paged) |
| `rx_ntfs_read_file` | read a result's data (resident, non-resident, sparse, fragmented); re-reads and re-validates the MFT record each time (fails with NOT_FOUND if it was reused) |
| `rx_check_header` | pure file-type header check shared by the scan and the post-recovery verification |
| `rx_carve_create / run / progress / result_count / get_results / read_file / destroy` | Deep Scan (ABI 2): signature search, see below |
| `rx_fat_scan_create / fs_type / run / progress / result_count / get_results / read_file / destroy` | FAT12/16/32 and exFAT Quick Scan (ABI 3), same structures as the NTFS Quick Scan |
| `rx_last_native_error` | thread-local Win32 error of the last failing call |

Status codes: `RX_OK 0, INVALID_ARG 1, BUFFER_TOO_SMALL 2, ACCESS_DENIED 3, NOT_FOUND 4, IO 5, UNSUPPORTED 6, CANCELLED 7, NOT_NTFS 8, CORRUPT 9, NOT_FAT 10, INTERNAL 99`.

## Hardware detection details
`CreateFile("\\.\E:", access 0)` then `IOCTL_STORAGE_QUERY_PROPERTY` with `StorageDeviceProperty` (bus type, removable flag, vendor/product),
`StorageDeviceSeekPenaltyProperty` (no seek penalty = SSD/flash) and `StorageDeviceTrimProperty`. Each query succeeds or fails independently and is flagged
in `RxVolumeInfo.flags`, so the UI only shows what was really reported. Serial numbers are deliberately not read.

## Build
CMake 3.15+ (verified with 4.5.0 + GCC 16.2 msvcrt, x64 and x86; MSVC not yet run); `src/RecoverX.Native/CMakeLists.txt` defines `_WIN32_WINNT=0x0601` (Windows 7), links the CRT statically, builds `rx_selftest.exe` for diagnostics.
Linux builds use `platform_stub.cpp` (disk-image files act as volumes) so the portable logic is unit-testable; Windows builds use `platform_win.cpp`.

## Phase 3: NTFS notes
- Parsing: boot sector (`parse_boot_sector`), update-sequence fixups (`apply_fixups`), attributes `$STANDARD_INFORMATION`, `$FILE_NAME` (Win32 name preferred over DOS/POSIX), `$DATA` (unnamed stream), `$ATTRIBUTE_LIST` (detected, not followed), data runs (`decode_runs`, rejects zero-length, out-of-volume and over-long run lists).
- Conditions (`RX_COND_*`): VERIFIED (clusters free + header matches, or resident + header matches), LIKELY (clusters free, type not verifiable), PARTIAL (some clusters reused), LOST (all reused, or header mismatch), UNSUPPORTED, EMPTY, UNKNOWN (bitmap unreadable).
- `RxFileEntry` is 1120 bytes on Windows (`path[520]`); sizes/offsets are asserted at compile time (`tests/NativeTests/abi_layout_check.cpp`) and in the C# tests.
- Tests: `tests/NativeTests/ntfs_tests.cpp` (pure + real image + cancellation + fuzz). Fixture generation: `tests/fixtures/make_ntfs_fixture.py`.

## Phase 4: Deep Scan (signature carving)
Files: `src/carve_format.{h,cpp}` (pure parsers, no I/O) and `src/carve_scan.cpp` (session).

- **Search area**: on NTFS, `rx_ntfs_scan_create` is reused to read `$Bitmap`, and only free clusters are examined (`RX_CARVEF_ALL_SPACE`
  searches everything). Without a map (FAT32, exFAT, damaged NTFS) the whole volume is read and `RX_SCANP_BITMAP_UNAVAILABLE` is set.
- **Candidates**: `match()` looks at every 512-byte boundary (files start on cluster boundaries on all supported file systems).
  Unreadable regions are retried in 64 KB pieces, zero-filled and counted (`read_errors`).
- **Parsers** (each bounded by a per-type size limit, every length validated):

| Type | Extensions | End / validation |
|---|---|---|
| JPEG | jpg | marker segments (SOF/DQT required), entropy data scanned for EOI; any illegal marker = corrupt; a 512-byte zero run = gap (damaged); a 1 MB zero run = the data ended (only the prefix is kept); EXIF DateTimeOriginal |
| PNG | png | every chunk CRC-32 to IEND |
| GIF | gif | blocks and sub-blocks to the trailer |
| BMP | bmp | header consistency (dimensions x depth = size); never better than LIKELY |
| PDF | pdf | last `%%EOF` whose `startxref` points at `xref` or an xref-stream object; incremental updates followed; a new `%PDF-` on a sector boundary stops the search |
| ZIP | zip, docx, xlsx, pptx, odt, ods, odp, epub, jar, apk | local headers walked; EOCD (and ZIP64) must close a central directory whose every entry points at a matching local header; type from entry names / `mimetype` |
| ISO BMFF | mp4, mov, m4a, 3gp, heic, avif, cr3 | top-level box walk; moov+mvhd+mdat (or meta for still images); mvhd creation time |
| RIFF | wav, avi, webp | chunk walk must end exactly at the RIFF size |
| MP3 | mp3 | ID3v2 tag + MPEG frame chain (>= 8 frames); tail confirmed by ID3v1 or Xing/Info byte count |
| 7z | 7z | start-header CRC and next-header CRC |
| EBML | mkv, webm | DocType + Segment; top-level element walk (unknown-size clusters followed) |
| OLE2 | doc, xls, ppt, msg | FAT (via DIFAT) and directory chain; type from stream names |

- **Evidence** (`RX_EV_*`): END_FOUND, STRUCTURE_OK, CHECKSUM_OK, TAIL_CONFIRMED (a structure beyond the first 64 KB or at the end was
  validated), TRUNCATED, CORRUPT, IN_USE (NTFS clusters now allocated), ALLOC_UNKNOWN, TIME_LOCAL.
  Condition: VERIFIED = end + structure + tail; LIKELY = end found but content not fully checkable; PARTIAL = corrupt/truncated (size =
  the part that checked out) or overlapping allocated clusters without a checksum to prove the data; LOST = header only.
- End-of-file searches (PDF `%%EOF`, ZIP end record) give up after 1 MB of consecutive zero bytes: that is wiped or never-written
  space, not file content, and stopping there avoids reading up to the per-type size limit for every damaged candidate.
- After a VERIFIED or LIKELY file the search continues after its end (embedded thumbnails/streams are not reported separately).
- `RxCarveEntry` is 64 bytes, `RxCarveProgress` 40, `RxCarveOptions` 16 (asserted in `abi_layout_check.cpp` and at load in C#).
- Tests: `tests/NativeTests/carve_tests.cpp` (fixture ground truth with SHA-256, NTFS free-space mode, files planted across allocated
  clusters, pause/resume/cancel, type mask, result cap, wiped tails, 3,000-case fuzz). Fixture: `tests/fixtures/make_carve_fixture.py`.

## Phase 5: FAT12/16/32 and exFAT Quick Scan
Files: `src/fat_format.{h,cpp}` (pure: boot sectors, long-name and exFAT checksums, timestamps, 8.3 names) and `src/fat_scan.cpp` (session).

- **Volume**: FAT type decided by cluster count (FAT12 < 4085 <= FAT16 < 65525 <= FAT32), exFAT by its OEM name; every BPB field validated.
  The whole FAT is loaded (up to 64 M entries); the allocation map is the FAT itself (FAT12/16/32) or the exFAT allocation bitmap.
- **Walk**: breadth-first over every directory: live directories through their cluster chains, deleted FAT directories from their first
  cluster while the clusters are free and the first one still starts with `.`/`..`, exFAT directories by their stream extension
  (contiguous flag or chain). Loop and depth limits.
- **Names**: FAT long names are rebuilt from the deleted LFN entries and accepted only if their checksum matches the short entry; for a
  deleted entry the lost first byte is found by trying every value against the checksum, which also restores that letter. A short-only
  name keeps `_` and `RX_ENT_NAME_GUESSED`. exFAT entry sets are accepted only when their set checksum (recomputed with the in-use bits)
  matches, so partly overwritten sets are never reported with a wrong name.
- **Data**: FAT clears a deleted file's chain, so data is read contiguously from the first cluster. exFAT files with the contiguous flag
  are exact; otherwise a FAT chain left intact is followed when it has exactly the file's length, ends cleanly and none of it is
  allocated (`RX_ENT_CHAIN_FOLLOWED`). Bytes past the exFAT valid-data length read as zeros.
- **Condition**: as for NTFS: clusters now allocated (all -> LOST, some -> PARTIAL), else the file-type header check (VERIFIED / LIKELY /
  LOST). Times: FAT local times converted with the system time zone; exFAT UTC offsets honoured.
- **Deep Scan**: `open_alloc_map` gives the carver a common `AllocMap` (NTFS `$Bitmap`, FAT table or exFAT bitmap; clusters start at the
  data heap), so Deep Scan reads only free clusters on all four file systems.
- Tests: `tests/NativeTests/fat_tests.cpp` on `fat32_basic.img` and `exfat_basic.img`, created and deleted-from by the Linux kernel's
  vfat and exfat drivers in a container (`tests/fixtures/make_fat_fixtures.sh`, `fat_populate.py`): names (Unicode, long, 8.3-only),
  deleted folders, fragmented and overwritten files, SHA-256 of every intact file; Deep Scan with the maps; 600 corrupted volumes.
- Large-volume check: `perf_tests` (manual) carves a 16 GB sparse image with files beyond 4/8/16 GB.
