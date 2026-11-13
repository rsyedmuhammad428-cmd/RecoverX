// Non-Windows host (Linux dev/CI). Hardware enumeration is unsupported here, but "volumes" can be disk-image
// files so the portable read/alignment logic can be unit tested without Windows. The stub deliberately rejects
// unaligned reads, like a raw Windows volume, so the aligning wrapper in api.cpp is really exercised.
#ifndef _WIN32
#include "internal.h"
#include <cerrno>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

namespace rx {

struct PlatformVolume { int fd; uint64_t size; };

static std::string narrow(const std::wstring& w) {
    std::string s;
    for (wchar_t c : w) s.push_back(c < 128 ? (char)c : '?');
    return s;
}

RxStatus platform_enumerate(std::vector<RxVolumeInfo>&) { return RX_ERR_UNSUPPORTED; }
RxStatus platform_query(const std::wstring&, RxVolumeInfo&) { return RX_ERR_UNSUPPORTED; }

RxStatus platform_open(const std::wstring& root, PlatformVolume** vol) {
    int fd = ::open(narrow(root).c_str(), O_RDONLY);
    if (fd < 0) { set_last_error((uint32_t)errno); return errno == ENOENT ? RX_ERR_NOT_FOUND : (errno == EACCES ? RX_ERR_ACCESS_DENIED : RX_ERR_IO); }
    struct stat st;
    if (fstat(fd, &st) != 0) { ::close(fd); return RX_ERR_IO; }
    *vol = new PlatformVolume{ fd, (uint64_t)st.st_size };
    return RX_OK;
}

void platform_close(PlatformVolume* v) { if (v) { ::close(v->fd); delete v; } }
uint32_t platform_sector_size(PlatformVolume*) { return 512; }
uint64_t platform_size(PlatformVolume* v) { return v->size; }

RxStatus platform_read_aligned(PlatformVolume* v, uint64_t offset, void* buf, uint32_t length, uint32_t* got) {
    *got = 0;
    bool tail = (offset + length == v->size);
    if (offset % 512 != 0 || (length % 512 != 0 && !tail)) return RX_ERR_IO;   // emulate raw-volume alignment rules
    ssize_t n = ::pread(v->fd, buf, length, (off_t)offset);
    if (n < 0) return RX_ERR_IO;
    *got = (uint32_t)n;
    return RX_OK;
}

uint64_t platform_local_to_utc(uint64_t ft) { return ft; }   // non-Windows test hosts: treat FAT times as UTC

bool platform_dest_info(const std::wstring&, std::wstring&, bool&) { return false; }

} // namespace rx
#endif
