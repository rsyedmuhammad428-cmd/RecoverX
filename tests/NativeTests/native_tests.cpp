// Dependency-free unit tests for the RecoverX native engine.
// Portable tests run everywhere. Image-file volume tests run on non-Windows hosts only (the stub platform treats a
// file as a volume). Windows hardware queries cannot be unit tested here; see docs/WINDOWS_COMPATIBILITY.md.
#include "rx_engine.h"
#include "internal.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

static int g_run = 0, g_failed = 0;
#define CHECK(cond) do { ++g_run; if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); } } while (0)

static std::vector<uint8_t> boot_sector() {
    std::vector<uint8_t> s(512, 0);
    s[510] = 0x55; s[511] = 0xAA;
    return s;
}
static void put(std::vector<uint8_t>& s, size_t off, const char* t) { std::memcpy(&s[off], t, std::strlen(t)); }
static uint32_t ident(const std::vector<uint8_t>& s) { uint32_t fs = 999; CHECK(rx_identify_filesystem(s.data(), (uint32_t)s.size(), &fs) == RX_OK); return fs; }

static void test_abi() {
    CHECK(rx_get_abi_version() == RX_ABI_VERSION);
    CHECK(std::wcslen(rx_get_version_string()) > 0);
    CHECK(rx_sizeof_volume_info() == sizeof(RxVolumeInfo));
    CHECK(rx_sizeof_probe_result() == sizeof(RxProbeResult));
#ifdef _WIN32
    CHECK(sizeof(RxVolumeInfo) == 216);     // must match the C# struct in RecoverX.Core/Native/NativeMethods.cs
    CHECK(sizeof(RxProbeResult) == 32);
#endif
}

static void test_identify() {
    auto s = boot_sector(); put(s, 3, "NTFS    "); CHECK(ident(s) == RX_FS_NTFS);
    s = boot_sector(); put(s, 3, "EXFAT   "); CHECK(ident(s) == RX_FS_EXFAT);
    s = boot_sector(); put(s, 3, "EXFAT   "); s[20] = 1; CHECK(ident(s) == RX_FS_UNKNOWN);   // MustBeZero violated
    s = boot_sector(); put(s, 82, "FAT32   "); CHECK(ident(s) == RX_FS_FAT32);
    s = boot_sector(); put(s, 54, "FAT16   "); CHECK(ident(s) == RX_FS_FAT16);
    s = boot_sector(); put(s, 54, "FAT12   "); CHECK(ident(s) == RX_FS_FAT12);
    s = boot_sector(); CHECK(ident(s) == RX_FS_UNKNOWN);
    s = boot_sector(); put(s, 3, "NTFS    "); s[511] = 0; CHECK(ident(s) == RX_FS_UNKNOWN);  // missing 55AA signature
    uint32_t fs = 0;
    CHECK(rx_identify_filesystem(s.data(), 100, &fs) == RX_ERR_INVALID_ARG);
    CHECK(rx_identify_filesystem(nullptr, 512, &fs) == RX_ERR_INVALID_ARG);
    CHECK(rx_identify_filesystem(s.data(), 512, nullptr) == RX_ERR_INVALID_ARG);
}

static void test_drive_letter_parsing() {
    wchar_t l = 0;
    CHECK(rx::parse_drive_letter(L"E:", l) && l == L'E');
    CHECK(rx::parse_drive_letter(L"e:\\", l) && l == L'E');
    CHECK(rx::parse_drive_letter(L"C:/", l) && l == L'C');
    // Arbitrary device paths must be rejected: the engine only addresses lettered volumes.
    CHECK(!rx::parse_drive_letter(L"", l));
    CHECK(!rx::parse_drive_letter(L"E", l));
    CHECK(!rx::parse_drive_letter(L"E:\\Windows", l));
    CHECK(!rx::parse_drive_letter(L"\\\\.\\E:", l));
    CHECK(!rx::parse_drive_letter(L"\\\\.\\PhysicalDrive0", l));
    CHECK(!rx::parse_drive_letter(L"1:", l));
}

static void test_destination_guard() {
    CHECK(rx::destination_verdict_by_letters(L"E:\\", L"E:\\Recovered") == RX_DEST_SAME_VOLUME);
    CHECK(rx::destination_verdict_by_letters(L"e:", L"E:/x") == RX_DEST_SAME_VOLUME);
    CHECK(rx::destination_verdict_by_letters(L"E:\\", L"D:\\Recovered") == RX_DEST_SAFE);
    CHECK(rx::destination_verdict_by_letters(L"E:\\", L"\\\\server\\share\\r") == RX_DEST_SAFE);
    CHECK(rx::destination_verdict_by_letters(L"E:\\", L"relative\\path") == RX_DEST_UNVERIFIABLE);
    CHECK(rx::destination_verdict_by_letters(L"junk", L"D:\\x") == RX_DEST_UNVERIFIABLE);

    int32_t v = -1;
#ifndef _WIN32
    CHECK(rx_validate_destination(L"E:\\", L"E:\\Recovered", &v) == RX_OK && v == RX_DEST_SAME_VOLUME);
    CHECK(rx_validate_destination(L"E:\\", L"F:\\Recovered", &v) == RX_OK && v == RX_DEST_SAFE);
#endif
    CHECK(rx_validate_destination(nullptr, L"D:\\", &v) == RX_ERR_INVALID_ARG);
    CHECK(rx_validate_destination(L"E:\\", nullptr, &v) == RX_ERR_INVALID_ARG);
    CHECK(rx_validate_destination(L"E:\\", L"D:\\", nullptr) == RX_ERR_INVALID_ARG);
    CHECK(rx_validate_destination(L"\\\\.\\E:", L"D:\\", &v) == RX_ERR_INVALID_ARG);
}

static void test_cancel() {
    RxCancelHandle t = rx_cancel_create();
    CHECK(t != nullptr);
    CHECK(rx_cancel_is_requested(t) == 0);
    rx_cancel_request(t);
    CHECK(rx_cancel_is_requested(t) == 1);
    rx_cancel_destroy(t);
    CHECK(rx_cancel_is_requested(nullptr) == 0);    // null-safe
    rx_cancel_request(nullptr);
    rx_cancel_destroy(nullptr);
}

static void test_arg_validation() {
    uint32_t n = 0;
    CHECK(rx_enumerate_volumes(nullptr, 0, nullptr) == RX_ERR_INVALID_ARG);
    CHECK(rx_query_volume(nullptr, nullptr) == RX_ERR_INVALID_ARG);
    RxVolumeInfo info; std::memset(&info, 0, sizeof info);             // struct_size left 0 on purpose
    CHECK(rx_query_volume(L"E:\\", &info) == RX_ERR_INVALID_ARG);
    CHECK(rx_volume_open(nullptr, nullptr) == RX_ERR_INVALID_ARG);
    CHECK(rx_volume_close(nullptr) == RX_ERR_INVALID_ARG);
    uint8_t b[8]; uint32_t got = 0;
    CHECK(rx_volume_read(nullptr, 0, b, 8, &got) == RX_ERR_INVALID_ARG);
    CHECK(rx_probe_volume(nullptr, nullptr) == RX_ERR_INVALID_ARG);
    (void)n;
#ifndef _WIN32
    CHECK(rx_enumerate_volumes(&info, 1, &n) == RX_ERR_UNSUPPORTED);   // stub platform has no hardware enumeration
#endif
}

#ifndef _WIN32
static std::string write_image(const char* name, size_t size, bool ntfs) {
    std::string path = std::string("/tmp/") + name;
    std::vector<uint8_t> data(size);
    for (size_t i = 0; i < size; ++i) data[i] = (uint8_t)((i * 7 + 3) & 0xFF);
    if (ntfs && size >= 512) { std::memset(data.data(), 0, 512); std::memcpy(&data[3], "NTFS    ", 8); data[510] = 0x55; data[511] = 0xAA; }
    FILE* f = std::fopen(path.c_str(), "wb");
    std::fwrite(data.data(), 1, size, f);
    std::fclose(f);
    return path;
}
static std::wstring wide(const std::string& s) { return std::wstring(s.begin(), s.end()); }

static void test_volume_reads() {
    const size_t N = 10000;                       // deliberately not a sector multiple
    std::string path = write_image("rx_test_plain.img", N, false);
    std::wstring wp = wide(path);
    RxVolumeHandle h = nullptr;
    CHECK(rx_volume_open(wp.c_str(), &h) == RX_OK && h);
    RxVolumeGeometry g; g.struct_size = sizeof g;
    CHECK(rx_volume_get_geometry(h, &g) == RX_OK && g.bytes_per_sector == 512 && g.total_bytes == N);

    auto expect = [&](uint64_t off, uint32_t len, uint32_t want_n) {
        std::vector<uint8_t> buf(len + 1, 0xEE);
        uint32_t got = 12345;
        RxStatus st = rx_volume_read(h, off, buf.data(), len, &got);
        bool ok = st == RX_OK && got == want_n;
        for (uint32_t i = 0; ok && i < got; ++i) ok = buf[i] == (uint8_t)(((off + i) * 7 + 3) & 0xFF);
        CHECK(ok);
        CHECK(buf[len] == 0xEE);                  // never writes past the caller's buffer
    };
    expect(0, 512, 512);                          // aligned
    expect(100, 1000, 1000);                      // unaligned start and length
    expect(511, 2, 2);                            // straddles a sector boundary
    expect(512, 1, 1);
    expect(9990, 100, 10);                        // clamped at end of volume
    expect(0, 10000, 10000);                      // whole volume
    expect(9999, 1, 1);
    expect(10000, 64, 0);                         // at end: zero bytes, still OK
    expect(50000, 64, 0);                         // past end
    expect(5, 0, 0);                              // zero length

    uint8_t b[4]; uint32_t got = 0;
    CHECK(rx_volume_read(h, 0, b, 17u * 1024u * 1024u, &got) == RX_ERR_INVALID_ARG);   // over the 16 MB cap
    CHECK(rx_volume_close(h) == RX_OK);

    CHECK(rx_volume_open(L"/tmp/definitely_not_here.img", &h) == RX_ERR_NOT_FOUND && h == nullptr);
    std::remove(path.c_str());
}

static void test_probe() {
    std::string path = write_image("rx_test_ntfs.img", 4096, true);
    std::wstring wp = wide(path);
    RxProbeResult p; std::memset(&p, 0, sizeof p); p.struct_size = sizeof p;
    CHECK(rx_probe_volume(wp.c_str(), &p) == RX_OK);
    CHECK(p.fs_id == RX_FS_NTFS && p.total_bytes == 4096 && p.bytes_per_sector == 512 && p.bytes_read == 512);
    std::remove(path.c_str());

    path = write_image("rx_test_tiny.img", 100, false);     // smaller than a boot sector
    wp = wide(path);
    p.struct_size = sizeof p;
    CHECK(rx_probe_volume(wp.c_str(), &p) == RX_OK && p.fs_id == RX_FS_UNKNOWN);
    std::remove(path.c_str());
}
#endif

int main() {
    test_abi();
    test_identify();
    test_drive_letter_parsing();
    test_destination_guard();
    test_cancel();
    test_arg_validation();
#ifndef _WIN32
    test_volume_reads();
    test_probe();
#endif
    std::printf("%d checks, %d failed\n", g_run, g_failed);
    return g_failed == 0 ? 0 : 1;
}
