#include "internal.h"
#include <cstring>

namespace rx {

static bool has(const uint8_t* s, uint32_t len, uint32_t off, const char* txt) {
    size_t n = std::strlen(txt);
    return off + n <= len && std::memcmp(s + off, txt, n) == 0;
}

RxStatus identify_filesystem(const uint8_t* s, uint32_t len, uint32_t& fs) {
    fs = RX_FS_UNKNOWN;
    if (!s || len < 512) return RX_ERR_INVALID_ARG;
    if (s[510] != 0x55 || s[511] != 0xAA) return RX_OK;          // no boot signature: unknown

    if (has(s, len, 3, "NTFS    ")) { fs = RX_FS_NTFS; return RX_OK; }

    if (has(s, len, 3, "EXFAT   ")) {
        // exFAT requires the MustBeZero region (bytes 11..63) to be zero.
        for (uint32_t i = 11; i < 64; ++i) if (s[i] != 0) return RX_OK;
        fs = RX_FS_EXFAT;
        return RX_OK;
    }
    if (has(s, len, 82, "FAT32   ")) { fs = RX_FS_FAT32; return RX_OK; }
    if (has(s, len, 54, "FAT16   ")) { fs = RX_FS_FAT16; return RX_OK; }
    if (has(s, len, 54, "FAT12   ")) { fs = RX_FS_FAT12; return RX_OK; }
    return RX_OK;
}

} // namespace rx
