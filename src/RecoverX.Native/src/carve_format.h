// Deep Scan (signature carving) format parsers: pure logic, no platform code. Given a reader positioned at a candidate
// file start, each parser walks the format's own structure to decide where the file ends and how much of it is
// consistent. Input is treated as hostile: every length and offset is validated before use.
#ifndef RX_CARVE_FORMAT_H
#define RX_CARVE_FORMAT_H
#include "rx_engine.h"
#include <string>
#include <vector>

namespace rx { namespace carve {

// Byte source relative to the candidate's first byte.
class Reader {
public:
    virtual ~Reader() {}
    // Reads up to `len` bytes at `off`; returns the number of bytes read (short at the end of the region or on error).
    virtual uint32_t read(uint64_t off, uint8_t* buf, uint32_t len) = 0;
    // Bytes from the candidate start to the end of the readable region (the volume end).
    virtual uint64_t available() const = 0;
};

// In-memory reader (tests, fuzzing).
class MemReader : public Reader {
public:
    MemReader(const uint8_t* p, size_t n) : p_(p), n_(n) {}
    uint32_t read(uint64_t off, uint8_t* buf, uint32_t len) override;
    uint64_t available() const override { return n_; }
private:
    const uint8_t* p_;
    size_t n_;
};

struct Result {
    uint32_t type = 0;            // RX_FT_*
    uint32_t condition = RX_COND_UNKNOWN;
    uint32_t evidence = 0;        // RX_EV_*
    uint64_t size = 0;
    uint64_t content_time = 0;    // FILETIME, 0 = unknown
    std::wstring ext;
};

// Minimum bytes `match` needs to look at.
const uint32_t kMatchBytes = 64;

// Identifies a candidate from the first bytes of a sector. Returns RX_FT_* or 0.
uint32_t match(const uint8_t* p, uint32_t len);
// Parses a candidate. Returns false when the bytes are not a real file of that type (nothing is reported).
bool parse(uint32_t type, Reader& r, Result& out);
// Maximum size considered for a type (also bounds how far a parser searches).
uint64_t max_size(uint32_t type);

uint32_t crc32(uint32_t crc, const uint8_t* p, size_t n);
// "YYYY:MM:DD HH:MM:SS" -> FILETIME (as if UTC). 0 if malformed or out of range.
uint64_t exif_time_to_filetime(const char* s, size_t n);

}} // namespace
#endif
