// Deep Scan format parsers. See carve_format.h. Each parser records only evidence it actually checked:
//   END_FOUND       the format's own end marker or length was located
//   STRUCTURE_OK    every structure walked from header to end was consistent
//   CHECKSUM_OK     format checksums matched (PNG chunk CRCs, 7z header CRCs)
//   TAIL_CONFIRMED  a structure near the END of the file was validated, so the data is not just a good header
// and the condition follows from that evidence (see finish()). Files are assumed contiguous; a fragmented file breaks
// its structure checks and is reported as PARTIAL/LOST, never as VERIFIED.
#include "carve_format.h"
#include <algorithm>
#include <cstring>

namespace rx { namespace carve {

namespace {

const uint64_t KB = 1024, MB = 1024 * KB, GB = 1024 * MB;
const uint64_t kTailZone = 64 * KB;   // a structure validated beyond this offset counts as "tail confirmed"

uint16_t be16(const uint8_t* p) { return (uint16_t)((p[0] << 8) | p[1]); }
uint32_t be32(const uint8_t* p) { return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | p[3]; }
uint64_t be64(const uint8_t* p) { return ((uint64_t)be32(p) << 32) | be32(p + 4); }
uint16_t le16(const uint8_t* p) { return (uint16_t)(p[0] | (p[1] << 8)); }
uint32_t le32(const uint8_t* p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }
uint64_t le64(const uint8_t* p) { return (uint64_t)le32(p) | ((uint64_t)le32(p + 4) << 32); }

// Windowed access to a Reader. Pointers returned by at()/span() stay valid until the next call. The window starts small
// (most candidates are small or rejected quickly) and doubles on each reload up to its maximum.
class Buf {
public:
    explicit Buf(Reader& r, uint32_t window = 1u << 20) : r_(r), win_(window), cur_(std::min<uint32_t>(64u * 1024u, window)) {}
    // Pointer to `n` bytes at `off`, or nullptr if they cannot all be read.
    const uint8_t* at(uint64_t off, uint32_t n) {
        if (n == 0 || n > win_) return nullptr;
        if (off >= ws_ && off + n <= ws_ + wl_) return data_.data() + (off - ws_);
        load(off, n);
        return wl_ >= n ? data_.data() : nullptr;
    }
    // Pointer to the bytes readable at `off` (at least `min`, up to the window); `got` receives the count (0 = none).
    const uint8_t* span(uint64_t off, uint32_t& got, uint32_t min = 1) {
        if (!(off >= ws_ && off + min <= ws_ + wl_)) load(off, min);
        got = (off >= ws_ && off < ws_ + wl_) ? (uint32_t)(ws_ + wl_ - off) : 0;
        return got ? data_.data() + (off - ws_) : nullptr;
    }
    int byte(uint64_t off) { const uint8_t* p = at(off, 1); return p ? *p : -1; }
private:
    void load(uint64_t off, uint32_t n) {
        uint32_t size = std::max<uint32_t>(cur_, n);
        if (data_.size() < size) data_.resize(size);
        ws_ = off; wl_ = r_.read(off, data_.data(), size);
        if (cur_ < win_) cur_ = std::min<uint32_t>(win_, cur_ * 2);
    }
    Reader& r_;
    uint32_t win_, cur_;
    std::vector<uint8_t> data_;
    uint64_t ws_ = 0;
    uint32_t wl_ = 0;
};

// A run of zero bytes this long does not occur inside the files we carve; it means the search has left the file (wiped or
// never-written space). Used to stop end-of-file searches early instead of reading hundreds of megabytes of empty disk.
const uint64_t kZeroAbort = 1024 * 1024;

// Searches for `needle` in [from, to); returns the offset or ~0. With zero_abort, also gives up (~0) after that many
// consecutive zero bytes. A window is at most 1 MB, so a match and a full abort-length run never share one window.
uint64_t find(Buf& b, uint64_t from, uint64_t to, const char* needle, uint32_t nlen, uint64_t zero_abort = 0) {
    uint64_t pos = from, zrun = 0;
    while (pos + nlen <= to) {
        uint32_t got = 0;
        const uint8_t* p = b.span(pos, got, nlen);
        if (!p || got < nlen) return ~0ull;
        uint64_t lim = std::min<uint64_t>(got, to - pos);
        if (lim < nlen) return ~0ull;
        const uint8_t* end = p + lim - nlen + 1;
        for (const uint8_t* q = p; q < end;) {
            q = static_cast<const uint8_t*>(std::memchr(q, (unsigned char)needle[0], (size_t)(end - q)));
            if (!q) break;
            if (std::memcmp(q, needle, nlen) == 0) return pos + (uint64_t)(q - p);
            ++q;
        }
        if (zero_abort) {
            for (uint64_t i = 0; i < lim; ++i) {
                if (p[i]) zrun = 0;
                else if (++zrun >= zero_abort) return ~0ull;
            }
        }
        uint64_t adv = lim - nlen + 1;
        pos += adv;
    }
    return ~0ull;
}

bool printable4(const uint8_t* p) {
    for (int i = 0; i < 4; ++i) if (p[i] < 0x20 || p[i] > 0x7E) return false;
    return true;
}

// Turns gathered evidence into a condition. `meaningful` = enough valid structure that the data is worth reporting.
void finish(Result& r) {
    const uint32_t ev = r.evidence;
    if (ev & (RX_EV_CORRUPT | RX_EV_TRUNCATED)) r.condition = RX_COND_PARTIAL;
    else if ((ev & RX_EV_END_FOUND) && (ev & RX_EV_STRUCTURE_OK) && (ev & RX_EV_TAIL_CONFIRMED)) r.condition = RX_COND_VERIFIED;
    else if (ev & RX_EV_END_FOUND) r.condition = RX_COND_LIKELY;
    else r.condition = RX_COND_LOST;
}

// ---------------------------------------------------------------- time helpers
int64_t days_from_civil(int64_t y, unsigned m, unsigned d) {   // H. Hinnant's algorithm; days since 1970-01-01
    y -= m <= 2;
    const int64_t era = (y >= 0 ? y : y - 399) / 400;
    const unsigned yoe = (unsigned)(y - era * 400);
    const unsigned doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
    const unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + (int64_t)doe - 719468;
}

uint64_t unix_to_filetime(int64_t secs) {
    if (secs < 0) return 0;
    return ((uint64_t)secs + 11644473600ull) * 10000000ull;
}

const int64_t kMinPlausible = 631152000;    // 1990-01-01
const int64_t kMaxPlausible = 4102444800;   // 2100-01-01

// ---------------------------------------------------------------- JPEG
void exif_date(const uint8_t* d, uint32_t n, Result& r) {
    // d points at the TIFF header inside an APP1 "Exif\0\0" segment.
    if (n < 8) return;
    bool le;
    if (d[0] == 'I' && d[1] == 'I') le = true; else if (d[0] == 'M' && d[1] == 'M') le = false; else return;
    auto u16 = [&](uint32_t o) -> uint32_t { return le ? le16(d + o) : be16(d + o); };
    auto u32 = [&](uint32_t o) -> uint32_t { return le ? le32(d + o) : be32(d + o); };
    if (u16(2) != 42) return;
    uint64_t best = 0, fallback = 0;
    uint32_t ifd = u32(4), exif_ifd = 0;
    for (int pass = 0; pass < 2 && ifd; ++pass) {
        if ((uint64_t)ifd + 2 > n) return;
        uint32_t count = u16(ifd);
        if (count > 512 || (uint64_t)ifd + 2 + (uint64_t)count * 12 > n) return;
        for (uint32_t i = 0; i < count; ++i) {
            uint32_t e = ifd + 2 + i * 12;
            uint32_t tag = u16(e), type = u16(e + 2), cnt = u32(e + 4), val = u32(e + 8);
            if (pass == 0 && tag == 0x8769 && type == 4) exif_ifd = val;
            if ((tag == 0x0132 || tag == 0x9003) && type == 2 && cnt >= 19 && cnt <= 64 && (uint64_t)val + 19 <= n) {
                uint64_t t = exif_time_to_filetime(reinterpret_cast<const char*>(d + val), 19);
                if (t) { if (tag == 0x9003) best = t; else fallback = t; }
            }
        }
        ifd = (pass == 0) ? exif_ifd : 0;
    }
    uint64_t t = best ? best : fallback;
    if (t) { r.content_time = t; r.evidence |= RX_EV_TIME_LOCAL; }
}

bool is_sof(uint8_t m) { return m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC; }

bool parse_jpeg(Reader& rd, Result& r) {
    Buf b(rd);
    r.type = RX_FT_JPEG; r.ext = L"jpg";
    const uint64_t limit = std::min<uint64_t>(max_size(RX_FT_JPEG), rd.available());
    uint64_t pos = 2;
    bool sof = false, dqt = false, sos = false, eoi = false, zero_gap = false;
    uint64_t broke_at = 0;
    uint32_t zero_run = 0;

    for (;;) {
        if (pos + 4 > limit) { r.evidence |= RX_EV_TRUNCATED; broke_at = limit; break; }
        const uint8_t* p = b.at(pos, 4);
        if (!p) { r.evidence |= RX_EV_TRUNCATED; broke_at = pos; break; }
        if (p[0] != 0xFF) { r.evidence |= RX_EV_CORRUPT; broke_at = pos; break; }
        uint8_t m = p[1];
        if (m == 0xFF) { ++pos; continue; }                                   // fill byte
        if (m == 0xD9) { pos += 2; eoi = true; break; }
        if (m == 0xD8 || m == 0x00) { r.evidence |= RX_EV_CORRUPT; broke_at = pos; break; }
        if ((m >= 0xD0 && m <= 0xD7) || m == 0x01) { pos += 2; continue; }
        uint32_t len = be16(p + 2);
        if (len < 2) { r.evidence |= RX_EV_CORRUPT; broke_at = pos; break; }
        if (pos + 2 + len > limit) { r.evidence |= RX_EV_TRUNCATED; broke_at = limit; break; }
        if (is_sof(m)) {
            const uint8_t* s = b.at(pos + 4, 6);
            if (!s || len < 8 || be16(s + 1) == 0 || be16(s + 3) == 0 || s[5] == 0 || s[5] > 4) { r.evidence |= RX_EV_CORRUPT; broke_at = pos; break; }
            sof = true;
        } else if (m == 0xDB) dqt = true;
        else if (m == 0xE1 && len >= 16 && !r.content_time) {
            uint32_t n = std::min<uint32_t>(len - 2, 65533);
            const uint8_t* s = b.at(pos + 4, n);
            if (s && std::memcmp(s, "Exif\0\0", 6) == 0) exif_date(s + 6, n - 6, r);
        }
        pos += 2 + len;
        if (m != 0xDA) continue;

        // ---- entropy-coded data: only stuffed 0xFF00, restart markers, or a following segment/EOI are legal ----
        sos = true;
        bool to_marker = false;
        while (!to_marker && !eoi) {
            if (pos >= limit) { r.evidence |= RX_EV_TRUNCATED; broke_at = limit; break; }
            uint32_t got = 0;
            const uint8_t* s = b.span(pos, got);
            if (!s) { r.evidence |= RX_EV_TRUNCATED; broke_at = pos; break; }
            uint32_t lim = (uint32_t)std::min<uint64_t>(got, limit - pos);
            const uint8_t* q = static_cast<const uint8_t*>(std::memchr(s, 0xFF, lim));
            uint32_t upto = q ? (uint32_t)(q - s) : lim;
            // A whole sector of zeros inside entropy data does not occur in a real image: likely a gap (fragmentation).
            // A very long run means the image data has ended (wiped / never-written space): keep only what came before it.
            uint64_t abort_at = 0;
            for (uint32_t i = 0; i < upto; ++i) {
                if (s[i]) zero_run = 0;
                else if (++zero_run >= 512) {
                    zero_gap = true;
                    if (zero_run >= kZeroAbort) { abort_at = pos + i + 1 - zero_run; break; }
                }
            }
            if (abort_at) { r.evidence |= RX_EV_CORRUPT; broke_at = abort_at; break; }
            if (!q) { pos += lim; continue; }
            uint64_t at = pos + upto;
            int nb = b.byte(at + 1);
            if (nb < 0) { r.evidence |= RX_EV_TRUNCATED; broke_at = at; break; }
            zero_run = 0;
            if (nb == 0x00 || (nb >= 0xD0 && nb <= 0xD7)) { pos = at + 2; continue; }
            if (nb == 0xFF) { pos = at + 1; continue; }
            if (nb == 0xD9) { pos = at + 2; eoi = true; break; }
            if (nb == 0xC4 || nb == 0xCC || nb == 0xDA || nb == 0xDB || nb == 0xDD || nb == 0xFE || (nb >= 0xE0 && nb <= 0xEF)) {
                pos = at; to_marker = true; break;                                 // progressive JPEG: next segment
            }
            r.evidence |= RX_EV_CORRUPT; broke_at = at; break;
        }
        if (eoi || (r.evidence & (RX_EV_CORRUPT | RX_EV_TRUNCATED))) break;
    }

    if (!sof) return false;                                                     // never saw a frame header: not a usable JPEG
    if (eoi && sos) {
        r.size = pos;
        r.evidence |= RX_EV_END_FOUND;
        if (dqt) r.evidence |= RX_EV_STRUCTURE_OK;
        // A sector of zeros inside the image data means part of the picture is missing (a gap from fragmentation or wiping):
        // keep the whole extent (the rest of the image follows) but report it as damaged, never as intact.
        if (zero_gap) r.evidence |= RX_EV_CORRUPT;
        else r.evidence |= RX_EV_TAIL_CONFIRMED;
    } else {
        if (eoi) { r.evidence |= RX_EV_CORRUPT; broke_at = pos; }               // EOI before any image data
        r.size = broke_at;
        if (r.size < 512) return false;
    }
    finish(r);
    return true;
}

// ---------------------------------------------------------------- PNG
bool parse_png(Reader& rd, Result& r) {
    Buf b(rd);
    r.type = RX_FT_PNG; r.ext = L"png";
    const uint64_t limit = std::min<uint64_t>(max_size(RX_FT_PNG), rd.available());
    uint64_t pos = 8;
    bool first = true, iend = false;
    for (;;) {
        if (pos + 12 > limit) { r.evidence |= RX_EV_TRUNCATED; break; }
        const uint8_t* p = b.at(pos, 8);
        if (!p) { r.evidence |= RX_EV_TRUNCATED; break; }
        uint32_t len = be32(p);
        bool letters = true;
        for (int i = 4; i < 8; ++i) { uint8_t c = p[i]; if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))) letters = false; }
        if (!letters || len > 0x7FFFFFFFu) { if (first) return false; r.evidence |= RX_EV_CORRUPT; break; }
        if (first && (std::memcmp(p + 4, "IHDR", 4) != 0 || len != 13)) return false;
        if (pos + 12 + len > limit) { r.evidence |= RX_EV_TRUNCATED; break; }
        bool is_iend = std::memcmp(p + 4, "IEND", 4) == 0;
        uint32_t crc = crc32(0, p + 4, 4);
        uint64_t off = pos + 8, left = len;
        bool short_read = false;
        while (left > 0) {
            uint32_t got = 0;
            const uint8_t* s = b.span(off, got);
            if (!s) { short_read = true; break; }
            uint32_t n = (uint32_t)std::min<uint64_t>(got, left);
            crc = crc32(crc, s, n);
            off += n; left -= n;
        }
        const uint8_t* c = short_read ? nullptr : b.at(pos + 8 + len, 4);
        if (!c) { r.evidence |= RX_EV_TRUNCATED; break; }
        if (be32(c) != crc) { if (first) return false; r.evidence |= RX_EV_CORRUPT; break; }
        pos += 12 + (uint64_t)len;
        first = false;
        if (is_iend) { iend = true; break; }
    }
    if (first) return false;
    r.size = pos;
    if (iend) r.evidence |= RX_EV_END_FOUND | RX_EV_STRUCTURE_OK | RX_EV_CHECKSUM_OK | RX_EV_TAIL_CONFIRMED;
    finish(r);
    return true;
}

// ---------------------------------------------------------------- GIF
bool skip_sub_blocks(Buf& b, uint64_t& pos, uint64_t limit) {
    for (;;) {
        if (pos >= limit) return false;
        int n = b.byte(pos);
        if (n < 0) return false;
        pos += 1 + (uint64_t)n;
        if (n == 0) return true;
    }
}

bool parse_gif(Reader& rd, Result& r) {
    Buf b(rd);
    r.type = RX_FT_GIF; r.ext = L"gif";
    const uint64_t limit = std::min<uint64_t>(max_size(RX_FT_GIF), rd.available());
    const uint8_t* h = b.at(0, 13);
    if (!h || le16(h + 6) == 0 || le16(h + 8) == 0) return false;
    uint64_t pos = 13;
    if (h[10] & 0x80) pos += 3ull << ((h[10] & 7) + 1);
    uint32_t images = 0;
    bool trailer = false;
    for (;;) {
        int t = b.byte(pos);
        if (t < 0 || pos >= limit) { r.evidence |= RX_EV_TRUNCATED; break; }
        if (t == 0x3B) { ++pos; trailer = true; break; }
        if (t == 0x21) {
            pos += 2;
            if (!skip_sub_blocks(b, pos, limit)) { r.evidence |= RX_EV_TRUNCATED; break; }
        } else if (t == 0x2C) {
            const uint8_t* d = b.at(pos + 1, 9);
            if (!d || le16(d + 4) == 0 || le16(d + 6) == 0) { r.evidence |= RX_EV_CORRUPT; break; }
            pos += 10;
            if (d[8] & 0x80) pos += 3ull << ((d[8] & 7) + 1);
            int lzw = b.byte(pos);
            if (lzw < 2 || lzw > 12) { r.evidence |= RX_EV_CORRUPT; break; }
            ++pos;
            if (!skip_sub_blocks(b, pos, limit)) { r.evidence |= RX_EV_TRUNCATED; break; }
            ++images;
        } else { r.evidence |= RX_EV_CORRUPT; break; }
    }
    if (images == 0) return false;
    r.size = std::min<uint64_t>(pos, limit);
    if (trailer) r.evidence |= RX_EV_END_FOUND | RX_EV_STRUCTURE_OK | RX_EV_TAIL_CONFIRMED;
    finish(r);
    return true;
}

// ---------------------------------------------------------------- BMP (only a size field: never better than LIKELY)
bool parse_bmp(Reader& rd, Result& r) {
    Buf b(rd);
    r.type = RX_FT_BMP; r.ext = L"bmp";
    const uint8_t* h = b.at(0, 54);
    if (!h) return false;
    uint32_t size = le32(h + 2), off = le32(h + 10), dib = le32(h + 14);
    if (le32(h + 6) != 0) return false;
    if (dib != 12 && dib != 40 && dib != 52 && dib != 56 && dib != 108 && dib != 124) return false;
    if (size < 14 + dib || off < 14 + dib || off >= size || size > max_size(RX_FT_BMP)) return false;
    uint64_t w, hgt; uint32_t bpp, comp = 0;
    if (dib == 12) { w = le16(h + 18); hgt = le16(h + 20); if (le16(h + 22) != 1) return false; bpp = le16(h + 24); }
    else {
        int32_t sw = (int32_t)le32(h + 18), sh = (int32_t)le32(h + 22);
        if (sw <= 0 || sw > 100000 || sh == 0 || sh > 100000 || sh < -100000) return false;
        w = (uint64_t)sw; hgt = (uint64_t)(sh < 0 ? -(int64_t)sh : sh);
        if (le16(h + 26) != 1) return false;
        bpp = le16(h + 28); comp = le32(h + 30);
        if (comp > 6) return false;
    }
    if (bpp != 1 && bpp != 4 && bpp != 8 && bpp != 16 && bpp != 24 && bpp != 32) return false;
    if (w == 0 || hgt == 0) return false;
    if (comp == 0 || comp == 3) {
        uint64_t row = ((bpp * w + 31) / 32) * 4;
        uint64_t need = (uint64_t)off + row * hgt;
        if (need > size) return false;                                // header and size disagree: not a real bitmap
        if (size - need > 4096) return false;
    }
    r.size = size;
    if (size > rd.available()) { r.size = rd.available(); r.evidence |= RX_EV_TRUNCATED; }
    else r.evidence |= RX_EV_END_FOUND | RX_EV_STRUCTURE_OK;          // no tail evidence exists for BMP
    finish(r);
    return true;
}

// ---------------------------------------------------------------- PDF
// Value after "startxref" in the 1 KB before `eof`; ~0 if missing.
uint64_t startxref_before(Buf& b, uint64_t eof) {
    uint64_t from = eof > 1024 ? eof - 1024 : 0;
    uint64_t best = ~0ull, p = from;
    for (;;) {
        uint64_t q = find(b, p, eof, "startxref", 9);
        if (q == ~0ull) break;
        best = q; p = q + 9;
    }
    if (best == ~0ull) return ~0ull;
    uint64_t pos = best + 9, v = 0;
    int digits = 0;
    for (int i = 0; i < 32; ++i, ++pos) {
        int c = b.byte(pos);
        if (c < 0) return ~0ull;
        if (c >= '0' && c <= '9') { if (++digits > 15) return ~0ull; v = v * 10 + (uint64_t)(c - '0'); }
        else if (digits) break;
        else if (c != ' ' && c != '\r' && c != '\n' && c != '\t') return ~0ull;
    }
    return digits ? v : ~0ull;
}

// Does offset `x` start a cross-reference table ("xref") or an xref stream object ("12 0 obj")?
bool xref_at(Buf& b, uint64_t x, uint64_t end) {
    if (x + 4 > end) return false;
    const uint8_t* p = b.at(x, 4);
    if (!p) return false;
    if (std::memcmp(p, "xref", 4) == 0) return true;
    uint64_t pos = x; int stage = 0, digits = 0;
    for (int i = 0; i < 24 && pos < end; ++i, ++pos) {
        int c = b.byte(pos);
        if (c < 0) return false;
        bool d = c >= '0' && c <= '9', sp = c == ' ';
        if (stage == 0) { if (d) ++digits; else if (sp && digits) stage = 1; else return false; }
        else if (stage == 1) { if (d) stage = 2; else if (!sp) return false; }
        else if (stage == 2) { if (sp) stage = 3; else if (!d) return false; }
        else if (stage == 3) { if (sp) continue; const uint8_t* o = b.at(pos, 3); return o && std::memcmp(o, "obj", 3) == 0; }
    }
    return false;
}

bool parse_pdf(Reader& rd, Result& r) {
    Buf b(rd);
    r.type = RX_FT_PDF; r.ext = L"pdf";
    const uint64_t limit = std::min<uint64_t>(max_size(RX_FT_PDF), rd.available());
    const uint8_t* h = b.at(0, 8);
    if (!h || !(h[5] == '1' || h[5] == '2') || h[6] != '.' || h[7] < '0' || h[7] > '9') return false;

    // The end is the last %%EOF whose "startxref" points at a real cross-reference section. Incremental updates append
    // further sections + %%EOF, so after each end we look a bounded distance further. Without any verifiable %%EOF the
    // first one is used and the result is only LIKELY.
    uint64_t end = 0, search = 8, horizon = limit;
    bool verified = false, any = false;
    for (;;) {
        uint64_t q = find(b, search, horizon, "%%EOF", 5, kZeroAbort);
        if (q == ~0ull) break;
        bool new_pdf = false;                                       // another PDF starting on a sector boundary: not ours
        for (uint64_t np = find(b, search, q, "%PDF-", 5); np != ~0ull; np = find(b, np + 1, q, "%PDF-", 5))
            if (np % 512 == 0) { new_pdf = true; break; }
        if (new_pdf) break;
        uint64_t e = q + 5;
        for (int i = 0; i < 2; ++i) { int c = b.byte(e); if (c == '\r' || c == '\n') ++e; else break; }
        uint64_t sx = startxref_before(b, q);
        bool ok = sx != ~0ull && sx < q && xref_at(b, sx, q);
        if (ok) { end = e; verified = true; }
        else if (!any) end = e;
        any = true;
        search = e;
        horizon = std::min<uint64_t>(limit, e + 16 * MB);
    }
    if (!any) return false;                                          // no end marker: cannot tell where it ends
    r.size = end;
    r.evidence |= RX_EV_END_FOUND;
    if (verified) r.evidence |= RX_EV_STRUCTURE_OK | RX_EV_TAIL_CONFIRMED;
    finish(r);
    return true;
}

// ---------------------------------------------------------------- ZIP family (zip, docx/xlsx/pptx, odt/ods/odp, jar, apk, epub)
struct ZipNames { bool content_types = false, word = false, xl = false, ppt = false, manifest_odf = false, jar = false, apk = false;
                  bool mimetype = false; std::string mime; };

void note_name(ZipNames& z, const uint8_t* n, uint32_t len) {
    std::string s(reinterpret_cast<const char*>(n), len);
    if (s == "[Content_Types].xml") z.content_types = true;
    else if (s.compare(0, 5, "word/") == 0) z.word = true;
    else if (s.compare(0, 3, "xl/") == 0) z.xl = true;
    else if (s.compare(0, 4, "ppt/") == 0) z.ppt = true;
    else if (s == "META-INF/manifest.xml") z.manifest_odf = true;
    else if (s == "META-INF/MANIFEST.MF") z.jar = true;
    else if (s == "AndroidManifest.xml") z.apk = true;
    else if (s == "mimetype") z.mimetype = true;
}

std::wstring zip_ext(const ZipNames& z) {
    if (z.content_types && z.word) return L"docx";
    if (z.content_types && z.xl) return L"xlsx";
    if (z.content_types && z.ppt) return L"pptx";
    if (z.mime == "application/vnd.oasis.opendocument.text") return L"odt";
    if (z.mime == "application/vnd.oasis.opendocument.spreadsheet") return L"ods";
    if (z.mime == "application/vnd.oasis.opendocument.presentation") return L"odp";
    if (z.mime == "application/epub+zip") return L"epub";
    if (z.apk) return L"apk";
    if (z.jar) return L"jar";
    return L"zip";
}

bool zip_method_ok(uint16_t m) { return m == 0 || m == 1 || m == 6 || m == 8 || m == 9 || m == 12 || m == 14 || m == 93 || m == 95 || m == 98 || m == 99; }

// Validates the central directory [cd, cd+cd_size) holding `entries` records; every record must point at a matching local header.
bool zip_check_cd(Buf& b, uint64_t cd, uint64_t cd_size, uint64_t entries, ZipNames& z) {
    uint64_t pos = cd, end = cd + cd_size, n = 0;
    while (pos < end) {
        const uint8_t* p = b.at(pos, 46);
        if (!p || std::memcmp(p, "PK\x01\x02", 4) != 0) return false;
        uint32_t nl = le16(p + 28), el = le16(p + 30), cl = le16(p + 32);
        uint64_t local = le32(p + 42);
        if (nl == 0 || nl > 4096) return false;
        if (n < 4096) { const uint8_t* nm = b.at(pos + 46, nl); if (!nm) return false; note_name(z, nm, nl); }
        if (local != 0xFFFFFFFFu) {
            const uint8_t* lh = b.at(local, 30);
            if (!lh || std::memcmp(lh, "PK\x03\x04", 4) != 0 || le16(lh + 26) != nl) return false;
        }
        pos += 46 + (uint64_t)nl + el + cl;
        if (++n > entries) return false;
    }
    return pos == end && n == entries;
}

bool parse_zip(Reader& rd, Result& r) {
    Buf b(rd);
    r.type = RX_FT_ZIP; r.ext = L"zip";
    const uint64_t limit = std::min<uint64_t>(max_size(RX_FT_ZIP), rd.available());
    ZipNames z;

    // 1. Walk local headers while their sizes are known (fast and strong evidence).
    uint64_t pos = 0;
    uint32_t locals = 0;
    bool search_eocd = false;
    for (;;) {
        const uint8_t* p = b.at(pos, 30);
        if (!p) { r.evidence |= RX_EV_TRUNCATED; break; }
        if (std::memcmp(p, "PK\x03\x04", 4) != 0) {
            if (std::memcmp(p, "PK\x01\x02", 4) == 0 || std::memcmp(p, "PK\x05\x06", 4) == 0 || std::memcmp(p, "PK\x06\x06", 4) == 0) { search_eocd = true; break; }
            if (locals == 0) return false;
            r.evidence |= RX_EV_CORRUPT; break;
        }
        uint16_t ver = le16(p + 4), flags = le16(p + 6), method = le16(p + 8);
        uint32_t nl = le16(p + 26), el = le16(p + 28);
        if (ver > 100 || !zip_method_ok(method) || nl == 0 || nl > 4096) { if (locals == 0) return false; r.evidence |= RX_EV_CORRUPT; break; }
        const uint8_t* nm = b.at(pos + 30, nl + el);
        if (!nm) { r.evidence |= RX_EV_TRUNCATED; break; }
        note_name(z, nm, nl);
        uint64_t csize = le32(p + 18);
        if (csize == 0xFFFFFFFFu) {                                                     // ZIP64 extra field
            uint32_t e = 0; bool found = false;
            while (e + 4 <= el) {
                uint32_t id = le16(nm + nl + e), sz = le16(nm + nl + e + 2);
                if (id == 1 && sz >= 16 && e + 4 + sz <= el) { csize = le64(nm + nl + e + 4 + 8); found = true; break; }
                e += 4 + sz;
            }
            if (!found) { search_eocd = true; break; }
        }
        if (z.mimetype && nl == 8 && method == 0 && csize > 0 && csize < 128 && z.mime.empty()) {
            const uint8_t* mt = b.at(pos + 30 + nl + el, (uint32_t)csize);
            if (mt) z.mime.assign(reinterpret_cast<const char*>(mt), (size_t)csize);
        }
        ++locals;
        if (flags & 0x08) { search_eocd = true; break; }                               // sizes follow the data: search instead
        pos += 30 + (uint64_t)nl + el + csize;
        if (pos > limit) { r.evidence |= RX_EV_TRUNCATED; break; }
    }

    // 2. Find the end-of-central-directory record that belongs to THIS archive (its directory must end right before it).
    if (search_eocd) {
        uint64_t q = pos;
        for (;;) {
            q = find(b, q, limit, "PK\x05\x06", 4, kZeroAbort);
            if (q == ~0ull) break;
            const uint8_t* e = b.at(q, 22);
            if (!e) break;
            uint64_t entries = le16(e + 10), cd_size = le32(e + 12), cd_off = le32(e + 16);
            uint32_t comment = le16(e + 20);
            bool zip64 = cd_off == 0xFFFFFFFFu || cd_size == 0xFFFFFFFFu || entries == 0xFFFF;
            uint64_t cd_end = q;
            if (zip64 && q >= 20) {
                const uint8_t* loc = b.at(q - 20, 20);
                if (loc && std::memcmp(loc, "PK\x06\x07", 4) == 0) {
                    uint64_t z64 = le64(loc + 8);
                    const uint8_t* zr = b.at(z64, 56);
                    if (zr && std::memcmp(zr, "PK\x06\x06", 4) == 0) {
                        entries = le64(zr + 32); cd_size = le64(zr + 40); cd_off = le64(zr + 48); cd_end = z64;
                    }
                }
            }
            if (le16(e + 4) == 0 && le16(e + 6) == 0 && cd_off + cd_size == cd_end && entries > 0) {
                uint64_t end = q + 22 + comment;
                if (end > rd.available()) { r.evidence |= RX_EV_TRUNCATED; r.size = rd.available(); finish(r); return true; }
                r.size = end;
                r.evidence &= ~(uint32_t)(RX_EV_TRUNCATED | RX_EV_CORRUPT);
                r.evidence |= RX_EV_END_FOUND;
                if (zip_check_cd(b, cd_off, cd_size, entries, z)) r.evidence |= RX_EV_STRUCTURE_OK | RX_EV_TAIL_CONFIRMED;
                else r.evidence |= RX_EV_CORRUPT;
                r.ext = zip_ext(z);
                finish(r);
                return true;
            }
            q += 4;
        }
    }
    // No central directory: only the local entries walked so far are usable.
    if (locals == 0 || pos < 512) return false;
    if (!(r.evidence & (RX_EV_CORRUPT | RX_EV_TRUNCATED))) r.evidence |= RX_EV_CORRUPT;
    r.size = std::min<uint64_t>(pos, rd.available());
    r.ext = zip_ext(z);
    finish(r);
    return true;
}

// ---------------------------------------------------------------- ISO base media (mp4, mov, m4a, 3gp, heic)
bool known_box(const uint8_t* t) {
    static const char* k[] = { "ftyp", "moov", "mdat", "free", "skip", "wide", "uuid", "meta", "pdin", "moof", "mfra", "sidx",
                               "styp", "prft", "emsg", "pnot", "junk", "ssix", "idat", "jumb", "PICT", "CDI1", "mvhd" };
    for (const char* x : k) if (std::memcmp(t, x, 4) == 0) return true;
    return false;
}

std::wstring brand_ext(const uint8_t* br) {
    auto is = [&](const char* s) { return std::memcmp(br, s, 4) == 0; };
    if (is("qt  ")) return L"mov";
    if (is("M4A ") || is("M4B ")) return L"m4a";
    if (is("3gp4") || is("3gp5") || is("3gp6") || is("3g2a") || is("3gp7")) return L"3gp";
    if (is("heic") || is("heix") || is("mif1") || is("msf1") || is("hevc")) return L"heic";
    if (is("avif")) return L"avif";
    if (is("crx ")) return L"cr3";
    return L"mp4";
}

bool parse_isobmff(Reader& rd, Result& r) {
    Buf b(rd);
    r.type = RX_FT_ISOBMFF;
    const uint64_t avail = rd.available();
    const uint8_t* h = b.at(0, 16);
    if (!h) return false;
    r.ext = brand_ext(h + 8);
    const bool still = r.ext == L"heic" || r.ext == L"avif";
    uint64_t pos = 0, last_hdr = 0;
    bool moov = false, mvhd = false, mdat = false, meta = false;
    int boxes = 0;
    for (; boxes < 100000; ++boxes) {
        const uint8_t* p = b.at(pos, 16);
        if (!p) { if (pos < avail) r.evidence |= RX_EV_TRUNCATED; break; }
        uint64_t size = be32(p), hdr = 8;
        if (!known_box(p + 4)) break;                                     // end of this file's boxes
        if (size == 1) { size = be64(p + 8); hdr = 16; }
        if (size == 0) { r.evidence |= RX_EV_CORRUPT; break; }           // "extends to end of file": end unknown when carving
        if (size < hdr) { if (boxes == 0) return false; r.evidence |= RX_EV_CORRUPT; break; }
        if (boxes == 0 && (std::memcmp(p + 4, "ftyp", 4) != 0 || size > 4096)) return false;
        if (std::memcmp(p + 4, "moov", 4) == 0) {
            moov = true;
            uint64_t c = pos + hdr, ce = pos + size;
            for (int i = 0; i < 64 && c + 8 <= ce; ++i) {                 // look for the movie header inside moov
                const uint8_t* ch = b.at(c, 32);
                if (!ch) break;
                uint64_t cs = be32(ch);
                if (cs < 8) break;
                if (std::memcmp(ch + 4, "mvhd", 4) == 0) {
                    mvhd = true;
                    uint64_t secs = ch[8] == 1 ? be64(ch + 12) : be32(ch + 12);
                    int64_t unix_secs = (int64_t)secs - 2082844800;
                    if (unix_secs > kMinPlausible && unix_secs < kMaxPlausible) r.content_time = unix_to_filetime(unix_secs);
                    break;
                }
                c += cs;
            }
        }
        if (std::memcmp(p + 4, "mdat", 4) == 0) mdat = true;
        if (std::memcmp(p + 4, "meta", 4) == 0) meta = true;
        if (pos + size > avail || pos + size > max_size(RX_FT_ISOBMFF)) { r.evidence |= RX_EV_TRUNCATED; pos = std::min<uint64_t>(avail, pos + size); break; }
        last_hdr = pos;
        pos += size;
    }
    if (boxes < 2 || pos <= 32) return false;                           // ftyp alone is not a file
    r.size = pos;
    bool complete = still ? (meta && (mdat || pos < 64 * KB)) : (moov && mvhd && mdat);
    if (!(r.evidence & (RX_EV_CORRUPT | RX_EV_TRUNCATED))) {
        r.evidence |= RX_EV_END_FOUND;
        if (complete) r.evidence |= RX_EV_STRUCTURE_OK;
        else r.evidence |= RX_EV_CORRUPT;                                   // essential boxes missing: cannot play as-is
        if (last_hdr > kTailZone || pos <= kTailZone) r.evidence |= RX_EV_TAIL_CONFIRMED;
    }
    finish(r);
    return true;
}

// ---------------------------------------------------------------- RIFF (avi, wav, webp)
bool parse_riff(Reader& rd, Result& r) {
    Buf b(rd);
    r.type = RX_FT_RIFF;
    const uint8_t* h = b.at(0, 16);
    if (!h) return false;
    if (std::memcmp(h + 8, "WAVE", 4) == 0) r.ext = L"wav";
    else if (std::memcmp(h + 8, "AVI ", 4) == 0) r.ext = L"avi";
    else if (std::memcmp(h + 8, "WEBP", 4) == 0) r.ext = L"webp";
    else return false;
    uint64_t total = (uint64_t)le32(h + 4) + 8;
    if (total < 24 || !printable4(h + 12)) return false;
    uint64_t pos = 12, last_hdr = 0;
    uint32_t chunks = 0;
    bool ok = true;
    while (pos + 8 <= total) {
        const uint8_t* c = b.at(pos, 8);
        if (!c) { r.evidence |= RX_EV_TRUNCATED; ok = false; break; }
        if (!printable4(c)) { ok = false; break; }
        uint64_t cs = le32(c + 4);
        last_hdr = pos;
        pos += 8 + cs + (cs & 1);
        ++chunks;
    }
    if (chunks == 0) return false;
    if (total > rd.available()) { r.size = rd.available(); r.evidence |= RX_EV_TRUNCATED; finish(r); return true; }
    r.size = total;
    r.evidence |= RX_EV_END_FOUND;
    if (ok && (pos == total || pos == total + 1)) {
        r.evidence |= RX_EV_STRUCTURE_OK;
        if (last_hdr > kTailZone || total <= kTailZone) r.evidence |= RX_EV_TAIL_CONFIRMED;
    } else r.evidence |= RX_EV_CORRUPT;
    finish(r);
    return true;
}

// ---------------------------------------------------------------- MP3 (ID3v2 tag + MPEG audio frames)
// Length of the MPEG audio frame whose header is at p, or 0 if p is not a valid frame header.
uint32_t mpeg_frame_len(const uint8_t* p, uint32_t* samples_rate = nullptr) {
    if (p[0] != 0xFF || (p[1] & 0xE0) != 0xE0) return 0;
    uint32_t ver = (p[1] >> 3) & 3, layer = (p[1] >> 1) & 3, bri = (p[2] >> 4) & 15, sri = (p[2] >> 2) & 3, pad = (p[2] >> 1) & 1;
    if (ver == 1 || layer == 0 || bri == 0 || bri == 15 || sri == 3) return 0;
    static const uint16_t br_v1[3][15] = {
        {0,32,64,96,128,160,192,224,256,288,320,352,384,416,448},   // layer I
        {0,32,48,56,64,80,96,112,128,160,192,224,256,320,384},      // layer II
        {0,32,40,48,56,64,80,96,112,128,160,192,224,256,320} };     // layer III
    static const uint16_t br_v2[3][15] = {
        {0,32,48,56,64,80,96,112,128,144,160,176,192,224,256},
        {0,8,16,24,32,40,48,56,64,80,96,112,128,144,160},
        {0,8,16,24,32,40,48,56,64,80,96,112,128,144,160} };
    static const uint32_t sr_tab[3] = { 44100, 48000, 32000 };
    uint32_t li = 3 - layer;                                         // 0 = layer I, 1 = II, 2 = III
    uint32_t br = (ver == 3 ? br_v1 : br_v2)[li][bri] * 1000u;
    uint32_t sr = sr_tab[sri] >> (ver == 3 ? 0 : (ver == 2 ? 1 : 2));
    if (samples_rate) *samples_rate = sr;
    if (li == 0) return (12 * br / sr + pad) * 4;
    if (li == 2 && ver != 3) return 72 * br / sr + pad;
    return 144 * br / sr + pad;
}

bool parse_mp3(Reader& rd, Result& r) {
    Buf b(rd);
    r.type = RX_FT_MP3; r.ext = L"mp3";
    const uint64_t limit = std::min<uint64_t>(max_size(RX_FT_MP3), rd.available());
    const uint8_t* h = b.at(0, 10);
    if (!h || h[3] < 2 || h[3] > 4 || h[4] == 0xFF) return false;
    for (int i = 6; i < 10; ++i) if (h[i] & 0x80) return false;
    uint64_t tag = 10 + (((uint64_t)h[6] << 21) | ((uint64_t)h[7] << 14) | ((uint64_t)h[8] << 7) | h[9]);
    if (h[5] & 0x10) tag += 10;
    if (tag > limit) return false;
    // Find the first frame (tags may be followed by padding).
    uint64_t pos = tag, first = ~0ull;
    for (uint64_t i = 0; i < 64 * KB && pos + 4 <= limit; ++i, ++pos) {
        const uint8_t* p = b.at(pos, 4);
        if (!p) break;
        if (p[0] == 0xFF && mpeg_frame_len(p)) {
            uint32_t len = mpeg_frame_len(p);
            const uint8_t* n = b.at(pos + len, 4);
            if (n && mpeg_frame_len(n)) { first = pos; break; }
        }
    }
    if (first == ~0ull) return false;
    pos = first;
    uint64_t frames = 0;
    while (pos + 4 <= limit) {
        const uint8_t* p = b.at(pos, 4);
        if (!p) break;
        uint32_t len = mpeg_frame_len(p);
        if (len < 24) break;
        if (pos + len > limit) { r.evidence |= RX_EV_TRUNCATED; break; }
        pos += len; ++frames;
    }
    if (frames < 8) return false;
    // Tail evidence: an ID3v1 tag right after the last frame, or a Xing/Info header whose byte count matches.
    bool tail = false;
    const uint8_t* t = b.at(pos, 3);
    if (t && std::memcmp(t, "TAG", 3) == 0 && pos + 128 <= rd.available()) { pos += 128; tail = true; }
    const uint8_t* f = b.at(first, 64);
    if (!tail && f) {
        uint32_t ver = (f[1] >> 3) & 3, mono = ((f[3] >> 6) & 3) == 3;
        uint32_t side = ver == 3 ? (mono ? 17 : 32) : (mono ? 9 : 17);
        const uint8_t* x = f + 4 + side;
        if (4 + side + 16 <= 64 && (std::memcmp(x, "Xing", 4) == 0 || std::memcmp(x, "Info", 4) == 0)) {
            uint32_t fl = be32(x + 4);
            if (fl & 2) {
                uint32_t bytes = be32(x + (fl & 1 ? 12 : 8));
                uint64_t walked = pos - first;
                if (bytes > 0 && (walked >= bytes ? walked - bytes : bytes - walked) <= 4096) tail = true;
            }
        }
    }
    r.size = pos;
    if (!(r.evidence & RX_EV_TRUNCATED)) {
        r.evidence |= RX_EV_END_FOUND | RX_EV_STRUCTURE_OK;
        if (tail) r.evidence |= RX_EV_TAIL_CONFIRMED;
    }
    finish(r);
    return true;
}

// ---------------------------------------------------------------- 7-Zip
bool parse_7z(Reader& rd, Result& r) {
    Buf b(rd);
    r.type = RX_FT_7Z; r.ext = L"7z";
    const uint8_t* h = b.at(0, 32);
    if (!h || h[6] != 0) return false;
    if (crc32(0, h + 12, 20) != le32(h + 8)) return false;           // start header checksum
    uint64_t off = le64(h + 12), size = le64(h + 20);
    uint32_t want = le32(h + 28);
    if (size == 0 || size > 256 * MB || off > max_size(RX_FT_7Z)) return false;
    uint64_t total = 32 + off + size;
    if (total > rd.available()) { r.size = rd.available(); r.evidence |= RX_EV_TRUNCATED; finish(r); return true; }
    uint32_t crc = 0;
    uint64_t pos = 32 + off, left = size;
    while (left) {
        uint32_t got = 0;
        const uint8_t* s = b.span(pos, got);
        if (!s) break;
        uint32_t n = (uint32_t)std::min<uint64_t>(got, left);
        crc = crc32(crc, s, n); pos += n; left -= n;
    }
    r.size = total;
    r.evidence |= RX_EV_END_FOUND;
    if (left == 0 && crc == want) r.evidence |= RX_EV_STRUCTURE_OK | RX_EV_CHECKSUM_OK | RX_EV_TAIL_CONFIRMED;
    else r.evidence |= RX_EV_CORRUPT;
    finish(r);
    return true;
}

// ---------------------------------------------------------------- EBML (mkv, webm)
// EBML variable-size integer. Returns length in bytes (0 = invalid). `unknown` set when all value bits are 1.
uint32_t ebml_vint(Buf& b, uint64_t pos, uint64_t& value, bool& unknown, bool keep_marker) {
    int c = b.byte(pos);
    if (c <= 0) return 0;
    uint32_t len = 1;
    while (len <= 8 && !(c & (0x80 >> (len - 1)))) ++len;
    if (len > 8) return 0;
    const uint8_t* p = b.at(pos, len);
    if (!p) return 0;
    uint64_t v = keep_marker ? (uint64_t)c : (uint64_t)(c & ((0x80 >> (len - 1)) - 1));
    bool all = (v == (uint64_t)((0x80 >> (len - 1)) - 1)) && !keep_marker;
    for (uint32_t i = 1; i < len; ++i) { v = (v << 8) | p[i]; if (p[i] != 0xFF) all = false; }
    value = v; unknown = all;
    return len;
}

bool ebml_top_id(uint64_t id) {
    return id == 0x114D9B74 || id == 0x1549A966 || id == 0x1654AE6B || id == 0x1F43B675 || id == 0x1C53BB6B ||
           id == 0x1941A469 || id == 0x1043A770 || id == 0x1254C367 || id == 0xEC || id == 0xBF;
}
bool ebml_cluster_child(uint64_t id) {
    return id == 0xE7 || id == 0xA3 || id == 0xA0 || id == 0xA7 || id == 0xAB || id == 0xEC || id == 0xBF || id == 0x5854 || id == 0xAF;
}

bool parse_ebml(Reader& rd, Result& r) {
    Buf b(rd);
    r.type = RX_FT_EBML; r.ext = L"mkv";
    const uint64_t avail = std::min<uint64_t>(rd.available(), max_size(RX_FT_EBML));
    uint64_t v; bool unk;
    uint32_t n = ebml_vint(b, 4, v, unk, false);
    if (!n || unk || v > 4096) return false;
    uint64_t hdr_end = 4 + n + v;
    // DocType inside the EBML header.
    uint64_t q = find(b, 4 + n, hdr_end, "\x42\x82", 2);
    if (q == ~0ull) return false;
    uint64_t dl; uint32_t m = ebml_vint(b, q + 2, dl, unk, false);
    if (!m || dl == 0 || dl > 16) return false;
    const uint8_t* dt = b.at(q + 2 + m, (uint32_t)dl);
    if (!dt) return false;
    std::string doctype(reinterpret_cast<const char*>(dt), (size_t)dl);
    while (!doctype.empty() && doctype.back() == 0) doctype.pop_back();
    if (doctype == "webm") r.ext = L"webm"; else if (doctype != "matroska") return false;

    uint64_t id; uint32_t il = ebml_vint(b, hdr_end, id, unk, true);
    if (il != 4 || id != 0x18538067) return false;
    uint64_t seg_size; bool seg_unknown;
    uint32_t sl = ebml_vint(b, hdr_end + 4, seg_size, seg_unknown, false);
    if (!sl) return false;
    uint64_t pos = hdr_end + 4 + sl, seg_end = seg_unknown ? avail : pos + seg_size, last_hdr = 0;
    if (seg_end > avail) { seg_end = avail; r.evidence |= RX_EV_TRUNCATED; }
    bool cluster = false, ok = true;
    for (int i = 0; i < 1000000 && pos < seg_end; ++i) {
        uint64_t eid, esz; bool eunk;
        uint32_t a = ebml_vint(b, pos, eid, unk, true);
        if (!a || a > 4 || !ebml_top_id(eid)) { ok = false; break; }
        uint32_t c = ebml_vint(b, pos + a, esz, eunk, false);
        if (!c) { ok = false; break; }
        last_hdr = pos;
        if (eid == 0x1F43B675) cluster = true;
        if (eunk) {
            if (eid != 0x1F43B675) { ok = false; break; }
            uint64_t cp = pos + a + c;                                      // unknown-size cluster: walk its children
            for (int k = 0; k < 1000000 && cp < seg_end; ++k) {
                uint64_t cid, csz; bool cunk;
                uint32_t ca = ebml_vint(b, cp, cid, unk, true);
                if (!ca || !ebml_cluster_child(cid)) break;
                uint32_t cc = ebml_vint(b, cp + ca, csz, cunk, false);
                if (!cc || cunk) break;
                cp += ca + cc + csz;
            }
            pos = cp;
        } else pos += a + c + esz;
    }
    if (!cluster) return false;
    if (seg_unknown) {
        r.size = std::min<uint64_t>(pos, avail);
        r.evidence |= RX_EV_END_FOUND;                                      // end inferred from the last valid element
        if (last_hdr > kTailZone || r.size <= kTailZone) r.evidence |= RX_EV_TAIL_CONFIRMED;
        r.evidence |= RX_EV_STRUCTURE_OK;
        r.evidence &= ~(uint32_t)RX_EV_TRUNCATED;
        finish(r);
        if (r.condition == RX_COND_VERIFIED) r.condition = RX_COND_LIKELY;  // no declared size to confirm against
        return true;
    }
    r.size = seg_end;
    if (!(r.evidence & RX_EV_TRUNCATED)) {
        r.evidence |= RX_EV_END_FOUND;
        if (ok && pos == seg_end) {
            r.evidence |= RX_EV_STRUCTURE_OK;
            if (last_hdr > kTailZone || seg_end <= kTailZone) r.evidence |= RX_EV_TAIL_CONFIRMED;
        } else r.evidence |= RX_EV_CORRUPT;
    }
    finish(r);
    return true;
}

// ---------------------------------------------------------------- OLE2 compound files (doc, xls, ppt, msg)
bool parse_ole2(Reader& rd, Result& r) {
    Buf b(rd);
    r.type = RX_FT_OLE2;
    const uint8_t* h = b.at(0, 512);
    if (!h) return false;
    uint16_t major = le16(h + 0x1A), shift = le16(h + 0x1E);
    if (le16(h + 0x1C) != 0xFFFE || !((major == 3 && shift == 9) || (major == 4 && shift == 12)) || le16(h + 0x20) != 6) return false;
    const uint32_t ss = 1u << shift, per = ss / 4;
    uint32_t nfat = le32(h + 0x2C), dir0 = le32(h + 0x30), difat = le32(h + 0x44), ndifat = le32(h + 0x48);
    if (nfat == 0 || nfat > (uint32_t)(max_size(RX_FT_OLE2) / ss / per + 1) || ndifat > 4096) return false;

    std::vector<uint32_t> fat_secs;
    for (uint32_t i = 0; i < 109 && fat_secs.size() < nfat; ++i) fat_secs.push_back(le32(h + 0x4C + i * 4));
    uint32_t d = difat;
    for (uint32_t k = 0; k < ndifat && fat_secs.size() < nfat; ++k) {
        if (d >= 0xFFFFFFFAu) return false;
        const uint8_t* s = b.at((uint64_t)(d + 1) * ss, ss);
        if (!s) { r.evidence |= RX_EV_TRUNCATED; break; }
        for (uint32_t i = 0; i < per - 1 && fat_secs.size() < nfat; ++i) fat_secs.push_back(le32(s + i * 4));
        d = le32(s + (per - 1) * 4);
    }
    if (fat_secs.size() < nfat) { if (!(r.evidence & RX_EV_TRUNCATED)) return false; }

    std::vector<uint32_t> fat;
    fat.reserve((size_t)fat_secs.size() * per);
    uint64_t max_meta = 0;
    for (uint32_t fs : fat_secs) {
        if (fs >= 0xFFFFFFFAu) return false;
        const uint8_t* s = b.at((uint64_t)(fs + 1) * ss, ss);
        if (!s) { r.evidence |= RX_EV_TRUNCATED; break; }
        max_meta = std::max<uint64_t>(max_meta, (uint64_t)(fs + 1) * ss);
        for (uint32_t i = 0; i < per; ++i) fat.push_back(le32(s + i * 4));
    }
    if (r.evidence & RX_EV_TRUNCATED) return false;
    uint64_t highest = 0; bool any = false;
    for (size_t i = 0; i < fat.size(); ++i) {
        uint32_t v = fat[i];
        if (v == 0xFFFFFFFFu) continue;
        if (v < 0xFFFFFFFCu && v >= fat.size()) return false;           // points outside the table: not a real file
        highest = i; any = true;
    }
    if (!any) return false;
    uint64_t total = (highest + 2) * (uint64_t)ss;

    // Directory chain: root entry + stream names decide the extension.
    bool root = false, word = false, xls = false, ppt = false, msg = false;
    uint32_t sec = dir0;
    for (int steps = 0; steps < 4096 && sec < fat.size(); ++steps) {
        const uint8_t* s = b.at((uint64_t)(sec + 1) * ss, ss);
        if (!s) break;
        max_meta = std::max<uint64_t>(max_meta, (uint64_t)(sec + 1) * ss);
        for (uint32_t e = 0; e + 128 <= ss; e += 128) {
            const uint8_t* de = s + e;
            uint32_t nl = le16(de + 64);
            if (nl < 2 || nl > 64 || (nl & 1)) continue;
            std::string name;
            for (uint32_t i = 0; i + 2 < nl; i += 2) name.push_back(de[i + 1] == 0 && de[i] < 128 ? (char)de[i] : '?');
            if (steps == 0 && e == 0) root = de[66] == 5 && name == "Root Entry";
            if (name == "WordDocument") word = true;
            else if (name == "Workbook" || name == "Book") xls = true;
            else if (name == "PowerPoint Document") ppt = true;
            else if (name.compare(0, 12, "__substg1.0_") == 0 || name == "__properties_version1.0") msg = true;
        }
        sec = fat[sec];
    }
    if (!root) return false;
    r.ext = word ? L"doc" : xls ? L"xls" : ppt ? L"ppt" : msg ? L"msg" : L"ole";
    if (total > rd.available()) { r.size = rd.available(); r.evidence |= RX_EV_TRUNCATED; finish(r); return true; }
    r.size = total;
    r.evidence |= RX_EV_END_FOUND | RX_EV_STRUCTURE_OK;
    // Only metadata actually read near the end confirms the tail; the stream data itself carries no checkable structure.
    if (max_meta * 4 >= total * 3) r.evidence |= RX_EV_TAIL_CONFIRMED;
    finish(r);
    return true;
}

} // namespace

// ---------------------------------------------------------------- public helpers
uint32_t MemReader::read(uint64_t off, uint8_t* buf, uint32_t len) {
    if (off >= n_) return 0;
    uint64_t n = std::min<uint64_t>(len, n_ - off);
    std::memcpy(buf, p_ + off, (size_t)n);
    return (uint32_t)n;
}

namespace {
struct CrcTable {
    uint32_t t[256];
    CrcTable() {
        for (uint32_t i = 0; i < 256; ++i) {
            uint32_t c = i;
            for (int k = 0; k < 8; ++k) c = (c & 1) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
    }
};
}

uint32_t crc32(uint32_t crc, const uint8_t* p, size_t n) {
    static const CrcTable table;   // thread-safe initialization (C++11 magic static)
    crc = ~crc;
    for (size_t i = 0; i < n; ++i) crc = table.t[(crc ^ p[i]) & 0xFF] ^ (crc >> 8);
    return ~crc;
}

uint64_t exif_time_to_filetime(const char* s, size_t n) {
    if (n < 19) return 0;
    auto num = [&](size_t at, size_t len, int& out) {
        out = 0;
        for (size_t i = 0; i < len; ++i) { char c = s[at + i]; if (c < '0' || c > '9') return false; out = out * 10 + (c - '0'); }
        return true;
    };
    int y, mo, d, h, mi, se;
    if (!num(0, 4, y) || s[4] != ':' || !num(5, 2, mo) || s[7] != ':' || !num(8, 2, d) || s[10] != ' ' ||
        !num(11, 2, h) || s[13] != ':' || !num(14, 2, mi) || s[16] != ':' || !num(17, 2, se)) return 0;
    if (mo < 1 || mo > 12 || d < 1 || d > 31 || h > 23 || mi > 59 || se > 60) return 0;
    int64_t secs = days_from_civil(y, (unsigned)mo, (unsigned)d) * 86400 + h * 3600 + mi * 60 + se;
    if (secs < kMinPlausible || secs > kMaxPlausible) return 0;
    return unix_to_filetime(secs);
}

uint64_t max_size(uint32_t type) {
    switch (type) {
    case RX_FT_JPEG: return 128 * MB;
    case RX_FT_PNG: return 256 * MB;
    case RX_FT_GIF: return 64 * MB;
    case RX_FT_BMP: return 512 * MB;
    case RX_FT_PDF: return 512 * MB;
    case RX_FT_ZIP: return 2 * GB;
    case RX_FT_ISOBMFF: return 32 * GB;
    case RX_FT_RIFF: return 4 * GB + 8;
    case RX_FT_MP3: return 256 * MB;
    case RX_FT_7Z: return 64 * GB;
    case RX_FT_EBML: return 32 * GB;
    case RX_FT_OLE2: return 1 * GB;
    default: return 0;
    }
}

uint32_t match(const uint8_t* p, uint32_t len) {
    if (len < kMatchBytes) return 0;
    switch (p[0]) {
    case 0xFF: if (p[1] == 0xD8 && p[2] == 0xFF && p[3] >= 0xC0 && p[3] != 0xFF) return RX_FT_JPEG; break;
    case 0x89: if (std::memcmp(p, "\x89PNG\r\n\x1A\n", 8) == 0) return RX_FT_PNG; break;
    case 'G':  if (std::memcmp(p, "GIF87a", 6) == 0 || std::memcmp(p, "GIF89a", 6) == 0) return RX_FT_GIF; break;
    case 'B':  if (p[1] == 'M' && le32(p + 6) == 0) return RX_FT_BMP; break;
    case '%':  if (std::memcmp(p, "%PDF-", 5) == 0) return RX_FT_PDF; break;
    case 'P':  if (std::memcmp(p, "PK\x03\x04", 4) == 0) return RX_FT_ZIP; break;
    case 'R':  if (std::memcmp(p, "RIFF", 4) == 0) return RX_FT_RIFF; break;
    case 'I':  if (p[1] == 'D' && p[2] == '3') return RX_FT_MP3; break;
    case '7':  if (std::memcmp(p, "7z\xBC\xAF\x27\x1C", 6) == 0) return RX_FT_7Z; break;
    case 0x1A: if (p[1] == 0x45 && p[2] == 0xDF && p[3] == 0xA3) return RX_FT_EBML; break;
    case 0xD0: if (std::memcmp(p, "\xD0\xCF\x11\xE0\xA1\xB1\x1A\xE1", 8) == 0) return RX_FT_OLE2; break;
    default: break;
    }
    if (p[0] == 0 && std::memcmp(p + 4, "ftyp", 4) == 0) {
        uint32_t s = be32(p);
        if (s >= 16 && s <= 4096 && printable4(p + 8)) return RX_FT_ISOBMFF;
    }
    return 0;
}

bool parse(uint32_t type, Reader& r, Result& out) {
    out = Result();
    switch (type) {
    case RX_FT_JPEG: return parse_jpeg(r, out);
    case RX_FT_PNG: return parse_png(r, out);
    case RX_FT_GIF: return parse_gif(r, out);
    case RX_FT_BMP: return parse_bmp(r, out);
    case RX_FT_PDF: return parse_pdf(r, out);
    case RX_FT_ZIP: return parse_zip(r, out);
    case RX_FT_ISOBMFF: return parse_isobmff(r, out);
    case RX_FT_RIFF: return parse_riff(r, out);
    case RX_FT_MP3: return parse_mp3(r, out);
    case RX_FT_7Z: return parse_7z(r, out);
    case RX_FT_EBML: return parse_ebml(r, out);
    case RX_FT_OLE2: return parse_ole2(r, out);
    default: return false;
    }
}

}} // namespace
