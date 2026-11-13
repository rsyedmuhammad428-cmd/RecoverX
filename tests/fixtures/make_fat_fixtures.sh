#!/bin/sh
# Builds the FAT32 and exFAT recovery fixtures with the REAL Linux FAT/exFAT drivers. Run in a disposable privileged container
# (loop mounts need it); only image files in OUT are created:
#   docker run --rm --privileged -v <repo>:/src:ro -v <out>:/out debian:12-slim sh /src/tests/fixtures/make_fat_fixtures.sh
# Output in /out: fat32_basic.img / exfat_basic.img (+ .manifest.tsv) and fat_fixtures.tar.gz.
set -eu
apt-get update -qq >/dev/null && apt-get install -y -qq dosfstools exfatprogs python3 >/dev/null
OUT=/out; MNT=/mnt/rx; SAMPLES=/tmp/samples
mkdir -p "$MNT" "$SAMPLES"
python3 /src/tests/fixtures/make_carve_fixture.py /tmp/carve --samples "$SAMPLES" >/dev/null

build() {   # $1 = name, $2 = size, then mkfs command
    name=$1; size=$2; shift 2
    img="$OUT/${name}_basic.img"; man="$OUT/${name}_basic.manifest.tsv"
    rm -f "$img"; truncate -s "$size" "$img"
    "$@" "$img" >/dev/null
    case $name in fat32) opts="loop,utf8,shortname=mixed" ;; *) opts="loop,iocharset=utf8" ;; esac
    mount -o "$opts" "$img" "$MNT"
    python3 /src/tests/fixtures/fat_populate.py create "$MNT" "$SAMPLES" "$man"
    python3 /src/tests/fixtures/fat_populate.py delete "$MNT" "$man"
    umount "$MNT"
    echo "built $img"
}
# FAT32 needs >= 65525 clusters: 288 MB with 4 KB clusters. exFAT: 64 MB with 4 KB clusters.
build fat32 288M mkfs.vfat -F 32 -S 512 -s 8 -n RXFAT32
build exfat 64M mkfs.exfat -c 4K -L RXEXFAT
cd "$OUT" && tar czf fat_fixtures.tar.gz fat32_basic.img exfat_basic.img && ls -la "$OUT"
