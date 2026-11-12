#include "ntfs_format.h"
#include <cstring>

namespace rx { namespace ntfs {

uint16_t rd16(const uint8_t* p) { return (uint16_t)(p[0] | (p[1] << 8)); }
uint32_t rd32(const uint8_t* p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }
uint64_t rd64(const uint8_t* p) { return (uint64_t)rd32(p) | ((uint64_t)rd32(p + 4) << 32); }

static bool is_pow2(uint64_t v) { return v != 0 && (v & (v - 1)) == 0; }

RxStatus parse_boot_sector(const uint8_t* s, uint32_t len, uint64_t device_bytes, Geometry& g) {
    g = Geometry();
    if (!s || len < 512) return RX_ERR_INVALID_ARG;
    if (std::memcmp(s + 3, "NTFS    ", 8) != 0 || s[510] != 0x55 || s[511] != 0xAA) return RX_ERR_NOT_NTFS;

    uint32_t bps = rd16(s + 0x0B);
    if (bps < 512 || bps > 4096 || !is_pow2(bps)) return RX_ERR_CORRUPT;

    uint32_t spc_raw = s[0x0D], spc;
    if (spc_raw == 0) return RX_ERR_CORRUPT;
    if (spc_raw <= 0x80) { if (!is_pow2(spc_raw)) return RX_ERR_CORRUPT; spc = spc_raw; }
    else { uint32_t shift = 256 - spc_raw; if (shift > 12) return RX_ERR_CORRUPT; spc = 1u << shift; }   // newer volumes: 2^n
    uint64_t cluster = (uint64_t)bps * spc;
    if (cluster > (2u << 20)) return RX_ERR_CORRUPT;

    uint64_t total_sectors = rd64(s + 0x28);
    if (total_sectors == 0 || total_sectors > (0xFFFFFFFFFFFFull)) return RX_ERR_CORRUPT;
    uint64_t ntfs_bytes = total_sectors * bps;
    uint64_t vol_bytes = ntfs_bytes < device_bytes ? ntfs_bytes : device_bytes;   // image shorter than the file system: scan what exists
    uint64_t total_clusters = vol_bytes / cluster;
    if (total_clusters == 0) return RX_ERR_CORRUPT;

    int8_t cpr = (int8_t)s[0x40];
    uint64_t rec;
    if (cpr > 0) rec = (uint64_t)cpr * cluster;
    else if (cpr < -16) return RX_ERR_CORRUPT;        // 2^17 bytes or more
    else rec = 1ull << (-cpr);
    if (rec < 512 || rec > 65536 || !is_pow2(rec) || rec % 512 != 0) return RX_ERR_CORRUPT;

    uint64_t mft_lcn = rd64(s + 0x30), mir = rd64(s + 0x38);
    if (mft_lcn >= total_clusters || (mft_lcn * cluster + rec) > vol_bytes) return RX_ERR_CORRUPT;

    g.bytes_per_sector = bps; g.cluster_size = (uint32_t)cluster; g.record_size = (uint32_t)rec;
    g.total_clusters = total_clusters; g.mft_lcn = mft_lcn; g.mftmirr_lcn = (mir < total_clusters) ? mir : 0;
    g.volume_bytes = vol_bytes;
    return RX_OK;
}

bool apply_fixups(uint8_t* r, uint32_t size) {
    if (size < 48 || std::memcmp(r, "FILE", 4) != 0) return false;
    uint32_t usa_off = rd16(r + 4), usa_cnt = rd16(r + 6);
    if (usa_cnt < 2 || usa_off < 42 || (usa_off & 1)) return false;
    if ((uint64_t)usa_off + 2ull * usa_cnt > size) return false;
    if ((uint64_t)(usa_cnt - 1) * 512 > size) return false;
    uint16_t usn = rd16(r + usa_off);
    for (uint32_t i = 1; i < usa_cnt; ++i) {
        uint32_t pos = i * 512 - 2;
        if (pos + 2 > size) return false;
        if (rd16(r + pos) != usn) return false;               // torn / damaged sector
        r[pos] = r[usa_off + 2 * i]; r[pos + 1] = r[usa_off + 2 * i + 1];
    }
    return true;
}

bool decode_runs(const uint8_t* p, size_t avail, uint64_t total_clusters, uint64_t start_vcn,
                 std::vector<Run>& out, uint64_t& total_len) {
    out.clear(); total_len = 0;
    size_t i = 0; int64_t lcn = 0; uint64_t vcn = start_vcn;
    const size_t kMaxRuns = 1u << 18;
    while (i < avail) {
        uint8_t h = p[i++];
        if (h == 0) return true;
        uint32_t ln = h & 0x0F, on = h >> 4;
        if (ln == 0 || ln > 8 || on > 8 || i + ln + on > avail) return false;
        uint64_t len = 0;
        for (uint32_t k = 0; k < ln; ++k) len |= (uint64_t)p[i + k] << (8 * k);
        i += ln;
        if (len == 0 || len > (1ull << 48)) return false;
        Run r; r.vcn = vcn; r.len = len;
        if (on == 0) { r.lcn = -1; }
        else {
            int64_t delta = 0;
            for (uint32_t k = 0; k < on; ++k) delta |= (int64_t)((uint64_t)p[i + k] << (8 * k));
            if (p[i + on - 1] & 0x80) { if (on < 8) delta |= -((int64_t)1 << (8 * on)); }   // sign extend
            i += on;
            lcn += delta;
            if (lcn < 0 || (uint64_t)lcn >= total_clusters || len > total_clusters - (uint64_t)lcn) return false;
            r.lcn = lcn;
        }
        if (out.size() >= kMaxRuns) return false;
        out.push_back(r);
        vcn += len; total_len += len;
        if (total_len > (1ull << 48)) return false;
    }
    return true;   // list ended without a terminator; accept what was decoded
}

static void read_utf16(const uint8_t* p, uint32_t chars, std::wstring& out) {
    out.clear();
    out.reserve(chars);
    for (uint32_t i = 0; i < chars; ++i) out.push_back((wchar_t)rd16(p + 2 * i));
}

static int ns_rank(uint8_t ns) { return ns == 1 || ns == 3 ? 3 : ns == 0 ? 2 : ns == 2 ? 1 : 0; }

bool parse_record(const uint8_t* rec, uint32_t size, uint64_t total_clusters, bool want_runs, ParsedRecord& o) {
    o = ParsedRecord();
    if (size < 48 || std::memcmp(rec, "FILE", 4) != 0) return false;
    uint32_t first = rd16(rec + 0x14), used = rd32(rec + 0x18);
    uint16_t flags = rd16(rec + 0x16);
    if (first < 0x18 || (first & 7) || first >= size) return false;
    if (used < first || used > size) return false;
    o.seq = rd16(rec + 0x10);
    o.in_use = (flags & 1) != 0;
    o.is_dir = (flags & 2) != 0;
    o.base_ref = rd64(rec + 0x20) & 0x0000FFFFFFFFFFFFull;

    uint32_t off = first;
    for (int guard = 0; guard < 256 && off + 8 <= used; ++guard) {
        uint32_t type = rd32(rec + off);
        if (type == ATTR_END) break;
        uint32_t alen = rd32(rec + off + 4);
        if (alen < 24 || (alen & 7) || (uint64_t)off + alen > used) return true;   // damaged tail: keep what was parsed
        const uint8_t* a = rec + off;
        bool nonres = a[8] != 0;
        uint32_t name_len = a[9];
        uint16_t aflags = rd16(a + 0x0C);

        if (type == ATTR_ATTRIBUTE_LIST) o.has_attr_list = true;

        if (!nonres && (type == ATTR_STANDARD_INFORMATION || type == ATTR_FILE_NAME)) {
            uint32_t clen = rd32(a + 0x10), coff = rd16(a + 0x14);
            if ((uint64_t)coff + clen <= alen) {
                const uint8_t* c = a + coff;
                if (type == ATTR_STANDARD_INFORMATION && clen >= 48) {
                    for (int k = 0; k < 4; ++k) o.si_times[k] = rd64(c + 8 * k);
                    o.have_si = true;
                } else if (type == ATTR_FILE_NAME && clen >= 66) {
                    uint32_t nl = c[64]; uint8_t ns = c[65];
                    if (66 + 2 * nl <= clen && nl > 0 && (!o.have_name || ns_rank(ns) > ns_rank(o.name_ns))) {
                        o.have_name = true; o.name_ns = ns;
                        o.parent_ref = rd64(c);
                        for (int k = 0; k < 4; ++k) o.fn_times[k] = rd64(c + 8 + 8 * k);
                        read_utf16(c + 66, nl, o.name);
                    }
                }
            }
        } else if (type == ATTR_DATA) {
            if (name_len != 0) o.has_named_data = true;      // alternate stream: ignored
            else if (!o.have_data) {
                if (!nonres) {
                    uint32_t clen = rd32(a + 0x10), coff = rd16(a + 0x14);
                    if ((uint64_t)coff + clen <= alen) {
                        o.have_data = true; o.data_resident = true;
                        o.res_off = off + coff; o.res_len = clen;
                        o.data_size = o.init_size = o.alloc_size = clen;
                    }
                } else if (alen >= 0x40) {
                    uint64_t start_vcn = rd64(a + 0x10);
                    uint32_t rl = rd16(a + 0x20);
                    if (start_vcn == 0 && rl >= 0x40 && rl < alen) {
                        o.have_data = true; o.data_resident = false;
                        o.data_flags = aflags; o.comp_unit = rd16(a + 0x22);
                        o.alloc_size = rd64(a + 0x28); o.data_size = rd64(a + 0x30); o.init_size = rd64(a + 0x38);
                        if (want_runs) {
                            o.runs_ok = decode_runs(a + rl, alen - rl, total_clusters, 0, o.runs, o.run_clusters);
                        }
                    }
                }
            }
            if (aflags & (ATTRF_COMPRESSED | ATTRF_ENCRYPTED | ATTRF_SPARSE)) o.data_flags |= (aflags & (ATTRF_COMPRESSED | ATTRF_ENCRYPTED | ATTRF_SPARSE));
        }
        off += alen;
    }
    return true;
}

}} // namespace
