#include "internal.h"
#include <atomic>
#include <cstring>
#include <new>

#define RX_VERSION_W L"0.2.0"

namespace {
thread_local uint32_t t_last_error = 0;
const uint32_t kMaxRead = 16u * 1024u * 1024u;
}

namespace rx {
void set_last_error(uint32_t c) { t_last_error = c; }
uint32_t get_last_error() { return t_last_error; }
}

struct RxVolume { rx::PlatformVolume* pv; uint32_t sector; uint64_t total; };
struct RxCancelToken { std::atomic<bool> flag; RxCancelToken() : flag(false) {} };

#define RX_GUARD_BEGIN try {
#define RX_GUARD_END } catch (const std::bad_alloc&) { return RX_ERR_INTERNAL; } catch (...) { return RX_ERR_INTERNAL; }

extern "C" {

RX_API uint32_t rx_get_abi_version(void) { return RX_ABI_VERSION; }
RX_API const wchar_t* rx_get_version_string(void) { return RX_VERSION_W; }
RX_API uint32_t rx_sizeof_volume_info(void) { return (uint32_t)sizeof(RxVolumeInfo); }
RX_API uint32_t rx_sizeof_probe_result(void) { return (uint32_t)sizeof(RxProbeResult); }
RX_API uint32_t rx_last_native_error(void) { return rx::get_last_error(); }

RX_API RxStatus rx_enumerate_volumes(RxVolumeInfo* out, uint32_t capacity, uint32_t* count) {
    RX_GUARD_BEGIN
    if (!count) return RX_ERR_INVALID_ARG;
    rx::set_last_error(0);
    std::vector<RxVolumeInfo> v;
    RxStatus st = rx::platform_enumerate(v);
    if (st != RX_OK) return st;
    *count = (uint32_t)v.size();
    if (!out || capacity < v.size()) return (out == nullptr && capacity == 0) ? RX_OK : RX_ERR_BUFFER_TOO_SMALL;
    for (size_t i = 0; i < v.size(); ++i) {
        if (out[i].struct_size != sizeof(RxVolumeInfo)) return RX_ERR_INVALID_ARG;
        out[i] = v[i];
    }
    return RX_OK;
    RX_GUARD_END
}

RX_API RxStatus rx_query_volume(const wchar_t* root, RxVolumeInfo* out) {
    RX_GUARD_BEGIN
    if (!root || !out || out->struct_size != sizeof(RxVolumeInfo)) return RX_ERR_INVALID_ARG;
    wchar_t letter;
    if (!rx::parse_drive_letter(root, letter)) return RX_ERR_INVALID_ARG;
    rx::set_last_error(0);
    return rx::platform_query(root, *out);
    RX_GUARD_END
}

RX_API RxStatus rx_volume_open(const wchar_t* root, RxVolumeHandle* handle) {
    RX_GUARD_BEGIN
    if (!root || !handle) return RX_ERR_INVALID_ARG;
    *handle = nullptr;
    rx::set_last_error(0);
    rx::PlatformVolume* pv = nullptr;
    RxStatus st = rx::platform_open(root, &pv);
    if (st != RX_OK) return st;
    RxVolume* v = new RxVolume();
    v->pv = pv;
    v->sector = rx::platform_sector_size(pv);
    v->total = rx::platform_size(pv);
    if (v->sector == 0) { rx::platform_close(pv); delete v; return RX_ERR_IO; }
    *handle = v;
    return RX_OK;
    RX_GUARD_END
}

RX_API RxStatus rx_volume_get_geometry(RxVolumeHandle h, RxVolumeGeometry* out) {
    RX_GUARD_BEGIN
    if (!h || !out || out->struct_size != sizeof(RxVolumeGeometry)) return RX_ERR_INVALID_ARG;
    out->bytes_per_sector = h->sector;
    out->total_bytes = h->total;
    return RX_OK;
    RX_GUARD_END
}

RX_API RxStatus rx_volume_read(RxVolumeHandle h, uint64_t offset, void* buffer, uint32_t length, uint32_t* bytes_read) {
    RX_GUARD_BEGIN
    if (!h || !buffer || !bytes_read || length > kMaxRead) return RX_ERR_INVALID_ARG;
    *bytes_read = 0;
    rx::set_last_error(0);
    if (length == 0 || offset >= h->total) return RX_OK;
    uint64_t avail = h->total - offset;
    uint32_t want = (uint32_t)(avail < length ? avail : length);

    const uint64_t ss = h->sector;
    uint64_t start = offset - (offset % ss);
    uint64_t end = offset + want;
    end = ((end + ss - 1) / ss) * ss;
    if (end > h->total) end = h->total;                         // totals are sector multiples for real volumes
    std::vector<uint8_t> tmp((size_t)(end - start));
    uint32_t got = 0;
    RxStatus st = rx::platform_read_aligned(h->pv, start, tmp.data(), (uint32_t)tmp.size(), &got);
    if (st != RX_OK) return st;
    uint64_t skip = offset - start;
    if (got <= skip) return RX_OK;
    uint32_t n = (uint32_t)(got - skip);
    if (n > want) n = want;
    std::memcpy(buffer, tmp.data() + skip, n);
    *bytes_read = n;
    return RX_OK;
    RX_GUARD_END
}

RX_API RxStatus rx_volume_close(RxVolumeHandle h) {
    RX_GUARD_BEGIN
    if (!h) return RX_ERR_INVALID_ARG;
    rx::platform_close(h->pv);
    delete h;
    return RX_OK;
    RX_GUARD_END
}

RX_API RxStatus rx_identify_filesystem(const uint8_t* sector, uint32_t length, uint32_t* fs_id) {
    RX_GUARD_BEGIN
    if (!fs_id) return RX_ERR_INVALID_ARG;
    return rx::identify_filesystem(sector, length, *fs_id);
    RX_GUARD_END
}

RX_API RxStatus rx_probe_volume(const wchar_t* root, RxProbeResult* out) {
    RX_GUARD_BEGIN
    if (!root || !out || out->struct_size != sizeof(RxProbeResult)) return RX_ERR_INVALID_ARG;
    out->fs_id = RX_FS_UNKNOWN; out->bytes_per_sector = 0; out->total_bytes = 0; out->bytes_read = 0; out->win32_error = 0;
    RxVolumeHandle h = nullptr;
    RxStatus st = rx_volume_open(root, &h);
    if (st != RX_OK) { out->win32_error = rx::get_last_error(); return st; }
    out->bytes_per_sector = h->sector;
    out->total_bytes = h->total;
    std::vector<uint8_t> sec(h->sector < 512 ? 512 : h->sector);
    uint32_t got = 0;
    st = rx_volume_read(h, 0, sec.data(), (uint32_t)sec.size(), &got);
    if (st == RX_OK) {
        out->bytes_read = got;
        uint32_t fs = 0;
        if (got >= 512 && rx::identify_filesystem(sec.data(), got, fs) == RX_OK) out->fs_id = fs;
    } else {
        out->win32_error = rx::get_last_error();
    }
    rx_volume_close(h);
    return st;
    RX_GUARD_END
}

RX_API RxStatus rx_validate_destination(const wchar_t* source_root, const wchar_t* dest_dir, int32_t* verdict) {
    RX_GUARD_BEGIN
    if (!source_root || !dest_dir || !verdict) return RX_ERR_INVALID_ARG;
    wchar_t letter;
    if (!rx::parse_drive_letter(source_root, letter)) return RX_ERR_INVALID_ARG;
    std::wstring src(source_root), dst(dest_dir);
    if (dst.empty()) return RX_ERR_INVALID_ARG;

    std::wstring dst_id, src_id;
    bool dst_dir = false, src_dir = false;
    bool have_dst = rx::platform_dest_info(dst, dst_id, dst_dir);
    if (have_dst && !dst_dir) { *verdict = RX_DEST_NOT_FOUND; return RX_OK; }
    std::wstring src_root_norm; src_root_norm += letter; src_root_norm += L":\\";
    bool have_src = rx::platform_dest_info(src_root_norm, src_id, src_dir);
    if (have_dst && have_src && !dst_id.empty() && !src_id.empty()) {
        *verdict = (dst_id == src_id) ? RX_DEST_SAME_VOLUME : RX_DEST_SAFE;
        return RX_OK;
    }
    *verdict = rx::destination_verdict_by_letters(src, dst);
    return RX_OK;
    RX_GUARD_END
}

RX_API RxCancelHandle rx_cancel_create(void) { try { return new RxCancelToken(); } catch (...) { return nullptr; } }
RX_API void rx_cancel_request(RxCancelHandle t) { if (t) t->flag.store(true); }
RX_API int32_t rx_cancel_is_requested(RxCancelHandle t) { return (t && t->flag.load()) ? 1 : 0; }
RX_API void rx_cancel_destroy(RxCancelHandle t) { delete t; }

} // extern "C"
