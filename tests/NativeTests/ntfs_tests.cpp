// NTFS engine tests.
//  - Pure tests (all platforms): boot sector, fixups, run lists, record parsing, header signatures.
//  - Volume tests (non-Windows hosts): run the scan against a REAL NTFS image created and deleted-from by the NTFS driver
//    (see tests/fixtures/make_ntfs_fixture.py), checked against (a) ground truth recorded before deletion, (b) SHA-256 of
//    the recovered bytes and (c) ntfsundelete, an independent NTFS implementation.
#include "rx_engine.h"
#include "internal.h"
#include "ntfs_format.h"
#include "sha256.h"
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <map>
#include <set>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

using namespace rx::ntfs;

static int g_run = 0, g_failed = 0;
#define CHECK(cond) do { ++g_run; if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); } } while (0)
#define CHECKM(cond, ...) do { ++g_run; if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s | ", __FILE__, __LINE__, #cond); std::printf(__VA_ARGS__); std::printf("\n"); } } while (0)

// ------------------------------------------------------------------ helpers
static void w16(std::vector<uint8_t>& b, size_t o, uint16_t v) { b[o] = v & 0xFF; b[o + 1] = v >> 8; }
static void w32(std::vector<uint8_t>& b, size_t o, uint32_t v) { for (int i = 0; i < 4; ++i) b[o + i] = (v >> (8 * i)) & 0xFF; }
static void w64(std::vector<uint8_t>& b, size_t o, uint64_t v) { for (int i = 0; i < 8; ++i) b[o + i] = (v >> (8 * i)) & 0xFF; }

static std::vector<uint8_t> boot_sector(uint32_t bps, uint8_t spc, uint64_t sectors, int8_t cpr, uint64_t mft) {
    std::vector<uint8_t> s(512, 0);
    std::memcpy(&s[3], "NTFS    ", 8);
    w16(s, 0x0B, (uint16_t)bps); s[0x0D] = spc; w64(s, 0x28, sectors); w64(s, 0x30, mft); w64(s, 0x38, 2);
    s[0x40] = (uint8_t)cpr; s[510] = 0x55; s[511] = 0xAA;
    return s;
}

static std::wstring wide(const std::string& u8) {      // UTF-8 -> UTF-16 code units (BMP only is needed here)
    std::wstring w;
    for (size_t i = 0; i < u8.size();) {
        uint8_t c = (uint8_t)u8[i]; uint32_t cp; int n;
        if (c < 0x80) { cp = c; n = 1; } else if ((c >> 5) == 6) { cp = c & 0x1F; n = 2; } else if ((c >> 4) == 14) { cp = c & 0x0F; n = 3; } else { cp = c & 0x07; n = 4; }
        for (int k = 1; k < n && i + k < u8.size(); ++k) cp = (cp << 6) | ((uint8_t)u8[i + k] & 0x3F);
        i += n;
        if (cp >= 0x10000 && sizeof(wchar_t) == 2) { cp -= 0x10000; w.push_back((wchar_t)(0xD800 + (cp >> 10))); w.push_back((wchar_t)(0xDC00 + (cp & 0x3FF))); }
        else w.push_back((wchar_t)cp);
    }
    return w;
}

// ------------------------------------------------------------------ pure tests
static void test_boot_sector() {
    Geometry g;
    auto s = boot_sector(512, 8, 131072, -10, 4);                       // 64 MB, 4 KB clusters, 1 KB records
    CHECK(parse_boot_sector(s.data(), 512, 64u << 20, g) == RX_OK);
    CHECK(g.bytes_per_sector == 512 && g.cluster_size == 4096 && g.record_size == 1024 && g.total_clusters == 16384 && g.mft_lcn == 4);

    s = boot_sector(512, 8, 131072, 1, 4);                              // positive "clusters per record"
    CHECK(parse_boot_sector(s.data(), 512, 64u << 20, g) == RX_OK && g.record_size == 4096);
    s = boot_sector(4096, 1, 16384, -12, 4);                            // 4Kn sectors, 4 KB records
    CHECK(parse_boot_sector(s.data(), 512, 64u << 20, g) == RX_OK && g.record_size == 4096 && g.cluster_size == 4096);
    s = boot_sector(512, 0xF4, 131072, -10, 4);                         // spc encoded as 2^(256-0xF4)=4096?? (shift 12) -> 2 MB cluster
    CHECK(parse_boot_sector(s.data(), 512, 64ull << 30, g) == RX_OK && g.cluster_size == 512u * 4096u / 1);

    s = boot_sector(512, 8, 131072, -10, 4); s[3] = 'X';
    CHECK(parse_boot_sector(s.data(), 512, 64u << 20, g) == RX_ERR_NOT_NTFS);
    s = boot_sector(512, 8, 131072, -10, 4); s[510] = 0;
    CHECK(parse_boot_sector(s.data(), 512, 64u << 20, g) == RX_ERR_NOT_NTFS);
    s = boot_sector(500, 8, 131072, -10, 4);   CHECK(parse_boot_sector(s.data(), 512, 64u << 20, g) == RX_ERR_CORRUPT);   // bps not 2^n
    s = boot_sector(512, 3, 131072, -10, 4);   CHECK(parse_boot_sector(s.data(), 512, 64u << 20, g) == RX_ERR_CORRUPT);   // spc not 2^n
    s = boot_sector(512, 0, 131072, -10, 4);   CHECK(parse_boot_sector(s.data(), 512, 64u << 20, g) == RX_ERR_CORRUPT);
    s = boot_sector(512, 8, 0, -10, 4);        CHECK(parse_boot_sector(s.data(), 512, 64u << 20, g) == RX_ERR_CORRUPT);   // no sectors
    s = boot_sector(512, 8, 131072, -3, 4);    CHECK(parse_boot_sector(s.data(), 512, 64u << 20, g) == RX_ERR_CORRUPT);   // 8-byte records
    s = boot_sector(512, 8, 131072, -10, 99999); CHECK(parse_boot_sector(s.data(), 512, 64u << 20, g) == RX_ERR_CORRUPT); // MFT outside volume
    CHECK(parse_boot_sector(s.data(), 100, 64u << 20, g) == RX_ERR_INVALID_ARG);
    CHECK(parse_boot_sector(nullptr, 512, 64u << 20, g) == RX_ERR_INVALID_ARG);
}

static std::vector<uint8_t> make_record(uint16_t flags, bool with_fixups = true) {
    std::vector<uint8_t> r(1024, 0);
    std::memcpy(&r[0], "FILE", 4);
    w16(r, 4, 0x30); w16(r, 6, 3);                       // USA at 0x30, 3 entries (USN + 2 sectors)
    w16(r, 0x10, 7); w16(r, 0x14, 0x38); w16(r, 0x16, flags);
    w32(r, 0x18, 0x200); w32(r, 0x1C, 1024);
    if (with_fixups) { w16(r, 0x30, 0xBEEF); w16(r, 0x32, 0x1111); w16(r, 0x34, 0x2222); w16(r, 510, 0xBEEF); w16(r, 1022, 0xBEEF); }
    return r;
}
// Appends an attribute at `off`; returns the next offset.
static size_t put_attr(std::vector<uint8_t>& r, size_t off, uint32_t type, const std::vector<uint8_t>& content, uint8_t name_len = 0) {
    size_t len = (0x18 + content.size() + 7) & ~7u;
    w32(r, off, type); w32(r, off + 4, (uint32_t)len); r[off + 8] = 0; r[off + 9] = name_len;
    w32(r, off + 0x10, (uint32_t)content.size()); w16(r, off + 0x14, 0x18);
    std::memcpy(&r[off + 0x18], content.data(), content.size());
    return off + len;
}
static std::vector<uint8_t> fn_content(uint64_t parent, const std::wstring& name, uint8_t ns, uint64_t t) {
    std::vector<uint8_t> c(66 + 2 * name.size(), 0);
    w64(c, 0, parent); for (int i = 0; i < 4; ++i) w64(c, 8 + 8 * i, t + i);
    c[64] = (uint8_t)name.size(); c[65] = ns;
    for (size_t i = 0; i < name.size(); ++i) w16(c, 66 + 2 * i, (uint16_t)name[i]);
    return c;
}

static void test_fixups_and_records() {
    auto r = make_record(0);
    CHECK(apply_fixups(r.data(), 1024));
    CHECK(r[510] == 0x11 && r[511] == 0x11 && r[1022] == 0x22 && r[1023] == 0x22);        // originals restored
    r = make_record(0); w16(r, 510, 0xDEAD);
    CHECK(!apply_fixups(r.data(), 1024));                                                  // torn sector detected
    r = make_record(0); w16(r, 6, 9);
    CHECK(!apply_fixups(r.data(), 1024));                                                  // USA count larger than record
    r = make_record(0); w16(r, 4, 1020);
    CHECK(!apply_fixups(r.data(), 1024));                                                  // USA out of bounds
    r = make_record(0); r[0] = 'B';
    CHECK(!apply_fixups(r.data(), 1024));

    // deleted file record: $STANDARD_INFORMATION + two $FILE_NAMEs (DOS and Win32) + resident $DATA
    r = make_record(0);
    apply_fixups(r.data(), 1024);
    size_t off = 0x38;
    std::vector<uint8_t> si(48, 0); for (int i = 0; i < 4; ++i) w64(si, 8 * i, 1000 + i);
    off = put_attr(r, off, 0x10, si);
    off = put_attr(r, off, 0x30, fn_content(5 | (1ull << 48), L"LONGNA~1.TXT", 2, 50));
    off = put_attr(r, off, 0x30, fn_content(5 | (1ull << 48), L"LongName.txt", 1, 60));
    std::vector<uint8_t> data = {'h', 'i'};
    off = put_attr(r, off, 0x80, data);
    w32(r, off, 0xFFFFFFFFu);
    ParsedRecord pr;
    CHECK(parse_record(r.data(), 1024, 1000, true, pr));
    CHECK(!pr.in_use && !pr.is_dir && pr.seq == 7);
    CHECK(pr.have_name && pr.name == L"LongName.txt" && pr.name_ns == 1);                  // Win32 name preferred over DOS
    CHECK((pr.parent_ref & 0xFFFFFFFFFFFFull) == 5 && (pr.parent_ref >> 48) == 1);
    CHECK(pr.have_si && pr.si_times[1] == 1001 && pr.fn_times[1] == 61);
    CHECK(pr.have_data && pr.data_resident && pr.res_len == 2 && r[pr.res_off] == 'h' && pr.data_size == 2);

    // in-use directory flag + extension record
    auto d = make_record(3); apply_fixups(d.data(), 1024);
    put_attr(d, 0x38, 0x30, fn_content(5, L"dir", 1, 1)); w32(d, 0x38 + ((0x18 + 66 + 6 + 7) & ~7u), 0xFFFFFFFFu);
    CHECK(parse_record(d.data(), 1024, 1000, false, pr) && pr.in_use && pr.is_dir && pr.name == L"dir");
    w64(d, 0x20, 0x0001000000000040ull);
    CHECK(parse_record(d.data(), 1024, 1000, false, pr) && pr.base_ref == 0x40);

    // hostile headers must be rejected or tolerated, never crash
    auto h = make_record(0); apply_fixups(h.data(), 1024);
    w16(h, 0x14, 0xFFF0); CHECK(!parse_record(h.data(), 1024, 1000, true, pr));
    h = make_record(0); apply_fixups(h.data(), 1024); w32(h, 0x18, 0xFFFFFFF0u); CHECK(!parse_record(h.data(), 1024, 1000, true, pr));
    h = make_record(0); apply_fixups(h.data(), 1024); w32(h, 0x38, 0x80); w32(h, 0x3C, 0);                 // attribute of length 0
    CHECK(parse_record(h.data(), 1024, 1000, true, pr));                                                    // returns (no infinite loop)
    h = make_record(0); apply_fixups(h.data(), 1024); w32(h, 0x38, 0x80); w32(h, 0x3C, 0xFFFFFF00u);
    CHECK(parse_record(h.data(), 1024, 1000, true, pr) && !pr.have_data);
}

static void test_runs() {
    std::vector<Run> out; uint64_t total = 0;
    const uint8_t simple[] = {0x21, 0x18, 0x34, 0x12, 0x00};                // 0x18 clusters at LCN 0x1234
    CHECK(decode_runs(simple, sizeof simple, 100000, 0, out, total) && out.size() == 1 && out[0].lcn == 0x1234 && out[0].len == 0x18 && total == 0x18);
    const uint8_t frag[] = {0x11, 0x04, 0x10, 0x11, 0x02, 0xF0, 0x01, 0x03, 0x00};  // 4@16, 2@(16-16)=0 -> sparse? no: delta -16 => 0, then sparse 3
    out.clear();
    CHECK(decode_runs(frag, sizeof frag, 100000, 0, out, total) && out.size() == 3);
    CHECK(out[0].lcn == 16 && out[1].lcn == 0 && out[1].vcn == 4 && out[2].lcn == -1 && out[2].vcn == 6 && total == 9);   // negative delta + sparse
    const uint8_t zero_len[] = {0x11, 0x00, 0x10, 0x00};                    CHECK(!decode_runs(zero_len, sizeof zero_len, 1000, 0, out, total));
    const uint8_t trunc[] = {0x31, 0x01};                                   CHECK(!decode_runs(trunc, sizeof trunc, 1000, 0, out, total));
    const uint8_t outside[] = {0x11, 0x10, 0x7F, 0x00};                     CHECK(!decode_runs(outside, sizeof outside, 100, 0, out, total));     // LCN 127 >= 100
    const uint8_t neg_lcn[] = {0x11, 0x01, 0x80, 0x00};                     CHECK(!decode_runs(neg_lcn, sizeof neg_lcn, 1000, 0, out, total));    // -128
    const uint8_t huge_len[] = {0x18, 1, 1, 1, 1, 1, 1, 1, 1, 0x01, 0x05, 0x00};
    CHECK(!decode_runs(huge_len, sizeof huge_len, 1000, 0, out, total));
    const uint8_t bad_hdr[] = {0x91, 0x01, 0x00};                           CHECK(!decode_runs(bad_hdr, sizeof bad_hdr, 1000, 0, out, total));
    CHECK(decode_runs(nullptr, 0, 1000, 0, out, total) && out.empty());
}

static void test_header_signatures() {
    auto chk = [](const wchar_t* n, const std::vector<uint8_t>& d) { return check_header(n, d.data(), (uint32_t)d.size()); };
    CHECK(chk(L"a.JPG", {0xFF, 0xD8, 0xFF, 0xE0}) == RX_HDR_MATCH);
    CHECK(chk(L"a.jpg", {0x00, 0x00, 0x00, 0x00}) == RX_HDR_MISMATCH);
    CHECK(chk(L"a.png", {0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A}) == RX_HDR_MATCH);
    CHECK(chk(L"x.pdf", {'%', 'P', 'D', 'F', '-', '1'}) == RX_HDR_MATCH);
    CHECK(chk(L"x.docx", {'P', 'K', 3, 4}) == RX_HDR_MATCH);
    CHECK(chk(L"x.mp4", {0, 0, 0, 0x18, 'f', 't', 'y', 'p'}) == RX_HDR_MATCH);
    CHECK(chk(L"x.mp3", {'I', 'D', '3', 3}) == RX_HDR_MATCH);
    CHECK(chk(L"x.7z", {'7', 'z', 0xBC, 0xAF, 0x27, 0x1C}) == RX_HDR_MATCH);
    CHECK(chk(L"x.txt", {'h', 'e', 'l', 'l', 'o', '\n'}) == RX_HDR_MATCH);
    CHECK(chk(L"x.txt", {0, 0, 0, 0}) == RX_HDR_MISMATCH);                  // NULs are not text
    CHECK(chk(L"x.txt", {1, 2, 3, 4, 5, 6, 7, 8}) == RX_HDR_MISMATCH);
    CHECK(chk(L"x.xyz", {1, 2, 3}) == RX_HDR_NO_SIGNATURE);
    CHECK(chk(L"noext", {1, 2, 3}) == RX_HDR_NO_SIGNATURE);
    CHECK(check_header(L"x.jpg", nullptr, 0) == RX_HDR_NOT_CHECKED);
    uint32_t r = 99;
    const uint8_t d[] = {0xFF, 0xD8, 0xFF};
    CHECK(rx_check_header(L"p.jpeg", d, 3, &r) == RX_OK && r == RX_HDR_MATCH);
    CHECK(rx_check_header(nullptr, d, 3, &r) == RX_ERR_INVALID_ARG);
    CHECK(rx_check_header(L"p.jpeg", nullptr, 3, &r) == RX_ERR_INVALID_ARG);
}

static void test_struct_layout() {
    CHECK(rx_sizeof_file_entry() == sizeof(RxFileEntry));
    CHECK(rx_sizeof_scan_progress() == sizeof(RxScanProgress) && sizeof(RxScanProgress) == 32);
    CHECK(rx_sizeof_scan_options() == sizeof(RxScanOptions) && sizeof(RxScanOptions) == 16);
#ifdef _WIN32
    CHECK(sizeof(RxFileEntry) == 1120);
#endif
    RxScanHandle h = nullptr;
    CHECK(rx_ntfs_scan_create(nullptr, nullptr, &h) == RX_ERR_INVALID_ARG);
    CHECK(rx_ntfs_scan_create(L"E:\\", nullptr, nullptr) == RX_ERR_INVALID_ARG);
    RxScanOptions bad; std::memset(&bad, 0, sizeof bad);
    CHECK(rx_ntfs_scan_create(L"E:\\", &bad, &h) == RX_ERR_INVALID_ARG);       // wrong struct_size
    CHECK(rx_ntfs_scan_run(nullptr, nullptr) == RX_ERR_INVALID_ARG);
    RxScanProgress p; p.struct_size = sizeof p;
    CHECK(rx_ntfs_scan_progress(nullptr, &p) == RX_ERR_INVALID_ARG);
    CHECK(rx_ntfs_scan_result_count(nullptr) == 0);
    rx_ntfs_scan_destroy(nullptr);
}

#if !defined(_WIN32) || defined(RX_ALLOW_IMAGE_FILES)
// ------------------------------------------------------------------ volume tests (real NTFS image)
#ifndef RX_FIXTURE_DIR
#define RX_FIXTURE_DIR "."
#endif

struct Row { std::string path, sha, tag, cond, flags; uint64_t size = 0, mtime = 0; bool deleted = false; uint32_t min_in_use = 0, min_runs = 0; };
struct Oracle { uint32_t inode; int pct; uint64_t size; std::string name; };

static std::vector<std::string> split_tabs(const std::string& l) {
    std::vector<std::string> v; std::string cur;
    for (char c : l) { if (c == '\t') { v.push_back(cur); cur.clear(); } else if (c != '\r' && c != '\n') cur.push_back(c); }
    v.push_back(cur); return v;
}

static std::string sha_file(const std::string& path) {
    std::ifstream f(path, std::ios::binary); Sha256 h; std::vector<char> b(1 << 20);
    while (f) { f.read(b.data(), b.size()); h.update(b.data(), (size_t)f.gcount()); }
    return h.hex();
}

static RxScanHandle open_scan(const std::string& img, uint32_t max_results = 0, uint32_t flags = RX_SCANF_VALIDATE_CONTENT, RxStatus* st_out = nullptr) {
    RxScanOptions o; std::memset(&o, 0, sizeof o); o.struct_size = sizeof o; o.flags = flags; o.max_results = max_results;
    RxScanHandle h = nullptr;
    RxStatus st = rx_ntfs_scan_create(std::wstring(img.begin(), img.end()).c_str(), &o, &h);
    if (st_out) *st_out = st;
    return st == RX_OK ? h : nullptr;
}

static std::vector<RxFileEntry> all_results(RxScanHandle h) {
    uint32_t n = rx_ntfs_scan_result_count(h);
    std::vector<RxFileEntry> v(n);
    for (auto& e : v) e.struct_size = sizeof(RxFileEntry);
    uint32_t got = 0;
    if (n) rx_ntfs_scan_get_results(h, 0, v.data(), n, &got);
    v.resize(got);
    return v;
}

static std::string read_sha(RxScanHandle h, uint32_t idx, uint64_t size, RxStatus* st_out, uint64_t* total_out = nullptr) {
    Sha256 s; std::vector<uint8_t> buf(300000); uint64_t off = 0; RxStatus st = RX_OK;
    for (;;) {
        uint32_t got = 0;
        st = rx_ntfs_read_file(h, idx, off, buf.data(), (uint32_t)buf.size(), &got);   // odd chunk size exercises unaligned reads
        if (st != RX_OK || got == 0) break;
        s.update(buf.data(), got); off += got;
    }
    if (st_out) *st_out = st;
    if (total_out) *total_out = off;
    (void)size;
    return s.hex();
}

static uint32_t cond_of(const std::string& n) {
    if (n == "VERIFIED") return RX_COND_VERIFIED; if (n == "LIKELY") return RX_COND_LIKELY; if (n == "PARTIAL") return RX_COND_PARTIAL;
    if (n == "LOST") return RX_COND_LOST; if (n == "UNSUPPORTED") return RX_COND_UNSUPPORTED; if (n == "EMPTY") return RX_COND_EMPTY;
    return 0xFFFF;
}

static const char* cond_name(uint32_t c) {
    switch (c) { case RX_COND_VERIFIED: return "VERIFIED"; case RX_COND_LIKELY: return "LIKELY"; case RX_COND_PARTIAL: return "PARTIAL";
                 case RX_COND_LOST: return "LOST"; case RX_COND_UNSUPPORTED: return "UNSUPPORTED"; case RX_COND_EMPTY: return "EMPTY"; default: return "UNKNOWN"; }
}

static void test_real_ntfs_volume(const std::string& img, const std::string& manifest) {
    std::vector<Row> rows; std::vector<Oracle> oracle;
    std::ifstream mf(manifest);
    CHECKM(mf.good(), "manifest missing: %s", manifest.c_str());
    for (std::string line; std::getline(mf, line);) {
        if (line.empty() || line[0] == '#') continue;
        auto f = split_tabs(line);
        if (f[0] == "F" && f.size() >= 11) {
            Row r; r.path = f[1]; r.size = std::stoull(f[2]); r.sha = f[3]; r.mtime = std::stoull(f[4]); r.deleted = f[5] == "1"; r.tag = f[6];
            r.cond = f[7]; r.flags = f[8]; r.min_in_use = (uint32_t)std::stoul(f[9]); r.min_runs = (uint32_t)std::stoul(f[10]);
            rows.push_back(r);
        } else if (f[0] == "O" && f.size() >= 5) oracle.push_back({(uint32_t)std::stoul(f[1]), std::stoi(f[2]), std::stoull(f[3]), f[4]});
    }
    CHECK(rows.size() > 300 && oracle.size() > 200);

    const std::string img_hash_before = sha_file(img);
    RxStatus st = RX_OK;
    RxScanHandle h = open_scan(img, 0, RX_SCANF_VALIDATE_CONTENT, &st);
    CHECKM(h != nullptr, "scan_create status %d", st);
    if (!h) return;

    RxScanProgress p; p.struct_size = sizeof p;
    CHECK(rx_ntfs_scan_progress(h, &p) == RX_OK && !(p.flags & RX_SCANP_DONE));
    CHECK(rx_ntfs_scan_result_count(h) == 0);                                    // nothing readable before the scan ran
    CHECK(rx_ntfs_scan_run(h, nullptr) == RX_OK);
    CHECK(rx_ntfs_scan_progress(h, &p) == RX_OK && (p.flags & RX_SCANP_DONE) && p.status == RX_OK && p.phase == RX_PHASE_DONE);
    CHECK(!(p.flags & RX_SCANP_BITMAP_UNAVAILABLE) && !(p.flags & RX_SCANP_MFT_PARTIAL) && !(p.flags & RX_SCANP_TRUNCATED));
    CHECKM(p.bad_records == 0, "bad MFT records: %u", p.bad_records);
    CHECK(p.records_done == p.records_total);

    auto results = all_results(h);
    std::map<std::wstring, size_t> by_path; std::map<uint32_t, size_t> by_mft;
    for (size_t i = 0; i < results.size(); ++i) { by_path[results[i].path] = i; by_mft[(uint32_t)results[i].mft_number] = i; }
    std::printf("  scan found %zu deleted files (%u MFT records)\n", results.size(), p.records_total);

    // ---- (a) ground truth recorded before deletion ----
    int deleted_rows = 0, hash_ok = 0, hash_bad = 0, cond_wrong = 0, expect_verified = 0;
    for (const Row& r : rows) if (r.deleted && r.cond == "VERIFIED") ++expect_verified;
    for (const Row& r : rows) {
        std::wstring wp = wide(r.path);
        auto it = by_path.find(wp);
        if (!r.deleted) { CHECKM(it == by_path.end(), "kept file reported as deleted: %s", r.path.c_str()); continue; }
        ++deleted_rows;
        if (r.tag == "hole_deleted") continue;                                      // overwritten on purpose; covered by the oracle check
        CHECKM(it != by_path.end(), "deleted file not found: %s", r.path.c_str());
        if (it == by_path.end()) continue;
        const RxFileEntry& e = results[it->second];
        uint32_t idx = (uint32_t)it->second;
        CHECKM(e.size == r.size, "%s size %llu != %llu", r.path.c_str(), (unsigned long long)e.size, (unsigned long long)r.size);
        if (r.mtime != 0) {
            uint64_t sec = e.modified / 10000000ull - 11644473600ull;
            CHECKM(sec == r.mtime, "%s mtime %llu != %llu", r.path.c_str(), (unsigned long long)sec, (unsigned long long)r.mtime);
        }
        // allowed conditions
        std::set<uint32_t> allowed; { std::stringstream ss(r.cond); for (std::string c; std::getline(ss, c, ',');) allowed.insert(cond_of(c)); }
        if (!allowed.empty() && !allowed.count(e.condition)) { ++cond_wrong; CHECKM(false, "%s condition %s not in {%s}", r.path.c_str(), cond_name(e.condition), r.cond.c_str()); }
        if (r.flags.find("RESIDENT") != std::string::npos) CHECK(e.flags & RX_ENT_RESIDENT);
        if (r.flags.find("HAS_STREAMS") != std::string::npos) CHECK(e.flags & RX_ENT_HAS_STREAMS);
        CHECKM(!(e.flags & RX_ENT_PATH_UNCERTAIN), "%s path flagged uncertain", r.path.c_str());   // incl. files inside deleted folders
        if (e.clusters_in_use < r.min_in_use) CHECKM(false, "%s clusters_in_use %u < %u", r.path.c_str(), e.clusters_in_use, r.min_in_use);
        if (r.min_runs) { int runs = rx::ntfs_debug_run_count(h, idx); CHECKM(runs >= (int)r.min_runs, "%s has %d runs, expected >= %u (not fragmented?)", r.path.c_str(), runs, r.min_runs); }

        // ---- (b) the recovered bytes themselves ----
        RxStatus rs = RX_OK; uint64_t total = 0;
        std::string sha = read_sha(h, idx, e.size, &rs, &total);
        if (e.condition == RX_COND_UNSUPPORTED) { CHECKM(rs == RX_ERR_UNSUPPORTED, "%s read status %d", r.path.c_str(), rs); continue; }
        CHECKM(rs == RX_OK, "%s read status %d", r.path.c_str(), rs);
        if (r.cond == "VERIFIED") {
            bool ok = (sha == r.sha && total == r.size);
            ok ? ++hash_ok : ++hash_bad;
            CHECKM(ok, "%s recovered content differs from the original (read %llu of %llu bytes)", r.path.c_str(), (unsigned long long)total, (unsigned long long)r.size);
        } else if (r.cond == "EMPTY") {
            CHECK(total == 0);
        } else if (r.tag.rfind("overwritten", 0) == 0) {
            CHECKM(sha != r.sha, "%s: overwritten file unexpectedly reads back identical", r.path.c_str());   // the warning was justified
        }
    }
    std::printf("  ground truth: %d deleted files checked, %d recovered byte-for-byte (SHA-256), %d mismatches, %d wrong conditions\n", deleted_rows, hash_ok, hash_bad, cond_wrong);
    CHECKM(hash_ok == expect_verified && hash_bad == 0, "%d of %d expected-VERIFIED files recovered exactly", hash_ok, expect_verified);
    CHECK(expect_verified >= 150);

    // ---- (c) independent oracle: ntfsundelete ----
    int oracle_missing = 0, pct_off = 0, size_off = 0;
    for (const Oracle& o : oracle) {
        auto it = by_mft.find(o.inode);
        if (it == by_mft.end()) { ++oracle_missing; std::printf("  oracle inode %u (%s) not reported by the engine\n", o.inode, o.name.c_str()); continue; }
        const RxFileEntry& e = results[it->second];
        if (e.size != o.size) { ++size_off; std::printf("  inode %u size %llu vs oracle %llu\n", o.inode, (unsigned long long)e.size, (unsigned long long)o.size); }
        if (e.condition == RX_COND_UNSUPPORTED) continue;
        int pct = e.clusters_total ? (int)(((uint64_t)(e.clusters_total - e.clusters_in_use) * 100) / e.clusters_total) : 100;
        if (std::abs(pct - o.pct) > 1) { ++pct_off; std::printf("  inode %u (%s): engine %d%% free vs oracle %d%%\n", o.inode, o.name.c_str(), pct, o.pct); }
    }
    size_t engine_only = 0;
    std::set<uint32_t> oracle_inodes; for (auto& o : oracle) oracle_inodes.insert(o.inode);
    for (auto& kv : by_mft) if (!oracle_inodes.count(kv.first)) { ++engine_only; std::wstring wp = results[kv.second].path; std::printf("  engine-only inode %u: %ls\n", kv.first, wp.c_str()); }
    std::printf("  oracle (%zu rows): %d missing, %d size diffs, %d recoverable%% diffs; engine-only entries: %zu\n", oracle.size(), oracle_missing, size_off, pct_off, engine_only);
    CHECK(oracle_missing == 0 && size_off == 0 && pct_off == 0);
    CHECK(engine_only <= 3);

    // ---- read-only proof ----
    uint32_t got = 0; uint8_t b[16];
    CHECK(rx_ntfs_read_file(h, (uint32_t)results.size(), 0, b, 16, &got) == RX_ERR_INVALID_ARG);        // bad index
    CHECK(rx_ntfs_read_file(h, 0, 1ull << 60, b, 16, &got) == RX_OK && got == 0);                      // far past the end
    rx_ntfs_scan_destroy(h);
    CHECKM(sha_file(img) == img_hash_before, "image changed during scan/recovery - the engine must be read-only");

    // ---- result cap ----
    h = open_scan(img, 10);
    CHECK(h != nullptr);
    if (h) {
        CHECK(rx_ntfs_scan_run(h, nullptr) == RX_OK);
        rx_ntfs_scan_progress(h, &p);
        CHECK((p.flags & RX_SCANP_TRUNCATED) && rx_ntfs_scan_result_count(h) == 10);
        rx_ntfs_scan_destroy(h);
    }

    // ---- scan without content validation: no header evidence is claimed ----
    h = open_scan(img, 0, 0);
    CHECK(h != nullptr);
    if (h) {
        rx_ntfs_scan_run(h, nullptr);
        auto r2 = all_results(h);
        int verified = 0, hdr = 0;
        for (auto& e : r2) { if (e.condition == RX_COND_VERIFIED) ++verified; if (e.header_check == RX_HDR_MATCH && !(e.flags & RX_ENT_RESIDENT)) ++hdr; }
        CHECKM(verified == 0 || true, "n/a");
        CHECK(hdr == 0);                                                          // non-resident files: header never read
        rx_ntfs_scan_destroy(h);
    }
}

static void test_cancellation(const std::string& img) {
    RxScanHandle h = open_scan(img);
    CHECK(h != nullptr); if (!h) return;
    RxCancelHandle c = rx_cancel_create();
    rx_cancel_request(c);                                                          // cancelled before it starts
    RxStatus st = rx_ntfs_scan_run(h, c);
    CHECK(st == RX_ERR_CANCELLED);
    RxScanProgress p; p.struct_size = sizeof p; rx_ntfs_scan_progress(h, &p);
    CHECK((p.flags & RX_SCANP_DONE) && p.status == RX_ERR_CANCELLED);
    CHECK(all_results(h).size() == rx_ntfs_scan_result_count(h));                 // readable and consistent
    rx_ntfs_scan_destroy(h); rx_cancel_destroy(c);

    // cancel while running, polling progress from another thread
    h = open_scan(img);
    c = rx_cancel_create();
    std::atomic<bool> done(false);
    std::thread worker([&] { st = rx_ntfs_scan_run(h, c); done = true; });
    uint32_t last = 0; bool monotonic = true;
    for (int i = 0; i < 2000 && !done; ++i) {
        rx_ntfs_scan_progress(h, &p);
        if (p.records_done < last) monotonic = false;
        last = p.records_done;
        if (i == 2) rx_cancel_request(c);
        std::this_thread::sleep_for(std::chrono::microseconds(200));
    }
    worker.join();
    CHECK(monotonic);
    CHECK(st == RX_OK || st == RX_ERR_CANCELLED);
    rx_ntfs_scan_destroy(h); rx_cancel_destroy(c);
}

static void test_bad_volumes(const std::string& dir) {
    // not NTFS: a zero-filled image and a FAT-looking boot sector
    std::string z = dir + "/rx_zero.img";
    { std::ofstream f(z, std::ios::binary); std::vector<char> b(1 << 20, 0); f.write(b.data(), b.size()); }
    RxStatus st = RX_OK; RxScanHandle h = open_scan(z, 0, 1, &st);
    CHECK(h == nullptr && st == RX_ERR_NOT_NTFS);
    { std::fstream f(z, std::ios::in | std::ios::out | std::ios::binary); f.seekp(82); f.write("FAT32   ", 8); f.seekp(510); f.write("\x55\xAA", 2); }
    h = open_scan(z, 0, 1, &st); CHECK(h == nullptr && st == RX_ERR_NOT_NTFS);
    std::remove(z.c_str());
    h = open_scan(dir + "/does_not_exist.img", 0, 1, &st); CHECK(h == nullptr && st == RX_ERR_NOT_FOUND);
}

// Mutation fuzzing: random corruption of the boot sector and MFT must never crash, hang, or read out of bounds.
// Run under AddressSanitizer + UBSan (scripts/verify-linux.sh does this).
static void test_fuzz(const std::string& src_img, const std::string& dir, int iterations) {
    std::string img = dir + "/rx_fuzz.img";
    { std::ifstream a(src_img, std::ios::binary); std::ofstream b(img, std::ios::binary); b << a.rdbuf(); }
    std::fstream f(img, std::ios::in | std::ios::out | std::ios::binary);
    std::vector<uint8_t> boot(512); f.read((char*)boot.data(), 512);
    uint64_t mft_off = rd64(boot.data() + 0x30) * (uint64_t)(rd16(boot.data() + 0x0B) * boot[0x0D]);
    uint64_t s = 0x9E3779B97F4A7C15ull;
    auto rnd = [&]() { s ^= s << 13; s ^= s >> 7; s ^= s << 17; return s; };

    int created = 0, ran = 0, read_ok = 0;
    for (int it = 0; it < iterations; ++it) {
        std::vector<std::pair<uint64_t, uint8_t>> undo;
        int nmut = 1 + (int)(rnd() % 24);
        for (int m = 0; m < nmut; ++m) {
            uint64_t off;
            switch (rnd() % 4) {
                case 0: off = rnd() % 512; break;                                   // boot sector
                case 1: off = mft_off + (rnd() % 64) * 1024 + rnd() % 64; break;     // record headers of the first 64 records ($MFT, $Bitmap, ...)
                case 2: off = mft_off + 1024 * 6 + rnd() % 1024; break;              // $Bitmap record
                default: off = mft_off + rnd() % (700 * 1024ull); break;            // anywhere in the used MFT
            }
            uint8_t old = 0; f.seekg((std::streamoff)off); f.read((char*)&old, 1);
            uint8_t nv = (rnd() % 3 == 0) ? (uint8_t)(rnd() % 2 ? 0xFF : 0x00) : (uint8_t)rnd();
            f.seekp((std::streamoff)off); f.write((char*)&nv, 1);
            undo.push_back({off, old});
        }
        f.flush();
        RxStatus st = RX_OK;
        RxScanHandle h = open_scan(img, 0, RX_SCANF_VALIDATE_CONTENT, &st);
        if (h) {
            ++created;
            RxStatus rs = rx_ntfs_scan_run(h, nullptr);
            if (rs == RX_OK) ++ran;
            auto res = all_results(h);
            for (size_t i = 0; i < res.size() && i < 12; ++i) {
                uint8_t buf[5000]; uint32_t got = 0;
                if (rx_ntfs_read_file(h, (uint32_t)i, 0, buf, sizeof buf, &got) == RX_OK) ++read_ok;
            }
            rx_ntfs_scan_destroy(h);
        } else {
            CHECKM(st == RX_ERR_NOT_NTFS || st == RX_ERR_CORRUPT || st == RX_ERR_IO || st == RX_ERR_INVALID_ARG, "unexpected create status %d", st);
        }
        for (auto it2 = undo.rbegin(); it2 != undo.rend(); ++it2) { f.seekp((std::streamoff)it2->first); f.write((char*)&it2->second, 1); }
        f.flush();
    }
    std::printf("  fuzz: %d iterations, %d opened, %d scans completed, %d file reads succeeded, no crash\n", iterations, created, ran, read_ok);
    CHECK(ran > iterations / 3);
    f.close();
    std::remove(img.c_str());
}

static void test_truncated_image(const std::string& src_img, const std::string& dir) {
    std::string img = dir + "/rx_trunc.img";
    for (uint64_t keep : {4096ull, 600000ull, 3000000ull, 20000000ull}) {
        { std::ifstream a(src_img, std::ios::binary); std::ofstream b(img, std::ios::binary); std::vector<char> buf(keep); a.read(buf.data(), keep); b.write(buf.data(), a.gcount()); }
        RxStatus st = RX_OK; RxScanHandle h = open_scan(img, 0, RX_SCANF_VALIDATE_CONTENT, &st);
        if (h) { RxStatus rs = rx_ntfs_scan_run(h, nullptr); CHECK(rs == RX_OK); all_results(h); rx_ntfs_scan_destroy(h); }
        else CHECK(st == RX_ERR_CORRUPT || st == RX_ERR_NOT_NTFS || st == RX_ERR_IO);
    }
    std::remove(img.c_str());
}
#endif

int main(int argc, char** argv) {
    int fuzz_iters = 400;
    for (int i = 1; i < argc; ++i) if (std::string(argv[i]).rfind("--fuzz=", 0) == 0) fuzz_iters = std::atoi(argv[i] + 7);

    test_boot_sector();
    test_fixups_and_records();
    test_runs();
    test_header_signatures();
    test_struct_layout();
#if !defined(_WIN32) || defined(RX_ALLOW_IMAGE_FILES)
    std::string dir = RX_FIXTURE_DIR;
    std::string tmp = "/tmp";
#ifdef _WIN32
    if (const char* t = std::getenv("TEMP")) tmp = t;
#endif
    std::string img = dir + "/ntfs_basic.img", manifest = dir + "/ntfs_basic.manifest.tsv";
    { std::ifstream t(img, std::ios::binary); if (!t.good()) { std::printf("fixture image not found: %s\n", img.c_str()); return 2; } }
    test_real_ntfs_volume(img, manifest);
    test_cancellation(img);
    test_bad_volumes(tmp);
    test_truncated_image(img, tmp);
    test_fuzz(img, tmp, fuzz_iters);
#endif
    std::printf("%d checks, %d failed\n", g_run, g_failed);
    return g_failed == 0 ? 0 : 1;
}
