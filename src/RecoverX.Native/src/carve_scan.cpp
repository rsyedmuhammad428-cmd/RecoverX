// Deep Scan session: reads a volume read-only, finds file signatures on every 512-byte boundary, and lets the format
// parsers (carve_format.cpp) decide each candidate's extent and condition. When the file system's allocation map can be read
// (NTFS $Bitmap, FAT12/16/32 table, exFAT allocation bitmap) only free clusters are examined, so existing files are not
// reported and the scan is faster on full drives.
#include "internal.h"
#include "carve_format.h"
#include <algorithm>
#include <atomic>
#include <cstring>
#include <memory>
#include <mutex>
#include <new>

namespace {

const uint32_t kDefaultMax = 250000;
const uint32_t kHardMax = 5000000;
const uint32_t kChunk = 4u * 1024u * 1024u;
const uint32_t kStep = 512;            // file starts are cluster aligned on every supported file system; 512 covers all
const uint32_t kRetryPiece = 64u * 1024u;

struct Source {
    RxVolumeHandle h = nullptr;
    std::mutex m;
    RxStatus read(uint64_t off, void* buf, uint32_t len, uint32_t* got) {
        std::lock_guard<std::mutex> l(m);
        return rx_volume_read(h, off, buf, len, got);
    }
};

// Reader for one candidate. Serves bytes from the chunk already in memory when possible, otherwise from the volume.
class VolReader : public rx::carve::Reader {
public:
    VolReader(Source& s, uint64_t base, uint64_t total, const uint8_t* chunk, uint64_t chunk_start, uint32_t chunk_len)
        : s_(s), base_(base), total_(total), chunk_(chunk), cs_(chunk_start), cl_(chunk_len) {}
    uint32_t read(uint64_t off, uint8_t* buf, uint32_t len) override {
        if (base_ + off >= total_) return 0;
        uint64_t abs = base_ + off;
        if ((uint64_t)len > total_ - abs) len = (uint32_t)(total_ - abs);
        if (chunk_ && abs >= cs_ && abs + len <= cs_ + cl_) { std::memcpy(buf, chunk_ + (abs - cs_), len); return len; }
        uint32_t done = 0;
        while (done < len) {
            uint32_t want = std::min<uint32_t>(len - done, 16u * 1024u * 1024u), got = 0;
            if (s_.read(abs + done, buf + done, want, &got) != RX_OK || got == 0) break;
            done += got;
        }
        return done;
    }
    uint64_t available() const override { return total_ - base_; }
private:
    Source& s_;
    uint64_t base_, total_;
    const uint8_t* chunk_;
    uint64_t cs_;
    uint32_t cl_;
};

struct CEntry {
    uint64_t off = 0, size = 0, time = 0;
    uint32_t type = 0, cond = 0, ev = 0, ctotal = 0, cuse = 0;
    wchar_t ext[8];
};

} // namespace

struct RxCarveScan {
    Source src;
    RxVolumeHandle vol = nullptr;
    uint64_t total = 0;
    RxCarveOptions opts;
    std::unique_ptr<rx::AllocMap> map;   // free-space map (NTFS / FAT / exFAT), null = read everything
    std::vector<CEntry> results;
    std::atomic<uint32_t> phase{RX_PHASE_OPENING}, flags{0}, status{0}, found{0}, read_errors{0};
    std::atomic<uint64_t> bytes_total{0}, bytes_done{0};
    std::atomic<bool> running{false};

    ~RxCarveScan() {
        if (vol) rx_volume_close(vol);
    }

    bool use_map() const { return map != nullptr; }

    // Byte offset <-> cluster number through the map (clusters start at map->heap).
    uint64_t cluster_at(uint64_t off) const { return off < map->heap ? map->first : map->first + (off - map->heap) / map->cluster_size; }
    uint64_t offset_of(uint64_t c) const { return map->heap + (c - map->first) * (uint64_t)map->cluster_size; }

    // Next region to examine at or after byte `from`: [a, b). Returns false when the volume is exhausted.
    bool next_region(uint64_t from, uint64_t& a, uint64_t& b) {
        if (from >= total) return false;
        if (!use_map()) { a = from; b = total; return true; }
        uint64_t s = 0, e = 0;
        for (uint64_t c = cluster_at(from);;) {
            if (!map->find_run(c, false, s, e)) return false;
            a = std::max<uint64_t>(offset_of(s), from);
            b = std::min<uint64_t>(offset_of(e), total);
            if (a < b) return true;
            if (offset_of(e) >= total) return false;
            c = e;
        }
        return true;
    }

    // Reads [off, off+len) into buf; on a read error retries in smaller pieces and zero-fills what cannot be read.
    uint32_t read_chunk(uint64_t off, uint8_t* buf, uint32_t len, bool& had_error) {
        uint32_t got = 0;
        if (src.read(off, buf, len, &got) == RX_OK && got == len) return got;
        had_error = true;
        uint32_t done = 0;
        while (done < len) {
            uint32_t want = std::min<uint32_t>(kRetryPiece, len - done), g = 0;
            if (src.read(off + done, buf + done, want, &g) != RX_OK || g < want) {
                std::memset(buf + done + g, 0, want - g);
                read_errors.fetch_add(1);
            }
            done += want;
            if (off + done >= total) break;
        }
        return std::min<uint32_t>(done, (uint32_t)std::min<uint64_t>(len, total - off));
    }

    void add_evidence(CEntry& e) {
        if (!use_map()) {
            if (!(opts.flags & RX_CARVEF_ALL_SPACE)) e.ev |= RX_EV_ALLOC_UNKNOWN;
            return;
        }
        uint64_t first = cluster_at(e.off), last = cluster_at(e.off + std::max<uint64_t>(e.size, 1) - 1);
        uint64_t count = last - first + 1;
        int64_t used = map->used(first, count);
        e.ctotal = (uint32_t)std::min<uint64_t>(count, 0xFFFFFFFFu);
        if (used < 0) { e.ev |= RX_EV_ALLOC_UNKNOWN; return; }
        e.cuse = (uint32_t)std::min<int64_t>(used, 0xFFFFFFFF);
        if (used > 0) {
            e.ev |= RX_EV_IN_USE;
            // Other files now occupy part of this extent. Only a matching checksum still proves the bytes are the original ones.
            if (!(e.ev & RX_EV_CHECKSUM_OK) && (e.cond == RX_COND_VERIFIED || e.cond == RX_COND_LIKELY)) e.cond = RX_COND_PARTIAL;
        }
    }
};

namespace rx {

// NTFS $Bitmap through the NTFS Quick Scan layer (cluster n starts at byte n * cluster size).
class NtfsAllocMap : public AllocMap {
public:
    explicit NtfsAllocMap(RxScanHandle h) : h_(h) {}
    ~NtfsAllocMap() override { rx_ntfs_scan_destroy(h_); }
    int64_t used(uint64_t c, uint64_t n) override { return ntfs_used_clusters(h_, c, n); }
    bool find_run(uint64_t from, bool u, uint64_t& a, uint64_t& b) override { return ntfs_find_run(h_, from, u, a, b); }
private:
    RxScanHandle h_;
};

AllocMap* open_alloc_map(const std::wstring& root) {
    RxScanHandle n = nullptr;
    if (rx_ntfs_scan_create(root.c_str(), nullptr, &n) == RX_OK) {
        uint32_t cs = 0; uint64_t nc = 0;
        if (ntfs_alloc_geometry(n, cs, nc)) {
            NtfsAllocMap* m = new NtfsAllocMap(n);
            m->heap = 0; m->first = 0; m->count = nc; m->cluster_size = cs;
            return m;
        }
        rx_ntfs_scan_destroy(n);
    }
    return open_fat_alloc_map(root);
}

} // namespace rx

extern "C" {

RX_API uint32_t rx_sizeof_carve_entry(void) { return (uint32_t)sizeof(RxCarveEntry); }
RX_API uint32_t rx_sizeof_carve_progress(void) { return (uint32_t)sizeof(RxCarveProgress); }
RX_API uint32_t rx_sizeof_carve_options(void) { return (uint32_t)sizeof(RxCarveOptions); }

RX_API RxStatus rx_carve_create(const wchar_t* root, const RxCarveOptions* options, RxCarveHandle* out) {
    try {
        if (!root || !out) return RX_ERR_INVALID_ARG;
        *out = nullptr;
        RxCarveOptions o; std::memset(&o, 0, sizeof o); o.struct_size = sizeof o;
        if (options) { if (options->struct_size != sizeof(RxCarveOptions)) return RX_ERR_INVALID_ARG; o = *options; }
        if (o.max_results == 0) o.max_results = kDefaultMax;
        if (o.max_results > kHardMax) o.max_results = kHardMax;
        if (o.type_mask == 0) o.type_mask = ~0u;

        std::unique_ptr<RxCarveScan> s(new RxCarveScan());
        s->opts = o;
        RxStatus st = rx_volume_open(root, &s->vol);
        if (st != RX_OK) return st;
        s->src.h = s->vol;
        RxVolumeGeometry geo; geo.struct_size = sizeof geo;
        st = rx_volume_get_geometry(s->vol, &geo);
        if (st != RX_OK) return st;
        s->total = geo.total_bytes;
        if (s->total == 0) return RX_ERR_IO;

        if (!(o.flags & RX_CARVEF_ALL_SPACE)) {
            // File-system free-space map. Any failure (unknown or damaged file system) falls back to reading the whole volume.
            s->map.reset(rx::open_alloc_map(root));
            if (s->map && (s->map->cluster_size < 512 || (s->map->cluster_size % 512) != 0 || (s->map->heap % 512) != 0)) s->map.reset();
            if (!s->map) s->flags |= RX_SCANP_BITMAP_UNAVAILABLE;
        }
        *out = s.release();
        return RX_OK;
    } catch (const std::bad_alloc&) { return RX_ERR_INTERNAL; } catch (...) { return RX_ERR_INTERNAL; }
}

RX_API RxStatus rx_carve_progress(RxCarveHandle s, RxCarveProgress* p) {
    if (!s || !p || p->struct_size != sizeof(RxCarveProgress)) return RX_ERR_INVALID_ARG;
    p->phase = s->phase; p->flags = s->flags; p->status = s->status;
    p->bytes_total = s->bytes_total; p->bytes_done = std::min<uint64_t>(s->bytes_done, s->bytes_total);
    p->found = s->found; p->read_errors = s->read_errors;
    return RX_OK;
}

RX_API RxStatus rx_carve_run(RxCarveHandle s, RxCancelHandle cancel) {
    if (!s) return RX_ERR_INVALID_ARG;
    bool expected = false;
    if (!s->running.compare_exchange_strong(expected, true) || (s->flags & RX_SCANP_DONE)) return RX_ERR_INVALID_ARG;
    RxStatus final_status = RX_OK;
    try {
        // Size of the work: free space on NTFS, otherwise the whole volume.
        uint64_t work = 0, a = 0, b = 0, cur = 0;
        if (s->use_map()) { while (s->next_region(cur, a, b)) { work += b - a; cur = b; } }
        else work = s->total;
        s->bytes_total = work;
        s->phase = RX_PHASE_SCANNING;

        std::vector<uint8_t> buf(kChunk);
        bool cancelled = false, full = false;
        uint64_t pos = 0;
        while (!cancelled && !full && s->next_region(pos, a, b)) {
            pos = a;
            while (pos < b && !cancelled && !full) {
                if (rx::poll_cancel(cancel, &s->flags, RX_SCANP_PAUSED)) { cancelled = true; break; }
                uint32_t want = (uint32_t)std::min<uint64_t>(kChunk, b - pos);
                // Read a little past the region so a signature right at its end still has the bytes `match` needs.
                uint32_t extra = (uint32_t)std::min<uint64_t>(rx::carve::kMatchBytes, s->total - (pos + want));
                bool err = false;
                uint32_t got = s->read_chunk(pos, buf.data(), want + extra, err);
                if (got == 0) { s->read_errors.fetch_add(1); s->bytes_done += want; pos += want; continue; }

                uint64_t next = pos + want;                      // where the following chunk starts
                for (uint32_t off = 0; off < want && off + rx::carve::kMatchBytes <= got; off += kStep) {
                    uint32_t t = rx::carve::match(buf.data() + off, got - off);
                    if (!t || !(s->opts.type_mask & (1u << t))) continue;
                    VolReader rdr(s->src, pos + off, s->total, buf.data(), pos, got);
                    rx::carve::Result res;
                    if (!rx::carve::parse(t, rdr, res) || res.size == 0) continue;

                    CEntry e;
                    e.off = pos + off; e.size = res.size; e.time = res.content_time;
                    e.type = res.type; e.cond = res.condition; e.ev = res.evidence;
                    std::memset(e.ext, 0, sizeof e.ext);
                    for (size_t i = 0; i < res.ext.size() && i < 7; ++i) e.ext[i] = res.ext[i];
                    s->add_evidence(e);
                    s->results.push_back(e);
                    s->found = (uint32_t)s->results.size();
                    if (s->results.size() >= s->opts.max_results) { s->flags |= RX_SCANP_TRUNCATED; full = true; break; }

                    // A file whose structure checked out to its end is not searched again for embedded signatures.
                    if (e.cond == RX_COND_VERIFIED || e.cond == RX_COND_LIKELY) {
                        uint64_t skip_to = ((e.off + e.size + kStep - 1) / kStep) * kStep;
                        if (skip_to >= next) { next = skip_to; break; }
                        off = (uint32_t)(skip_to - pos) - kStep;
                    }
                }
                uint64_t adv = std::min<uint64_t>(next, b) - pos;
                s->bytes_done += adv;
                pos = next;
            }
            if (pos < b) break;                                   // cancelled / full
        }
        final_status = cancelled ? RX_ERR_CANCELLED : RX_OK;
        if (!cancelled) s->bytes_done = s->bytes_total.load();
    } catch (const std::bad_alloc&) { final_status = RX_ERR_INTERNAL; }
    catch (...) { final_status = RX_ERR_INTERNAL; }
    s->status = (uint32_t)final_status;
    s->phase = RX_PHASE_DONE;
    s->flags |= RX_SCANP_DONE;
    s->running = false;
    return final_status;
}

RX_API uint32_t rx_carve_result_count(RxCarveHandle s) {
    return (s && (s->flags & RX_SCANP_DONE)) ? (uint32_t)s->results.size() : 0;
}

RX_API RxStatus rx_carve_get_results(RxCarveHandle s, uint32_t start, RxCarveEntry* out, uint32_t cap, uint32_t* count) {
    try {
        if (!s || !out || !count) return RX_ERR_INVALID_ARG;
        *count = 0;
        if (!(s->flags & RX_SCANP_DONE)) return RX_ERR_INVALID_ARG;
        for (uint32_t i = 0; i < cap && (uint64_t)start + i < s->results.size(); ++i) {
            if (out[i].struct_size != sizeof(RxCarveEntry)) return RX_ERR_INVALID_ARG;
            const CEntry& e = s->results[start + i];
            RxCarveEntry& o = out[i];
            o.type = e.type; o.condition = e.cond; o.evidence = e.ev; o.offset = e.off; o.size = e.size; o.content_time = e.time;
            o.clusters_total = e.ctotal; o.clusters_in_use = e.cuse;
            std::wmemcpy(o.ext, e.ext, 8);
            o.ext[7] = 0;
            ++*count;
        }
        return RX_OK;
    } catch (...) { return RX_ERR_INTERNAL; }
}

RX_API RxStatus rx_carve_read_file(RxCarveHandle s, uint32_t index, uint64_t offset, void* buffer, uint32_t length, uint32_t* got) {
    try {
        if (!s || !buffer || !got || length > 16u * 1024u * 1024u) return RX_ERR_INVALID_ARG;
        *got = 0;
        if (!(s->flags & RX_SCANP_DONE) || index >= s->results.size()) return RX_ERR_INVALID_ARG;
        const CEntry& e = s->results[index];
        if (offset >= e.size) return RX_OK;
        uint32_t want = (uint32_t)std::min<uint64_t>(length, e.size - offset);
        return s->src.read(e.off + offset, buffer, want, got);
    } catch (...) { return RX_ERR_INTERNAL; }
}

RX_API void rx_carve_destroy(RxCarveHandle s) { delete s; }

} // extern "C"
