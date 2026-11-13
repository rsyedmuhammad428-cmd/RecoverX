// Pure FAT12/16/32 and exFAT on-disk format helpers: no I/O. All input is treated as hostile.
#ifndef RX_FAT_FORMAT_H
#define RX_FAT_FORMAT_H
#include "rx_engine.h"
#include <string>

namespace rx { namespace fat {

enum Kind { KIND_NONE = 0, KIND_FAT12, KIND_FAT16, KIND_FAT32, KIND_EXFAT };

struct Volume {
    Kind kind = KIND_NONE;
    uint32_t bytes_per_sector = 0;
    uint32_t cluster_size = 0;
    uint64_t fat_offset = 0;          // byte offset of the first FAT
    uint64_t fat_bytes = 0;           // size of one FAT
    uint64_t heap_offset = 0;         // byte offset of cluster 2
    uint32_t cluster_count = 0;       // valid clusters are 2 .. cluster_count + 1
    uint32_t root_cluster = 0;        // FAT32 / exFAT
    uint64_t root_offset = 0;         // FAT12/16 fixed root directory
    uint32_t root_bytes = 0;
    uint64_t volume_bytes = 0;

    bool valid_cluster(uint64_t c) const { return c >= 2 && c < (uint64_t)cluster_count + 2; }
    uint64_t cluster_offset(uint64_t c) const { return heap_offset + (c - 2) * (uint64_t)cluster_size; }
    // End-of-chain / bad marker for this FAT type (anything >= is not a next-cluster number).
    uint32_t eoc_min() const {
        switch (kind) { case KIND_FAT12: return 0xFF7; case KIND_FAT16: return 0xFFF7; case KIND_FAT32: return 0x0FFFFFF7; default: return 0xFFFFFFF7u; }
    }
};

// RX_OK, RX_ERR_NOT_FAT (not a FAT/exFAT boot sector) or RX_ERR_CORRUPT (FAT/exFAT but inconsistent).
RxStatus parse_boot(const uint8_t* sector, uint32_t len, uint64_t device_bytes, Volume& v);

// Long-file-name checksum of an 11-byte short name.
uint8_t lfn_checksum(const uint8_t* sfn11);
// exFAT entry-set checksum over `entries` 32-byte entries, computed as if every entry were in use (InUse bit set).
uint16_t exfat_set_checksum(const uint8_t* set, uint32_t entries);
// DOS date/time (+ 10 ms units) -> FILETIME of that wall-clock time, 0 if invalid.
uint64_t dos_to_filetime(uint16_t date, uint16_t time, uint8_t tens_of_ms);
// exFAT 32-bit timestamp + 10 ms + UTC offset byte -> UTC FILETIME when the offset is valid, otherwise wall-clock (local_out=true).
uint64_t exfat_to_filetime(uint32_t ts, uint8_t tens_of_ms, uint8_t utc_offset, bool& local_out);
// 8.3 name -> display name. `deleted`: the first byte was overwritten by the deletion marker (shown as '_', guessed=true).
std::wstring short_name(const uint8_t* entry32, bool deleted, bool& guessed);

}} // namespace
#endif
