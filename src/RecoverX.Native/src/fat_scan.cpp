// FAT12/16/32 and exFAT Quick Scan: walks every directory (live and deleted), reports deleted files with names, folders, sizes
// and dates, grades them with the allocation map and a content check, and serves their data for recovery. Read-only.
#include "internal.h"
#include "fat_format.h"
#include "ntfs_format.h"      // rx::ntfs::check_header (file-type evidence shared with the NTFS scan)
#include <algorithm>
#include <atomic>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <new>
#include <unordered_set>

using namespace rx::fat;

namespace {

const uint32_t kDefaultMaxResults = 250000;
const uint32_t kHardMaxResults = 5000000;
const uint64_t kMaxFatEntries = 64ull * 1024 * 1024;     // 256 MB table: a 2 TB FAT32 volume with 32 KB clusters
const uint64_t kMaxDirBytes = 64ull * 1024 * 1024;
const uint32_t kMaxDeletedDirClusters = 64;
const uint32_t kMaxDepth = 64;
const uint32_t kHeaderBytes = 512;

uint16_t r16(const uint8_t* p) { return (uint16_t)(p[0] | (p[1] << 8)); }
uint32_t r32(const uint8_t* p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }
uint64_t r64(const uint8_t* p) { return (uint64_t)r32(p) | ((uint64_t)r32(p + 4) << 32); }

struct Source {
    RxVolumeHandle h = nullptr;
    std::mutex m;
    bool read(uint64_t off, void* buf, uint32_t len) {
        std::lock_guard<std::mutex> l(m);
        uint32_t got = 0;
        return rx_volume_read(h, off, buf, len, &got) == RX_OK && got == len;
    }
};

struct Ext { uint32_t cluster, count; };

struct FEntry {
    uint64_t dirent = 0, size = 0, init = 0, created = 0, modified = 0, accessed = 0;
    uint32_t flags = 0, cond = RX_COND_UNKNOWN, header = RX_HDR_NOT_CHECKED, ctotal = 0, cuse = 0;
    std::wstring path, name;
    std::vector<Ext> runs;
};

enum JobKind { JOB_FIXED_ROOT, JOB_CHAIN, JOB_CONTIGUOUS, JOB_DELETED_FAT_DIR };
struct DirJob { JobKind kind; uint32_t cluster; uint64_t length; std::wstring path; bool deleted; uint32_t depth; };

void add_run(std::vector<Ext>& runs, uint32_t c) {
    if (!runs.empty() && runs.back().cluster + runs.back().count == c) ++runs.back().count;
    else runs.push_back(Ext{ c, 1 });
}

} // namespace

struct RxFatScan {
    Source src;
    RxVolumeHandle vol = nullptr;
    Volume v;
    RxScanOptions opts;
    std::vector<uint32_t> fat;                 // next-cluster table, index = cluster number
    std::vector<uint8_t> alloc;                // 1 bit per cluster (bit i = cluster i+2), 1 = allocated
    bool alloc_ok = false;
    std::vector<FEntry> results;
    std::atomic<uint32_t> phase{RX_PHASE_OPENING}, rec_total{0}, rec_done{0}, ncand{0}, pflags{0}, pstatus{0}, bad{0};
    std::atomic<bool> running{false};

    ~RxFatScan() { if (vol) rx_volume_close(vol); }

    uint32_t next(uint64_t c) const { return c < fat.size() ? fat[(size_t)c] : 0xFFFFFFFFu; }

    bool is_used(uint64_t c) const {
        uint64_t i = c - 2;
        return (alloc[(size_t)(i >> 3)] >> (i & 7)) & 1;
    }

    int64_t used(uint64_t c, uint64_t n) const {
        if (!alloc_ok) return -1;
        int64_t u = 0;
        for (uint64_t k = 0; k < n; ++k) {
            uint64_t x = c + k;
            if (!v.valid_cluster(x)) { ++u; continue; }       // outside the volume: certainly not this file's data
            if (is_used(x)) ++u;
        }
        return u;
    }

    bool find_run(uint64_t from, bool want_used, uint64_t& s, uint64_t& e) const {
        if (!alloc_ok) return false;
        const uint64_t end = (uint64_t)v.cluster_count + 2;
        const uint8_t skip = want_used ? 0x00 : 0xFF;
        auto scan = [&](uint64_t c, bool state) {
            while (c < end) {
                uint64_t i = c - 2;
                if ((i & 7) == 0 && i + 8 <= v.cluster_count && alloc[(size_t)(i >> 3)] == (state ? 0x00 : 0xFF)) { c += 8; continue; }
                if (is_used(c) == state) break;
                ++c;
            }
            return c;
        };
        (void)skip;
        uint64_t c = scan(std::max<uint64_t>(from, 2), want_used);
        if (c >= end) return false;
        s = c;
        e = scan(c, !want_used);
        return true;
    }

    // ---- loading ----
    RxStatus load_fat() {
        uint64_t entries = (uint64_t)v.cluster_count + 2;
        if (entries > kMaxFatEntries) return RX_ERR_UNSUPPORTED;
        uint64_t bytes = v.kind == KIND_FAT12 ? (entries * 3 + 1) / 2 : v.kind == KIND_FAT16 ? entries * 2 : entries * 4;
        if (bytes > v.fat_bytes) bytes = v.fat_bytes;
        std::vector<uint8_t> raw((size_t)bytes);
        const uint32_t chunk = 4u * 1024 * 1024;
        for (uint64_t off = 0; off < bytes; off += chunk) {
            uint32_t n = (uint32_t)std::min<uint64_t>(chunk, bytes - off);
            if (!src.read(v.fat_offset + off, raw.data() + off, n)) return RX_ERR_IO;
        }
        fat.assign((size_t)entries, 0);
        for (uint64_t c = 0; c < entries; ++c) {
            uint32_t x;
            if (v.kind == KIND_FAT12) {
                uint64_t o = c * 3 / 2;
                if (o + 1 >= raw.size()) break;
                uint16_t w = (uint16_t)(raw[(size_t)o] | (raw[(size_t)o + 1] << 8));
                x = (c & 1) ? (w >> 4) : (w & 0x0FFF);
            } else if (v.kind == KIND_FAT16) { if (c * 2 + 1 >= raw.size()) break; x = r16(&raw[(size_t)(c * 2)]); }
            else { if (c * 4 + 3 >= raw.size()) break; x = r32(&raw[(size_t)(c * 4)]); if (v.kind == KIND_FAT32) x &= 0x0FFFFFFF; }
            fat[(size_t)c] = x;
        }
        if (v.kind != KIND_EXFAT) {                            // FAT12/16/32: a non-zero entry means allocated
            alloc.assign((size_t)((v.cluster_count + 7) / 8), 0);
            for (uint64_t c = 2; c < entries; ++c) if (fat[(size_t)c]) alloc[(size_t)((c - 2) >> 3)] |= (uint8_t)(1u << ((c - 2) & 7));
            alloc_ok = true;
        }
        return RX_OK;
    }

    // Cluster list of a chain (live structures). Stops at end-of-chain, at an invalid link, at a loop, or after max_clusters.
    std::vector<Ext> chain(uint32_t start, uint64_t max_clusters) const {
        std::vector<Ext> runs;
        uint64_t c = start, n = 0;
        const uint32_t eoc = v.eoc_min();
        while (v.valid_cluster(c) && n < max_clusters && n <= v.cluster_count) {
            add_run(runs, (uint32_t)c);
            ++n;
            uint32_t nx = next(c);
            if (nx >= eoc || nx < 2) break;
            c = nx;
        }
        return runs;
    }

    std::vector<Ext> contiguous(uint32_t start, uint64_t clusters) const {
        std::vector<Ext> runs;
        if (!v.valid_cluster(start) || clusters == 0) return runs;
        uint64_t last = std::min<uint64_t>((uint64_t)start + clusters, (uint64_t)v.cluster_count + 2);
        runs.push_back(Ext{ start, (uint32_t)(last - start) });
        return runs;
    }

    bool read_runs(const std::vector<Ext>& runs, uint64_t max_bytes, std::vector<uint8_t>& out) {
        out.clear();
        for (const Ext& r : runs) {
            uint64_t bytes = (uint64_t)r.count * v.cluster_size;
            if (out.size() + bytes > max_bytes) bytes = max_bytes - out.size();
            if (bytes == 0) break;
            size_t at = out.size();
            out.resize(at + (size_t)bytes);
            if (!src.read(v.cluster_offset(r.cluster), out.data() + at, (uint32_t)bytes)) { out.resize(at); return !out.empty(); }
        }
        return true;
    }

    RxStatus load_exfat_bitmap() {
        std::vector<uint8_t> root;
        read_runs(chain(v.root_cluster, 256), kMaxDirBytes, root);
        for (size_t i = 0; i + 32 <= root.size(); i += 32) {
            uint8_t t = root[i];
            if (t == 0) break;
            if (t != 0x81 || (root[i + 1] & 1)) continue;                   // first allocation bitmap
            uint32_t first = r32(&root[i + 20]);
            uint64_t len = r64(&root[i + 24]);
            if (!v.valid_cluster(first) || len < (v.cluster_count + 7) / 8 || len > 512ull * 1024 * 1024) return RX_ERR_CORRUPT;
            uint64_t clusters = (len + v.cluster_size - 1) / v.cluster_size;
            std::vector<Ext> runs = chain(first, clusters);
            uint64_t have = 0; for (const Ext& r : runs) have += r.count;
            if (have < clusters) runs = contiguous(first, clusters);         // chain not recorded: bitmap stored contiguously
            std::vector<uint8_t> bits;
            if (!read_runs(runs, len, bits) || bits.size() < (v.cluster_count + 7) / 8) return RX_ERR_IO;
            bits.resize((size_t)((v.cluster_count + 7) / 8));
            alloc.swap(bits);
            alloc_ok = true;
            return RX_OK;
        }
        return RX_ERR_NOT_FOUND;
    }

    // ---- directory walk ----
    bool dir_bytes(const DirJob& j, std::vector<uint8_t>& out) {
        out.clear();
        switch (j.kind) {
        case JOB_FIXED_ROOT:
            out.resize(v.root_bytes);
            return v.root_bytes == 0 || src.read(v.root_offset, out.data(), v.root_bytes);
        case JOB_CHAIN: {
            uint64_t max_c = j.length ? (j.length + v.cluster_size - 1) / v.cluster_size : kMaxDirBytes / v.cluster_size;
            return read_runs(chain(j.cluster, std::min<uint64_t>(max_c, kMaxDirBytes / v.cluster_size)), kMaxDirBytes, out);
        }
        case JOB_CONTIGUOUS: {
            uint64_t n = std::min<uint64_t>((j.length + v.cluster_size - 1) / v.cluster_size, kMaxDirBytes / v.cluster_size);
            return read_runs(contiguous(j.cluster, n), kMaxDirBytes, out);
        }
        case JOB_DELETED_FAT_DIR: {
            // The chain was cleared: read clusters from the start while they are free and look like directory data.
            // The first cluster must still begin with the "." and ".." entries, or the folder was overwritten.
            std::vector<uint8_t> c(v.cluster_size);
            for (uint32_t k = 0; k < kMaxDeletedDirClusters; ++k) {
                uint64_t cl = (uint64_t)j.cluster + k;
                if (!v.valid_cluster(cl) || (alloc_ok && is_used(cl))) break;
                if (!src.read(v.cluster_offset(cl), c.data(), v.cluster_size)) break;
                if (k == 0 && !(c[0] == '.' && c[1] == ' ' && (c[11] & 0x10) && c[32] == '.' && c[33] == '.' && (c[43] & 0x10))) return false;
                if (k > 0 && !looks_like_fat_dir(c)) break;
                out.insert(out.end(), c.begin(), c.end());
                bool ended = false;
                for (size_t i = 0; i < c.size(); i += 32) if (c[i] == 0) { ended = true; break; }
                if (ended) break;
            }
            return !out.empty();
        }
        }
        return false;
    }

    static bool looks_like_fat_dir(const std::vector<uint8_t>& c) {
        size_t good = 0, total = 0;
        for (size_t i = 0; i < c.size(); i += 32) {
            if (c[i] == 0) break;
            ++total;
            uint8_t a = c[i + 11];
            bool lfn = (a & 0x3F) == 0x0F;
            if (lfn || (!(a & 0xC0) && c[i] >= 0x20)) ++good;
        }
        return total > 0 && good * 10 >= total * 9;
    }

    void report(FEntry&& e) {
        if (results.size() >= opts.max_results) { pflags |= RX_SCANP_TRUNCATED; return; }
        results.push_back(std::move(e));
        ncand = (uint32_t)results.size();
    }

    void walk_fat(const std::vector<uint8_t>& d, const DirJob& j, uint64_t dir_disk_base, std::deque<DirJob>& queue) {
        std::vector<const uint8_t*> lfn;
        for (size_t i = 0; i + 32 <= d.size(); i += 32) {
            const uint8_t* e = &d[i];
            if (e[0] == 0x00) break;
            uint8_t attr = e[11];
            if ((attr & 0x3F) == 0x0F) { lfn.push_back(e); continue; }
            std::vector<const uint8_t*> parts;
            parts.swap(lfn);
            if ((attr & 0x08) && !(attr & 0x10)) continue;                    // volume label
            if (e[0] == '.' && (e[1] == ' ' || e[1] == '.')) continue;         // "." and ".."
            bool deleted = e[0] == 0xE5 || j.deleted;
            bool guessed = false;
            std::wstring name = short_name(e, e[0] == 0xE5, guessed);
            if (!parts.empty()) {
                uint8_t ck = parts[0][13];
                bool same = true;
                for (const uint8_t* p : parts) if (p[13] != ck) same = false;
                bool match = false;
                if (same) {
                    if (e[0] != 0xE5) match = lfn_checksum(e) == ck;
                    else {
                        uint8_t tmp[11]; std::memcpy(tmp, e, 11);
                        for (int b = 0x20; b < 0x100 && !match; ++b) { if (b == 0xE5) continue; tmp[0] = (uint8_t)b; match = lfn_checksum(tmp) == ck; }
                    }
                }
                if (match) {
                    std::wstring ln;
                    static const int offs[13] = { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 };
                    bool done = false;
                    for (size_t k = parts.size(); k-- > 0 && !done;) {               // on disk: last part first
                        for (int o : offs) {
                            uint16_t ch = r16(parts[k] + o);
                            if (ch == 0x0000) { done = true; break; }
                            if (ch == 0xFFFF) continue;
                            ln.push_back(ch < 0x20 || ch == L'\\' || ch == L'/' ? L'_' : (wchar_t)ch);
                        }
                    }
                    if (!ln.empty()) { name = ln; guessed = false; }
                }
            }
            std::wstring path = j.path + L"\\" + name;
            uint32_t start = r16(e + 26);
            if (v.kind == KIND_FAT32) start |= (uint32_t)r16(e + 20) << 16;
            if (attr & 0x10) {
                if (j.depth + 1 >= kMaxDepth || !v.valid_cluster(start)) continue;
                queue.push_back(DirJob{ deleted ? JOB_DELETED_FAT_DIR : JOB_CHAIN, start, 0, path, deleted, j.depth + 1 });
                rec_total.fetch_add(1);
                continue;
            }
            if (!deleted) continue;
            FEntry f;
            f.dirent = dir_disk_base == ~0ull ? 0 : dir_disk_base + i;
            f.path = path; f.name = name;
            f.size = f.init = r32(e + 28);
            f.created = rx::platform_local_to_utc(dos_to_filetime(r16(e + 16), r16(e + 14), e[13]));
            f.modified = rx::platform_local_to_utc(dos_to_filetime(r16(e + 24), r16(e + 22), 0));
            f.accessed = rx::platform_local_to_utc(dos_to_filetime(r16(e + 18), 0, 0));
            if (guessed) f.flags |= RX_ENT_NAME_GUESSED;
            if (f.size > 0) {
                if (!v.valid_cluster(start)) f.flags |= RX_ENT_BAD_RUNS;
                else f.runs = contiguous(start, (f.size + v.cluster_size - 1) / v.cluster_size);   // chain cleared on deletion
            }
            report(std::move(f));
        }
    }

    void walk_exfat(const std::vector<uint8_t>& d, const DirJob& j, uint64_t dir_disk_base, std::deque<DirJob>& queue) {
        size_t i = 0;
        while (i + 32 <= d.size()) {
            const uint8_t* e = &d[i];
            uint8_t t = e[0];
            if (t == 0x00) break;
            if ((t & 0x7F) != 0x05) { i += 32; continue; }                    // only file entry sets (0x85 / deleted 0x05)
            uint32_t sc = e[1];
            if (sc < 2 || sc > 18 || i + 32 * (sc + 1) > d.size() || (e[32] & 0x7F) != 0x40) { i += 32; continue; }
            bool deleted = !(t & 0x80) || j.deleted;
            bool intact = r16(e + 2) == exfat_set_checksum(e, sc + 1);
            if (!intact && deleted) { i += 32; continue; }                     // partly overwritten set: name not trustworthy
            const uint8_t* st = e + 32;
            uint32_t nl = st[3], flags = st[1], first = r32(st + 20);
            uint64_t vdl = r64(st + 8), dl = r64(st + 24);
            std::wstring name;
            for (uint32_t k = 2; k <= sc && name.size() < nl; ++k) {
                const uint8_t* ne = e + 32 * k;
                if ((ne[0] & 0x7F) != 0x41) break;
                for (int o = 2; o < 32 && name.size() < nl; o += 2) {
                    uint16_t ch = r16(ne + o);
                    name.push_back(ch < 0x20 || ch == L'\\' || ch == L'/' ? L'_' : (wchar_t)ch);
                }
            }
            if (name.empty()) name = L"_";
            std::wstring path = j.path + L"\\" + name;
            uint16_t attr = r16(e + 4);
            if (attr & 0x10) {
                if (j.depth + 1 < kMaxDepth && v.valid_cluster(first))
                    { queue.push_back(DirJob{ (flags & 2) ? JOB_CONTIGUOUS : JOB_CHAIN, first, dl, path, deleted, j.depth + 1 }); rec_total.fetch_add(1); }
            } else if (deleted) {
                FEntry f;
                f.dirent = dir_disk_base == ~0ull ? 0 : dir_disk_base + i;
                f.path = path; f.name = name;
                f.size = dl; f.init = std::min(vdl, dl);
                bool local = false;
                f.created = exfat_to_filetime(r32(e + 8), e[20], e[22], local); if (local) f.created = rx::platform_local_to_utc(f.created);
                f.modified = exfat_to_filetime(r32(e + 12), e[21], e[23], local); if (local) f.modified = rx::platform_local_to_utc(f.modified);
                f.accessed = exfat_to_filetime(r32(e + 16), 0, e[24], local); if (local) f.accessed = rx::platform_local_to_utc(f.accessed);
                if (dl > 0) {
                    uint64_t need = (dl + v.cluster_size - 1) / v.cluster_size;
                    if (!v.valid_cluster(first)) f.flags |= RX_ENT_BAD_RUNS;
                    else if (flags & 2) f.runs = contiguous(first, need);
                    else {
                        // FAT chain: usable only if it is still exactly this file's length, ends cleanly, and none of it is allocated.
                        std::vector<Ext> ch = chain(first, need + 1);
                        uint64_t got = 0; for (const Ext& r : ch) got += r.count;
                        uint64_t last = ch.empty() ? 0 : (uint64_t)ch.back().cluster + ch.back().count - 1;
                        bool ok = got == need && next(last) == 0xFFFFFFFFu;
                        if (ok && alloc_ok) for (const Ext& r : ch) if (used(r.cluster, r.count) > 0) ok = false;
                        if (ok && ch.size() > 1) { f.runs = ch; f.flags |= RX_ENT_CHAIN_FOLLOWED; }
                        else f.runs = contiguous(first, need);
                    }
                }
                report(std::move(f));
            }
            i += 32 * (sc + 1);
        }
    }

    // Disk offset of a directory's first byte when its data is one contiguous piece (used for the entry id); ~0 otherwise.
    uint64_t base_of(const DirJob& j) const {
        if (j.kind == JOB_FIXED_ROOT) return v.root_offset;
        return v.valid_cluster(j.cluster) ? v.cluster_offset(j.cluster) : ~0ull;
    }

    RxStatus run(RxCancelHandle cancel) {
        phase = RX_PHASE_SCANNING;
        std::deque<DirJob> queue;
        if (v.kind == KIND_FAT12 || v.kind == KIND_FAT16) queue.push_back(DirJob{ JOB_FIXED_ROOT, 0, 0, L"", false, 0 });
        else queue.push_back(DirJob{ JOB_CHAIN, v.root_cluster, 0, L"", false, 0 });
        rec_total = 1;
        std::unordered_set<uint32_t> seen;
        std::vector<uint8_t> d;
        bool cancelled = false;
        while (!queue.empty()) {
            if (rx::poll_cancel(cancel, &pflags, RX_SCANP_PAUSED)) { cancelled = true; break; }
            DirJob j = std::move(queue.front());
            queue.pop_front();
            rec_done.fetch_add(1);
            if (j.kind != JOB_FIXED_ROOT && !seen.insert(j.cluster).second) continue;   // loops / shared clusters
            if (!dir_bytes(j, d)) { if (!j.deleted) bad.fetch_add(1); continue; }
            if (v.kind == KIND_EXFAT) walk_exfat(d, j, base_of(j), queue);
            else walk_fat(d, j, base_of(j), queue);
            if (pflags & RX_SCANP_TRUNCATED) break;
        }
        if (!cancelled) {
            phase = RX_PHASE_VALIDATING;
            rec_total = (uint32_t)results.size(); rec_done = 0;
            std::vector<uint32_t> order(results.size());
            for (uint32_t k = 0; k < order.size(); ++k) order[k] = k;
            std::sort(order.begin(), order.end(), [&](uint32_t a, uint32_t b) {
                uint32_t ca = results[a].runs.empty() ? 0 : results[a].runs[0].cluster, cb = results[b].runs.empty() ? 0 : results[b].runs[0].cluster;
                return ca < cb;
            });
            const bool check_content = (opts.flags & RX_SCANF_VALIDATE_CONTENT) != 0;
            uint32_t done = 0;
            for (uint32_t k : order) {
                if ((done++ & 63) == 0 && rx::poll_cancel(cancel, &pflags, RX_SCANP_PAUSED)) { cancelled = true; break; }
                rec_done = done;
                grade(results[k], check_content);
            }
        }
        phase = RX_PHASE_DONE;
        return cancelled ? RX_ERR_CANCELLED : RX_OK;
    }

    void grade(FEntry& f, bool check_content) {
        if (f.size == 0) { f.cond = RX_COND_EMPTY; return; }
        if (f.flags & RX_ENT_BAD_RUNS) { f.cond = RX_COND_LOST; return; }
        uint64_t total = 0; for (const Ext& r : f.runs) total += r.count;
        uint64_t need = (f.size + v.cluster_size - 1) / v.cluster_size;
        f.ctotal = (uint32_t)std::min<uint64_t>(total, 0xFFFFFFFFu);
        if (total < need) { f.flags |= RX_ENT_BAD_RUNS; f.cond = RX_COND_LOST; return; }   // runs past the end of the volume
        int64_t u = 0;
        for (const Ext& r : f.runs) { int64_t x = used(r.cluster, r.count); if (x < 0) { u = -1; break; } u += x; }
        if (u < 0) { f.cond = RX_COND_UNKNOWN; return; }
        f.cuse = (uint32_t)std::min<int64_t>(u, 0xFFFFFFFF);
        if ((uint64_t)u >= total) { f.cond = RX_COND_LOST; return; }
        if (u > 0) { f.cond = RX_COND_PARTIAL; return; }
        f.cond = RX_COND_LIKELY;
        if (!check_content) return;
        uint8_t head[kHeaderBytes];
        uint32_t want = (uint32_t)std::min<uint64_t>(f.size, kHeaderBytes);
        if (read_data(f, 0, head, want) == want) {
            f.header = rx::ntfs::check_header(f.name, head, want);
            if (f.header == RX_HDR_MATCH) f.cond = RX_COND_VERIFIED;
            else if (f.header == RX_HDR_MISMATCH) f.cond = RX_COND_LOST;
        }
    }

    // Reads file content through its cluster runs; bytes past the valid-data length are zeros. Returns bytes produced.
    uint32_t read_data(const FEntry& f, uint64_t offset, uint8_t* out, uint32_t len) {
        if (offset >= f.size) return 0;
        len = (uint32_t)std::min<uint64_t>(len, f.size - offset);
        uint32_t done = 0;
        uint64_t run_start = 0;                                   // file offset where the current run begins
        for (const Ext& r : f.runs) {
            uint64_t run_bytes = (uint64_t)r.count * v.cluster_size;
            while (done < len && offset + done < run_start + run_bytes) {
                uint64_t pos = offset + done, in = pos - run_start;
                uint32_t n = (uint32_t)std::min<uint64_t>(len - done, run_bytes - in);
                n = (uint32_t)std::min<uint64_t>(n, 16u * 1024 * 1024);
                if (pos >= f.init) std::memset(out + done, 0, n);
                else {
                    n = (uint32_t)std::min<uint64_t>(n, f.init - pos);
                    if (!src.read(v.cluster_offset(r.cluster) + in, out + done, n)) return done;
                }
                done += n;
            }
            run_start += run_bytes;
            if (done >= len) break;
        }
        return done;
    }
};

// ------------------------------------------------------------------------------------------------
namespace rx {

uint32_t fs_id_of(const Volume& v) {
    switch (v.kind) { case KIND_FAT12: return RX_FS_FAT12; case KIND_FAT16: return RX_FS_FAT16; case KIND_FAT32: return RX_FS_FAT32; case KIND_EXFAT: return RX_FS_EXFAT; default: return RX_FS_UNKNOWN; }
}

class FatAllocMap : public AllocMap {
public:
    explicit FatAllocMap(RxFatScan* s) : s_(s) {
        heap = s->v.heap_offset; first = 2; count = s->v.cluster_count; cluster_size = s->v.cluster_size;
    }
    ~FatAllocMap() override { rx_fat_scan_destroy(s_); }
    int64_t used(uint64_t c, uint64_t n) override {
        if (c < 2) { uint64_t skip = 2 - c; if (n <= skip) return (int64_t)n; n -= skip; c = 2; }
        return s_->used(c, n);
    }
    bool find_run(uint64_t from, bool u, uint64_t& a, uint64_t& b) override { return s_->find_run(from, u, a, b); }
private:
    RxFatScan* s_;
};

AllocMap* open_fat_alloc_map(const std::wstring& root) {
    RxFatScan* s = nullptr;
    if (rx_fat_scan_create(root.c_str(), nullptr, &s) != RX_OK) return nullptr;
    if (!s->alloc_ok) { rx_fat_scan_destroy(s); return nullptr; }
    return new FatAllocMap(s);
}

} // namespace rx

extern "C" {

RX_API RxStatus rx_fat_scan_create(const wchar_t* root, const RxScanOptions* options, RxFatScanHandle* out) {
    try {
        if (!root || !out) return RX_ERR_INVALID_ARG;
        *out = nullptr;
        RxScanOptions o; std::memset(&o, 0, sizeof o); o.struct_size = sizeof o; o.flags = RX_SCANF_VALIDATE_CONTENT;
        if (options) { if (options->struct_size != sizeof(RxScanOptions)) return RX_ERR_INVALID_ARG; o = *options; }
        if (o.max_results == 0) o.max_results = kDefaultMaxResults;
        if (o.max_results > kHardMaxResults) o.max_results = kHardMaxResults;
        std::unique_ptr<RxFatScan> s(new RxFatScan());
        s->opts = o;
        RxStatus st = rx_volume_open(root, &s->vol);
        if (st != RX_OK) return st;
        s->src.h = s->vol;
        RxVolumeGeometry geo; geo.struct_size = sizeof geo;
        if ((st = rx_volume_get_geometry(s->vol, &geo)) != RX_OK) return st;
        std::vector<uint8_t> boot(std::max<uint32_t>(512, geo.bytes_per_sector));
        if (!s->src.read(0, boot.data(), (uint32_t)boot.size())) return RX_ERR_IO;
        st = parse_boot(boot.data(), (uint32_t)boot.size(), geo.total_bytes, s->v);
        if (st != RX_OK) return st;
        if ((st = s->load_fat()) != RX_OK) return st;
        if (s->v.kind == KIND_EXFAT && s->load_exfat_bitmap() != RX_OK) s->pflags |= RX_SCANP_BITMAP_UNAVAILABLE;
        *out = s.release();
        return RX_OK;
    } catch (const std::bad_alloc&) { return RX_ERR_INTERNAL; } catch (...) { return RX_ERR_INTERNAL; }
}

RX_API uint32_t rx_fat_scan_fs_type(RxFatScanHandle s) { return s ? rx::fs_id_of(s->v) : RX_FS_UNKNOWN; }

RX_API RxStatus rx_fat_scan_run(RxFatScanHandle s, RxCancelHandle cancel) {
    if (!s) return RX_ERR_INVALID_ARG;
    bool expected = false;
    if (!s->running.compare_exchange_strong(expected, true) || (s->pflags & RX_SCANP_DONE)) return RX_ERR_INVALID_ARG;
    RxStatus st;
    try { st = s->run(cancel); }
    catch (const std::bad_alloc&) { st = RX_ERR_INTERNAL; }
    catch (...) { st = RX_ERR_INTERNAL; }
    s->pstatus = (uint32_t)st;
    s->phase = RX_PHASE_DONE;
    s->pflags |= RX_SCANP_DONE;
    s->running = false;
    return st;
}

RX_API RxStatus rx_fat_scan_progress(RxFatScanHandle s, RxScanProgress* p) {
    if (!s || !p || p->struct_size != sizeof(RxScanProgress)) return RX_ERR_INVALID_ARG;
    p->phase = s->phase; p->records_total = s->rec_total; p->records_done = std::min<uint32_t>(s->rec_done, s->rec_total);
    p->candidates = s->ncand; p->flags = s->pflags; p->status = s->pstatus; p->bad_records = s->bad;
    return RX_OK;
}

RX_API uint32_t rx_fat_scan_result_count(RxFatScanHandle s) {
    return (s && (s->pflags & RX_SCANP_DONE)) ? (uint32_t)s->results.size() : 0;
}

RX_API RxStatus rx_fat_scan_get_results(RxFatScanHandle s, uint32_t start, RxFileEntry* out, uint32_t cap, uint32_t* count) {
    try {
        if (!s || !out || !count) return RX_ERR_INVALID_ARG;
        *count = 0;
        if (!(s->pflags & RX_SCANP_DONE)) return RX_ERR_INVALID_ARG;
        for (uint32_t i = 0; i < cap && (uint64_t)start + i < s->results.size(); ++i) {
            if (out[i].struct_size != sizeof(RxFileEntry)) return RX_ERR_INVALID_ARG;
            const FEntry& e = s->results[start + i];
            RxFileEntry& o = out[i];
            o.flags = e.flags; o.condition = e.cond; o.header_check = e.header;
            o.mft_number = e.dirent; o.size = e.size; o.created = e.created; o.modified = e.modified; o.accessed = e.accessed;
            o.record_changed = 0; o.clusters_total = e.ctotal; o.clusters_in_use = e.cuse; o.record_seq = 0; o.reserved = 0;
            size_t n = std::min<size_t>(e.path.size(), 519);
            if (e.path.size() > 519) o.flags |= RX_ENT_NAME_TRUNCATED;
            std::wmemcpy(o.path, e.path.c_str(), n);
            o.path[n] = 0;
            ++*count;
        }
        return RX_OK;
    } catch (...) { return RX_ERR_INTERNAL; }
}

RX_API RxStatus rx_fat_scan_read_file(RxFatScanHandle s, uint32_t index, uint64_t offset, void* buffer, uint32_t length, uint32_t* got) {
    try {
        if (!s || !buffer || !got || length > 16u * 1024u * 1024u) return RX_ERR_INVALID_ARG;
        *got = 0;
        if (!(s->pflags & RX_SCANP_DONE) || index >= s->results.size()) return RX_ERR_INVALID_ARG;
        const FEntry& e = s->results[index];
        if (e.flags & RX_ENT_BAD_RUNS) return RX_ERR_CORRUPT;
        if (offset >= e.size) return RX_OK;
        uint32_t want = (uint32_t)std::min<uint64_t>(length, e.size - offset);
        *got = s->read_data(e, offset, static_cast<uint8_t*>(buffer), want);
        return *got == want ? RX_OK : RX_ERR_IO;
    } catch (...) { return RX_ERR_INTERNAL; }
}

RX_API void rx_fat_scan_destroy(RxFatScanHandle s) { delete s; }

} // extern "C"
