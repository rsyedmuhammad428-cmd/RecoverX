#!/usr/bin/env python3
"""Runs INSIDE the fixture container (see make_fat_fixtures.sh) against a mounted FAT32 or exFAT volume.
    fat_populate.py create MOUNT SAMPLES MANIFEST    write files, record SHA-256 (before anything is deleted)
    fat_populate.py delete MOUNT MANIFEST            overwrite victims, fill the volume, then delete the target files
Files are created and deleted through the kernel's FAT/exFAT driver, so the on-disk result is exactly what a real deletion leaves.
"""
import hashlib, os, random, shutil, subprocess, sys

rng = random.Random(20261113)

def sync(): subprocess.run(["sync"], check=True)

def blob(n, seed):
    r = random.Random(seed)
    out = bytearray()
    for i in range((n + 4095) // 4096):
        out += (i.to_bytes(8, "little") + r.getrandbits(64).to_bytes(8, "little")) * 256   # unique per 4 KB, compressible
    return bytes(out[:n])

def text(n, seed):
    r = random.Random(seed)
    words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet"]
    s = ""
    while len(s) < n: s += r.choice(words) + (" " if r.random() < 0.9 else "\n")
    return s[:n].encode()

def create(mnt, samples, manifest):
    rows = []
    def put(rel, data, deleted, tag, expect):
        full = os.path.join(mnt, rel)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "wb") as f: f.write(data)
        rows.append([rel, len(data), hashlib.sha256(data).hexdigest(), int(deleted), tag, expect])
    def sample(name): return open(os.path.join(samples, name), "rb").read()

    put("keep/keep.txt", text(3000, 1), False, "kept", "-")
    put("photos/beach.jpg", sample("photo_exif.jpg"), True, "jpeg", "VERIFIED")
    put("photos/kept_photo.jpg", sample("photo_small.jpg"), False, "kept", "-")
    put("docs/report.pdf", sample("letter.pdf"), True, "pdf", "VERIFIED")
    put("docs/letter.docx", sample("report.docx"), True, "docx", "VERIFIED")
    put("media/clip.mp4", sample("clip_moov_last.mp4"), True, "mp4", "VERIFIED")
    put("media/song.mp3", sample("song.mp3"), True, "mp3", "VERIFIED")
    put("Résumé – ünïcödé file.txt", text(5000, 2), True, "unicode_name", "VERIFIED")
    put("a very long file name that needs several long-name directory entries to store.txt", text(9000, 3), True, "long_name", "VERIFIED")
    put("UPPER.TXT", text(2500, 4), True, "short_name_only", "VERIFIED")
    put("small.txt", b"a small file well under one cluster\n", True, "small", "VERIFIED")
    put("empty.dat", b"", True, "empty", "EMPTY")
    put("deleted_dir/inner/notes.txt", text(6000, 5), True, "in_deleted_dir", "VERIFIED")
    put("deleted_dir/picture.png", sample("image.png"), True, "in_deleted_dir", "VERIFIED")
    for i in range(40):
        put("many/f%03d.txt" % i, text(300 + i * 37, 100 + i), i % 2 == 1, "many", "VERIFIED" if i % 2 == 1 else "-")
    sync()
    # Fragmented file: a.bin, then b.bin right after it, then a.bin grows -> a.bin's second part lies after b.bin.
    a = os.path.join(mnt, "frag", "a.bin"); os.makedirs(os.path.dirname(a), exist_ok=True)
    first = blob(40960, 7)
    with open(a, "wb") as f: f.write(first)
    sync()
    put("frag/b.bin", blob(8192, 8), False, "kept", "-")
    sync()
    second = blob(40960, 9)
    with open(a, "ab") as f: f.write(second)
    sync()
    rows.append(["frag/a.bin", len(first) + len(second), hashlib.sha256(first + second).hexdigest(), 1, "fragmented", "PARTIAL,LIKELY"])
    # Victim: deleted first, then its clusters are reused by the filler -> must never be reported as intact.
    put("ow/victim.jpg", sample("photo_exif.jpg")[:20000] + blob(60000, 11), True, "overwritten", "LOST,PARTIAL")
    sync()
    with open(manifest, "w", encoding="utf-8", newline="\n") as m:
        m.write("# path\tsize\tsha256\tdeleted\ttag\texpect\n")
        for r in rows: m.write("\t".join(str(x) for x in r) + "\n")

def delete(mnt, manifest):
    rows = [l.rstrip("\n").split("\t") for l in open(manifest, encoding="utf-8") if not l.startswith("#")]
    os.remove(os.path.join(mnt, "ow", "victim.jpg")); sync()
    # Fill the volume completely (kept): every free cluster, including the victim's, now holds filler data.
    pattern = (b"RecoverX filler data, not a deleted file. " * 100)[:4096]
    with open(os.path.join(mnt, "filler.bin"), "wb") as f:
        try:
            while True: f.write(pattern * 64)
        except OSError: pass
    sync()
    for r in rows:
        if r[3] == "1" and r[4] != "overwritten":
            p = os.path.join(mnt, r[0])
            if os.path.exists(p): os.remove(p)
    shutil.rmtree(os.path.join(mnt, "deleted_dir"))
    sync()

if __name__ == "__main__":
    if sys.argv[1] == "create": create(sys.argv[2], sys.argv[3], sys.argv[4])
    else: delete(sys.argv[2], sys.argv[3])
