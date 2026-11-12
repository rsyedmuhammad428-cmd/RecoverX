// rx_selftest: console diagnostic used by scripts/Test-Compatibility.ps1 and by hand on each Windows version.
// Read-only. Prints engine info, hardware details of every volume, and (when elevated) a boot-sector probe.
#include "rx_engine.h"
#include "sha256.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

static const char* bus_name(uint32_t b) {
    switch (b) {
    case RX_BUS_USB: return "USB"; case RX_BUS_SATA: return "SATA"; case RX_BUS_ATA: return "ATA"; case RX_BUS_NVME: return "NVMe";
    case RX_BUS_SD: return "SD"; case RX_BUS_MMC: return "MMC"; case RX_BUS_SAS: return "SAS"; case RX_BUS_SCSI: return "SCSI";
    case RX_BUS_RAID: return "RAID"; case RX_BUS_VIRTUAL: return "Virtual"; case RX_BUS_FILE_BACKED_VIRTUAL: return "FileBackedVirtual";
    case RX_BUS_SPACES: return "StorageSpaces"; default: return "Unknown";
    }
}
static const char* fs_name(uint32_t f) {
    switch (f) { case RX_FS_NTFS: return "NTFS"; case RX_FS_FAT32: return "FAT32"; case RX_FS_EXFAT: return "exFAT";
                 case RX_FS_FAT16: return "FAT16"; case RX_FS_FAT12: return "FAT12"; default: return "unknown"; }
}

// rx_selftest --scan E [--hash]   (E = drive letter; a file path also works on non-Windows builds) : NTFS Quick Scan of a drive, one tab-separated ENTRY line per deleted file.
// Used by scripts/Test-NtfsVhd.ps1 to compare the engine against files whose content was known before deletion.
// UTF-8 output independent of the console code page / C locale, so scripts can parse names exactly.
static void print_utf8(const wchar_t* w) {
    for (size_t i = 0; w[i]; ++i) {
        uint32_t cp = (uint32_t)w[i];
        if (sizeof(wchar_t) == 2 && cp >= 0xD800 && cp < 0xDC00 && w[i + 1] >= 0xDC00 && w[i + 1] < 0xE000) { cp = 0x10000 + ((cp - 0xD800) << 10) + ((uint32_t)w[i + 1] - 0xDC00); ++i; }
        if (cp < 0x80) std::putchar((int)cp);
        else if (cp < 0x800) { std::putchar(0xC0 | (cp >> 6)); std::putchar(0x80 | (cp & 0x3F)); }
        else if (cp < 0x10000) { std::putchar(0xE0 | (cp >> 12)); std::putchar(0x80 | ((cp >> 6) & 0x3F)); std::putchar(0x80 | (cp & 0x3F)); }
        else { std::putchar(0xF0 | (cp >> 18)); std::putchar(0x80 | ((cp >> 12) & 0x3F)); std::putchar(0x80 | ((cp >> 6) & 0x3F)); std::putchar(0x80 | (cp & 0x3F)); }
    }
}

static const char* cond_name(uint32_t c) {
    switch (c) { case RX_COND_VERIFIED: return "VERIFIED"; case RX_COND_LIKELY: return "LIKELY"; case RX_COND_PARTIAL: return "PARTIAL";
                 case RX_COND_LOST: return "LOST"; case RX_COND_UNSUPPORTED: return "UNSUPPORTED"; case RX_COND_EMPTY: return "EMPTY"; default: return "UNKNOWN"; }
}

static int scan_mode(const char* target, bool hash) {
    std::wstring root;
    if (std::strlen(target) == 1) { root += (wchar_t)target[0]; root += L":\\"; }     // drive letter
    else for (const char* c = target; *c; ++c) root += (wchar_t)(unsigned char)*c;   // disk-image path (non-Windows builds only)
    RxScanOptions o; std::memset(&o, 0, sizeof o); o.struct_size = sizeof o; o.flags = RX_SCANF_VALIDATE_CONTENT;
    RxScanHandle h = nullptr;
    RxStatus st = rx_ntfs_scan_create(root.c_str(), &o, &h);
    std::printf("scan_create status=%d\n", st);
    if (st != RX_OK) return st == RX_ERR_ACCESS_DENIED ? 4 : 3;
    st = rx_ntfs_scan_run(h, nullptr);
    RxScanProgress p; p.struct_size = sizeof p; rx_ntfs_scan_progress(h, &p);
    std::printf("scan_run status=%d records=%u bad_records=%u flags=0x%x\n", st, p.records_total, p.bad_records, p.flags);
    uint32_t n = rx_ntfs_scan_result_count(h);
    std::vector<RxFileEntry> page(256);
    uint32_t counts[8] = {0};
    for (uint32_t start = 0; start < n; start += 256) {
        for (auto& e : page) e.struct_size = sizeof(RxFileEntry);
        uint32_t got = 0;
        if (rx_ntfs_scan_get_results(h, start, page.data(), 256, &got) != RX_OK) break;
        for (uint32_t i = 0; i < got; ++i) {
            const RxFileEntry& e = page[i];
            if (e.condition < 8) ++counts[e.condition];
            std::string sha = "-";
            if (hash && e.size > 0 && e.condition != RX_COND_UNSUPPORTED) {
                Sha256 s; std::vector<uint8_t> buf(1 << 20); uint64_t off = 0;
                for (;;) { uint32_t g = 0; if (rx_ntfs_read_file(h, start + i, off, buf.data(), (uint32_t)buf.size(), &g) != RX_OK || g == 0) break; s.update(buf.data(), g); off += g; }
                sha = (off == e.size) ? s.hex() : "SHORT";
            }
            std::printf("ENTRY\t"); print_utf8(e.path); std::printf("\t%llu\t%s\t%s\n", (unsigned long long)e.size, cond_name(e.condition), sha.c_str());
        }
    }
    std::printf("summary deleted=%u verified=%u likely=%u partial=%u lost=%u unsupported=%u empty=%u unknown=%u\n", n,
                counts[RX_COND_VERIFIED], counts[RX_COND_LIKELY], counts[RX_COND_PARTIAL], counts[RX_COND_LOST], counts[RX_COND_UNSUPPORTED], counts[RX_COND_EMPTY], counts[RX_COND_UNKNOWN]);
    rx_ntfs_scan_destroy(h);
    return st == RX_OK ? 0 : 1;
}

int main(int argc, char** argv) {
    if (argc >= 3 && std::strcmp(argv[1], "--scan") == 0) {
        bool hash = argc >= 4 && std::strcmp(argv[3], "--hash") == 0;
        return scan_mode(argv[2], hash);
    }
    std::printf("engine_abi=%u engine_version=%ls\n", rx_get_abi_version(), rx_get_version_string());
    std::printf("sizeof(RxVolumeInfo)=%u sizeof(RxProbeResult)=%u\n", rx_sizeof_volume_info(), rx_sizeof_probe_result());

    std::vector<RxVolumeInfo> v(26);
    for (auto& x : v) x.struct_size = sizeof(RxVolumeInfo);
    uint32_t n = 0;
    RxStatus st = rx_enumerate_volumes(v.data(), (uint32_t)v.size(), &n);
    std::printf("enumerate status=%d volumes=%u\n", st, n);
    if (st != RX_OK) return 2;

    int failures = 0;
    for (uint32_t i = 0; i < n; ++i) {
        const RxVolumeInfo& x = v[i];
        std::printf("%ls bus=%s media=%s removable=%d trim=%d flags=0x%x err=%u vendor='%ls' product='%ls'\n",
                    x.root, bus_name(x.bus_type),
                    (x.flags & RX_VOLF_SEEK_QUERIED) ? (x.media_type == RX_MEDIA_SSD ? "SSD/flash" : "HDD") : "unknown",
                    x.removable_media, x.trim_enabled, x.flags, x.win32_error, x.vendor, x.product);
        RxProbeResult p = {}; p.struct_size = sizeof(p);
        RxStatus ps = rx_probe_volume(x.root, &p);
        if (ps == RX_OK)
            std::printf("  probe: fs=%s sector=%u total=%llu bytes_read=%u\n", fs_name(p.fs_id), p.bytes_per_sector,
                        (unsigned long long)p.total_bytes, p.bytes_read);
        else if (ps == RX_ERR_ACCESS_DENIED)
            std::printf("  probe: access denied (run as administrator to test raw read access)\n");
        else { std::printf("  probe: status=%d win32=%u\n", ps, p.win32_error); ++failures; }
    }
    std::printf("selftest_result=%s\n", failures == 0 ? "OK" : "PROBE_FAILURES");
    return failures == 0 ? 0 : 1;
}
