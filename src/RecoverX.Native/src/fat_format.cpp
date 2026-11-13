// FAT12/16/32 and exFAT format helpers (see fat_format.h). Follows the Microsoft FAT specification (fatgen103) and the exFAT
// specification: FAT type is decided by cluster count, never by the label in the boot sector.
#include "fat_format.h"
#include <cstring>

namespace rx { namespace fat {

namespace {
uint16_t r16(const uint8_t* p) { return (uint16_t)(p[0] | (p[1] << 8)); }
uint32_t r32(const uint8_t* p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }
uint64_t r64(const uint8_t* p) { return (uint64_t)r32(p) | ((uint64_t)r32(p + 4) << 32); }
bool pow2(uint32_t v) { return v && !(v & (v - 1)); }

int64_t days_from_civil(int64_t y, unsigned m, unsigned d) {
    y -= m <= 2;
    const int64_t era = (y >= 0 ? y : y - 399) / 400;
    const unsigned yoe = (unsigned)(y - era * 400);
    const unsigned doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
    const unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + (int64_t)doe - 719468;
}

uint64_t civil_to_filetime(int y, int mo, int d, int h, int mi, int s, int ms10) {
    if (y < 1980 || y > 2107 || mo < 1 || mo > 12 || d < 1 || d > 31 || h > 23 || mi > 59 || s > 59) return 0;
    int64_t secs = days_from_civil(y, (unsigned)mo, (unsigned)d) * 86400 + h * 3600 + mi * 60 + s;
    return ((uint64_t)(secs + 11644473600LL)) * 10000000ull + (uint64_t)ms10 * 100000ull;
}
} // namespace

RxStatus parse_boot(const uint8_t* s, uint32_t len, uint64_t device_bytes, Volume& v) {
    v = Volume();
    if (!s || len < 512) return RX_ERR_INVALID_ARG;
    if (s[510] != 0x55 || s[511] != 0xAA) return RX_ERR_NOT_FAT;

    if (std::memcmp(s + 3, "EXFAT   ", 8) == 0) {
        uint32_t bps_shift = s[0x6C], spc_shift = s[0x6D];
        if (bps_shift < 9 || bps_shift > 12 || spc_shift > 25 - bps_shift || s[0x6E] < 1 || s[0x6E] > 2) return RX_ERR_CORRUPT;
        v.kind = KIND_EXFAT;
        v.bytes_per_sector = 1u << bps_shift;
        v.cluster_size = v.bytes_per_sector << spc_shift;
        uint64_t vol_len = r64(s + 0x48);
        uint32_t fat_off = r32(s + 0x50), fat_len = r32(s + 0x54), heap = r32(s + 0x58), count = r32(s + 0x5C), root = r32(s + 0x60);
        if (vol_len == 0 || fat_off < 24 || fat_len == 0 || heap < fat_off + fat_len || count == 0 || count > 0xFFFFFFF5u) return RX_ERR_CORRUPT;
        v.fat_offset = (uint64_t)fat_off * v.bytes_per_sector;
        v.fat_bytes = (uint64_t)fat_len * v.bytes_per_sector;
        v.heap_offset = (uint64_t)heap * v.bytes_per_sector;
        v.cluster_count = count;
        v.root_cluster = root;
        v.volume_bytes = vol_len * v.bytes_per_sector;
        if (device_bytes && v.volume_bytes > device_bytes) v.volume_bytes = device_bytes;
        if ((uint64_t)(count + 2) * 4 > v.fat_bytes || !v.valid_cluster(root)) return RX_ERR_CORRUPT;
        if (v.heap_offset + (uint64_t)count * v.cluster_size > v.volume_bytes + v.cluster_size) {
            // A volume shorter than its header claims (truncated image): keep only the clusters that exist.
            if (v.heap_offset >= v.volume_bytes) return RX_ERR_CORRUPT;
            v.cluster_count = (uint32_t)((v.volume_bytes - v.heap_offset) / v.cluster_size);
            if (!v.valid_cluster(root)) return RX_ERR_CORRUPT;
        }
        return RX_OK;
    }

    // FAT12/16/32 BIOS parameter block. A jump instruction marks a boot sector (NTFS/exFAT are excluded above or by the OEM name).
    if (!(s[0] == 0xEB || s[0] == 0xE9) || std::memcmp(s + 3, "NTFS    ", 8) == 0) return RX_ERR_NOT_FAT;
    uint32_t bps = r16(s + 11), spc = s[13], rsv = r16(s + 14), nfats = s[16], root_entries = r16(s + 17);
    uint32_t tot16 = r16(s + 19), fat16 = r16(s + 22), tot32 = r32(s + 32), fat32 = r32(s + 36);
    if (!(bps == 512 || bps == 1024 || bps == 2048 || bps == 4096) || !pow2(spc) || spc > 128 || rsv == 0 || nfats < 1 || nfats > 2)
        return RX_ERR_NOT_FAT;
    uint64_t total = tot16 ? tot16 : tot32;
    uint64_t fatsz = fat16 ? fat16 : fat32;
    if (total == 0 || fatsz == 0) return RX_ERR_CORRUPT;
    uint64_t root_sectors = ((uint64_t)root_entries * 32 + bps - 1) / bps;
    uint64_t data_start = rsv + nfats * fatsz + root_sectors;
    if (data_start >= total) return RX_ERR_CORRUPT;
    uint64_t clusters = (total - data_start) / spc;
    v.bytes_per_sector = bps;
    v.cluster_size = bps * spc;
    if (v.cluster_size > 64u * 1024u) return RX_ERR_CORRUPT;
    v.kind = clusters < 4085 ? KIND_FAT12 : clusters < 65525 ? KIND_FAT16 : KIND_FAT32;
    if (v.kind == KIND_FAT32 && (fat16 != 0 || root_entries != 0)) return RX_ERR_CORRUPT;
    if (v.kind != KIND_FAT32 && root_entries == 0) return RX_ERR_CORRUPT;
    if (clusters > 0x0FFFFFF5) return RX_ERR_CORRUPT;
    v.cluster_count = (uint32_t)clusters;
    v.fat_offset = (uint64_t)rsv * bps;
    v.fat_bytes = fatsz * bps;
    uint64_t entry_bits = v.kind == KIND_FAT12 ? 12 : v.kind == KIND_FAT16 ? 16 : 32;
    if (((uint64_t)(clusters + 2) * entry_bits + 7) / 8 > v.fat_bytes) return RX_ERR_CORRUPT;
    v.heap_offset = data_start * bps;
    v.volume_bytes = total * bps;
    if (device_bytes && v.volume_bytes > device_bytes) {
        v.volume_bytes = device_bytes;
        if (v.heap_offset >= device_bytes) return RX_ERR_CORRUPT;
        uint64_t fit = (device_bytes - v.heap_offset) / v.cluster_size;
        if (fit < v.cluster_count) v.cluster_count = (uint32_t)fit;
    }
    if (v.kind == KIND_FAT32) {
        v.root_cluster = r32(s + 44);
        if (!v.valid_cluster(v.root_cluster)) return RX_ERR_CORRUPT;
    } else {
        v.root_offset = (uint64_t)(rsv + nfats * fatsz) * bps;
        v.root_bytes = root_entries * 32;
    }
    return RX_OK;
}

uint8_t lfn_checksum(const uint8_t* n) {
    uint8_t sum = 0;
    for (int i = 0; i < 11; ++i) sum = (uint8_t)(((sum & 1) << 7) + (sum >> 1) + n[i]);
    return sum;
}

uint16_t exfat_set_checksum(const uint8_t* set, uint32_t entries) {
    uint16_t sum = 0;
    for (uint32_t i = 0; i < entries * 32; ++i) {
        if (i == 2 || i == 3) continue;
        uint8_t b = set[i];
        if (i % 32 == 0) b |= 0x80;                      // the checksum was computed while the entries were in use
        sum = (uint16_t)(((sum & 1) ? 0x8000 : 0) + (sum >> 1) + b);
    }
    return sum;
}

uint64_t dos_to_filetime(uint16_t date, uint16_t time, uint8_t tens_of_ms) {
    if (date == 0) return 0;
    int y = 1980 + (date >> 9), mo = (date >> 5) & 15, d = date & 31;
    int h = time >> 11, mi = (time >> 5) & 63, s = (time & 31) * 2;
    int extra_s = tens_of_ms / 100, ms10 = tens_of_ms % 100;
    if (tens_of_ms > 199) return 0;
    return civil_to_filetime(y, mo, d, h, mi, s + extra_s > 59 ? 59 : s + extra_s, ms10);
}

uint64_t exfat_to_filetime(uint32_t ts, uint8_t tens_of_ms, uint8_t utc_offset, bool& local_out) {
    uint64_t ft = dos_to_filetime((uint16_t)(ts >> 16), (uint16_t)(ts & 0xFFFF), tens_of_ms);
    local_out = true;
    if (!ft) return 0;
    if (utc_offset & 0x80) {
        int off = (int8_t)(uint8_t)(utc_offset << 1) >> 1;   // 7-bit signed count of 15-minute steps
        int64_t delta = (int64_t)off * 15 * 60 * 10000000LL;
        local_out = false;
        return (uint64_t)((int64_t)ft - delta);
    }
    return ft;
}

std::wstring short_name(const uint8_t* e, bool deleted, bool& guessed) {
    guessed = false;
    const bool lower_base = (e[12] & 0x08) != 0, lower_ext = (e[12] & 0x10) != 0;
    auto ch = [&](uint8_t c, bool lower) -> wchar_t {
        if (c < 0x20 || c >= 0x7F || c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') return L'_';
        if (lower && c >= 'A' && c <= 'Z') c = (uint8_t)(c - 'A' + 'a');
        return (wchar_t)c;
    };
    std::wstring base, ext;
    for (int i = 0; i < 8; ++i) {
        uint8_t c = e[i];
        if (i == 0) {
            if (deleted) { base.push_back(L'_'); guessed = true; continue; }
            if (c == 0x05) c = 0xE5;
        }
        base.push_back(ch(c, lower_base));
    }
    for (int i = 8; i < 11; ++i) ext.push_back(ch(e[i], lower_ext));
    while (!base.empty() && base.back() == L' ') base.pop_back();
    while (!ext.empty() && ext.back() == L' ') ext.pop_back();
    if (base.empty()) base = L"_";
    return ext.empty() ? base : base + L"." + ext;
}

}} // namespace
