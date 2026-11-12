// Compile-time guard: the C# structs in RecoverX.Core/Native/NativeMethods.cs assume exactly these Windows layouts.
// Compiled with a Windows toolchain (wchar_t = 2 bytes) by scripts/verify-linux.sh. A mismatch fails the build.
#include <cstddef>
#include "rx_engine.h"
#define RX_ASSERT_EQ(a, b) static_assert((a) == (b), #a " must equal " #b)
RX_ASSERT_EQ(sizeof(wchar_t), 2);
RX_ASSERT_EQ(sizeof(RxVolumeInfo), 216);
RX_ASSERT_EQ(sizeof(RxProbeResult), 32);
RX_ASSERT_EQ(sizeof(RxVolumeGeometry), 16);
RX_ASSERT_EQ(offsetof(RxVolumeInfo, struct_size), 0);
RX_ASSERT_EQ(offsetof(RxVolumeInfo, bus_type), 8);
RX_ASSERT_EQ(offsetof(RxVolumeInfo, removable_media), 16);
RX_ASSERT_EQ(offsetof(RxVolumeInfo, flags), 24);
RX_ASSERT_EQ(offsetof(RxVolumeInfo, root), 32);
RX_ASSERT_EQ(offsetof(RxVolumeInfo, vendor), 40);
RX_ASSERT_EQ(offsetof(RxVolumeInfo, product), 120);
RX_ASSERT_EQ(offsetof(RxProbeResult, total_bytes), 16);
RX_ASSERT_EQ(offsetof(RxProbeResult, bytes_read), 24);
// Phase 3 structures
RX_ASSERT_EQ(sizeof(RxScanOptions), 16);
RX_ASSERT_EQ(sizeof(RxScanProgress), 32);
RX_ASSERT_EQ(sizeof(RxFileEntry), 1120);
RX_ASSERT_EQ(offsetof(RxFileEntry, flags), 4);
RX_ASSERT_EQ(offsetof(RxFileEntry, condition), 8);
RX_ASSERT_EQ(offsetof(RxFileEntry, header_check), 12);
RX_ASSERT_EQ(offsetof(RxFileEntry, mft_number), 16);
RX_ASSERT_EQ(offsetof(RxFileEntry, size), 24);
RX_ASSERT_EQ(offsetof(RxFileEntry, created), 32);
RX_ASSERT_EQ(offsetof(RxFileEntry, modified), 40);
RX_ASSERT_EQ(offsetof(RxFileEntry, accessed), 48);
RX_ASSERT_EQ(offsetof(RxFileEntry, record_changed), 56);
RX_ASSERT_EQ(offsetof(RxFileEntry, clusters_total), 64);
RX_ASSERT_EQ(offsetof(RxFileEntry, clusters_in_use), 68);
RX_ASSERT_EQ(offsetof(RxFileEntry, record_seq), 72);
RX_ASSERT_EQ(offsetof(RxFileEntry, path), 80);
RX_ASSERT_EQ(offsetof(RxScanProgress, phase), 4);
RX_ASSERT_EQ(offsetof(RxScanProgress, flags), 20);
RX_ASSERT_EQ(offsetof(RxScanProgress, bad_records), 28);
int main() { return 0; }
