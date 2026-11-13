#ifndef RX_INTERNAL_H
#define RX_INTERNAL_H
#include "rx_engine.h"
#include <atomic>
#include <string>
#include <vector>

// Cooperative cancellation + pause token shared by all long operations (Quick Scan, Deep Scan).
struct RxCancelToken {
    std::atomic<bool> flag;
    std::atomic<bool> paused;
    RxCancelToken() : flag(false), paused(false) {}
};

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

// Returns true if cancellation was requested. While the token is paused this blocks (sleeping in short steps) until it is
// resumed or cancelled; `is_paused` (optional) is set while waiting so progress can report "paused".
bool poll_cancel(RxCancelHandle token, std::atomic<uint32_t>* flags = nullptr, uint32_t paused_flag = 0);

// FAT stores wall-clock local time; converts such a FILETIME to UTC (Windows: the system's local-time rules; elsewhere unchanged).
uint64_t platform_local_to_utc(uint64_t local_filetime);

// ---- Allocation map: which clusters a file system marks as in use. Deep Scan reads only free clusters. ----
// Clusters [first, first + count) are stored back to back starting at byte offset `heap` on the volume.
class AllocMap {
public:
    virtual ~AllocMap() {}
    uint64_t heap = 0, first = 0, count = 0;
    uint32_t cluster_size = 0;
    // Allocated clusters in [c, c+n) (absolute cluster numbers, clipped to the map), or -1 if unknown.
    virtual int64_t used(uint64_t c, uint64_t n) = 0;
    // First run [start, end) at or after cluster `from` whose state is `used`; false if none.
    virtual bool find_run(uint64_t from, bool used, uint64_t& start, uint64_t& end) = 0;
};
// NTFS first, then FAT12/16/32 / exFAT. nullptr when the volume has no readable allocation map.
AllocMap* open_alloc_map(const std::wstring& root);
AllocMap* open_fat_alloc_map(const std::wstring& root);     // fat_scan.cpp

// ---- NTFS allocation map, used by Deep Scan to read only free space (implemented in ntfs_scan.cpp) ----
bool    ntfs_alloc_geometry(RxScanHandle s, uint32_t& cluster_size, uint64_t& total_clusters);
// Number of allocated clusters in [lcn, lcn+len), or -1 if the bitmap is unavailable.
int64_t ntfs_used_clusters(RxScanHandle s, uint64_t lcn, uint64_t len);
// Finds the first run of clusters >= from whose allocation state is `used`; returns false if none / bitmap unavailable.
bool    ntfs_find_run(RxScanHandle s, uint64_t from, bool used, uint64_t& start, uint64_t& end);

// Test helper (not part of the C ABI): number of data runs of result `index`, or -1.
int ntfs_debug_run_count(RxScanHandle scan, uint32_t index);

} // namespace rx
#endif
