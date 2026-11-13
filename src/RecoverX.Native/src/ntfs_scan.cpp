// NTFS Quick Scan: reads the Master File Table read-only, finds deleted file records, validates them against the
// cluster bitmap and file headers, rebuilds paths, and serves file content for recovery.
#include "internal.h"
#include "ntfs_format.h"
#include <algorithm>
#include <atomic>
#include <cstring>
#include <memory>
#include <mutex>
#include <new>
#include <unordered_map>

using namespace rx::ntfs;

namespace {

const uint32_t kDefaultMaxResults = 250000;
const uint32_t kHardMaxResults = 5000000;
const uint32_t kHeaderBytes = 512;
const uint32_t kChunkBytes = 4u * 1024u * 1024u;
const uint32_t kBitmapWindow = 1u << 20;

// ---- serialized access to the volume handle (a Windows volume handle has one file pointer) ----
struct Source {
    RxVolumeHandle h = nullptr;
    std::mutex m;
    RxStatus read(uint64_t off, void* buf, uint32_t len, uint32_t* got) {
        std::lock_guard<std::mutex> l(m);
        return rx_volume_read(h, off, buf, len, got);
    }
};

// ---- reads a logical byte stream described by a run list (the MFT itself, $Bitmap, or a file) ----
class RunReader {
public:
    RunReader(Source& s, uint32_t cluster, const std::vector<Run>& runs, uint64_t size, uint64_t init)
        : s_(s), cs_(cluster), runs_(runs), size_(size), init_(init < size ? init : size) {}

    RxStatus read(uint64_t off, void* buf, uint32_t len, uint32_t* got) {
        *got = 0;
        if (off >= size_) return RX_OK;
        if ((uint64_t)len > size_ - off) len = (uint32_t)(size_ - off);
        uint8_t* out = static_cast<uint8_t*>(buf);
        uint32_t done = 0;
        while (done < len) {
            uint64_t pos = off + done, vcn = pos / cs_, in = pos % cs_;
            auto it = std::upper_bound(runs_.begin(), runs_.end(), vcn, [](uint64_t v, const Run& r) { return v < r.vcn; });
            if (it == runs_.begin()) break;
            --it;
            if (vcn >= it->vcn + it->len) break;                           // run list shorter than the stream
            uint64_t run_left = (it->vcn + it->len - vcn) * cs_ - in;
            uint32_t n = (uint32_t)std::min<uint64_t>(len - done, run_left);
            if (pos >= init_ || it->lcn < 0) {
                if (pos < init_) n = (uint32_t)std::min<uint64_t>(n, init_ - pos);
                std::memset(out + done, 0, n);                             // hole, or beyond initialized size
            } else {
                n = (uint32_t)std::min<uint64_t>(n, init_ - pos);
                uint32_t g = 0;
                RxStatus st = s_.read(((uint64_t)it->lcn + (vcn - it->vcn)) * cs_ + in, out + done, n, &g);   // offset of this cluster inside the run
                if (st != RX_OK) return st;
                if (g == 0) break;
                n = g;
            }
            done += n;
        }
        *got = done;
        return RX_OK;
    }

private:
    Source& s_;
    uint32_t cs_;
    const std::vector<Run>& runs_;
    uint64_t size_, init_;
};

uint8_t popcount8(uint8_t v) { uint8_t c = 0; while (v) { c += v & 1; v >>= 1; } return c; }

struct DirInfo { uint64_t parent_ref; uint16_t seq; bool in_use; std::wstring name; };

struct Entry {
    uint64_t mft = 0, size = 0, created = 0, modified = 0, accessed = 0, changed = 0, parent_ref = 0;
    uint32_t flags = 0, condition = 0, header = 0, ctotal = 0, cuse = 0;
    uint16_t seq = 0;
    std::wstring name, path;
};

struct Cand {
    Entry e;
    std::vector<Run> runs;
    std::vector<uint8_t> head;
    bool resident = false, have_data = false, runs_ok = true, has_attr_list = false;
    uint16_t data_flags = 0;
    uint64_t first_lcn = 0, init_size = 0;
};

struct FileCtx {
    bool valid = false;
    uint32_t index = 0;
    bool resident = false;
    std::vector<uint8_t> bytes;      // resident content
    std::vector<Run> runs;
    uint64_t size = 0, init = 0;
};

} // namespace

struct RxNtfsScan {
    Source src;
    RxVolumeHandle vol = nullptr;
    Geometry g;
    RxScanOptions opts;
    std::vector<Run> mft_runs;
    uint64_t mft_size = 0, mft_init = 0;
    bool mft_partial = false;

    // $Bitmap
    bool bitmap_ok = false;
    std::vector<Run> bm_runs;
    uint64_t bm_size = 0;
    std::vector<uint8_t> bm_win;
    uint64_t bm_win_start = ~0ull;

    std::vector<Entry> results;
    std::mutex file_m;
    FileCtx ctx;

    std::atomic<uint32_t> phase{RX_PHASE_OPENING}, rec_total{0}, rec_done{0}, ncand{0}, pflags{0}, pstatus{0}, bad{0};

    ~RxNtfsScan() { if (vol) rx_volume_close(vol); }

    // Reads one MFT record (fixups applied). Returns false if unreadable or damaged.
    bool read_record(uint64_t idx, std::vector<uint8_t>& rec) {
        rec.assign(g.record_size, 0);
        RunReader rr(src, g.cluster_size, mft_runs, mft_size, mft_init);
        uint32_t got = 0;
        if (rr.read(idx * g.record_size, rec.data(), g.record_size, &got) != RX_OK || got != g.record_size) return false;
        return apply_fixups(rec.data(), g.record_size);
    }

    // Byte `byte` of $Bitmap (8 clusters), or -1 if unreadable.
    int bitmap_byte(uint64_t byte) {
        if (!bitmap_ok || byte >= bm_size) return -1;
        uint64_t ws = (byte / kBitmapWindow) * kBitmapWindow;
        if (ws != bm_win_start) {
            uint32_t want = (uint32_t)std::min<uint64_t>(kBitmapWindow, bm_size - ws), got = 0;
            bm_win.assign(want, 0);
            RunReader rr(src, g.cluster_size, bm_runs, bm_size, bm_size);
            if (rr.read(ws, bm_win.data(), want, &got) != RX_OK || got != want) { bm_win_start = ~0ull; return -1; }
            bm_win_start = ws;
        }
        return bm_win[(size_t)(byte - ws)];
    }

    // First cluster >= from (and < total_clusters) whose allocation bit equals `used`; total_clusters if none, ~0 on error.
    uint64_t next_with_state(uint64_t from, bool used) {
        uint64_t c = from;
        const uint8_t skip = used ? 0x00 : 0xFF;      // whole bytes that cannot contain the wanted state
        while (c < g.total_clusters) {
            int v = bitmap_byte(c / 8);
            if (v < 0) return ~0ull;
            if ((c % 8) == 0 && (uint8_t)v == skip) { c += 8; continue; }
            if ((((v >> (c % 8)) & 1) != 0) == used) return c;
            ++c;
        }
        return g.total_clusters;
    }

    // Number of allocated clusters in [lcn, lcn+len), or -1 if the bitmap cannot be read.
    int64_t bitmap_count(uint64_t lcn, uint64_t len) {
        if (!bitmap_ok) return -1;
        int64_t used = 0;
        uint64_t c = lcn, end = lcn + len;
        while (c < end) {
            uint64_t byte = c / 8;
            if (byte >= bm_size) return -1;
            uint64_t ws = (byte / kBitmapWindow) * kBitmapWindow;
            if (ws != bm_win_start) {
                uint32_t want = (uint32_t)std::min<uint64_t>(kBitmapWindow, bm_size - ws), got = 0;
                bm_win.assign(want, 0);
                RunReader rr(src, g.cluster_size, bm_runs, bm_size, bm_size);
                if (rr.read(ws, bm_win.data(), want, &got) != RX_OK || got != want) { bm_win_start = ~0ull; return -1; }
                bm_win_start = ws;
            }
            uint64_t in_win = byte - ws;
            uint32_t bit = (uint32_t)(c % 8);
            uint8_t v = bm_win[(size_t)in_win];
            if (bit == 0 && end - c >= 8) { used += popcount8(v); c += 8; }
            else { used += (v >> bit) & 1; ++c; }
        }
        return used;
    }
};

namespace {

std::wstring build_path(RxNtfsScan& s, const Entry& e, const std::unordered_map<uint64_t, DirInfo>& dirs, bool& uncertain) {
    std::vector<const std::wstring*> parts;
    uint64_t ref = e.parent_ref;
    bool reached_root = false;
    for (int depth = 0; depth < 64; ++depth) {
        uint64_t no = ref & 0x0000FFFFFFFFFFFFull;
        uint16_t ref_seq = (uint16_t)(ref >> 48);
        if (no == 5) { reached_root = true; break; }
        auto it = dirs.find(no);
        if (it == dirs.end()) { uncertain = true; break; }
        const DirInfo& d = it->second;
        // A deleted folder's sequence number may have moved on by one; anything else means the record was reused.
        if (d.seq != ref_seq && !(!d.in_use && d.seq == (uint16_t)(ref_seq + 1))) { uncertain = true; break; }
        parts.push_back(&d.name);
        ref = d.parent_ref;
    }
    if (!reached_root) { uncertain = true; if (parts.size() >= 64) uncertain = true; }
    (void)s;
    std::wstring p;
    p += L"\\";
    if (!reached_root) p += L"[unknown]\\";
    for (size_t i = parts.size(); i-- > 0;) { p += *parts[i]; p += L"\\"; }
    p += e.name;
    return p;
}

} // namespace

// ------------------------------------------------------------------------------------------------
extern "C" {

RX_API uint32_t rx_sizeof_file_entry(void) { return (uint32_t)sizeof(RxFileEntry); }
RX_API uint32_t rx_sizeof_scan_progress(void) { return (uint32_t)sizeof(RxScanProgress); }
RX_API uint32_t rx_sizeof_scan_options(void) { return (uint32_t)sizeof(RxScanOptions); }

RX_API RxStatus rx_check_header(const wchar_t* name, const uint8_t* data, uint32_t length, uint32_t* result) {
    try {
        if (!name || !result || (!data && length)) return RX_ERR_INVALID_ARG;
        *result = rx::ntfs::check_header(name, data, length);
        return RX_OK;
    } catch (...) { return RX_ERR_INTERNAL; }
}

RX_API RxStatus rx_ntfs_scan_create(const wchar_t* root, const RxScanOptions* options, RxScanHandle* out) {
    try {
        if (!root || !out) return RX_ERR_INVALID_ARG;
        *out = nullptr;
        RxScanOptions o; std::memset(&o, 0, sizeof o); o.struct_size = sizeof o; o.flags = RX_SCANF_VALIDATE_CONTENT;
        if (options) { if (options->struct_size != sizeof(RxScanOptions)) return RX_ERR_INVALID_ARG; o = *options; }
        if (o.max_results == 0) o.max_results = kDefaultMaxResults;
        if (o.max_results > kHardMaxResults) o.max_results = kHardMaxResults;

        std::unique_ptr<RxNtfsScan> s(new RxNtfsScan());
        s->opts = o;
        RxStatus st = rx_volume_open(root, &s->vol);
        if (st != RX_OK) return st;
        s->src.h = s->vol;

        RxVolumeGeometry geo; geo.struct_size = sizeof geo;
        st = rx_volume_get_geometry(s->vol, &geo);
        if (st != RX_OK) return st;

        std::vector<uint8_t> boot(std::max<uint32_t>(512, geo.bytes_per_sector));
        uint32_t got = 0;
        st = s->src.read(0, boot.data(), (uint32_t)boot.size(), &got);
        if (st != RX_OK) return st;
        st = parse_boot_sector(boot.data(), got, geo.total_bytes, s->g);
        if (st != RX_OK) return st;
        const Geometry& g = s->g;

        // $MFT is record 0; fall back to the mirror copy if the primary is damaged.
        std::vector<uint8_t> rec(g.record_size);
        ParsedRecord pr;
        bool have = false;
        uint64_t lcns[2] = { g.mft_lcn, g.mftmirr_lcn };
        for (int i = 0; i < 2 && !have; ++i) {
            if (i == 1 && lcns[1] == 0) break;
            uint32_t n = 0;
            if (s->src.read(lcns[i] * g.cluster_size, rec.data(), g.record_size, &n) != RX_OK || n != g.record_size) continue;
            if (!apply_fixups(rec.data(), g.record_size)) continue;
            if (!parse_record(rec.data(), g.record_size, g.total_clusters, true, pr)) continue;
            if (pr.have_data && !pr.data_resident && pr.runs_ok && !pr.runs.empty() && pr.data_size >= g.record_size) have = true;
        }
        if (!have) return RX_ERR_CORRUPT;
        s->mft_runs = pr.runs;
        s->mft_size = pr.data_size;
        s->mft_init = pr.init_size ? pr.init_size : pr.data_size;
        s->mft_partial = pr.run_clusters * (uint64_t)g.cluster_size < pr.data_size;

        // $Bitmap is record 6.
        std::vector<uint8_t> brec;
        if (s->read_record(6, brec)) {
            ParsedRecord bp;
            if (parse_record(brec.data(), g.record_size, g.total_clusters, true, bp) && bp.have_data && !bp.data_resident
                && bp.runs_ok && bp.data_size > 0 && bp.run_clusters * (uint64_t)g.cluster_size >= bp.data_size
                && bp.data_size * 8 >= g.total_clusters) {
                s->bm_runs = bp.runs; s->bm_size = bp.data_size; s->bitmap_ok = true;
            }
        }
        *out = s.release();
        return RX_OK;
    } catch (const std::bad_alloc&) { return RX_ERR_INTERNAL; } catch (...) { return RX_ERR_INTERNAL; }
}

RX_API RxStatus rx_ntfs_scan_progress(RxScanHandle s, RxScanProgress* p) {
    if (!s || !p || p->struct_size != sizeof(RxScanProgress)) return RX_ERR_INVALID_ARG;
    p->phase = s->phase; p->records_total = s->rec_total; p->records_done = s->rec_done; p->candidates = s->ncand;
    p->flags = s->pflags; p->status = s->pstatus; p->bad_records = s->bad;
    return RX_OK;
}

RX_API RxStatus rx_ntfs_scan_run(RxScanHandle s, RxCancelHandle cancel) {
    if (!s) return RX_ERR_INVALID_ARG;
    RxStatus final_status = RX_OK;
    try {
        const Geometry& g = s->g;
        if (!s->bitmap_ok) s->pflags |= RX_SCANP_BITMAP_UNAVAILABLE;
        if (s->mft_partial) s->pflags |= RX_SCANP_MFT_PARTIAL;

        uint64_t nrec = s->mft_size / g.record_size;
        if (nrec > 0xFFFFFFFFull) nrec = 0xFFFFFFFFull;
        s->rec_total = (uint32_t)nrec;
        s->phase = RX_PHASE_SCANNING;

        std::unordered_map<uint64_t, DirInfo> dirs;
        std::vector<Cand> cands;
        const uint32_t chunk_recs = std::max<uint32_t>(1, kChunkBytes / g.record_size);
        std::vector<uint8_t> buf((size_t)chunk_recs * g.record_size);
        RunReader mft(s->src, g.cluster_size, s->mft_runs, s->mft_size, s->mft_init);
        bool cancelled = false;

        for (uint64_t start = 0; start < nrec && !cancelled; start += chunk_recs) {
            if (rx::poll_cancel(cancel, &s->pflags, RX_SCANP_PAUSED)) { cancelled = true; break; }
            uint32_t n = (uint32_t)std::min<uint64_t>(chunk_recs, nrec - start), got = 0;
            RxStatus rs = mft.read(start * g.record_size, buf.data(), n * g.record_size, &got);
            if (rs != RX_OK) { s->bad += n; s->rec_done = (uint32_t)(start + n); continue; }   // unreadable region: count and move on
            uint32_t have = got / g.record_size;
            if (have < n) s->bad += (n - have);
            for (uint32_t j = 0; j < have; ++j) {
                uint8_t* rec = buf.data() + (size_t)j * g.record_size;
                uint64_t idx = start + j;
                if (std::memcmp(rec, "FILE", 4) != 0) { if (std::memcmp(rec, "BAAD", 4) == 0) ++s->bad; continue; }
                if (!apply_fixups(rec, g.record_size)) { ++s->bad; continue; }
                uint16_t fl = rd16(rec + 0x16);
                bool in_use = (fl & 1) != 0, is_dir = (fl & 2) != 0;
                ParsedRecord pr;
                if (!parse_record(rec, g.record_size, g.total_clusters, !in_use && !is_dir, pr)) { ++s->bad; continue; }
                if (pr.base_ref != 0) continue;                            // extension record
                if (pr.is_dir) {
                    if (pr.have_name) dirs[idx] = DirInfo{ pr.parent_ref, pr.seq, pr.in_use, pr.name };
                    continue;
                }
                if (pr.in_use || idx < 16 || !pr.have_name) continue;      // only deleted, nameable files

                if (cands.size() >= s->opts.max_results) { s->pflags |= RX_SCANP_TRUNCATED; continue; }
                Cand c;
                Entry& e = c.e;
                e.mft = idx; e.seq = pr.seq; e.name = pr.name; e.parent_ref = pr.parent_ref;
                e.created  = pr.have_si ? pr.si_times[0] : pr.fn_times[0];
                e.modified = pr.have_si ? pr.si_times[1] : pr.fn_times[1];
                e.changed  = pr.have_si ? pr.si_times[2] : pr.fn_times[2];
                e.accessed = pr.have_si ? pr.si_times[3] : pr.fn_times[3];
                e.size = pr.have_data ? pr.data_size : 0;
                c.have_data = pr.have_data; c.resident = pr.have_data && pr.data_resident; c.runs_ok = pr.runs_ok;
                c.has_attr_list = pr.has_attr_list; c.data_flags = pr.data_flags; c.init_size = pr.init_size;
                if (c.resident) {
                    e.flags |= RX_ENT_RESIDENT;
                    uint32_t take = std::min<uint32_t>(pr.res_len, kHeaderBytes);
                    c.head.assign(rec + pr.res_off, rec + pr.res_off + take);
                } else if (pr.have_data) {
                    c.runs.swap(pr.runs);
                    for (const Run& r : c.runs) if (r.lcn >= 0) { c.first_lcn = (uint64_t)r.lcn; break; }
                }
                if (pr.has_attr_list) e.flags |= RX_ENT_ATTR_LIST;
                if (pr.has_named_data) e.flags |= RX_ENT_HAS_STREAMS;
                if (pr.data_flags & ATTRF_COMPRESSED) e.flags |= RX_ENT_COMPRESSED;
                if (pr.data_flags & ATTRF_ENCRYPTED) e.flags |= RX_ENT_ENCRYPTED;
                if (pr.data_flags & ATTRF_SPARSE) e.flags |= RX_ENT_SPARSE;
                if (!pr.runs_ok) e.flags |= RX_ENT_BAD_RUNS;
                cands.push_back(std::move(c));
                s->ncand = (uint32_t)cands.size();
            }
            s->rec_done = (uint32_t)std::min<uint64_t>(start + n, 0xFFFFFFFFull);
        }

        // ---- validation: cluster bitmap + file header evidence ----
        if (!cancelled) {
            s->phase = RX_PHASE_VALIDATING;
            s->rec_total = (uint32_t)cands.size(); s->rec_done = 0;
            std::vector<uint32_t> order(cands.size());
            for (uint32_t i = 0; i < order.size(); ++i) order[i] = i;
            std::sort(order.begin(), order.end(), [&](uint32_t a, uint32_t b) { return cands[a].first_lcn < cands[b].first_lcn; });   // fewer seeks
            const bool check_content = (s->opts.flags & RX_SCANF_VALIDATE_CONTENT) != 0;
            uint32_t done = 0;
            for (uint32_t oi : order) {
                if ((done++ & 63) == 0 && rx::poll_cancel(cancel, &s->pflags, RX_SCANP_PAUSED)) { cancelled = true; break; }
                s->rec_done = done;
                Cand& c = cands[oi];
                Entry& e = c.e;
                uint32_t cond = RX_COND_UNKNOWN;
                if (c.has_attr_list && !c.have_data) cond = RX_COND_UNSUPPORTED;
                else if (!c.have_data || e.size == 0) cond = RX_COND_EMPTY;
                else if (e.flags & (RX_ENT_COMPRESSED | RX_ENT_ENCRYPTED)) cond = RX_COND_UNSUPPORTED;
                else if (c.resident) {
                    e.header = check_content ? rx::ntfs::check_header(e.name, c.head.data(), (uint32_t)c.head.size()) : (uint32_t)RX_HDR_NOT_CHECKED;
                    cond = e.header == RX_HDR_MATCH ? RX_COND_VERIFIED : RX_COND_LIKELY;   // content is inside the MFT record itself
                } else {
                    uint64_t clusters = 0;
                    for (const Run& r : c.runs) if (r.lcn >= 0) clusters += r.len;
                    uint64_t needed = (e.size + g.cluster_size - 1) / g.cluster_size;
                    uint64_t covered = 0; for (const Run& r : c.runs) covered += r.len;
                    e.ctotal = (uint32_t)std::min<uint64_t>(clusters, 0xFFFFFFFFull);
                    if (!c.runs_ok || covered < needed) { cond = RX_COND_LOST; e.flags |= RX_ENT_BAD_RUNS; }
                    else {
                        int64_t used = 0; bool unknown = false;
                        for (const Run& r : c.runs) {
                            if (r.lcn < 0) continue;
                            int64_t u = s->bitmap_count((uint64_t)r.lcn, r.len);
                            if (u < 0) { unknown = true; break; }
                            used += u;
                        }
                        if (unknown) cond = RX_COND_UNKNOWN;
                        else {
                            e.cuse = (uint32_t)std::min<int64_t>(used, 0xFFFFFFFF);
                            if (clusters > 0 && (uint64_t)used >= clusters) cond = RX_COND_LOST;
                            else if (used > 0) cond = RX_COND_PARTIAL;
                            else {
                                cond = RX_COND_LIKELY;
                                if (check_content && clusters > 0) {
                                    uint8_t head[kHeaderBytes]; uint32_t got = 0;
                                    RunReader rr(s->src, g.cluster_size, c.runs, e.size, c.init_size ? c.init_size : e.size);
                                    uint32_t want = (uint32_t)std::min<uint64_t>(e.size, kHeaderBytes);
                                    if (rr.read(0, head, want, &got) == RX_OK && got > 0) {
                                        e.header = rx::ntfs::check_header(e.name, head, got);
                                        if (e.header == RX_HDR_MATCH) cond = RX_COND_VERIFIED;
                                        else if (e.header == RX_HDR_MISMATCH) cond = RX_COND_LOST;
                                    }
                                }
                            }
                        }
                    }
                }
                e.condition = cond;
            }
        }

        // ---- paths (always runs, so cancelled scans still return usable names) ----
        s->phase = RX_PHASE_PATHS;
        s->rec_total = (uint32_t)cands.size();
        s->results.reserve(cands.size());
        for (Cand& c : cands) {
            bool uncertain = false;
            c.e.path = build_path(*s, c.e, dirs, uncertain);
            if (uncertain) c.e.flags |= RX_ENT_PATH_UNCERTAIN;
            s->results.push_back(std::move(c.e));
        }
        std::sort(s->results.begin(), s->results.end(), [](const Entry& a, const Entry& b) { return a.mft < b.mft; });
        s->rec_done = s->rec_total.load();
        final_status = cancelled ? RX_ERR_CANCELLED : RX_OK;
    } catch (const std::bad_alloc&) { final_status = RX_ERR_INTERNAL; }
    catch (...) { final_status = RX_ERR_INTERNAL; }
    s->pstatus = (uint32_t)final_status;
    s->phase = RX_PHASE_DONE;
    s->pflags |= RX_SCANP_DONE;
    return final_status;
}

RX_API uint32_t rx_ntfs_scan_result_count(RxScanHandle s) {
    return (s && (s->pflags & RX_SCANP_DONE)) ? (uint32_t)s->results.size() : 0;
}

RX_API RxStatus rx_ntfs_scan_get_results(RxScanHandle s, uint32_t start, RxFileEntry* out, uint32_t cap, uint32_t* count) {
    try {
        if (!s || !out || !count) return RX_ERR_INVALID_ARG;
        *count = 0;
        if (!(s->pflags & RX_SCANP_DONE)) return RX_ERR_INVALID_ARG;
        for (uint32_t i = 0; i < cap && (uint64_t)start + i < s->results.size(); ++i) {
            if (out[i].struct_size != sizeof(RxFileEntry)) return RX_ERR_INVALID_ARG;
            const Entry& e = s->results[start + i];
            RxFileEntry& o = out[i];
            o.flags = e.flags; o.condition = e.condition; o.header_check = e.header;
            o.mft_number = e.mft; o.size = e.size; o.created = e.created; o.modified = e.modified; o.accessed = e.accessed;
            o.record_changed = e.changed; o.clusters_total = e.ctotal; o.clusters_in_use = e.cuse; o.record_seq = e.seq; o.reserved = 0;
            size_t n = std::min<size_t>(e.path.size(), 519);
            if (e.path.size() > 519) o.flags |= RX_ENT_NAME_TRUNCATED;
            std::wmemcpy(o.path, e.path.c_str(), n);
            o.path[n] = 0;
            ++*count;
        }
        return RX_OK;
    } catch (...) { return RX_ERR_INTERNAL; }
}

RX_API RxStatus rx_ntfs_read_file(RxScanHandle s, uint32_t index, uint64_t offset, void* buffer, uint32_t length, uint32_t* got_out) {
    try {
        if (!s || !buffer || !got_out || length > 16u * 1024u * 1024u) return RX_ERR_INVALID_ARG;
        *got_out = 0;
        if (!(s->pflags & RX_SCANP_DONE) || index >= s->results.size()) return RX_ERR_INVALID_ARG;
        std::lock_guard<std::mutex> lock(s->file_m);
        const Entry& e = s->results[index];

        if (!s->ctx.valid || s->ctx.index != index) {
            s->ctx = FileCtx();
            std::vector<uint8_t> rec;
            if (!s->read_record(e.mft, rec)) return RX_ERR_IO;
            ParsedRecord pr;
            if (!parse_record(rec.data(), s->g.record_size, s->g.total_clusters, true, pr)) return RX_ERR_CORRUPT;
            if (pr.in_use || pr.seq != e.seq || !pr.have_data) return RX_ERR_NOT_FOUND;   // record was reused since the scan
            if (pr.data_flags & (ATTRF_COMPRESSED | ATTRF_ENCRYPTED)) return RX_ERR_UNSUPPORTED;
            FileCtx c;
            c.valid = true; c.index = index; c.size = pr.data_size; c.init = pr.init_size;
            if (pr.data_resident) { c.resident = true; c.bytes.assign(rec.begin() + pr.res_off, rec.begin() + pr.res_off + pr.res_len); }
            else { if (!pr.runs_ok) return RX_ERR_CORRUPT; c.runs.swap(pr.runs); }
            s->ctx = std::move(c);
        }
        const FileCtx& c = s->ctx;
        if (offset >= c.size) return RX_OK;
        uint32_t want = (uint32_t)std::min<uint64_t>(length, c.size - offset);
        if (c.resident) {
            std::memcpy(buffer, c.bytes.data() + offset, want);
            *got_out = want;
            return RX_OK;
        }
        RunReader rr(s->src, s->g.cluster_size, c.runs, c.size, c.init);
        return rr.read(offset, buffer, want, got_out);
    } catch (const std::bad_alloc&) { return RX_ERR_INTERNAL; } catch (...) { return RX_ERR_INTERNAL; }
}

RX_API void rx_ntfs_scan_destroy(RxScanHandle s) { delete s; }

} // extern "C"

namespace rx {
bool ntfs_alloc_geometry(RxScanHandle s, uint32_t& cluster_size, uint64_t& total_clusters) {
    if (!s || !s->bitmap_ok) return false;
    cluster_size = s->g.cluster_size; total_clusters = s->g.total_clusters;
    return true;
}

int64_t ntfs_used_clusters(RxScanHandle s, uint64_t lcn, uint64_t len) {
    if (!s || !s->bitmap_ok) return -1;
    if (lcn >= s->g.total_clusters) return -1;
    if (len > s->g.total_clusters - lcn) len = s->g.total_clusters - lcn;
    return s->bitmap_count(lcn, len);
}

bool ntfs_find_run(RxScanHandle s, uint64_t from, bool used, uint64_t& start, uint64_t& end) {
    if (!s || !s->bitmap_ok) return false;
    uint64_t a = s->next_with_state(from, used);
    if (a == ~0ull || a >= s->g.total_clusters) return false;
    uint64_t b = s->next_with_state(a, !used);
    if (b == ~0ull) b = s->g.total_clusters;
    start = a; end = b;
    return true;
}

int ntfs_debug_run_count(RxScanHandle s, uint32_t index) {
    if (!s || index >= s->results.size()) return -1;
    std::vector<uint8_t> rec;
    if (!s->read_record(s->results[index].mft, rec)) return -1;
    ParsedRecord pr;
    if (!parse_record(rec.data(), s->g.record_size, s->g.total_clusters, true, pr)) return -1;
    int n = 0;
    for (const Run& r : pr.runs) if (r.lcn >= 0) ++n;
    return n;
}
}
