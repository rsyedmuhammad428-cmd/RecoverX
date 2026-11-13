#!/usr/bin/env python3
"""Builds the Deep Scan (carving) test image and its ground-truth manifest. Python 3 standard library only; any OS.

    python tests/fixtures/make_carve_fixture.py OUTPUT_DIR [--samples DIR]

Output: carve_basic.img (8 MB raw "volume" with no file system, zero background, like a quick-formatted card) and
carve_basic.manifest.tsv. Each file is placed at a sector-aligned offset, as every file system does.

What the files are:
  * Real, decodable media produced by real encoders written here: baseline JPEG (own DCT + Huffman encoder, with an EXIF
    date), PNG (zlib), GIF (LZW), BMP, WAV (PCM), PDF (valid xref, one with an incremental update), ZIP/DOCX made with
    Python's zipfile (one with data descriptors).
  * Structurally valid containers whose payload is filler: MP4 (ftyp/mdat/moov+mvhd), MKV (EBML/Segment/Cluster/Cues),
    MP3 (ID3v2 + real MPEG frame headers + ID3v1), 7z (correct start/next-header CRCs), DOC (OLE2 FAT + directory).
    They test the parsers' structure walking, not media playback.
  * Negative cases: a fragmented JPEG, a JPEG with a zero-filled gap, a PNG whose tail was overwritten, a fake JPEG header.
--samples DIR also writes each intact file separately (used to cross-check the JPEG/PNG/GIF/BMP with Windows' decoder).
"""
import hashlib, io, math, os, random, struct, sys, tarfile, zipfile, zlib

rng = random.Random(20261112)
def noise(n): return bytes(rng.getrandbits(8) for _ in range(n))

# ------------------------------------------------------------------ JPEG (baseline, 4:4:4, own encoder)
ZZ = [0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,12,19,26,33,40,48,41,34,27,20,13,6,7,14,21,28,
      35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63]
C = [[(math.sqrt(0.125) if u == 0 else 0.5) * math.cos((2 * x + 1) * u * math.pi / 16) for x in range(8)] for u in range(8)]

def fdct(block):  # separable 8x8 DCT-II, block = 64 floats (row major)
    tmp = [0.0] * 64
    for y in range(8):
        row = block[y * 8:(y + 1) * 8]
        for u in range(8):
            cu = C[u]; tmp[y * 8 + u] = sum(cu[x] * row[x] for x in range(8))
    out = [0.0] * 64
    for u in range(8):
        for v in range(8):
            cv = C[v]; out[v * 8 + u] = sum(cv[y] * tmp[y * 8 + u] for y in range(8))
    return out

class Bits:
    def __init__(self): self.out = bytearray(); self.acc = 0; self.n = 0
    def put(self, code, length):
        for i in range(length - 1, -1, -1):
            self.acc = (self.acc << 1) | ((code >> i) & 1); self.n += 1
            if self.n == 8:
                self.out.append(self.acc)
                if self.acc == 0xFF: self.out.append(0)          # byte stuffing
                self.acc = 0; self.n = 0
    def flush(self):
        while self.n: self.put(1, 1)

def fixed_table(symbols, length):
    """Valid Huffman table: every symbol gets the same code length (all-ones code stays unused)."""
    assert len(symbols) < (1 << length)
    bits = [0] * 16; bits[length - 1] = len(symbols)
    codes = {s: (i, length) for i, s in enumerate(symbols)}
    return bits, list(symbols), codes

DC_SYMS = list(range(12))
AC_SYMS = [0x00, 0xF0] + [(r << 4) | s for r in range(16) for s in range(1, 11)]
DC_T = fixed_table(DC_SYMS, 4)
AC_T = fixed_table(AC_SYMS, 8)

def magnitude(v):
    n = abs(v).bit_length()
    return n, (v if v >= 0 else v + (1 << n) - 1)

def segment(marker, payload): return bytes([0xFF, marker]) + struct.pack(">H", len(payload) + 2) + payload

def exif_app1(datetime_str):
    s = datetime_str.encode() + b"\0"                             # 20 bytes
    tiff = bytearray(b"II*\0" + struct.pack("<I", 8))
    # IFD0 at 8: one entry (ExifIFD pointer) -> Exif IFD at 26: one entry (DateTimeOriginal) -> string at 44
    tiff += struct.pack("<H", 1) + struct.pack("<HHII", 0x8769, 4, 1, 26) + struct.pack("<I", 0)
    tiff += struct.pack("<H", 1) + struct.pack("<HHII", 0x9003, 2, len(s), 44) + struct.pack("<I", 0)
    tiff += s
    return segment(0xE1, b"Exif\0\0" + bytes(tiff))

def make_jpeg(w, h, seed, exif=None):
    r = random.Random(seed)
    fx, fy, ph = r.uniform(0.02, 0.08), r.uniform(0.02, 0.08), r.uniform(0, 6)
    W, H = (w + 7) // 8 * 8, (h + 7) // 8 * 8
    def px(x, y):
        x, y = min(x, w - 1), min(y, h - 1)
        R = 128 + 100 * math.sin(x * fx + ph); G = 128 + 100 * math.sin(y * fy); B = (x * 3 + y * 2) % 256
        if ((x // 16) + (y // 16)) % 2: R = 255 - R
        return R, G, B
    ql = [max(1, 4 + (i // 8 + i % 8)) for i in range(64)]
    qc = [max(1, 8 + 2 * (i // 8 + i % 8)) for i in range(64)]
    bits = Bits(); pred = [0, 0, 0]
    for by in range(0, H, 8):
        for bx in range(0, W, 8):
            Y, Cb, Cr = [], [], []
            for y in range(8):
                for x in range(8):
                    R, G, B = px(bx + x, by + y)
                    Y.append(0.299 * R + 0.587 * G + 0.114 * B - 128)
                    Cb.append(-0.168736 * R - 0.331264 * G + 0.5 * B)
                    Cr.append(0.5 * R - 0.418688 * G - 0.081312 * B)
            for ci, (blk, qt) in enumerate(((Y, ql), (Cb, qc), (Cr, qc))):
                coef = fdct(blk)
                zz = [int(round(coef[ZZ[i]] / qt[ZZ[i]])) for i in range(64)]
                d = zz[0] - pred[ci]; pred[ci] = zz[0]
                n, v = magnitude(d)
                code, ln = DC_T[2][n]; bits.put(code, ln)
                if n: bits.put(v, n)
                run = 0
                for k in range(1, 64):
                    a = zz[k]
                    if a == 0: run += 1; continue
                    while run > 15: code, ln = AC_T[2][0xF0]; bits.put(code, ln); run -= 16
                    n, v = magnitude(a); n = min(n, 10)
                    code, ln = AC_T[2][(run << 4) | n]; bits.put(code, ln); bits.put(v, n); run = 0
                if run: code, ln = AC_T[2][0x00]; bits.put(code, ln)
    bits.flush()
    out = bytearray(b"\xFF\xD8")
    out += segment(0xE0, b"JFIF\0\x01\x01\0\0\x01\0\x01\0\0")
    if exif: out += exif_app1(exif)
    out += segment(0xDB, bytes([0]) + bytes(ql[ZZ[i]] for i in range(64)) + bytes([1]) + bytes(qc[ZZ[i]] for i in range(64)))
    out += segment(0xC0, bytes([8]) + struct.pack(">HH", h, w) + bytes([3, 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1]))
    for cls_id, t in ((0x00, DC_T), (0x10, AC_T), (0x01, DC_T), (0x11, AC_T)):
        out += segment(0xC4, bytes([cls_id]) + bytes(t[0]) + bytes(t[1]))
    out += segment(0xDA, bytes([3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]))
    out += bits.out + b"\xFF\xD9"
    return bytes(out)

# ------------------------------------------------------------------ PNG / GIF / BMP
def png_chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
def make_png(w, h, seed):
    r = random.Random(seed)
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        for x in range(w): raw += bytes(((x * 7 + y) % 256, (y * 5) % 256, r.getrandbits(8) if (x + y) % 3 == 0 else 90))
    comp = zlib.compress(bytes(raw), 6)
    third = len(comp) // 3
    idats = [comp[:third], comp[third:2 * third], comp[2 * third:]]
    out = b"\x89PNG\r\n\x1a\n" + png_chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
    out += png_chunk(b"tEXt", b"Software\0RecoverX fixture")
    for d in idats: out += png_chunk(b"IDAT", d)
    return out + png_chunk(b"IEND", b"")

def gif_lzw_uncompressed(pixels):
    """LZW stream that never grows the dictionary (clear code every 250 literals): valid, decodable, uncompressed."""
    bits, acc, n, out = 9, 0, 0, bytearray()
    def put(code):
        nonlocal acc, n
        acc |= code << n; n += bits
        while n >= 8: out.append(acc & 0xFF); acc >>= 8; n -= 8
    for i, p in enumerate(pixels):
        if i % 250 == 0: put(256)
        put(p)
    put(257)
    if n: out.append(acc & 0xFF)
    return bytes(out)

def make_gif(w, h, frames=2):
    out = bytearray(b"GIF89a" + struct.pack("<HH", w, h) + bytes([0xF7, 0, 0]))     # 256-colour global table
    out += bytes(v for i in range(256) for v in (i, (i * 3) % 256, 255 - i))
    out += b"\x21\xFF\x0BNETSCAPE2.0\x03\x01\x00\x00\x00"
    for f in range(frames):
        out += b"\x21\xF9\x04\x04\x0A\x00\x00\x00"
        out += b"\x2C" + struct.pack("<HHHH", 0, 0, w, h) + b"\x00" + b"\x08"
        data = gif_lzw_uncompressed([((x + f * 20) ^ y) & 0xFF for y in range(h) for x in range(w)])
        for i in range(0, len(data), 255): out += bytes([len(data[i:i + 255])]) + data[i:i + 255]
        out += b"\x00"
    return bytes(out + b"\x3B")

def make_bmp(w, h):
    row = (w * 3 + 3) // 4 * 4
    pix = bytearray()
    for y in range(h):
        line = bytearray()
        for x in range(w): line += bytes(((x * 4) % 256, (y * 5) % 256, ((x + y) * 3) % 256))
        pix += line + bytes(row - len(line))
    size = 54 + len(pix)
    return b"BM" + struct.pack("<IHHI", size, 0, 0, 54) + struct.pack("<IiiHHIIiiII", 40, w, h, 1, 24, 0, len(pix), 2835, 2835, 0, 0) + bytes(pix)

# ------------------------------------------------------------------ PDF
def make_pdf(pages_text, update=False):
    objs = []
    body = "".join("BT /F1 11 Tf 40 %d Td (%s) Tj ET\n" % (760 - (i % 60) * 12, t) for i, t in enumerate(pages_text))
    objs.append(b"<< /Type /Catalog /Pages 2 0 R >>")
    objs.append(b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
    objs.append(b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>")
    stream = body.encode()
    objs.append(b"<< /Length %d >>\nstream\n" % len(stream) + stream + b"\nendstream")
    objs.append(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offs = []
    for i, o in enumerate(objs):
        offs.append(len(out)); out += b"%d 0 obj\n" % (i + 1) + o + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1)
    for o in offs: out += b"%010d 00000 n \n" % o
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, xref)
    if update:                                                   # incremental update: new Info object + xref + %%EOF
        info = len(out)
        out += b"6 0 obj\n<< /Title (RecoverX updated) /Producer (fixture) >>\nendobj\n"
        x2 = len(out)
        out += b"xref\n0 1\n0000000000 65535 f \n6 1\n%010d 00000 n \n" % info
        out += b"trailer\n<< /Size 7 /Root 1 0 R /Info 6 0 R /Prev %d >>\nstartxref\n%d\n%%%%EOF\n" % (xref, x2)
    return bytes(out)

# ------------------------------------------------------------------ ZIP / DOCX
class Unseekable(io.RawIOBase):
    def __init__(self): self.buf = bytearray()
    def writable(self): return True
    def write(self, b): self.buf += b; return len(b)

def make_docx():
    bio = io.BytesIO()
    with zipfile.ZipFile(bio, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                   '<Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" '
                   'ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        z.writestr("_rels/.rels", '<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                   '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
        paras = "".join("<w:p><w:r><w:t>Paragraph %d of the RecoverX deep scan fixture.</w:t></w:r></w:p>" % i for i in range(400))
        z.writestr("word/document.xml", '<?xml version="1.0"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>' + paras + "</w:body></w:document>")
        z.writestr(zipfile.ZipInfo("word/media/image1.png"), make_png(96, 96, 7), compress_type=zipfile.ZIP_STORED)
    return bio.getvalue()

def make_zip_streamed():
    u = Unseekable()
    with zipfile.ZipFile(u, "w", zipfile.ZIP_DEFLATED) as z:
        for i in range(6): z.writestr("notes/file%02d.txt" % i, ("line %d " % i) * 3000)
        z.writestr("blob.bin", noise(20000))
    return bytes(u.buf)

# ------------------------------------------------------------------ MP4 / MKV / MP3 / WAV / 7z / DOC
def box(t, payload): return struct.pack(">I", 8 + len(payload)) + t + payload
def mvhd(created_unix):
    secs = created_unix + 2082844800
    return box(b"mvhd", struct.pack(">I", 0) + struct.pack(">IIII", secs, secs, 1000, 5000) + struct.pack(">IH", 0x10000, 0x100)
               + bytes(10) + struct.pack(">9I", 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000) + bytes(24) + struct.pack(">I", 2))
def make_mp4(moov_last, created_unix, payload):
    ftyp = box(b"ftyp", b"isom" + struct.pack(">I", 0x200) + b"isomiso2mp41")
    moov = box(b"moov", mvhd(created_unix) + box(b"udta", box(b"name", b"RecoverX fixture")))
    mdat = box(b"mdat", payload)
    return ftyp + (box(b"free", bytes(8)) + mdat + moov if moov_last else moov + mdat)

def ebml_id(i): return i.to_bytes((i.bit_length() + 7) // 8, "big")
def ebml_size(n):
    for L in range(1, 9):
        if n < (1 << (7 * L)) - 1: return ((1 << (7 * L)) | n).to_bytes(L, "big")
    raise ValueError
def el(i, payload): return ebml_id(i) + ebml_size(len(payload)) + payload
def uint_el(i, v): return el(i, v.to_bytes(max(1, (v.bit_length() + 7) // 8), "big"))
def make_mkv():
    head = el(0x1A45DFA3, uint_el(0x4286, 1) + uint_el(0x42F7, 1) + uint_el(0x42F2, 4) + uint_el(0x42F3, 8) + el(0x4282, b"matroska")
              + uint_el(0x4287, 4) + uint_el(0x4285, 2))
    info = el(0x1549A966, uint_el(0x2AD7B1, 1000000) + el(0x4D80, b"RecoverX") + el(0x5741, b"fixture"))
    tracks = el(0x1654AE6B, el(0xAE, uint_el(0xD7, 1) + uint_el(0x73C5, 1) + uint_el(0x83, 1) + el(0x86, b"V_UNCOMPRESSED")))
    clusters = b"".join(el(0x1F43B675, uint_el(0xE7, k * 1000) + el(0xA3, b"\x81\x00\x00\x80" + noise(30000))) for k in range(3))
    cues = el(0x1C53BB6B, el(0xBB, uint_el(0xB3, 0) + el(0xB7, uint_el(0xF7, 1) + uint_el(0xF1, len(info) + len(tracks)))))
    seg_payload = info + tracks + clusters + cues
    return head + ebml_id(0x18538067) + (0x01 << 56 | len(seg_payload)).to_bytes(8, "big") + seg_payload

def make_mp3(frames):
    title = b"\x00RecoverX fixture"
    tag_body = b"TIT2" + struct.pack(">I", len(title)) + b"\x00\x00" + title + bytes(64)   # trailing padding
    n = len(tag_body)
    ss = bytes([(n >> 21) & 0x7F, (n >> 14) & 0x7F, (n >> 7) & 0x7F, n & 0x7F])
    out = bytearray(b"ID3\x03\x00\x00" + ss + tag_body)
    for i in range(frames): out += b"\xFF\xFB\x90\x64" + noise(417 - 4)           # MPEG-1 Layer III 128 kbps 44.1 kHz
    out += b"TAG" + b"RecoverX fixture".ljust(30, b"\0") + bytes(95)
    return bytes(out)

def make_wav(samples):
    data = bytearray()
    for i in range(samples): data += struct.pack("<h", int(12000 * math.sin(i * 0.05)))
    fmt = struct.pack("<HHIIHH", 1, 1, 22050, 44100, 2, 16)
    info = b"INFO" + b"ISFT" + struct.pack("<I", 9) + b"RecoverX\0" + b"\0"
    chunks = b"fmt " + struct.pack("<I", len(fmt)) + fmt + b"data" + struct.pack("<I", len(data)) + bytes(data)
    chunks += b"LIST" + struct.pack("<I", len(info)) + info
    return b"RIFF" + struct.pack("<I", 4 + len(chunks)) + b"WAVE" + chunks

def make_7z(packed_len):
    packed = noise(packed_len)
    nh = bytes([0x01, 0x00])                                                     # kHeader, kEnd
    start = struct.pack("<QQI", len(packed), len(nh), zlib.crc32(nh) & 0xFFFFFFFF)
    return b"7z\xbc\xaf\x27\x1c\x00\x04" + struct.pack("<I", zlib.crc32(start) & 0xFFFFFFFF) + start + packed + nh

def make_doc(stream_len):
    """OLE2 v3 compound file: FAT sectors first, then the directory, then one 'WordDocument' stream (>= 4096 bytes)."""
    SS, PER = 512, 128
    stream = (b"RecoverX OLE2 fixture stream. " * (stream_len // 30 + 1))[:stream_len]
    nstream = (len(stream) + SS - 1) // SS
    nfat = 1
    while nfat * PER < nfat + 1 + nstream: nfat += 1
    dir_sec, first = nfat, nfat + 1
    fat = [0xFFFFFFFD] * nfat + [0xFFFFFFFE]
    fat += [first + i + 1 if i < nstream - 1 else 0xFFFFFFFE for i in range(nstream)]
    fat += [0xFFFFFFFF] * (nfat * PER - len(fat))
    def dirent(name, typ, start, size, child=0xFFFFFFFF):
        n = (name + "\0").encode("utf-16-le")
        return (n.ljust(64, b"\0") + struct.pack("<HBB", len(n), typ, 1) + struct.pack("<III", 0xFFFFFFFF, 0xFFFFFFFF, child)
                + bytes(16) + bytes(4) + bytes(16) + struct.pack("<IQ", start, size))   # start sector, 64-bit size: 128-byte entry
    directory = dirent("Root Entry", 5, 0xFFFFFFFE, 0, child=1) + dirent("WordDocument", 2, first, len(stream))
    directory = directory.ljust(SS, b"\0")
    hdr = bytearray(b"\xD0\xCF\x11\xE0\xA1\xB1\x1A\xE1" + bytes(16) + struct.pack("<HHHHH", 0x3E, 3, 0xFFFE, 9, 6) + bytes(6))
    hdr += struct.pack("<IIIIIIIII", 0, nfat, dir_sec, 0, 4096, 0xFFFFFFFE, 0, 0xFFFFFFFE, 0)
    hdr += struct.pack("<109I", *(list(range(nfat)) + [0xFFFFFFFF] * (109 - nfat)))
    assert len(hdr) == 512
    return bytes(hdr) + struct.pack("<%dI" % len(fat), *fat) + directory + stream.ljust(nstream * SS, b"\0")

# ------------------------------------------------------------------ layout
def main():
    out_dir = sys.argv[1] if len(sys.argv) > 1 else "."
    samples = sys.argv[sys.argv.index("--samples") + 1] if "--samples" in sys.argv else None
    os.makedirs(out_dir, exist_ok=True)
    img = bytearray(8 * 1024 * 1024)
    rows = []                                                    # name offset size sha type ext expect exact
    pos = 64 * 1024

    def place(name, data, typ, ext, expect, exact=True, align=4096, record_len=None):
        nonlocal pos
        pos = (pos + align - 1) // align * align
        assert pos + len(data) < len(img), name
        img[pos:pos + len(data)] = data
        n = record_len if record_len is not None else len(data)
        rows.append((name, pos, n, hashlib.sha256(bytes(data[:n])).hexdigest(), typ, ext, expect, "1" if exact else "0"))
        if samples and exact: open(os.path.join(samples, name), "wb").write(data)
        at = pos
        pos += len(data) + 8192
        return at

    if samples: os.makedirs(samples, exist_ok=True)
    jpeg_a = make_jpeg(320, 240, 1, exif="2024:05:17 14:30:00")
    place("photo_exif.jpg", jpeg_a, "JPEG", "jpg", "VERIFIED")
    place("photo_small.jpg", make_jpeg(64, 48, 2), "JPEG", "jpg", "VERIFIED", align=512)
    place("image.png", make_png(300, 200, 3), "PNG", "png", "VERIFIED")
    place("anim.gif", make_gif(120, 90), "GIF", "gif", "VERIFIED", align=512)
    place("picture.bmp", make_bmp(64, 48), "BMP", "bmp", "LIKELY")
    place("letter.pdf", make_pdf(["Line %d of the RecoverX test document" % i for i in range(1500)]), "PDF", "pdf", "VERIFIED")
    place("updated.pdf", make_pdf(["Updated document line %d" % i for i in range(300)], update=True), "PDF", "pdf", "VERIFIED", align=512)
    place("report.docx", make_docx(), "ZIP", "docx", "VERIFIED")
    place("streamed.zip", make_zip_streamed(), "ZIP", "zip", "VERIFIED")
    place("clip_moov_last.mp4", make_mp4(True, 1709294400, noise(150000)), "ISOBMFF", "mp4", "VERIFIED")
    place("clip_moov_first.mp4", make_mp4(False, 1709294400, noise(90000)), "ISOBMFF", "mp4", "LIKELY")
    place("movie.mkv", make_mkv(), "EBML", "mkv", "VERIFIED")
    place("song.mp3", make_mp3(240), "MP3", "mp3", "VERIFIED")
    place("tone.wav", make_wav(60000), "RIFF", "wav", "VERIFIED")
    place("archive.7z", make_7z(40000), "7Z", "7z", "VERIFIED")
    place("legacy.doc", make_doc(70000), "OLE2", "doc", "LIKELY")

    # ---- negative cases ----
    frag = make_jpeg(256, 256, 4)
    cut = len(frag) // 3
    a = place("fragmented.jpg", frag[:cut] + noise(40000), "JPEG", "jpg", "PARTIAL", exact=False, record_len=cut)
    place("fragmented_tail.bin", frag[cut:], "NONE", "", "NONE", exact=False)              # rest stored elsewhere
    gap = make_jpeg(200, 200, 5)
    g = len(gap) // 2
    place("zero_gap.jpg", gap[:g] + bytes(4096) + gap[g:], "JPEG", "jpg", "PARTIAL", exact=False)
    png = bytearray(make_png(400, 300, 6))
    tail = int(len(png) * 0.6)
    png[tail:] = noise(len(png) - tail)
    place("overwritten.png", bytes(png), "PNG", "png", "PARTIAL", exact=False)
    place("fake_header.jpg", b"\xFF\xD8\xFF\xE0\x00\x10JFIF\x00\x01\x01\x00\x00\x01\x00\x01\x00\x00" + noise(20000), "JPEG", "jpg", "NONE", exact=False)

    open(os.path.join(out_dir, "carve_basic.img"), "wb").write(img)
    with open(os.path.join(out_dir, "carve_basic.manifest.tsv"), "w", newline="\n") as m:
        m.write("# name\toffset\tsize\tsha256\ttype\text\texpect\texact\n")
        for r in rows: m.write("\t".join(str(x) for x in r) + "\n")
    with tarfile.open(os.path.join(out_dir, "carve_basic.tar.gz"), "w:gz", compresslevel=9) as t:
        t.add(os.path.join(out_dir, "carve_basic.img"), arcname="carve_basic.img")
    print("wrote %d entries, image %d bytes" % (len(rows), len(img)))

if __name__ == "__main__":
    main()
