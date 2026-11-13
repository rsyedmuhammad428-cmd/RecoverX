// Windows implementation. Targets Windows 7 SP1 and later: only APIs available on Windows 7 are used, and the
// storage property structures are declared locally so the build does not depend on a particular SDK version.
#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include "internal.h"
#include <cwctype>

namespace rx {

namespace {

const DWORD kIoctlStorageQueryProperty = 0x002D1400;   // CTL_CODE(IOCTL_STORAGE_BASE, 0x0500, METHOD_BUFFERED, FILE_ANY_ACCESS)
const DWORD kIoctlDiskGetDriveGeometry = 0x00070000;
const DWORD kIoctlDiskGetLengthInfo    = 0x0007405C;

const DWORD kPropDevice = 0;       // StorageDeviceProperty
const DWORD kPropSeekPenalty = 7;  // StorageDeviceSeekPenaltyProperty (Windows 7+)
const DWORD kPropTrim = 8;         // StorageDeviceTrimProperty        (Windows 7+)

struct RxQuery { DWORD PropertyId; DWORD QueryType; BYTE Extra[1]; };

struct RxDeviceDescriptor {
    DWORD Version; DWORD Size;
    BYTE DeviceType; BYTE DeviceTypeModifier; BYTE RemovableMedia; BYTE CommandQueueing;
    DWORD VendorIdOffset; DWORD ProductIdOffset; DWORD ProductRevisionOffset; DWORD SerialNumberOffset;
    DWORD BusType; DWORD RawPropertiesLength; BYTE RawDeviceProperties[1];
};
struct RxSeekPenalty { DWORD Version; DWORD Size; BYTE IncursSeekPenalty; };
struct RxTrim { DWORD Version; DWORD Size; BYTE TrimEnabled; };
struct RxLengthInfo { LARGE_INTEGER Length; };
struct RxDiskGeometry { LARGE_INTEGER Cylinders; DWORD MediaType; DWORD TracksPerCylinder; DWORD SectorsPerTrack; DWORD BytesPerSector; };

bool query_property(HANDLE h, DWORD prop, void* out, DWORD cap, DWORD& returned) {
    RxQuery q; q.PropertyId = prop; q.QueryType = 0; q.Extra[0] = 0;   // PropertyStandardQuery
    return DeviceIoControl(h, kIoctlStorageQueryProperty, &q, sizeof(q), out, cap, &returned, NULL) != 0;
}

void copy_ascii_trimmed(const BYTE* base, DWORD total, DWORD offset, wchar_t* dst, size_t cap) {
    dst[0] = 0;
    if (offset == 0 || offset >= total) return;
    size_t n = 0;
    while (offset + n < total && base[offset + n] != 0 && n < cap - 1) {
        BYTE c = base[offset + n];
        dst[n] = (c >= 32 && c < 127) ? (wchar_t)c : L'?';
        ++n;
    }
    while (n > 0 && dst[n - 1] == L' ') --n;
    dst[n] = 0;
    size_t lead = 0;
    while (dst[lead] == L' ') ++lead;
    if (lead) { for (size_t i = 0; i <= n - lead; ++i) dst[i] = dst[i + lead]; }
}

void init_info(RxVolumeInfo& v, wchar_t letter) {
    ZeroMemory(&v, sizeof(v));
    v.struct_size = sizeof(RxVolumeInfo);
    v.abi_version = RX_ABI_VERSION;
    v.removable_media = -1;
    v.trim_enabled = -1;
    v.root[0] = letter; v.root[1] = L':'; v.root[2] = L'\\'; v.root[3] = 0;
}

void fill_hardware(HANDLE h, RxVolumeInfo& v) {
    BYTE buf[1024];
    DWORD ret = 0;
    ZeroMemory(buf, sizeof(buf));
    if (query_property(h, kPropDevice, buf, sizeof(buf), ret) && ret >= sizeof(RxDeviceDescriptor) - 1) {
        const RxDeviceDescriptor* d = reinterpret_cast<const RxDeviceDescriptor*>(buf);
        v.bus_type = d->BusType;
        v.removable_media = d->RemovableMedia ? 1 : 0;
        DWORD total = ret < sizeof(buf) ? ret : (DWORD)sizeof(buf);
        copy_ascii_trimmed(buf, total, d->VendorIdOffset, v.vendor, 40);
        copy_ascii_trimmed(buf, total, d->ProductIdOffset, v.product, 48);
        v.flags |= RX_VOLF_DEVICE_QUERIED;
    } else {
        v.win32_error = GetLastError();
    }
    ZeroMemory(buf, sizeof(buf));
    if (query_property(h, kPropSeekPenalty, buf, sizeof(buf), ret) && ret >= sizeof(RxSeekPenalty)) {
        const RxSeekPenalty* s = reinterpret_cast<const RxSeekPenalty*>(buf);
        v.media_type = s->IncursSeekPenalty ? RX_MEDIA_HDD : RX_MEDIA_SSD;
        v.flags |= RX_VOLF_SEEK_QUERIED;
    }
    ZeroMemory(buf, sizeof(buf));
    if (query_property(h, kPropTrim, buf, sizeof(buf), ret) && ret >= sizeof(RxTrim)) {
        const RxTrim* t = reinterpret_cast<const RxTrim*>(buf);
        v.trim_enabled = t->TrimEnabled ? 1 : 0;
        v.flags |= RX_VOLF_TRIM_QUERIED;
    }
}

std::wstring device_path(wchar_t letter) {
    std::wstring p = L"\\\\.\\";
    p += letter; p += L':';
    return p;
}

RxStatus map_error(DWORD e) {
    switch (e) {
    case ERROR_ACCESS_DENIED: return RX_ERR_ACCESS_DENIED;
    case ERROR_FILE_NOT_FOUND: case ERROR_PATH_NOT_FOUND: case ERROR_INVALID_DRIVE: return RX_ERR_NOT_FOUND;
    default: return RX_ERR_IO;
    }
}

} // namespace

struct PlatformVolume { HANDLE h; uint32_t sector; uint64_t size; };

RxStatus platform_query(const std::wstring& root, RxVolumeInfo& out) {
    wchar_t letter;
    if (!parse_drive_letter(root, letter)) return RX_ERR_INVALID_ARG;
    init_info(out, letter);
    // Access 0 = query-only handle: no read access to the data and no administrator rights needed.
    HANDLE h = CreateFileW(device_path(letter).c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) {
        out.flags |= RX_VOLF_OPEN_FAILED;
        out.win32_error = GetLastError();
        return RX_OK;
    }
    fill_hardware(h, out);
    CloseHandle(h);
    return RX_OK;
}

RxStatus platform_enumerate(std::vector<RxVolumeInfo>& out) {
    UINT oldMode = SetErrorMode(SEM_FAILCRITICALERRORS);   // no "insert a disk" dialogs for empty card readers
    DWORD mask = GetLogicalDrives();
    for (int i = 0; i < 26; ++i) {
        if (!(mask & (1u << i))) continue;
        wchar_t root[4] = { (wchar_t)(L'A' + i), L':', L'\\', 0 };
        UINT type = GetDriveTypeW(root);
        if (type != DRIVE_FIXED && type != DRIVE_REMOVABLE) continue;
        RxVolumeInfo info;
        if (platform_query(root, info) == RX_OK) out.push_back(info);
    }
    SetErrorMode(oldMode);
    return RX_OK;
}

#ifdef RX_ALLOW_IMAGE_FILES
// TEST BUILDS ONLY (native_tests / ntfs_tests / carve_tests): a disk-image file stands in for a volume so the engine can be
// verified against real file-system images on Windows without administrator rights. The shipped DLL is built without
// this define and accepts drive letters only.
static RxStatus open_image(const std::wstring& path, PlatformVolume** vol);
#endif

RxStatus platform_open(const std::wstring& root, PlatformVolume** vol) {
    wchar_t letter;
#ifdef RX_ALLOW_IMAGE_FILES
    if (!parse_drive_letter(root, letter)) return open_image(root, vol);
#else
    if (!parse_drive_letter(root, letter)) return RX_ERR_INVALID_ARG;
#endif
    // Read-only: GENERIC_READ only, never GENERIC_WRITE. FILE_SHARE_WRITE is required because the volume is mounted.
    HANDLE h = CreateFileW(device_path(letter).c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) { DWORD e = GetLastError(); set_last_error(e); return map_error(e); }

    DWORD ret = 0;
    RxLengthInfo len; ZeroMemory(&len, sizeof(len));
    if (!DeviceIoControl(h, kIoctlDiskGetLengthInfo, NULL, 0, &len, sizeof(len), &ret, NULL)) {
        DWORD e = GetLastError(); set_last_error(e); CloseHandle(h); return RX_ERR_IO;
    }
    RxDiskGeometry geo; ZeroMemory(&geo, sizeof(geo));
    uint32_t sector = 512;
    if (DeviceIoControl(h, kIoctlDiskGetDriveGeometry, NULL, 0, &geo, sizeof(geo), &ret, NULL) && geo.BytesPerSector >= 512)
        sector = geo.BytesPerSector;

    PlatformVolume* v = new PlatformVolume();
    v->h = h; v->sector = sector; v->size = (uint64_t)len.Length.QuadPart;
    *vol = v;
    return RX_OK;
}

#ifdef RX_ALLOW_IMAGE_FILES
static RxStatus open_image(const std::wstring& path, PlatformVolume** vol) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) { DWORD e = GetLastError(); set_last_error(e); return map_error(e); }
    LARGE_INTEGER size;
    if (!GetFileSizeEx(h, &size)) { DWORD e = GetLastError(); set_last_error(e); CloseHandle(h); return RX_ERR_IO; }
    PlatformVolume* v = new PlatformVolume();
    v->h = h; v->sector = 512; v->size = (uint64_t)size.QuadPart;
    *vol = v;
    return RX_OK;
}
#endif

void platform_close(PlatformVolume* v) { if (v) { if (v->h != INVALID_HANDLE_VALUE) CloseHandle(v->h); delete v; } }
uint32_t platform_sector_size(PlatformVolume* v) { return v->sector; }
uint64_t platform_size(PlatformVolume* v) { return v->size; }

RxStatus platform_read_aligned(PlatformVolume* v, uint64_t offset, void* buf, uint32_t length, uint32_t* got) {
    *got = 0;
    LARGE_INTEGER pos; pos.QuadPart = (LONGLONG)offset;
    if (!SetFilePointerEx(v->h, pos, NULL, FILE_BEGIN)) { DWORD e = GetLastError(); set_last_error(e); return map_error(e); }
    uint8_t* p = static_cast<uint8_t*>(buf);
    uint32_t total = 0;
    while (total < length) {
        DWORD n = 0;
        if (!ReadFile(v->h, p + total, length - total, &n, NULL)) { DWORD e = GetLastError(); set_last_error(e); return map_error(e); }
        if (n == 0) break;
        total += n;
    }
    *got = total;
    return RX_OK;
}

uint64_t platform_local_to_utc(uint64_t ft) {
    if (ft == 0) return 0;
    FILETIME local, utc;
    local.dwLowDateTime = (DWORD)ft; local.dwHighDateTime = (DWORD)(ft >> 32);
    if (!LocalFileTimeToFileTime(&local, &utc)) return ft;
    return ((uint64_t)utc.dwHighDateTime << 32) | utc.dwLowDateTime;
}

bool platform_dest_info(const std::wstring& path, std::wstring& volume_id, bool& exists_as_dir) {
    volume_id.clear();
    DWORD attr = GetFileAttributesW(path.c_str());
    exists_as_dir = (attr != INVALID_FILE_ATTRIBUTES) && (attr & FILE_ATTRIBUTE_DIRECTORY);
    wchar_t root[MAX_PATH + 1]; wchar_t guid[64];
    if (GetVolumePathNameW(path.c_str(), root, MAX_PATH) && GetVolumeNameForVolumeMountPointW(root, guid, 64))
        volume_id = guid;
    return true;
}

} // namespace rx
#endif // _WIN32
