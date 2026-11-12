// File-content header checks: gives the Quick Scan real evidence that data still looks like the file type its name claims.
#include "ntfs_format.h"
#include <cstring>
#include <initializer_list>

namespace rx { namespace ntfs {

static bool starts(const uint8_t* d, uint32_t len, uint32_t off, const char* s, uint32_t n) {
    return (uint64_t)off + n <= len && std::memcmp(d + off, s, n) == 0;
}

static bool is_text_ext(const std::string& e) {
    static const char* t[] = {"txt","csv","log","ini","xml","html","htm","json","md","cfg","bat","cmd","css","js","sql","yaml","yml"};
    for (const char* x : t) if (e == x) return true;
    return false;
}

static bool looks_like_text(const uint8_t* d, uint32_t len) {
    if (len >= 2 && ((d[0] == 0xFF && d[1] == 0xFE) || (d[0] == 0xFE && d[1] == 0xFF))) return true;   // UTF-16 BOM
    uint32_t bad = 0;
    for (uint32_t i = 0; i < len; ++i) {
        uint8_t c = d[i];
        if (c == 0) return false;
        if (c < 32 && c != 9 && c != 10 && c != 13 && c != 12 && c != 27) ++bad;
    }
    return bad * 20 <= len;   // at most 5% control characters
}

uint32_t check_header(const std::wstring& name, const uint8_t* d, uint32_t len) {
    size_t dot = name.rfind(L'.');
    if (dot == std::wstring::npos || dot + 1 >= name.size() || name.size() - dot > 12) return RX_HDR_NO_SIGNATURE;
    std::string ext;
    for (size_t i = dot + 1; i < name.size(); ++i) {
        wchar_t c = name[i];
        if (c > 127) return RX_HDR_NO_SIGNATURE;
        if (c >= L'A' && c <= L'Z') c = (wchar_t)(c - L'A' + L'a');
        ext.push_back((char)c);
    }
    if (len == 0) return RX_HDR_NOT_CHECKED;

    auto is = [&](std::initializer_list<const char*> l) { for (const char* x : l) if (ext == x) return true; return false; };

    if (is({"jpg","jpeg","jfif"}))  return (len >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"png"}))                return (len >= 8 && std::memcmp(d, "\x89PNG\r\n\x1A\n", 8) == 0) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"gif"}))                return starts(d, len, 0, "GIF8", 4) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"bmp"}))                return starts(d, len, 0, "BM", 2) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"tif","tiff"}))         return (starts(d, len, 0, "II*\0", 4) || starts(d, len, 0, "MM\0*", 4)) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"webp"}))               return (starts(d, len, 0, "RIFF", 4) && starts(d, len, 8, "WEBP", 4)) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"pdf"}))                return starts(d, len, 0, "%PDF-", 5) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"zip","docx","xlsx","pptx","odt","ods","odp","jar","apk"}))
                                    return (starts(d, len, 0, "PK\x03\x04", 4) || starts(d, len, 0, "PK\x05\x06", 4)) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"rar"}))                return starts(d, len, 0, "Rar!\x1A\x07", 6) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"7z"}))                 return starts(d, len, 0, "7z\xBC\xAF\x27\x1C", 6) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"gz"}))                 return (len >= 2 && d[0] == 0x1F && d[1] == 0x8B) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"tar"}))                return starts(d, len, 257, "ustar", 5) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"mp3"}))                return (starts(d, len, 0, "ID3", 3) || (len >= 2 && d[0] == 0xFF && (d[1] & 0xE0) == 0xE0)) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"wav"}))                return (starts(d, len, 0, "RIFF", 4) && starts(d, len, 8, "WAVE", 4)) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"avi"}))                return (starts(d, len, 0, "RIFF", 4) && starts(d, len, 8, "AVI ", 4)) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"flac"}))               return starts(d, len, 0, "fLaC", 4) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"ogg"}))                return starts(d, len, 0, "OggS", 4) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"mp4","m4v","m4a","mov","3gp"}))
                                    return (starts(d, len, 4, "ftyp", 4) || starts(d, len, 4, "moov", 4) || starts(d, len, 4, "wide", 4) || starts(d, len, 4, "mdat", 4) || starts(d, len, 4, "free", 4)) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"mkv","webm"}))         return (len >= 4 && d[0] == 0x1A && d[1] == 0x45 && d[2] == 0xDF && d[3] == 0xA3) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"wmv","wma","asf"}))    return (len >= 4 && d[0] == 0x30 && d[1] == 0x26 && d[2] == 0xB2 && d[3] == 0x75) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"flv"}))                return starts(d, len, 0, "FLV\x01", 4) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"doc","xls","ppt"}))    return (len >= 8 && std::memcmp(d, "\xD0\xCF\x11\xE0\xA1\xB1\x1A\xE1", 8) == 0) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"rtf"}))                return starts(d, len, 0, "{\\rtf", 5) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is({"exe","dll"}))          return starts(d, len, 0, "MZ", 2) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    if (is_text_ext(ext))           return looks_like_text(d, len) ? RX_HDR_MATCH : RX_HDR_MISMATCH;
    return RX_HDR_NO_SIGNATURE;
}

}} // namespace
