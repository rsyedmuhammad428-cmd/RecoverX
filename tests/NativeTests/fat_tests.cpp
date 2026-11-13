// FAT12/16/32 and exFAT Quick Scan tests.
//  - Pure format tests (boot sectors, long-name checksum, exFAT set checksum, timestamps).
//  - Ground truth: fat32_basic.img and exfat_basic.img, created and deleted-from by the Linux kernel's FAT/exFAT drivers
//    (tests/fixtures/make_fat_fixtures.sh). Every deleted file must be found by name and path with the expected condition, and
//    every file expected intact must read back with the SHA-256 recorded before deletion. Kept files must not be reported.
//  - The engine never modifies an image; Deep Scan uses the FAT/exFAT free-space maps; fuzzing, truncation, cancellation.
#include "rx_engine.h"
#include "internal.h"
#include "fat_format.h"
#include "sha256.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iterator>
#include <map>
#include <string>
#include <vector>

using namespace rx::fat;

static int g_run = 0, g_failed = 0;
#define CHECK(cond) do { ++g_run; if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); } } while (0)
#define CHECKM(cond, ...) do { ++g_run; if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s | ", __FILE__, __LINE__, #cond); std::printf(__VA_ARGS__); std::printf("\n"); } } while (0)

#ifndef RX_FIXTURE_DIR
#define RX_FIXTURE_DIR "."
#endif

static const char* cond_name(uint32_t c) {
    static const char* n[] = { "UNKNOWN", "VERIFIED", "LIKELY", "PARTIAL", "LOST", "UNSUPPORTED", "EMPTY" };
    return c < 7 ? n[c] : "?";
}
static std::wstring wide(const std::string& u8) {
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
static std::string narrow(const std::wstring& w) { std::string s; for (wchar_t c : w) s.push_back(c < 128 ? (char)c : '?'); return s; }
static std::wstring lower(std::wstring s) { for (auto& c : s) if (c >= L'A' && c <= L'Z') c = (wchar_t)(c - L'A' + L'a'); return s; }
static std::vector<uint8_t> read_all(const std::string& p) {
    std::ifstream f(p, std::ios::binary);
    return std::vector<uint8_t>((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
}
static std::string sha_of(const std::vector<uint8_t>& d) { Sha256 h; h.update(d.data(), d.size()); return h.hex(); }

// ------------------------------------------------------------------ pure
static void test_pure(const std::string& dir) {
    // Checksum of the 8.3 name "UPPER   TXT" computed independently (fatgen103 algorithm, by hand in Python).
    const uint8_t sfn[11] = { 'U','P','P','E','R',' ',' ',' ','T','X','T' };
    uint8_t sum = 0; for (int i = 0; i < 11; ++i) sum = (uint8_t)(((sum & 1) << 7) + (sum >> 1) + sfn[i]);
    CHECK(lfn_checksum(sfn) == sum);
    CHECK(dos_to_filetime(0, 0, 0) == 0);
    // 2024-05-17 14:30:00 local: date = (44<<9)|(5<<5)|17, time = (14<<11)|(30<<5)
    uint64_t ft = dos_to_filetime((uint16_t)((44 << 9) | (5 << 5) | 17), (uint16_t)((14 << 11) | (30 << 5)), 0);
    CHECK(ft == (1715956200ull + 11644473600ull) * 10000000ull);
    CHECK(dos_to_filetime((uint16_t)((44 << 9) | (13 << 5) | 17), 0, 0) == 0);        // month 13
    bool local = false;
    uint32_t ts = ((uint32_t)((44 << 9) | (5 << 5) | 17) << 16) | (uint32_t)((14 << 11) | (30 << 5));
    CHECK(exfat_to_filetime(ts, 0, 0x80 | 8, local) == ft - 2ull * 3600 * 10000000ull && !local);   // UTC+02:00
    CHECK(exfat_to_filetime(ts, 0, 0, local) == ft && local);
    bool guessed = false;
    uint8_t e[32]; std::memset(e, ' ', 11); std::memcpy(e, "\xE5PPER   TXT", 11); e[12] = 0;
    CHECK(short_name(e, true, guessed) == L"_PPER.TXT" && guessed);
    std::memcpy(e, "README  MD ", 11); e[12] = 0x18;
    CHECK(short_name(e, false, guessed) == L"readme.md" && !guessed);

    Volume v;
    auto f32 = read_all(dir + "/fat32_basic.img"), ex = read_all(dir + "/exfat_basic.img");
    CHECK(f32.size() > 1000000 && ex.size() > 1000000);
    if (f32.size() < 512 || ex.size() < 512) return;
    CHECK(parse_boot(f32.data(), 512, f32.size(), v) == RX_OK && v.kind == KIND_FAT32 && v.cluster_size == 4096 && v.root_cluster == 2);
    CHECK(parse_boot(ex.data(), 512, ex.size(), v) == RX_OK && v.kind == KIND_EXFAT && v.cluster_size == 4096);
    std::vector<uint8_t> b(f32.begin(), f32.begin() + 512);
    b[13] = 3; CHECK(parse_boot(b.data(), 512, 0, v) == RX_ERR_NOT_FAT);              // sectors per cluster not 2^n
    b = std::vector<uint8_t>(f32.begin(), f32.begin() + 512); b[510] = 0; CHECK(parse_boot(b.data(), 512, 0, v) == RX_ERR_NOT_FAT);
    b = std::vector<uint8_t>(f32.begin(), f32.begin() + 512); b[36] = b[37] = b[38] = b[39] = 0; CHECK(parse_boot(b.data(), 512, 0, v) == RX_ERR_CORRUPT);
    b = std::vector<uint8_t>(512, 0); b[510] = 0x55; b[511] = 0xAA; std::memcpy(&b[3], "NTFS    ", 8); b[0] = 0xEB;
    CHECK(parse_boot(b.data(), 512, 0, v) == RX_ERR_NOT_FAT);
    CHECK(parse_boot(nullptr, 512, 0, v) == RX_ERR_INVALID_ARG);
}

// ------------------------------------------------------------------ helpers over the C ABI
static RxFatScanHandle open_scan(const std::string& img, RxStatus* st = nullptr, uint32_t max = 0) {
    RxScanOptions o; std::memset(&o, 0, sizeof o); o.struct_size = sizeof o; o.flags = RX_SCANF_VALIDATE_CONTENT; o.max_results = max;
    RxFatScanHandle h = nullptr;
    RxStatus s = rx_fat_scan_create(std::wstring(img.begin(), img.end()).c_str(), &o, &h);
    if (st) *st = s;
    return s == RX_OK ? h : nullptr;
}
static std::vector<RxFileEntry> results(RxFatScanHandle h) {
    uint32_t n = rx_fat_scan_result_count(h);
    std::vector<RxFileEntry> v(n);
    for (auto& e : v) e.struct_size = sizeof(RxFileEntry);
    uint32_t got = 0;
    if (n) CHECK(rx_fat_scan_get_results(h, 0, v.data(), n, &got) == RX_OK);
    v.resize(got);
    return v;
}
static std::string read_sha(RxFatScanHandle h, uint32_t idx, uint64_t size) {
    Sha256 s; std::vector<uint8_t> buf(70001); uint64_t off = 0;
    while (off < size) {
        uint32_t got = 0;
        if (rx_fat_scan_read_file(h, idx, off, buf.data(), (uint32_t)buf.size(), &got) != RX_OK || got == 0) break;
        s.update(buf.data(), got); off += got;
    }
    return off == size ? s.hex() : std::string("short-read");
}

struct Row { std::string path, sha, tag, expect; uint64_t size = 0; bool deleted = false; };
static std::vector<Row> manifest(const std::string& p) {
    std::vector<Row> rows;
    std::ifstream f(p);
    for (std::string l; std::getline(f, l);) {
        if (l.empty() || l[0] == '#') continue;
        std::vector<std::string> c; std::string cur;
        for (char ch : l) { if (ch == '\t') { c.push_back(cur); cur.clear(); } else if (ch != '\r') cur.push_back(ch); }
        c.push_back(cur);
        if (c.size() < 6) continue;
        Row r; r.path = c[0]; r.size = std::stoull(c[1]); r.sha = c[2]; r.deleted = c[3] == "1"; r.tag = c[4]; r.expect = c[5];
        rows.push_back(r);
    }
    return rows;
}

// ------------------------------------------------------------------ ground truth
static void test_volume(const std::string& img, const std::string& man, bool exfat) {
    const char* label = exfat ? "exFAT" : "FAT32";
    auto rows = manifest(man);
    CHECK(rows.size() > 50);
    const std::string before = sha_of(read_all(img));
    RxStatus st = RX_OK;
    RxFatScanHandle h = open_scan(img, &st);
    CHECKM(h != nullptr, "%s create status %d", label, st);
    if (!h) return;
    CHECK(rx_fat_scan_fs_type(h) == (exfat ? (uint32_t)RX_FS_EXFAT : (uint32_t)RX_FS_FAT32));
    CHECK(rx_fat_scan_run(h, nullptr) == RX_OK);
    RxScanProgress p; p.struct_size = sizeof p;
    CHECK(rx_fat_scan_progress(h, &p) == RX_OK && (p.flags & RX_SCANP_DONE) && !(p.flags & RX_SCANP_BITMAP_UNAVAILABLE));
    auto res = results(h);

    std::map<std::wstring, uint32_t> by_path;
    for (uint32_t i = 0; i < res.size(); ++i) by_path[lower(res[i].path)] = i;
    int deleted = 0, exact = 0;
    for (const Row& r : rows) {
        std::wstring want = L"\\" + wide(r.path);
        for (auto& c : want) if (c == L'/') c = L'\\';
        if (!exfat && r.tag == "short_name_only") want = L"\\_PPER.TXT";      // FAT keeps no long name for it: first letter lost
        auto it = by_path.find(lower(want));
        if (!r.deleted) { CHECKM(it == by_path.end(), "%s: kept file reported as deleted: %s", label, r.path.c_str()); continue; }
        ++deleted;
        CHECKM(it != by_path.end(), "%s: deleted file not found: %s", label, r.path.c_str());
        if (it == by_path.end()) continue;
        const RxFileEntry& e = res[it->second];
        CHECKM(e.size == r.size, "%s: %s size %llu, expected %llu", label, r.path.c_str(), (unsigned long long)e.size, (unsigned long long)r.size);
        std::string cn = cond_name(e.condition);
        CHECKM(("," + r.expect + ",").find("," + cn + ",") != std::string::npos, "%s: %s condition %s, expected %s (in use %u/%u)",
               label, r.path.c_str(), cn.c_str(), r.expect.c_str(), e.clusters_in_use, e.clusters_total);
        if (r.tag == "short_name_only") CHECK(exfat ? !(e.flags & RX_ENT_NAME_GUESSED) : (e.flags & RX_ENT_NAME_GUESSED) != 0);
        if (e.condition == RX_COND_VERIFIED || e.condition == RX_COND_LIKELY || e.condition == RX_COND_EMPTY) {
            std::string sha = e.size ? read_sha(h, it->second, e.size) : r.sha;
            CHECKM(sha == r.sha, "%s: %s rated %s but content differs", label, r.path.c_str(), cn.c_str());
            if (sha == r.sha) ++exact;
        }
        if (r.tag == "overwritten") CHECKM(read_sha(h, it->second, e.size) != r.sha, "%s: overwritten file still matches", label);
        if (r.tag == "fragmented" && exfat) CHECK((e.flags & RX_ENT_CHAIN_FOLLOWED) != 0);
        CHECK(e.modified != 0 && e.created != 0);
    }
    std::printf("  %s: %u deleted files found, %d of %d expected files checked, %d recovered byte-for-byte (SHA-256)\n",
                label, (unsigned)res.size(), deleted, deleted, exact);
    CHECK(exact >= deleted - 3);                                              // all but the overwritten / fragmented ones
    uint8_t buf[16]; uint32_t got = 0;
    CHECK(rx_fat_scan_read_file(h, (uint32_t)res.size(), 0, buf, 16, &got) == RX_ERR_INVALID_ARG);
    rx_fat_scan_destroy(h);
    CHECKM(sha_of(read_all(img)) == before, "%s image changed - the engine must be read-only", label);
}

// Deep Scan on FAT/exFAT now reads only free clusters and finds the deleted media byte-identical.
static void test_deep_scan_map(const std::string& img, const std::string& man, const char* label) {
    auto rows = manifest(man);
    std::map<std::string, std::string> want;               // sha -> path, deleted files of carvable types
    for (const Row& r : rows) if (r.deleted && (r.tag == "jpeg" || r.tag == "pdf" || r.tag == "docx" || r.tag == "mp4" || r.tag == "mp3")) want[r.sha] = r.path;
    RxCarveHandle h = nullptr;
    CHECK(rx_carve_create(std::wstring(img.begin(), img.end()).c_str(), nullptr, &h) == RX_OK);
    if (!h) return;
    RxCarveProgress p; p.struct_size = sizeof p;
    rx_carve_progress(h, &p);
    CHECKM(!(p.flags & RX_SCANP_BITMAP_UNAVAILABLE), "%s: Deep Scan has no free-space map", label);
    CHECK(rx_carve_run(h, nullptr) == RX_OK);
    rx_carve_progress(h, &p);
    uint64_t img_size = read_all(img).size();
    CHECKM(p.bytes_total < img_size / 2, "%s: examined %llu of %llu bytes", label, (unsigned long long)p.bytes_total, (unsigned long long)img_size);
    uint32_t n = rx_carve_result_count(h), found = 0;
    std::vector<RxCarveEntry> v(n);
    for (auto& e : v) e.struct_size = sizeof e;
    uint32_t got = 0;
    if (n) rx_carve_get_results(h, 0, v.data(), n, &got);
    for (uint32_t i = 0; i < got; ++i) {
        CHECK(!(v[i].evidence & RX_EV_ALLOC_UNKNOWN));
        Sha256 s; std::vector<uint8_t> buf(1 << 20); uint64_t off = 0;
        while (off < v[i].size) { uint32_t g = 0; if (rx_carve_read_file(h, i, off, buf.data(), (uint32_t)buf.size(), &g) != RX_OK || !g) break; s.update(buf.data(), g); off += g; }
        if (want.count(s.hex()) && v[i].condition == RX_COND_VERIFIED) ++found;
    }
    CHECKM(found == want.size(), "%s: Deep Scan recovered %u of %u deleted media files", label, found, (unsigned)want.size());
    std::printf("  %s Deep Scan: free space only (%llu of %llu bytes), %u of %u deleted media files carved intact\n", label,
                (unsigned long long)p.bytes_total, (unsigned long long)img_size, found, (unsigned)want.size());
    rx_carve_destroy(h);
}

// ------------------------------------------------------------------ robustness
static void test_fuzz(const std::string& src, const std::string& tmp, int iterations, bool exfat) {
    auto img = read_all(src);
    if (img.size() < 1024 * 1024) return;
    Volume v;
    if (parse_boot(img.data(), 512, img.size(), v) != RX_OK) { CHECK(false); return; }
    std::string path = tmp + (exfat ? "/rx_fuzz_exfat.img" : "/rx_fuzz_fat.img");
    uint64_t s = 0x9E3779B97F4A7C15ull ^ (exfat ? 77 : 0);
    auto rnd = [&]() { s ^= s << 13; s ^= s >> 7; s ^= s << 17; return s; };
    // Mutate only the metadata area (boot, FAT, first directory clusters): the rest of the image is file data.
    const uint64_t meta_end = std::min<uint64_t>(v.heap_offset + 64ull * v.cluster_size, img.size());
    int created = 0, ran = 0;
    for (int it = 0; it < iterations; ++it) {
        std::vector<uint8_t> d(img.begin(), img.begin() + (size_t)meta_end);
        int nmut = 1 + (int)(rnd() % 40);
        for (int m = 0; m < nmut; ++m) {
            uint64_t off;
            switch (rnd() % 3) {
                case 0: off = rnd() % 512; break;
                case 1: off = v.fat_offset + rnd() % std::min<uint64_t>(v.fat_bytes, 65536); break;
                default: off = v.heap_offset + rnd() % (meta_end - v.heap_offset); break;
            }
            if (off < d.size()) d[(size_t)off] = (rnd() % 3 == 0) ? (uint8_t)(rnd() % 2 ? 0xFF : 0) : (uint8_t)rnd();
        }
        { std::ofstream o(path, std::ios::binary); o.write((const char*)d.data(), (std::streamsize)d.size()); }
        RxStatus st = RX_OK;
        RxFatScanHandle h = open_scan(path, &st);
        if (!h) { CHECKM(st == RX_ERR_NOT_FAT || st == RX_ERR_CORRUPT || st == RX_ERR_IO || st == RX_ERR_UNSUPPORTED, "unexpected create status %d", st); continue; }
        ++created;
        if (rx_fat_scan_run(h, nullptr) == RX_OK) ++ran;
        auto res = results(h);
        for (size_t i = 0; i < res.size() && i < 8; ++i) { uint8_t buf[4096]; uint32_t g = 0; rx_fat_scan_read_file(h, (uint32_t)i, 0, buf, sizeof buf, &g); }
        rx_fat_scan_destroy(h);
    }
    std::remove(path.c_str());
    std::printf("  %s fuzz: %d iterations, %d opened, %d scans completed, no crash\n", exfat ? "exFAT" : "FAT32", iterations, created, ran);
    CHECK(ran > iterations / 4);
}

static void test_cancel_and_args(const std::string& img) {
    RxFatScanHandle h = open_scan(img);
    RxCancelHandle t = rx_cancel_create();
    rx_cancel_request(t);
    CHECK(h && rx_fat_scan_run(h, t) == RX_ERR_CANCELLED);
    if (h) { results(h); rx_fat_scan_destroy(h); }
    rx_cancel_destroy(t);
    h = open_scan(img, nullptr, 5);                                          // result cap
    CHECK(h && rx_fat_scan_run(h, nullptr) == RX_OK);
    if (h) { RxScanProgress p; p.struct_size = sizeof p; rx_fat_scan_progress(h, &p); CHECK(rx_fat_scan_result_count(h) == 5 && (p.flags & RX_SCANP_TRUNCATED)); rx_fat_scan_destroy(h); }
    RxFatScanHandle bad = nullptr;
    CHECK(rx_fat_scan_create(nullptr, nullptr, &bad) == RX_ERR_INVALID_ARG);
    CHECK(rx_fat_scan_run(nullptr, nullptr) == RX_ERR_INVALID_ARG);
    CHECK(rx_fat_scan_result_count(nullptr) == 0);
    rx_fat_scan_destroy(nullptr);
}

int main(int argc, char** argv) {
    int fuzz = 300;
    for (int i = 1; i < argc; ++i) if (std::string(argv[i]).rfind("--fuzz=", 0) == 0) fuzz = std::atoi(argv[i] + 7);
    std::string dir = RX_FIXTURE_DIR, tmp = "/tmp";
#ifdef _WIN32
    if (const char* t = std::getenv("TEMP")) tmp = t;
#endif
    std::string f32 = dir + "/fat32_basic.img", ex = dir + "/exfat_basic.img";
    { std::ifstream t(f32, std::ios::binary); if (!t.good()) { std::printf("fixture image not found: %s\n", f32.c_str()); return 2; } }
    test_pure(dir);
    test_volume(f32, dir + "/fat32_basic.manifest.tsv", false);
    test_volume(ex, dir + "/exfat_basic.manifest.tsv", true);
    test_deep_scan_map(f32, dir + "/fat32_basic.manifest.tsv", "FAT32");
    test_deep_scan_map(ex, dir + "/exfat_basic.manifest.tsv", "exFAT");
    // NTFS images must be refused by the FAT scanner (and vice versa) without harm.
    RxStatus st = RX_OK; RxFatScanHandle h = open_scan(dir + "/ntfs_basic.img", &st);
    CHECK(h == nullptr && st == RX_ERR_NOT_FAT);
    RxScanHandle n = nullptr;
    CHECK(rx_ntfs_scan_create(std::wstring(f32.begin(), f32.end()).c_str(), nullptr, &n) == RX_ERR_NOT_NTFS);
    test_cancel_and_args(f32);
    test_fuzz(f32, tmp, fuzz, false);
    test_fuzz(ex, tmp, fuzz, true);
    std::printf("%d checks, %d failed\n", g_run, g_failed);
    return g_failed == 0 ? 0 : 1;
}
