/*
 * RecoverX native engine - public C ABI (version 1).
 *
 * Design rules
 *  - Plain C types only; no C++ exceptions cross this boundary (every call is wrapped, failures become RxStatus).
 *  - The engine exposes a small set of fixed operations. There is NO write API and no way to pass an
 *    arbitrary device path: volumes are addressed by drive letter only ("E:" or "E:\").
 *  - Structs begin with struct_size (set by the caller) so layouts can grow in later ABI versions.
 *  - Strings are UTF-16 (wchar_t on Windows), NUL-terminated, fixed-size inside structs.
 *  - Calling convention: cdecl. C# side: CallingConvention.Cdecl, CharSet.Unicode.
 */
#ifndef RX_ENGINE_H
#define RX_ENGINE_H

#include <stddef.h>
#include <stdint.h>
#include <wchar.h>

#ifdef _WIN32
#  ifdef RX_ENGINE_BUILD
#    define RX_API __declspec(dllexport)
#  else
#    define RX_API __declspec(dllimport)
#  endif
#else
#  define RX_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define RX_ABI_VERSION 1u

typedef int32_t RxStatus;
enum {
    RX_OK = 0,
    RX_ERR_INVALID_ARG = 1,
    RX_ERR_BUFFER_TOO_SMALL = 2,
    RX_ERR_ACCESS_DENIED = 3,      /* raw volume reads need administrator rights */
    RX_ERR_NOT_FOUND = 4,
    RX_ERR_IO = 5,
    RX_ERR_UNSUPPORTED = 6,        /* e.g. engine built for a non-Windows host */
    RX_ERR_CANCELLED = 7,
    RX_ERR_NOT_NTFS = 8,           /* boot sector is not NTFS */
    RX_ERR_CORRUPT = 9,            /* NTFS metadata is damaged beyond use */
    RX_ERR_INTERNAL = 99
};

/* STORAGE_BUS_TYPE values from the Windows SDK, reproduced so callers need no SDK. */
enum {
    RX_BUS_UNKNOWN = 0, RX_BUS_SCSI = 1, RX_BUS_ATAPI = 2, RX_BUS_ATA = 3, RX_BUS_1394 = 4,
    RX_BUS_SSA = 5, RX_BUS_FIBRE = 6, RX_BUS_USB = 7, RX_BUS_RAID = 8, RX_BUS_ISCSI = 9,
    RX_BUS_SAS = 10, RX_BUS_SATA = 11, RX_BUS_SD = 12, RX_BUS_MMC = 13, RX_BUS_VIRTUAL = 14,
    RX_BUS_FILE_BACKED_VIRTUAL = 15, RX_BUS_SPACES = 16, RX_BUS_NVME = 17
};

enum { RX_MEDIA_UNKNOWN = 0, RX_MEDIA_HDD = 1, RX_MEDIA_SSD = 2 };   /* SSD includes flash media */

/* RxVolumeInfo.flags */
enum {
    RX_VOLF_DEVICE_QUERIED = 1,   /* bus type / vendor / product are valid        */
    RX_VOLF_SEEK_QUERIED   = 2,   /* media_type is valid                          */
    RX_VOLF_TRIM_QUERIED   = 4,   /* trim_enabled is valid                        */
    RX_VOLF_OPEN_FAILED    = 8    /* the volume could not be opened for queries   */
};

enum {
    RX_FS_UNKNOWN = 0, RX_FS_NTFS = 1, RX_FS_FAT32 = 2, RX_FS_EXFAT = 3, RX_FS_FAT16 = 4, RX_FS_FAT12 = 5
};

enum { RX_DEST_SAFE = 0, RX_DEST_SAME_VOLUME = 1, RX_DEST_NOT_FOUND = 2, RX_DEST_UNVERIFIABLE = 3 };

/* Hardware description of one volume. Windows layout is 216 bytes (checked by rx_sizeof_volume_info). */
typedef struct RxVolumeInfo {
    uint32_t struct_size;      /* caller sets to sizeof(RxVolumeInfo) */
    uint32_t abi_version;
    uint32_t bus_type;         /* RX_BUS_* */
    uint32_t media_type;       /* RX_MEDIA_* (valid when RX_VOLF_SEEK_QUERIED) */
    int32_t  removable_media;  /* 1 / 0, -1 = unknown */
    int32_t  trim_enabled;     /* 1 / 0, -1 = unknown */
    uint32_t flags;            /* RX_VOLF_* */
    uint32_t win32_error;      /* last failing Win32 error, 0 if none */
    wchar_t  root[4];          /* "C:\" */
    wchar_t  vendor[40];
    wchar_t  product[48];
} RxVolumeInfo;

typedef struct RxProbeResult {
    uint32_t struct_size;
    uint32_t fs_id;            /* RX_FS_* detected from the boot sector */
    uint32_t bytes_per_sector;
    uint32_t reserved;
    uint64_t total_bytes;
    uint32_t bytes_read;       /* bytes read from sector 0 */
    uint32_t win32_error;
} RxProbeResult;

typedef struct RxVolumeGeometry {
    uint32_t struct_size;
    uint32_t bytes_per_sector;
    uint64_t total_bytes;
} RxVolumeGeometry;

typedef struct RxVolume* RxVolumeHandle;
typedef struct RxCancelToken* RxCancelHandle;

RX_API uint32_t       rx_get_abi_version(void);
RX_API const wchar_t* rx_get_version_string(void);
RX_API uint32_t       rx_sizeof_volume_info(void);
RX_API uint32_t       rx_sizeof_probe_result(void);

/* Hardware details for every fixed/removable lettered volume. capacity==0 / out==NULL only reports the count. */
RX_API RxStatus rx_enumerate_volumes(RxVolumeInfo* out, uint32_t capacity, uint32_t* count);
RX_API RxStatus rx_query_volume(const wchar_t* root, RxVolumeInfo* out);

/* Read-only access. Opening a raw volume requires administrator rights. */
RX_API RxStatus rx_volume_open(const wchar_t* root, RxVolumeHandle* handle);
RX_API RxStatus rx_volume_get_geometry(RxVolumeHandle handle, RxVolumeGeometry* out);
/* Any offset/length is allowed; alignment to the sector size is handled internally. Reads past the end are clamped. */
RX_API RxStatus rx_volume_read(RxVolumeHandle handle, uint64_t offset, void* buffer, uint32_t length, uint32_t* bytes_read);
RX_API RxStatus rx_volume_close(RxVolumeHandle handle);

/* Open + read sector 0 + identify file system + close, in one call. */
RX_API RxStatus rx_probe_volume(const wchar_t* root, RxProbeResult* out);
/* Pure function: identify NTFS / exFAT / FAT32 / FAT16 / FAT12 from a boot sector. */
RX_API RxStatus rx_identify_filesystem(const uint8_t* sector, uint32_t length, uint32_t* fs_id);

/* Safety: refuse to write recovered data onto the volume being scanned. */
RX_API RxStatus rx_validate_destination(const wchar_t* source_root, const wchar_t* dest_dir, int32_t* verdict);

/* Cooperative cancellation for long operations (used by scans from Phase 3). */
RX_API RxCancelHandle rx_cancel_create(void);
RX_API void           rx_cancel_request(RxCancelHandle token);
RX_API int32_t        rx_cancel_is_requested(RxCancelHandle token);
RX_API void           rx_cancel_destroy(RxCancelHandle token);

RX_API uint32_t rx_last_native_error(void);   /* thread-local Win32 error of the last failing call */

/* ---------------- Phase 3: NTFS Quick Scan (read-only) ---------------- */

/* Result of a file-header check. */
enum { RX_HDR_NOT_CHECKED = 0, RX_HDR_MATCH = 1, RX_HDR_MISMATCH = 2, RX_HDR_NO_SIGNATURE = 3 };

/* Recovery condition, based on evidence gathered from the disk (never just "the name exists"). */
enum {
    RX_COND_UNKNOWN = 0,       /* could not be assessed (e.g. cluster bitmap unreadable)             */
    RX_COND_VERIFIED = 1,      /* data location is free and the content header matches the file type */
    RX_COND_LIKELY = 2,        /* data location is free; content type could not be checked           */
    RX_COND_PARTIAL = 3,       /* some of the file's clusters are now used by other files            */
    RX_COND_LOST = 4,          /* clusters reused, or content no longer matches the file type        */
    RX_COND_UNSUPPORTED = 5,   /* compressed / encrypted / needs metadata not followed yet           */
    RX_COND_EMPTY = 6          /* zero-length file                                                   */
};

/* RxFileEntry.flags */
enum {
    RX_ENT_RESIDENT = 1, RX_ENT_PATH_UNCERTAIN = 2, RX_ENT_COMPRESSED = 4, RX_ENT_ENCRYPTED = 8, RX_ENT_SPARSE = 16,
    RX_ENT_ATTR_LIST = 32, RX_ENT_NAME_TRUNCATED = 64, RX_ENT_HAS_STREAMS = 128, RX_ENT_BAD_RUNS = 256
};

/* RxScanOptions.flags */
enum { RX_SCANF_VALIDATE_CONTENT = 1 };

/* RxScanProgress.phase / .flags */
enum { RX_PHASE_OPENING = 1, RX_PHASE_SCANNING = 2, RX_PHASE_VALIDATING = 3, RX_PHASE_PATHS = 4, RX_PHASE_DONE = 5 };
enum { RX_SCANP_TRUNCATED = 1, RX_SCANP_DONE = 2, RX_SCANP_BITMAP_UNAVAILABLE = 4, RX_SCANP_MFT_PARTIAL = 8 };

typedef struct RxScanOptions {
    uint32_t struct_size;
    uint32_t flags;            /* RX_SCANF_* */
    uint32_t max_results;      /* 0 = default (250000); hard limit 5000000 */
    uint32_t reserved;
} RxScanOptions;

typedef struct RxScanProgress {
    uint32_t struct_size;
    uint32_t phase;
    uint32_t records_total;
    uint32_t records_done;
    uint32_t candidates;
    uint32_t flags;
    uint32_t status;           /* final RxStatus once RX_SCANP_DONE is set */
    uint32_t bad_records;      /* MFT records that failed integrity checks */
} RxScanProgress;

/* One deleted file found in the MFT. Windows layout is 1120 bytes. Times are Windows FILETIME (0 = unknown). */
typedef struct RxFileEntry {
    uint32_t struct_size;
    uint32_t flags;            /* RX_ENT_* */
    uint32_t condition;        /* RX_COND_* */
    uint32_t header_check;     /* RX_HDR_* */
    uint64_t mft_number;
    uint64_t size;             /* bytes, from the $DATA attribute */
    uint64_t created;
    uint64_t modified;
    uint64_t accessed;
    uint64_t record_changed;   /* last change of the MFT record; often close to the deletion time, but not a guaranteed deletion time */
    uint32_t clusters_total;
    uint32_t clusters_in_use;  /* clusters now allocated to something else */
    uint32_t record_seq;
    uint32_t reserved;
    wchar_t  path[520];        /* "\\folder\\name.ext", reconstructed from parent records */
} RxFileEntry;

typedef struct RxNtfsScan* RxScanHandle;

RX_API uint32_t rx_sizeof_file_entry(void);
RX_API uint32_t rx_sizeof_scan_progress(void);
RX_API uint32_t rx_sizeof_scan_options(void);

/* Opens the volume read-only and validates the NTFS structures. Needs administrator rights on a real volume. */
RX_API RxStatus rx_ntfs_scan_create(const wchar_t* root, const RxScanOptions* options, RxScanHandle* scan);
/* Blocking. Returns RX_ERR_CANCELLED if cancelled; results found so far remain readable. */
RX_API RxStatus rx_ntfs_scan_run(RxScanHandle scan, RxCancelHandle cancel);
/* Thread-safe: may be called from another thread while rx_ntfs_scan_run is executing. */
RX_API RxStatus rx_ntfs_scan_progress(RxScanHandle scan, RxScanProgress* progress);
RX_API uint32_t rx_ntfs_scan_result_count(RxScanHandle scan);
RX_API RxStatus rx_ntfs_scan_get_results(RxScanHandle scan, uint32_t start, RxFileEntry* out, uint32_t capacity, uint32_t* count);
/* Reads file content (not metadata) of result `index`. Read-only; the caller writes the recovered file. */
RX_API RxStatus rx_ntfs_read_file(RxScanHandle scan, uint32_t index, uint64_t offset, void* buffer, uint32_t length, uint32_t* bytes_read);
RX_API void     rx_ntfs_scan_destroy(RxScanHandle scan);

/* Checks whether `data` (the first bytes of a file) matches the file type implied by `name`'s extension. */
RX_API RxStatus rx_check_header(const wchar_t* name, const uint8_t* data, uint32_t length, uint32_t* result);

#ifdef __cplusplus
}
#endif
#endif /* RX_ENGINE_H */
