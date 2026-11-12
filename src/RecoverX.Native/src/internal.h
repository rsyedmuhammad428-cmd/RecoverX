#ifndef RX_INTERNAL_H
#define RX_INTERNAL_H
#include "rx_engine.h"
#include <string>
#include <vector>

namespace rx {

void set_last_error(uint32_t code);
uint32_t get_last_error();

// ---- platform layer (platform_win.cpp on Windows, platform_stub.cpp elsewhere) ----
struct PlatformVolume;   // opaque
RxStatus platform_enumerate(std::vector<RxVolumeInfo>& out);
RxStatus platform_query(const std::wstring& root, RxVolumeInfo& out);
RxStatus platform_open(const std::wstring& root, PlatformVolume** vol);
void     platform_close(PlatformVolume* vol);
uint32_t platform_sector_size(PlatformVolume* vol);
uint64_t platform_size(PlatformVolume* vol);
// Reads exactly `length` bytes at a sector-aligned offset; `length` is a multiple of the sector size.
RxStatus platform_read_aligned(PlatformVolume* vol, uint64_t offset, void* buf, uint32_t length, uint32_t* got);
bool     platform_dest_info(const std::wstring& path, std::wstring& volume_id, bool& exists_as_dir);

// ---- portable logic ----
RxStatus identify_filesystem(const uint8_t* s, uint32_t len, uint32_t& fs);
int32_t  destination_verdict_by_letters(const std::wstring& source_root, const std::wstring& dest_dir);
bool     parse_drive_letter(const std::wstring& root, wchar_t& letter);   // accepts "E:" or "E:\" only

// Test helper (not part of the C ABI): number of data runs of result `index`, or -1.
int ntfs_debug_run_count(RxScanHandle scan, uint32_t index);

} // namespace rx
#endif
