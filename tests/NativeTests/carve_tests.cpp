// Deep Scan (signature carving) tests.
//  - Pure parser tests on in-memory data (all platforms).
//  - Ground truth: tests/fixtures/carve_basic.img (built by make_carve_fixture.py). Every intact file must be found at its
//    exact offset with its exact size, SHA-256 and expected condition; negative cases (fragmented, overwritten, fake
//    header) must never be reported as intact.
//  - NTFS free-space mode against the real NTFS fixture; the engine must never modify an image.
//  - Pause / resume / cancel / type mask / result cap; mutation fuzzing of every parser.
#include "rx_engine.h"
#include "internal.h"
#include "carve_format.h"
#include "ntfs_format.h"
#include "sha256.h"
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <map>
#include <string>
#include <thread>
#include <vector>

using namespace rx::carve;

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
static uint32_t cond_of(const std::string& s) {
    for (uint32_t i = 0; i < 7; ++i) if (s == cond_name(i)) return i;
    return 99;
}
static std::wstring wpath(const std::string& s) { return std::wstring(s.begin(), s.end()); }
static std::string narrow(const wchar_t* w) { std::string s; for (; *w; ++w) s.push_back((char)*w); return s; }

static std::vector<uint8_t> read_all(const std::string& path) {
    std::ifstream f(path, std::ios::binary);
    return std::vector<uint8_t>((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
}
static std::string sha_of(const uint8_t* p, size_t n) { Sha256 h; h.update(p, n); return h.hex(); }

// ------------------------------------------------------------------ pure tests
static void test_pure() {
    CHECK(crc32(0, reinterpret_cast<const uint8_t*>("123456789"), 9) == 0xCBF43926u);    // standard check value
    const char* t = "2024:05:17 14:30:00";
    uint64_t ft = exif_time_to_filetime(t, 19);
    CHECK(ft == (1715956200ull + 11644473600ull) * 10000000ull);
    CHECK(exif_time_to_filetime("2024:13:17 14:30:00", 19) == 0);
    CHECK(exif_time_to_filetime("0000:00:00 00:00:00", 19) == 0);
    CHECK(exif_time_to_filetime("garbage", 7) == 0);

    uint8_t b[64]; std::memset(b, 0, sizeof b);
    CHECK(match(b, 64) == 0);
    std::memcpy(b, "\xFF\xD8\xFF\xE0", 4); CHECK(match(b, 64) == RX_FT_JPEG);
    CHECK(match(b, 10) == 0);                                                // too few bytes to judge
    std::memcpy(b, "\x89PNG\r\n\x1A\n", 8); CHECK(match(b, 64) == RX_FT_PNG);
    std::memset(b, 0, 64); std::memcpy(b, "\x00\x00\x00\x18" "ftyp" "isom", 12); CHECK(match(b, 64) == RX_FT_ISOBMFF);
    std::memset(b, 0, 64); std::memcpy(b, "\x00\x00\x00\x08" "ftyp" "\x01\x02\x03\x04", 12); CHECK(match(b, 64) == 0);   // implausible
    std::memset(b, 0, 64); std::memcpy(b, "%PDF-", 5); CHECK(match(b, 64) == RX_FT_PDF);

    // Truncated / garbage candidates are rejected or reported as damaged, never as intact.
    std::vector<uint8_t> j = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
    j.resize(4000, 0x5A);
    MemReader mr(j.data(), j.size());
    Result r;
    bool got = parse(RX_FT_JPEG, mr, r);
    CHECK(!got || (r.condition != RX_COND_VERIFIED && r.condition != RX_COND_LIKELY));
    std::vector<uint8_t> pdf(5000, ' ');
    std::memcpy(pdf.data(), "%PDF-1.7\n", 9);
    MemReader mp(pdf.data(), pdf.size());
    CHECK(!parse(RX_FT_PDF, mp, r));                                        // no %%EOF: end unknown, nothing reported
    CHECK(!parse(999, mp, r));
}

// ------------------------------------------------------------------ helpers over the C ABI
static RxCarveHandle open_carve(const std::string& img, uint32_t flags = 0, uint32_t mask = 0, uint32_t max = 0, RxStatus* st_out = nullptr) {
    RxCarveOptions o; std::memset(&o, 0, sizeof o); o.struct_size = sizeof o; o.flags = flags; o.type_mask = mask; o.max_results = max;
    RxCarveHandle h = nullptr;
    RxStatus st = rx_carve_create(wpath(img).c_str(), &o, &h);
    if (st_out) *st_out = st;
    return st == RX_OK ? h : nullptr;
}
static std::vector<RxCarveEntry> results(RxCarveHandle h) {
    uint32_t n = rx_carve_result_count(h);
    std::vector<RxCarveEntry> v(n);
    for (auto& e : v) e.struct_size = sizeof(RxCarveEntry);
    uint32_t got = 0;
    if (n) CHECK(rx_carve_get_results(h, 0, v.data(), n, &got) == RX_OK);
    v.resize(got);
    return v;
}
static std::string carved_sha(RxCarveHandle h, uint32_t idx, uint64_t size) {
    Sha256 s; std::vector<uint8_t> buf(70001); uint64_t off = 0;           // odd chunk size: exercises unaligned reads
    while (off < size) {
        uint32_t got = 0;
        if (rx_carve_read_file(h, idx, off, buf.data(), (uint32_t)buf.size(), &got) != RX_OK || got == 0) break;
        s.update(buf.data(), got); off += got;
    }
    return off == size ? s.hex() : std::string("short-read");
}

// ------------------------------------------------------------------ ground truth
struct Row { std::string name, sha, type, ext, expect; uint64_t off = 0, size = 0; bool exact = false; };

static void test_fixture(const std::string& img, const std::string& manifest) {
    std::vector<Row> rows;
    std::ifstream mf(manifest);
    CHECKM(mf.good(), "manifest missing: %s", manifest.c_str());
    for (std::string line; std::getline(mf, line);) {
        if (line.empty() || line[0] == '#') continue;
        std::vector<std::string> f; std::string cur;
        for (char c : line) { if (c == '\t') { f.push_back(cur); cur.clear(); } else if (c != '\r') cur.push_back(c); }
        f.push_back(cur);
        if (f.size() < 8) continue;
        Row r; r.name = f[0]; r.off = std::stoull(f[1]); r.size = std::stoull(f[2]); r.sha = f[3]; r.type = f[4]; r.ext = f[5]; r.expect = f[6]; r.exact = f[7] == "1";
        rows.push_back(r);
    }
    CHECK(rows.size() >= 20);
    const std::string before = sha_of(read_all(img).data(), read_all(img).size());

    RxStatus st = RX_OK;
    RxCarveHandle h = open_carve(img, 0, 0, 0, &st);
    CHECKM(h != nullptr, "carve_create status %d", st);
    if (!h) return;
    RxCarveProgress p; p.struct_size = sizeof p;
    CHECK(rx_carve_progress(h, &p) == RX_OK && (p.flags & RX_SCANP_BITMAP_UNAVAILABLE));   // raw image: no file system map
    auto t0 = std::chrono::steady_clock::now();
    CHECK(rx_carve_run(h, nullptr) == RX_OK);
    double secs = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
    CHECK(rx_carve_progress(h, &p) == RX_OK && (p.flags & RX_SCANP_DONE) && p.bytes_done == p.bytes_total && p.bytes_total == 8u * 1024 * 1024);
    CHECK(rx_carve_run(h, nullptr) == RX_ERR_INVALID_ARG);                    // a session runs once
    auto res = results(h);
    std::printf("  carve fixture: %u results in %.2f s\n", (unsigned)res.size(), secs);

    std::map<uint64_t, uint32_t> by_off;
    for (uint32_t i = 0; i < res.size(); ++i) by_off[res[i].offset] = i;
    int intact_ok = 0;
    for (const Row& r : rows) {
        auto it = by_off.find(r.off);
        if (r.expect == "NONE") {
            bool bad = it != by_off.end() && (res[it->second].condition == RX_COND_VERIFIED || res[it->second].condition == RX_COND_LIKELY);
            CHECKM(!bad, "%s must not be reported as recoverable (got %s)", r.name.c_str(), bad ? cond_name(res[it->second].condition) : "-");
            continue;
        }
        CHECKM(it != by_off.end(), "%s not found at offset %llu", r.name.c_str(), (unsigned long long)r.off);
        if (it == by_off.end()) continue;
        const RxCarveEntry& e = res[it->second];
        CHECKM(e.condition == cond_of(r.expect), "%s: condition %s, expected %s (evidence 0x%x)", r.name.c_str(), cond_name(e.condition), r.expect.c_str(), e.evidence);
        CHECKM(narrow(e.ext) == r.ext, "%s: ext %s, expected %s", r.name.c_str(), narrow(e.ext).c_str(), r.ext.c_str());
        CHECK(e.evidence & RX_EV_ALLOC_UNKNOWN);
        if (r.exact) {
            CHECKM(e.size == r.size, "%s: size %llu, expected %llu", r.name.c_str(), (unsigned long long)e.size, (unsigned long long)r.size);
            std::string sha = carved_sha(h, it->second, e.size);
            CHECKM(sha == r.sha, "%s: content differs from the original", r.name.c_str());
            if (e.size == r.size && sha == r.sha) ++intact_ok;
        } else if (r.expect == "PARTIAL") {
            CHECKM(e.size <= r.size + 4096 && e.size > 0, "%s: partial size %llu", r.name.c_str(), (unsigned long long)e.size);
        }
        if (r.name == "photo_exif.jpg") CHECK(e.content_time == exif_time_to_filetime("2024:05:17 14:30:00", 19) && (e.evidence & RX_EV_TIME_LOCAL));
        if (r.name == "clip_moov_last.mp4") CHECK(e.content_time == (1709294400ull + 11644473600ull) * 10000000ull);
    }
    std::printf("  carve fixture: %d intact files recovered byte-for-byte\n", intact_ok);
    // Nothing in the zero background or inside verified files may be reported.
    for (const RxCarveEntry& e : res) {
        bool known = false;
        for (const Row& r : rows) if (r.off == e.offset) known = true;
        CHECKM(known, "unexpected result at offset %llu (%s, %s)", (unsigned long long)e.offset, narrow(e.ext).c_str(), cond_name(e.condition));
    }
    uint8_t buf[16]; uint32_t got = 0;
    CHECK(rx_carve_read_file(h, (uint32_t)res.size(), 0, buf, 16, &got) == RX_ERR_INVALID_ARG);
    CHECK(rx_carve_read_file(h, 0, res[0].size, buf, 16, &got) == RX_OK && got == 0);   // past the end: nothing
    rx_carve_destroy(h);
    auto after = read_all(img);
    CHECKM(sha_of(after.data(), after.size()) == before, "image changed during the scan - the engine must be read-only");

    // Type mask: only PNG.
    h = open_carve(img, 0, 1u << RX_FT_PNG);
    CHECK(h && rx_carve_run(h, nullptr) == RX_OK);
    if (h) { auto v = results(h); CHECK(v.size() == 2); for (auto& e : v) CHECK(e.type == RX_FT_PNG); rx_carve_destroy(h); }
    // Result cap.
    h = open_carve(img, 0, 0, 3);
    CHECK(h && rx_carve_run(h, nullptr) == RX_OK);
    if (h) { CHECK(rx_carve_result_count(h) == 3); rx_carve_progress(h, &p); CHECK(p.flags & RX_SCANP_TRUNCATED); rx_carve_destroy(h); }
}

// ------------------------------------------------------------------ pause / resume / cancel
static void test_pause_cancel(const std::string& img) {
    RxCarveHandle h = open_carve(img);
    RxCancelHandle tok = rx_cancel_create();
    CHECK(h && tok);
    if (!h || !tok) return;
    rx_cancel_set_paused(tok, 1);
    CHECK(rx_cancel_is_paused(tok) == 1);
    std::atomic<int> result{-1};
    std::thread th([&] { result = rx_carve_run(h, tok); });
    RxCarveProgress p; p.struct_size = sizeof p;
    bool saw_paused = false;
    for (int i = 0; i < 100 && !saw_paused; ++i) { std::this_thread::sleep_for(std::chrono::milliseconds(20)); rx_carve_progress(h, &p); saw_paused = (p.flags & RX_SCANP_PAUSED) != 0; }
    CHECK(saw_paused);
    CHECK(result == -1 && p.bytes_done == 0);                                 // paused before reading anything
    rx_cancel_set_paused(tok, 0);
    th.join();
    CHECK(result == RX_OK);
    rx_carve_progress(h, &p);
    CHECK(!(p.flags & RX_SCANP_PAUSED) && p.found > 10);
    rx_carve_destroy(h);

    // Cancel while paused: must return promptly with CANCELLED and keep results readable.
    h = open_carve(img);
    rx_cancel_destroy(tok);
    tok = rx_cancel_create();
    rx_cancel_set_paused(tok, 1);
    result = -1;
    std::thread th2([&] { result = rx_carve_run(h, tok); });
    std::this_thread::sleep_for(std::chrono::milliseconds(100));
    rx_cancel_request(tok);
    th2.join();
    CHECK(result == RX_ERR_CANCELLED);
    rx_carve_progress(h, &p);
    CHECK((p.flags & RX_SCANP_DONE) && p.status == RX_ERR_CANCELLED);
    results(h);
    rx_carve_destroy(h);
    rx_cancel_destroy(tok);

    // Bad arguments.
    RxCarveHandle bad = nullptr;
    CHECK(rx_carve_create(nullptr, nullptr, &bad) == RX_ERR_INVALID_ARG);
    RxCarveOptions o; std::memset(&o, 0, sizeof o);
    CHECK(rx_carve_create(wpath(img).c_str(), &o, &bad) == RX_ERR_INVALID_ARG);
    CHECK(rx_carve_run(nullptr, nullptr) == RX_ERR_INVALID_ARG);
    CHECK(rx_carve_result_count(nullptr) == 0);
    rx_carve_destroy(nullptr);
    rx_cancel_set_paused(nullptr, 1);
    CHECK(rx_cancel_is_paused(nullptr) == 0);
}

// ------------------------------------------------------------------ NTFS free-space mode
static void test_ntfs_free_space(const std::string& ntfs_img) {
    auto before = read_all(ntfs_img);
    std::string h0 = sha_of(before.data(), before.size());
    RxCarveHandle h = open_carve(ntfs_img);
    CHECK(h != nullptr);
    if (!h) return;
    RxCarveProgress p; p.struct_size = sizeof p;
    rx_carve_progress(h, &p);
    CHECK(!(p.flags & RX_SCANP_BITMAP_UNAVAILABLE));                          // NTFS map in use
    CHECK(rx_carve_run(h, nullptr) == RX_OK);
    rx_carve_progress(h, &p);
    CHECKM(p.bytes_total > 0 && p.bytes_total < before.size(), "free space %llu of %llu", (unsigned long long)p.bytes_total, (unsigned long long)before.size());
    auto res = results(h);
    int verified = 0;
    for (auto& e : res) {
        CHECK(e.clusters_total > 0 && !(e.evidence & RX_EV_ALLOC_UNKNOWN));
        if (e.condition == RX_COND_VERIFIED) ++verified;
        if (e.evidence & RX_EV_IN_USE) CHECK(e.condition != RX_COND_LIKELY);
    }
    // The fixture's deleted "media" files are header-only blobs, not real media: none of them may look intact.
    CHECKM(verified == 0, "%d header-only blobs reported as intact", verified);
    std::printf("  ntfs free-space carve: %llu of %llu bytes examined, %u candidates, 0 false 'intact'\n",
                (unsigned long long)p.bytes_total, (unsigned long long)before.size(), (unsigned)res.size());
    rx_carve_destroy(h);

    h = open_carve(ntfs_img, RX_CARVEF_ALL_SPACE);                           // whole volume on request
    CHECK(h && rx_carve_run(h, nullptr) == RX_OK);
    if (h) { rx_carve_progress(h, &p); CHECK(p.bytes_total == before.size()); rx_carve_destroy(h); }
    auto after = read_all(ntfs_img);
    CHECK(sha_of(after.data(), after.size()) == h0);
}

// ------------------------------------------------------------------ files planted in NTFS free space
// A disposable copy of the real NTFS image gets three intact files written straight into its clusters (bypassing the file
// system, exactly like data left behind by a deleted file): one wholly in free space, and two that start in free space but
// run into clusters allocated to existing files. Expectations:
//   wholly free                -> VERIFIED, 0 clusters in use, bytes identical
//   JPEG running into used     -> PARTIAL + IN_USE (no checksum can prove the overlapping part is original)
//   PNG running into used      -> VERIFIED + IN_USE (every chunk CRC still matches, which proves the bytes are original)
static void test_ntfs_planted(const std::string& ntfs_img, const std::string& carve_img, const std::string& tmp_dir) {
    auto carve = read_all(carve_img);
    const uint64_t jpg_off = 65536, jpg_len = 28889, png_off = 114688, png_len = 138253;   // photo_exif.jpg, image.png
    if (carve.size() < png_off + png_len) { CHECK(false); return; }
    std::vector<uint8_t> jpg(carve.begin() + jpg_off, carve.begin() + jpg_off + jpg_len);
    std::vector<uint8_t> png(carve.begin() + png_off, carve.begin() + png_off + png_len);
    CHECK(match(jpg.data(), 64) == RX_FT_JPEG && match(png.data(), 64) == RX_FT_PNG);

    // Clusters of the NTFS system files (MFT records 0-15: $MFT, $MFTMirr, $LogFile, $Bitmap, ...) must not be overwritten,
    // or the allocation map the test relies on would be destroyed.
    std::vector<std::pair<uint64_t, uint64_t>> system_runs;
    {
        auto vol = read_all(ntfs_img);
        rx::ntfs::Geometry g;
        if (rx::ntfs::parse_boot_sector(vol.data(), 512, vol.size(), g) == RX_OK) {
            for (uint64_t i = 0; i < 16; ++i) {
                uint64_t off = g.mft_lcn * g.cluster_size + i * g.record_size;
                if (off + g.record_size > vol.size()) break;
                std::vector<uint8_t> rec(vol.begin() + off, vol.begin() + off + g.record_size);
                rx::ntfs::ParsedRecord pr;
                if (!rx::ntfs::apply_fixups(rec.data(), g.record_size) || !rx::ntfs::parse_record(rec.data(), g.record_size, g.total_clusters, true, pr)) continue;
                for (const auto& r : pr.runs) if (r.lcn >= 0) system_runs.push_back({ (uint64_t)r.lcn, (uint64_t)r.lcn + r.len });
            }
        }
    }
    CHECK(system_runs.size() >= 4);
    auto touches_system = [&](uint64_t a, uint64_t b) {
        for (const auto& r : system_runs) if (a < r.second && r.first < b) return true;
        return false;
    };

    // Choose places from the volume's own allocation map.
    RxScanHandle n = nullptr;
    CHECK(rx_ntfs_scan_create(wpath(ntfs_img).c_str(), nullptr, &n) == RX_OK);
    if (!n) return;
    uint32_t cs = 0; uint64_t nc = 0;
    CHECK(rx::ntfs_alloc_geometry(n, cs, nc));
    uint64_t c = 0, s = 0, e = 0, free_at = 0, png_at = 0, jpg_at = 0;
    while (rx::ntfs_find_run(n, c, false, s, e)) {
        uint64_t us = 0, ue = 0;
        uint64_t png_cl = (png_len + cs - 1) / cs, jpg_cl = (jpg_len + cs - 1) / cs;
        bool used_after = rx::ntfs_find_run(n, e, true, us, ue) && us == e;
        // Each straddling file gets its own free->used boundary; the used part must be ordinary file data.
        if (used_after && e - s >= 16) {
            if (!png_at && ue - us >= png_cl && !touches_system(e - 11, e - 11 + png_cl)) png_at = e - 11;      // 11 free, rest in use
            else if (!jpg_at && ue - us >= jpg_cl && !touches_system(e - 2, e - 2 + jpg_cl)) jpg_at = e - 2;     // 2 free, rest in use
        }
        if (!free_at && e - s >= 64) free_at = s + 8;                       // well inside a free run
        c = e;
    }
    rx_ntfs_scan_destroy(n);
    CHECKM(free_at && png_at && jpg_at, "no suitable runs (free %llu png %llu jpg %llu)", (unsigned long long)free_at, (unsigned long long)png_at, (unsigned long long)jpg_at);
    if (!free_at || !png_at || !jpg_at) return;

    std::string img = tmp_dir + "/rx_planted.img";
    { std::ifstream a(ntfs_img, std::ios::binary); std::ofstream b(img, std::ios::binary); b << a.rdbuf(); }
    {
        std::fstream f(img, std::ios::in | std::ios::out | std::ios::binary);
        auto put = [&](uint64_t cl, const std::vector<uint8_t>& d) { f.seekp((std::streamoff)(cl * cs)); f.write((const char*)d.data(), (std::streamsize)d.size()); };
        put(free_at, jpg); put(png_at, png); put(jpg_at, jpg);
    }
    RxCarveHandle h = open_carve(img);
    CHECK(h != nullptr);
    if (!h) { std::remove(img.c_str()); return; }
    RxCarveProgress p; p.struct_size = sizeof p;
    rx_carve_progress(h, &p);
    CHECKM(!(p.flags & RX_SCANP_BITMAP_UNAVAILABLE), "planting damaged the NTFS metadata");
    CHECK(rx_carve_run(h, nullptr) == RX_OK);
    auto res = results(h);
    int seen = 0;
    for (uint32_t i = 0; i < res.size(); ++i) {
        const RxCarveEntry& r = res[i];
        if (r.offset == free_at * cs) {
            ++seen;
            CHECKM(r.condition == RX_COND_VERIFIED && r.clusters_in_use == 0 && !(r.evidence & RX_EV_IN_USE), "free JPEG: %s in use %u", cond_name(r.condition), r.clusters_in_use);
            CHECK(r.size == jpg_len && carved_sha(h, i, r.size) == sha_of(jpg.data(), jpg.size()));
        } else if (r.offset == png_at * cs) {
            ++seen;
            CHECKM(r.condition == RX_COND_VERIFIED && (r.evidence & RX_EV_IN_USE) && (r.evidence & RX_EV_CHECKSUM_OK) && r.clusters_in_use > 0,
                   "straddling PNG: %s ev 0x%x in use %u", cond_name(r.condition), r.evidence, r.clusters_in_use);
        } else if (r.offset == jpg_at * cs) {
            ++seen;
            CHECKM(r.condition == RX_COND_PARTIAL && (r.evidence & RX_EV_IN_USE) && r.clusters_in_use > 0,
                   "straddling JPEG: %s ev 0x%x in use %u", cond_name(r.condition), r.evidence, r.clusters_in_use);
        }
    }
    CHECKM(seen == 3, "found %d of 3 planted files", seen);
    std::printf("  ntfs planted: %d of 3 planted files found with the expected evidence (%u results)\n", seen, (unsigned)res.size());
    rx_carve_destroy(h);
    std::remove(img.c_str());
}

// ------------------------------------------------------------------ wiped tails
// A file whose remainder was wiped (zeros, as on a formatted card) must not turn into a huge mostly-zero "partial" file, and the
// end-of-file searches must give up quickly instead of reading hundreds of megabytes of empty space.
static void test_wiped_tails(const std::string& img_path) {
    auto img = read_all(img_path);
    const size_t jpg_off = 65536, cut = 10000;                                   // first 10 KB of photo_exif.jpg
    std::vector<uint8_t> d(img.begin() + jpg_off, img.begin() + jpg_off + cut);
    d.resize(cut + 3 * 1024 * 1024, 0);
    MemReader mr(d.data(), d.size());
    Result r;
    auto t0 = std::chrono::steady_clock::now();
    CHECK(parse(RX_FT_JPEG, mr, r));
    CHECKM(r.condition == RX_COND_PARTIAL && r.size <= cut && r.size + 4096 > cut, "wiped JPEG: %s size %llu", cond_name(r.condition), (unsigned long long)r.size);
    const uint64_t jpeg_kept = r.size;

    std::vector<uint8_t> pdf(40u * 1024 * 1024, 0);                              // header, then 40 MB of empty space, then a stray %%EOF
    std::memcpy(pdf.data(), "%PDF-1.4\n1 0 obj\n", 17);
    std::memcpy(pdf.data() + pdf.size() - 6, "%%EOF\n", 6);
    MemReader mp(pdf.data(), pdf.size());
    CHECK(!parse(RX_FT_PDF, mp, r));                                              // search stopped in the empty space
    std::vector<uint8_t> zip(img.begin() + 499712, img.begin() + 499712 + 4000);  // start of streamed.zip (data descriptors)
    zip.resize(40u * 1024 * 1024, 0);
    MemReader mz(zip.data(), zip.size());
    bool zr = parse(RX_FT_ZIP, mz, r);
    CHECK(!zr || r.condition == RX_COND_PARTIAL || r.condition == RX_COND_LOST);
    double secs = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
    CHECKM(secs < 2.0, "wiped-tail parsing took %.2f s", secs);
    std::printf("  wiped tails: JPEG kept %llu of %u valid bytes, PDF/ZIP searches stopped in empty space (%.2f s)\n",
                (unsigned long long)jpeg_kept, (unsigned)cut, secs);
}

// ------------------------------------------------------------------ fuzzing
static void test_fuzz(const std::string& img_path, int iterations) {
    auto img = read_all(img_path);
    std::vector<uint64_t> starts;
    for (uint64_t off = 0; off + kMatchBytes <= img.size(); off += 512) if (match(img.data() + off, (uint32_t)(img.size() - off))) starts.push_back(off);
    CHECK(starts.size() >= 18);
    uint64_t s = 0x9E3779B97F4A7C15ull;
    auto rnd = [&]() { s ^= s << 13; s ^= s >> 7; s ^= s << 17; return s; };
    int parsed = 0;
    for (int it = 0; it < iterations; ++it) {
        uint64_t st = starts[rnd() % starts.size()];
        size_t len = (size_t)std::min<uint64_t>(img.size() - st, 1u << 18 | (rnd() % (1u << 18)));
        if (rnd() % 4 == 0) len = (size_t)(rnd() % len) + 1;                  // truncated candidate
        std::vector<uint8_t> d(img.begin() + st, img.begin() + st + len);
        int nmut = 1 + (int)(rnd() % 40);
        for (int m = 0; m < nmut; ++m) {
            size_t off = (rnd() % 3 == 0) ? (size_t)(rnd() % std::min<size_t>(d.size(), 4096)) : (size_t)(rnd() % d.size());
            d[off] = (rnd() % 3 == 0) ? (uint8_t)(rnd() % 2 ? 0xFF : 0x00) : (uint8_t)rnd();
        }
        uint32_t t = match(d.data(), (uint32_t)d.size());
        if (!t) t = 1 + (uint32_t)(rnd() % (RX_FT_COUNT - 1));                // force every parser to see garbage
        MemReader mr(d.data(), d.size());
        Result r;
        if (parse(t, mr, r)) {
            ++parsed;
            CHECKM(r.size <= d.size(), "type %u reported size %llu beyond the data (%zu)", t, (unsigned long long)r.size, d.size());
            CHECK(r.condition >= RX_COND_VERIFIED && r.condition <= RX_COND_LOST);
        }
    }
    std::printf("  fuzz: %d mutated candidates, %d parsed, no crash\n", iterations, parsed);
}

int main(int argc, char** argv) {
    int fuzz_iters = 3000;
    for (int i = 1; i < argc; ++i) if (std::string(argv[i]).rfind("--fuzz=", 0) == 0) fuzz_iters = std::atoi(argv[i] + 7);
    test_pure();
    std::string dir = RX_FIXTURE_DIR;
    std::string img = dir + "/carve_basic.img";
    { std::ifstream t(img, std::ios::binary); if (!t.good()) { std::printf("fixture image not found: %s\n", img.c_str()); return 2; } }
    test_fixture(img, dir + "/carve_basic.manifest.tsv");
    test_pause_cancel(img);
    test_ntfs_free_space(dir + "/ntfs_basic.img");
    std::string tmp = "/tmp";
#ifdef _WIN32
    if (const char* t = std::getenv("TEMP")) tmp = t;
#endif
    test_ntfs_planted(dir + "/ntfs_basic.img", img, tmp);
    test_wiped_tails(img);
    test_fuzz(img, fuzz_iters);
    std::printf("%d checks, %d failed\n", g_run, g_failed);
    return g_failed == 0 ? 0 : 1;
}
