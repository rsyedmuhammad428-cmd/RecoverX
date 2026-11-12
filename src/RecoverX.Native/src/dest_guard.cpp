#include "internal.h"
#include <cwctype>

namespace rx {

bool parse_drive_letter(const std::wstring& root, wchar_t& letter) {
    if (root.size() == 2 && root[1] == L':') { /* "E:" */ }
    else if (root.size() == 3 && root[1] == L':' && (root[2] == L'\\' || root[2] == L'/')) { /* "E:\" */ }
    else return false;
    wchar_t c = root[0];
    if (!((c >= L'A' && c <= L'Z') || (c >= L'a' && c <= L'z'))) return false;
    letter = (wchar_t)std::towupper(c);
    return true;
}

static bool letter_of_path(const std::wstring& p, wchar_t& letter) {
    if (p.size() >= 2 && p[1] == L':') {
        wchar_t c = p[0];
        if ((c >= L'A' && c <= L'Z') || (c >= L'a' && c <= L'z')) { letter = (wchar_t)std::towupper(c); return true; }
    }
    return false;
}

static bool is_unc(const std::wstring& p) {
    return p.size() >= 2 && p[0] == L'\\' && p[1] == L'\\' && !(p.size() >= 3 && (p[2] == L'?' || p[2] == L'.'));
}

// Drive-letter based fallback. Used on its own when volume GUIDs are unavailable, and by tests.
int32_t destination_verdict_by_letters(const std::wstring& source_root, const std::wstring& dest_dir) {
    wchar_t s, d;
    if (!parse_drive_letter(source_root, s)) return RX_DEST_UNVERIFIABLE;
    if (is_unc(dest_dir)) return RX_DEST_SAFE;                  // a network share cannot be the local source volume
    if (!letter_of_path(dest_dir, d)) return RX_DEST_UNVERIFIABLE;
    return s == d ? RX_DEST_SAME_VOLUME : RX_DEST_SAFE;
}

} // namespace rx
