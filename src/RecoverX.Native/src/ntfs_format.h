// Pure NTFS on-disk format helpers: no I/O, no platform code. Everything here treats its input as hostile
// (it may come from a damaged or deliberately malformed disk) and validates every offset before use.
#ifndef RX_NTFS_FORMAT_H
#define RX_NTFS_FORMAT_H
#include "rx_engine.h"
#include <string>
#include <vector>

namespace rx { namespace ntfs {

struct Geometry {
    uint32_t bytes_per_sector = 0;
    uint32_t cluster_size = 0;
    uint32_t record_size = 0;
    uint64_t total_clusters = 0;
    uint64_t mft_lcn = 0;
    uint64_t mftmirr_lcn = 0;
    uint64_t volume_bytes = 0;      // bytes the engine may address (min of NTFS size and device size)
};

struct Run {
    uint64_t vcn = 0;               // first virtual cluster of the run
    int64_t  lcn = -1;              // first logical cluster, -1 = sparse (hole)
    uint64_t len = 0;               // clusters
};

enum : uint32_t { ATTR_STANDARD_INFORMATION = 0x10, ATTR_ATTRIBUTE_LIST = 0x20, ATTR_FILE_NAME = 0x30, ATTR_DATA = 0x80, ATTR_END = 0xFFFFFFFFu };
enum : uint16_t { ATTRF_COMPRESSED = 0x0001, ATTRF_ENCRYPTED = 0x4000, ATTRF_SPARSE = 0x8000 };

struct ParsedRecord {
    bool     in_use = false;
    bool     is_dir = false;
    uint16_t seq = 0;
    uint64_t base_ref = 0;                  // non-zero: this is an extension record of another file
    bool     has_attr_list = false;         // attributes continue in other records (not followed in this version)
    bool     has_named_data = false;        // alternate data stream(s) present
    bool     have_name = false;
    std::wstring name;
    uint8_t  name_ns = 255;
    uint64_t parent_ref = 0;
    uint64_t fn_times[4] = {0, 0, 0, 0};    // created, modified, mft-changed, accessed
    bool     have_si = false;
    uint64_t si_times[4] = {0, 0, 0, 0};
    bool     have_data = false;             // unnamed $DATA found
    bool     data_resident = false;
    uint32_t res_off = 0, res_len = 0;      // resident content, relative to the record start
    uint16_t data_flags = 0;
    uint16_t comp_unit = 0;
    uint64_t data_size = 0, alloc_size = 0, init_size = 0;
    bool     runs_ok = true;                // false if the run list was malformed
    std::vector<Run> runs;
    uint64_t run_clusters = 0;
};

uint16_t rd16(const uint8_t* p);
uint32_t rd32(const uint8_t* p);
uint64_t rd64(const uint8_t* p);

RxStatus parse_boot_sector(const uint8_t* sector, uint32_t len, uint64_t device_bytes, Geometry& g);
bool     apply_fixups(uint8_t* record, uint32_t size);
// Decodes a run list. Returns false if malformed (bad header, zero length, run outside the volume, too many runs).
bool     decode_runs(const uint8_t* p, size_t avail, uint64_t total_clusters, uint64_t start_vcn,
                     std::vector<Run>& out, uint64_t& total_len);
// Record must already have had fixups applied. Returns false if the header itself is unusable.
bool     parse_record(const uint8_t* rec, uint32_t size, uint64_t total_clusters, bool want_runs, ParsedRecord& out);

// File-content signature check (see rx_check_header).
uint32_t check_header(const std::wstring& name, const uint8_t* data, uint32_t len);

}} // namespace
#endif
