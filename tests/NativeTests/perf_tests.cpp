// Large-volume and performance test (Windows; run manually: perf_tests [size_gb], default 16).
// Builds a SPARSE image in %TEMP% (only the planted files use real disk space), plants intact files beyond the 4 GB and 8 GB
// marks and in the last megabyte, runs a whole-volume Deep Scan, and checks that every file is found at its exact 64-bit
// offset with identical content. Reports throughput and the process's peak memory. The image is deleted afterwards.
#ifdef _WIN32
#include <windows.h>
#include <psapi.h>
#endif
#include "rx_engine.h"
#include "sha256.h"
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iterator>
#include <string>
#include <vector>

#ifndef RX_FIXTURE_DIR
#define RX_FIXTURE_DIR "."
#endif

int main(int argc, char** argv) {
#ifndef _WIN32
    std::printf("perf_tests: Windows only\n");
    return 0;
#else
    uint64_t gb = argc > 1 ? std::strtoull(argv[1], nullptr, 10) : 16;
    const uint64_t size = gb << 30;
    std::string dir = RX_FIXTURE_DIR;
    std::ifstream fx(dir + "/carve_basic.img", std::ios::binary);
    std::vector<uint8_t> carve((std::istreambuf_iterator<char>(fx)), std::istreambuf_iterator<char>());
    if (carve.size() < 300000) { std::printf("carve fixture missing\n"); return 2; }
    struct Plant { const char* name; uint64_t src, len, at; };
    Plant plants[] = {
        { "photo_exif.jpg", 65536, 28889, (5ull << 30) + 4096 },
        { "image.png", 114688, 138253, (9ull << 30) + 12288 },
        { "letter.pdf", 319488, 105847, size - (1ull << 20) },
    };
    char tmp[MAX_PATH]; GetTempPathA(MAX_PATH, tmp);
    std::string path = std::string(tmp) + "rx_perf.img";
    HANDLE h = CreateFileA(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) { std::printf("cannot create %s\n", path.c_str()); return 2; }
    DWORD ret = 0;
    if (!DeviceIoControl(h, FSCTL_SET_SPARSE, nullptr, 0, nullptr, 0, &ret, nullptr)) { std::printf("sparse files not supported here\n"); CloseHandle(h); DeleteFileA(path.c_str()); return 2; }
    LARGE_INTEGER li; li.QuadPart = (LONGLONG)size;
    SetFilePointerEx(h, li, nullptr, FILE_BEGIN); SetEndOfFile(h);
    std::vector<std::string> want;
    for (const Plant& p : plants) {
        li.QuadPart = (LONGLONG)p.at; SetFilePointerEx(h, li, nullptr, FILE_BEGIN);
        DWORD w = 0; WriteFile(h, carve.data() + p.src, (DWORD)p.len, &w, nullptr);
        Sha256 s; s.update(carve.data() + p.src, (size_t)p.len); want.push_back(s.hex());
    }
    CloseHandle(h);

    int failed = 0;
    std::wstring wpath(path.begin(), path.end());
    RxCarveHandle c = nullptr;
    if (rx_carve_create(wpath.c_str(), nullptr, &c) != RX_OK) { std::printf("carve_create failed\n"); DeleteFileA(path.c_str()); return 1; }
    auto t0 = std::chrono::steady_clock::now();
    RxStatus st = rx_carve_run(c, nullptr);
    double secs = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
    RxCarveProgress p; p.struct_size = sizeof p; rx_carve_progress(c, &p);
    uint32_t n = rx_carve_result_count(c);
    std::vector<RxCarveEntry> res(n); for (auto& e : res) e.struct_size = sizeof e;
    uint32_t got = 0; if (n) rx_carve_get_results(c, 0, res.data(), n, &got);
    for (size_t k = 0; k < 3; ++k) {
        bool ok = false;
        for (uint32_t i = 0; i < got; ++i) {
            if (res[i].offset != plants[k].at) continue;
            Sha256 s; std::vector<uint8_t> buf(1 << 20); uint64_t off = 0;
            while (off < res[i].size) { uint32_t g = 0; if (rx_carve_read_file(c, i, off, buf.data(), (uint32_t)buf.size(), &g) != RX_OK || !g) break; s.update(buf.data(), g); off += g; }
            ok = res[i].size == plants[k].len && s.hex() == want[k] && res[i].condition == RX_COND_VERIFIED;
        }
        std::printf("  %-15s at %6.2f GB: %s\n", plants[k].name, plants[k].at / 1073741824.0, ok ? "found intact" : "NOT FOUND / DIFFERENT");
        if (!ok) ++failed;
    }
    if (got != 3) { std::printf("  unexpected result count %u\n", got); ++failed; }
    rx_carve_destroy(c);
    PROCESS_MEMORY_COUNTERS pmc; pmc.cb = sizeof pmc;
    GetProcessMemoryInfo(GetCurrentProcess(), &pmc, sizeof pmc);
    std::printf("  Deep Scan of %llu GB: status %d, %.1f s, %.0f MB/s, peak memory %.1f MB\n", (unsigned long long)gb, st, secs,
                (double)p.bytes_total / 1048576.0 / secs, pmc.PeakWorkingSetSize / 1048576.0);
    DeleteFileA(path.c_str());
    std::printf("%s\n", failed ? "PERF TEST FAILED" : "perf test passed");
    return failed ? 1 : 0;
#endif
}
