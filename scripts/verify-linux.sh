#!/bin/sh
# Checks that can run without Windows. This is exactly what was run when Phase 2 was produced.
# Needs: cmake, g++, python3; optional: mingw-w64 (cross-compile check), mono-mcs + libmono-sqlite4.0-cil (managed tests).
set -e
cd "$(dirname "$0")/.."

echo "== 1. native engine: build + unit tests (Linux host, image-file volumes) =="
cmake -S . -B build/linux -DCMAKE_BUILD_TYPE=Release >/dev/null
cmake --build build/linux
(cd build/linux && ctest --output-on-failure)

if command -v x86_64-w64-mingw32-g++ >/dev/null 2>&1; then
  echo "== 2. native engine: cross-compile the Windows code path (x64 + x86) =="
  for a in x86_64 i686; do
    cmake -S . -B build/mingw-$a -DCMAKE_BUILD_TYPE=Release -DCMAKE_TOOLCHAIN_FILE=cmake/mingw-w64-$a.cmake >/dev/null
    cmake --build build/mingw-$a
    $a-w64-mingw32-g++ -std=c++14 -Isrc/RecoverX.Native/include -fsyntax-only tests/NativeTests/abi_layout_check.cpp
    echo "$a: Windows build OK, ABI layout assertions OK"
  done
else
  echo "== 2. skipped (mingw-w64 not installed) =="
fi

echo "== 2b. native engine under AddressSanitizer + UBSan (includes 400 random MFT/boot-sector corruptions) =="
cmake -S . -B build/asan -DCMAKE_BUILD_TYPE=Debug -DRX_SANITIZE=ON >/dev/null
cmake --build build/asan
ASAN_OPTIONS=detect_leaks=1 UBSAN_OPTIONS=halt_on_error=1 ./build/asan/tests/NativeTests/ntfs_tests --fuzz=400 | tail -3

echo "== 2c. rx_selftest --scan output vs the fixture's independent ground truth (SHA-256) =="
./build/linux/src/RecoverX.Native/rx_selftest --scan build/linux/fixtures/ntfs_basic.img --hash > build/scan_out.txt
python3 - <<'PY'
rows = {}
for l in open("tests/fixtures/ntfs_basic.manifest.tsv", encoding="utf-8"):
    f = l.rstrip("\n").split("\t")
    if f[0] == "F" and f[5] == "1" and f[7] == "VERIFIED": rows[f[1].lower()] = (int(f[2]), f[3])
got = {}
for l in open("build/scan_out.txt", encoding="utf-8"):
    f = l.rstrip("\n").split("\t")
    if f[0] == "ENTRY": got[f[1].lower()] = (int(f[2]), f[4])
ok = sum(1 for p, (sz, sh) in rows.items() if p in got and got[p][0] == sz and (got[p][1] == sh or sz == 0))
print("%d of %d recoverable files match size and SHA-256" % (ok, len(rows)))
assert ok == len(rows), "self-test output does not match ground truth"
PY

echo "== 3. localization lint =="
python3 scripts/check-localization.py
