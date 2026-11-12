# Native engine C ABI (version 1)

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
| `rx_cancel_*` | cooperative cancellation tokens |
| `rx_ntfs_scan_create` | open a drive read-only, validate NTFS boot sector, `$MFT` (mirror fallback) and `$Bitmap` |
| `rx_ntfs_scan_run` | blocking Quick Scan; cancellable; progress readable from another thread via `rx_ntfs_scan_progress` |
| `rx_ntfs_scan_result_count / get_results` | deleted files: path, size, timestamps, condition, header check, clusters in use, flags (paged) |
| `rx_ntfs_read_file` | read a result's data (resident, non-resident, sparse, fragmented); re-reads and re-validates the MFT record each time (fails with NOT_FOUND if it was reused) |
| `rx_check_header` | pure file-type header check shared by the scan and the post-recovery verification |
| `rx_last_native_error` | thread-local Win32 error of the last failing call |

Status codes: `RX_OK 0, INVALID_ARG 1, BUFFER_TOO_SMALL 2, ACCESS_DENIED 3, NOT_FOUND 4, IO 5, UNSUPPORTED 6, CANCELLED 7, NOT_NTFS 8, CORRUPT 9, INTERNAL 99`.

## Hardware detection details
`CreateFile("\\.\E:", access 0)` then `IOCTL_STORAGE_QUERY_PROPERTY` with `StorageDeviceProperty` (bus type, removable flag, vendor/product),
`StorageDeviceSeekPenaltyProperty` (no seek penalty = SSD/flash) and `StorageDeviceTrimProperty`. Each query succeeds or fails independently and is flagged
in `RxVolumeInfo.flags`, so the UI only shows what was really reported. Serial numbers are deliberately not read.

## Build
CMake 3.15+; `src/RecoverX.Native/CMakeLists.txt` defines `_WIN32_WINNT=0x0601` (Windows 7), links the CRT statically, builds `rx_selftest.exe` for diagnostics.
Linux builds use `platform_stub.cpp` (disk-image files act as volumes) so the portable logic is unit-testable; Windows builds use `platform_win.cpp`.

## Phase 3: NTFS notes
- Parsing: boot sector (`parse_boot_sector`), update-sequence fixups (`apply_fixups`), attributes `$STANDARD_INFORMATION`, `$FILE_NAME` (Win32 name preferred over DOS/POSIX), `$DATA` (unnamed stream), `$ATTRIBUTE_LIST` (detected, not followed), data runs (`decode_runs`, rejects zero-length, out-of-volume and over-long run lists).
- Conditions (`RX_COND_*`): VERIFIED (clusters free + header matches, or resident + header matches), LIKELY (clusters free, type not verifiable), PARTIAL (some clusters reused), LOST (all reused, or header mismatch), UNSUPPORTED, EMPTY, UNKNOWN (bitmap unreadable).
- `RxFileEntry` is 1120 bytes on Windows (`path[520]`); sizes/offsets are asserted at compile time (`tests/NativeTests/abi_layout_check.cpp`) and in the C# tests.
- Tests: `tests/NativeTests/ntfs_tests.cpp` (pure + real image + cancellation + fuzz). Fixture generation: `tests/fixtures/make_ntfs_fixture.py`.
