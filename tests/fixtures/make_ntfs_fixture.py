#!/usr/bin/env python3
"""Builds a disposable NTFS test volume with REAL NTFS structures and a ground-truth manifest.

Requires Linux, root, and the ntfs-3g package (mkntfs, ntfs-3g, FUSE). Output (in the given folder):
    ntfs_basic.img        sparse 64 MB NTFS volume (512-byte sectors, 4 KB clusters)
    ntfs_basic.manifest.json   one record per file: path, size, sha256, mtime, deleted, tag, extra expectations
Files are created through the NTFS driver, their hashes recorded, then selected files are deleted through the same
driver, so the resulting MFT records are exactly what a real deletion leaves behind.
Usage: sudo python3 make_ntfs_fixture.py OUTPUT_DIR
"""
import hashlib, json, os, random, subprocess, sys, time

out = sys.argv[1] if len(sys.argv) > 1 else "."
os.makedirs(out, exist_ok=True)
img = os.path.join(out, "ntfs_basic.img")
mnt = os.path.join(out, "mnt_tmp")
rng = random.Random(20261107)          # fixed seed: reproducible content

def run(*a, check=True): return subprocess.run(a, check=check, capture_output=True, text=True)

if os.path.exists(img): os.remove(img)
run("truncate", "-s", "64M", img)
run("mkntfs", "-F", "-Q", "-L", "RXFIXTURE", "-s", "512", "-c", "4096", img)
os.makedirs(mnt, exist_ok=True)
run("ntfs-3g", "-o", "rw,streams_interface=windows,compression", img, mnt)

files = []     # manifest records
def blob(n, head=b""):
    """Deterministic binary content, unique per 4 KB block (so a mis-ordered or missing cluster changes the hash)
    but highly compressible, which keeps the committed fixture small."""
    if not n: return b""
    out = bytearray()
    for idx in range((n + 4095) // 4096):
        out += (idx.to_bytes(8, "little") + rng.getrandbits(64).to_bytes(8, "little")) * 256
    data = bytearray(out[:n])
    data[:len(head)] = head[:n]
    return bytes(data)
def text(n):
    words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet"]
    s = ""
    while len(s.encode("utf-8")) < n: s += rng.choice(words) + " "
    return s.encode("utf-8")[:n]

def add(path, data, delete, tag, mtime=None, expect=None, stream=None):
    full = os.path.join(mnt, path.lstrip("/"))
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "wb") as f: f.write(data)
    if stream is not None:
        with open(full + ":secret", "wb") as f: f.write(stream)
    if mtime: os.utime(full, (mtime, mtime))
    st = os.stat(full)
    files.append({"path": "\\" + path.strip("/").replace("/", "\\"), "size": len(data),
                  "sha256": hashlib.sha256(data).hexdigest(), "mtime": int(st.st_mtime), "deleted": delete,
                  "tag": tag, "expect": expect or {}, "has_stream": stream is not None})
    return full


def fill_volume():
    """Writes a filler file until the volume is completely full; returns its path."""
    path = os.path.join(mnt, "filler.bin")
    f = open(path, "wb", buffering=0)          # unbuffered: ENOSPC surfaces on the write that hits it
    chunk = b"\xAA" * (1 << 20)
    for size in (1 << 20, 1 << 16, 4096):
        try:
            while True:
                f.write(chunk[:size])
        except OSError:
            pass                                  # full at this granularity
    try: f.close()
    except OSError: pass
    os.sync()
    return path

T = lambda s: int(time.mktime(time.strptime(s, "%Y-%m-%d %H:%M:%S")))
add("keep/keep.txt", text(500), False, "kept")
add("docs/small.txt", text(120), True, "resident_text", T("2020-02-03 04:05:06"), {"flags": ["RESIDENT"], "condition": "VERIFIED"})
add("docs/report.pdf", blob(300_000, b"%PDF-1.4\n"), True, "pdf", T("2021-03-04 05:06:07"), {"condition": "VERIFIED"})
add("pics/photo1.jpg", blob(200_000, b"\xff\xd8\xff\xe0\x00\x10JFIF"), True, "jpg", T("2019-12-31 23:59:58"), {"condition": "VERIFIED"})
add("pics/big_video.mp4", blob(3_000_000, b"\x00\x00\x00\x18ftypmp42"), True, "mp4_3MB", None, {"condition": "VERIFIED"})
add("empty.dat", b"", True, "empty", None, {"condition": "EMPTY"})
add("docs/r\u00e9sum\u00e9_\u00f1.txt", text(2048), True, "unicode_latin", None, {"condition": "VERIFIED"})
add("\u65e5\u672c\u8a9e/\u30c6\u30b9\u30c8.txt", text(1024), True, "unicode_cjk", None, {"condition": "VERIFIED"})
add("deleted_dir/inner/notes.txt", text(3000), True, "in_deleted_dir", None, {"condition": "VERIFIED"})
add("deleted_dir/pic.png", blob(50_000, b"\x89PNG\r\n\x1a\n"), True, "png_in_deleted_dir", None, {"condition": "VERIFIED"})
add("a/b/c/d/e/f/g/h/deep.txt", text(800), True, "deep_path", None, {"condition": "VERIFIED"})
add("ads/withstream.txt", text(1000), True, "alt_stream", None, {"condition": "VERIFIED", "flags": ["HAS_STREAMS"]}, stream=text(500))

# a pair of ~1 MB files written alternately (the allocator keeps each contiguous, so this is NOT a fragmentation test)
fa, fb = os.path.join(mnt, "frag/a.zip"), os.path.join(mnt, "frag/b.zip")
os.makedirs(os.path.dirname(fa), exist_ok=True)
da = bytearray(b"PK\x03\x04"); db = bytearray(b"PK\x03\x04")
with open(fa, "wb") as A, open(fb, "wb") as B:
    A.write(da); B.write(db); A.flush(); B.flush()
    for _ in range(16):
        ca, cb = blob(65536), blob(65536)
        A.write(ca); A.flush(); os.fsync(A.fileno()); da += ca
        B.write(cb); B.flush(); os.fsync(B.fileno()); db += cb
for p, d, dele, tag in (("frag/a.zip", da, True, "zip_large_deleted"), ("frag/b.zip", db, False, "zip_large_kept")):
    files.append({"path": "\\" + p.replace("/", "\\"), "size": len(d), "sha256": hashlib.sha256(bytes(d)).hexdigest(),
                  "mtime": int(os.stat(os.path.join(mnt, p)).st_mtime), "deleted": dele, "tag": tag,
                  "expect": {"condition": "VERIFIED"} if dele else {}, "has_stream": False})

# many small files: exercises chunked MFT reading
for i in range(300):
    add("many/f%03d.txt" % i, text(100), i % 2 == 1, "many_deleted" if i % 2 else "many_kept", None,
        {"condition": "VERIFIED"} if i % 2 else {})


# ---- cycle B: a genuinely fragmented file ----
# 200 small files, volume filled, every other small file deleted -> the only free space is scattered 2-cluster holes,
# so the next large file MUST be split across many fragments.
for i in range(200):
    add("holes/h%03d.dat" % i, blob(8192), i % 2 == 1, "hole_deleted" if i % 2 else "hole_kept", None, {})
filler = fill_volume()
for i in range(1, 200, 2):
    os.remove(os.path.join(mnt, "holes/h%03d.dat" % i))
os.sync()
frag = blob(400_000, b"PK\x03\x04")
os.makedirs(os.path.join(mnt, "frag"), exist_ok=True)
with open(os.path.join(mnt, "frag/frag.zip"), "wb") as f: f.write(frag)
os.sync()
os.remove(filler)
os.sync()
files.append({"path": "\\frag\\frag.zip", "size": len(frag), "sha256": hashlib.sha256(frag).hexdigest(),
              "mtime": int(os.stat(os.path.join(mnt, "frag/frag.zip")).st_mtime), "deleted": True, "tag": "fragmented",
              "expect": {"condition": "VERIFIED", "min_runs": 10}, "has_stream": False})

# files that will be overwritten after deletion (created now, deleted later in a controlled order)
add("ow/old_full.jpg", blob(400_000, b"\xff\xd8\xff\xe0\x00\x10JFIF"), True, "overwritten_full", None, {"condition_in": ["PARTIAL", "LOST"], "min_clusters_in_use": 90})
add("ow/old_half.jpg", blob(400_000, b"\xff\xd8\xff\xe0\x00\x10JFIF"), True, "overwritten_half", None, {"condition_in": ["PARTIAL", "LOST"], "min_clusters_in_use": 20})
# compressed file (LZNT1): the engine must report it as unsupported rather than return garbage.
# The driver compresses new files created inside a folder whose compressed attribute is set.
compressed_ok = True
cdir = os.path.join(mnt, "comp")
os.makedirs(cdir, exist_ok=True)
r = run("setfattr", "-n", "system.ntfs_attrib_be", "-v", "0x00000800", cdir, check=False)
cpath = os.path.join(cdir, "compressed.txt")
if r.returncode != 0: compressed_ok = False
cdata = text(60_000)
with open(cpath, "wb") as f: f.write(cdata)
attr = run("getfattr", "-n", "system.ntfs_attrib_be", "-e", "hex", cpath, check=False).stdout
if "0x00000" not in attr or int(attr.split("=")[-1].strip(), 16) & 0x800 == 0: compressed_ok = False
files.append({"path": "\\comp\\compressed.txt", "size": len(cdata), "sha256": hashlib.sha256(cdata).hexdigest(),
              "mtime": int(os.stat(cpath).st_mtime), "deleted": True, "tag": "compressed",
              "expect": {"condition": "UNSUPPORTED"} if compressed_ok else {"skip": "could not set compression"}, "has_stream": False})
os.sync()

# ---- force real cluster re-use: fill the volume, then delete + rewrite so the freed clusters are the only ones left ----
filler = fill_volume()
os.sync()
os.remove(os.path.join(mnt, "ow/old_full.jpg"))
os.sync()
full_new = blob(400_000)
with open(os.path.join(mnt, "ow/new_full.bin"), "wb") as f: f.write(full_new)      # takes (nearly) all of old_full's clusters
os.sync()
os.remove(os.path.join(mnt, "ow/old_half.jpg"))
os.sync()
half_new = blob(200_000)
with open(os.path.join(mnt, "ow/new_half.bin"), "wb") as f: f.write(half_new)      # takes about half of old_half's clusters
os.sync()
os.remove(filler)
for name, data, tag in (("new_full.bin", full_new, "overwriter_full"), ("new_half.bin", half_new, "overwriter_half")):
    files.append({"path": "\\ow\\" + name, "size": len(data), "sha256": hashlib.sha256(data).hexdigest(),
                  "mtime": int(time.time()), "deleted": False, "tag": tag, "expect": {}, "has_stream": False})
os.sync()

# ---- now the ordinary deletions (no writes happen after this point, so these files' clusters stay intact) ----
special = ("in_deleted_dir", "png_in_deleted_dir", "overwritten_full", "overwritten_half", "hole_deleted")
for f in files:
    if f["deleted"] and f["tag"] not in special:
        os.remove(os.path.join(mnt, f["path"].lstrip("\\").replace("\\", "/")))
subprocess.run(["rm", "-r", os.path.join(mnt, "deleted_dir")], check=True)
subprocess.run(["rm", "-r", os.path.join(mnt, "a")], check=True)
os.sync()
run("umount", mnt, check=False); run("fusermount", "-u", mnt, check=False)
os.rmdir(mnt)

# ---- independent oracle: ntfsundelete's own view of every deleted file (inode, % of data still unallocated) ----
oracle = []
scan = subprocess.run(["ntfsundelete", "--scan", "--match", "*", img], capture_output=True, text=True).stdout
for line in scan.splitlines():
    parts = line.split(None, 6)
    if len(parts) == 7 and parts[0].isdigit() and parts[2].endswith("%") and parts[1].startswith("F"):
        oracle.append({"inode": int(parts[0]), "pct": int(parts[2][:-1]), "size": int(parts[5]), "name": parts[6]})

with open(os.path.join(out, "ntfs_basic.manifest.json"), "w", encoding="utf-8") as m:
    json.dump({"cluster_size": 4096, "sector_size": 512, "files": files, "oracle": oracle}, m, indent=1, ensure_ascii=False)
print("created", img, "with", len(files), "files;", sum(1 for f in files if f["deleted"]), "deleted; oracle rows:", len(oracle), "; compression test:", compressed_ok)

# ---- derived files used by the C++ tests ----
# TSV: "F" rows = ground truth recorded BEFORE deletion; "O" rows = ntfsundelete's independent view.
with open(os.path.join(out, "ntfs_basic.manifest.tsv"), "w", encoding="utf-8") as t:
    t.write("# F path size sha256 mtime deleted tag allowed_conditions flags min_clusters_in_use min_runs\n")
    for f in files:
        e = f["expect"]
        cond = ",".join(e.get("condition_in", [e["condition"]] if "condition" in e else []))
        t.write("\t".join(["F", f["path"], str(f["size"]), f["sha256"], str(f["mtime"]), "1" if f["deleted"] else "0", f["tag"],
                           cond, ",".join(e.get("flags", [])), str(e.get("min_clusters_in_use", 0)), str(e.get("min_runs", 0))]) + "\n")
    for o in oracle:
        t.write("\t".join(["O", str(o["inode"]), str(o["pct"]), str(o["size"]), o["name"]]) + "\n")
import tarfile
with tarfile.open(os.path.join(out, "ntfs_basic.tar.gz"), "w:gz", compresslevel=9) as tf:
    tf.add(img, arcname="ntfs_basic.img")
print("wrote manifest.tsv and ntfs_basic.tar.gz (%d bytes)" % os.path.getsize(os.path.join(out, "ntfs_basic.tar.gz")))
