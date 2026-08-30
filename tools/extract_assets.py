#!/usr/bin/env python3
"""Regenerate assets/ from your own copy of the original Raptor game data.

This repository ships NO game content. Raptor's art, audio, maps and text are
the property of their rights holders and are not redistributed here. Supply
FILE0000.GLB and FILE0001.GLB from a legitimate copy of the game and this
script unpacks them into the layout the Godot project expects.

    tools/extract_assets.py <dir-containing-GLBs>

Everything except the music render is pure standard library -- no dosraptor
checkout, no C toolchain, no third-party packages. Rendering the music to
.ogg needs cmake, a C++ compiler and ffmpeg, because the authentic Apogee
OPL2 FM sound comes from libADLMIDI (see --skip-music).

Ported from dosraptor's C extractor; every output is verified byte-identical
(or pixel-identical, for PNGs) against it.
"""

import struct
import zlib

# ---------------------------------------------------------------- GLB archive

_SERIAL = b"32768GLB"
_SEED = 0x19
_KEYFILE = 28  # opt(4) offset(4) filesize(4) name[16]

GLB_ENCODED = 1


def decrypt(buf):
    """The GLB cipher (GFX/GLBAPI.C:GLB_DeCrypt).

    Each byte is offset by the key byte and the previous *ciphertext* byte,
    so decryption has to keep the original byte before overwriting it.
    """
    klen = len(_SERIAL)
    kidx = _SEED % klen
    prev = _SERIAL[kidx]
    out = bytearray(len(buf))
    for i, b in enumerate(buf):
        out[i] = (b - _SERIAL[kidx] - prev) & 0xFF
        prev = b
        kidx += 1
        if kidx >= klen:
            kidx = 0
    return bytes(out)


def _ident(ch):
    return ("A" <= ch <= "Z") or ("a" <= ch <= "z") or ("0" <= ch <= "9") or ch == "_"


def _sanitize(name):
    """Trim a *trailing* run of non-identifier characters (some names carry a
    stray '//', e.g. 'SMSHIELD_PIC//'). Interior oddities are preserved --
    'N$_PIC' is a real sprite name (main.c:sanitize_item_name)."""
    n = len(name)
    while n > 0 and not _ident(name[n - 1]):
        n -= 1
    return name[:n]


class Item:
    __slots__ = ("index", "name", "offset", "size", "encoded", "_archive")

    def __init__(self, index, name, offset, size, encoded, archive):
        self.index = index
        self.name = name
        self.offset = offset
        self.size = size
        self.encoded = encoded
        self._archive = archive

    def data(self):
        raw = self._archive.raw[self.offset:self.offset + self.size]
        return decrypt(raw) if self.encoded else raw

    def __repr__(self):
        return f"<Item {self.index} {self.name} size={self.size}>"


class Archive:
    """One .GLB file: an encrypted item table followed by item payloads."""

    def __init__(self, path):
        self.path = path
        with open(path, "rb") as fh:
            self.raw = fh.read()
        if len(self.raw) < _KEYFILE:
            raise ValueError(f"{path}: too small to be a GLB archive")
        _, count, _ = struct.unpack_from("<III", decrypt(self.raw[:_KEYFILE]))
        self.items = []
        for j in range(count):
            off = _KEYFILE * (j + 1)
            rec = decrypt(self.raw[off:off + _KEYFILE])
            if len(rec) < _KEYFILE:
                raise ValueError(f"{path}: item table truncated at {j}")
            opt, ioff, isize = struct.unpack_from("<III", rec)
            name = _sanitize(rec[12:28].split(b"\0")[0].decode("latin-1"))
            self.items.append(
                Item(j, name, ioff, isize, opt == GLB_ENCODED, self))

    def by_name(self, name):
        for it in self.items:
            if it.name == name:
                return it
        return None


class Library:
    """FILE0000 + FILE0001 addressed as one item space, matching the game's
    GLB_GetItemInfo() global ordering (archive 0 first, then archive 1)."""

    def __init__(self, *paths):
        self.archives = [Archive(p) for p in paths]
        self.items = []
        for arc in self.archives:
            self.items.extend(arc.items)

    def by_name(self, name):
        for it in self.items:
            if it.name == name:
                return it
        return None

    def suffix(self, suffix):
        return [it for it in self.items if it.name.endswith(suffix)]


# -------------------------------------------------------------------- palette

def palette_rgba(pal6):
    """Expand a 768-byte VGA DAC palette (6 bits per channel) to RGBA.

    Colour index 0 is the transparent key. 6->8 bit is (v << 2) | (v >> 4),
    matching png_writer.c:indexed_to_rgba.
    """
    if len(pal6) < 768:
        raise ValueError(f"palette too short: {len(pal6)} bytes")
    pal = bytearray(256 * 4)
    for i in range(256):
        r, g, b = pal6[i * 3], pal6[i * 3 + 1], pal6[i * 3 + 2]
        pal[i * 4 + 0] = ((r << 2) | (r >> 4)) & 0xFF
        pal[i * 4 + 1] = ((g << 2) | (g >> 4)) & 0xFF
        pal[i * 4 + 2] = ((b << 2) | (b >> 4)) & 0xFF
        pal[i * 4 + 3] = 0 if i == 0 else 255
    return bytes(pal)


def indexed_to_rgba(indexed, pal):
    out = bytearray(len(indexed) * 4)
    for p, idx in enumerate(indexed):
        out[p * 4:p * 4 + 4] = pal[idx * 4:idx * 4 + 4]
    return bytes(out)


# ----------------------------------------------------------------- GFX_PIC

GSPRITE, GPIC = 0, 1
_PIC_HDR = 20   # type, opt1, opt2, width, height
_SEG = 16       # x, y, offset, length


def rasterize(data):
    """Decode a GFX_PIC item into (width, height, 8-bit indexed pixels).

    Two layouts (main.c:rasterize_pic):
      GPIC    - linear width*height block
      GSPRITE - run of (x, y, offset, length) segments, terminated by
                offset == 0xFFFFFFFF; transparent pixels are simply absent.
    Returns None for anything malformed, which the callers skip.
    """
    if len(data) < _PIC_HDR:
        return None
    kind, _, _, w, h = struct.unpack_from("<iiiii", data)
    if not (0 < w <= 4096 and 0 < h <= 4096):
        return None
    canvas = bytearray(w * h)

    if kind == GPIC:
        need = _PIC_HDR + w * h
        if len(data) < need:
            return None
        canvas[:] = data[_PIC_HDR:need]
    elif kind == GSPRITE:
        p, end = _PIC_HDR, len(data)
        for _ in range(65536):
            if p + _SEG > end:
                break
            sx, sy, soff, slen = struct.unpack_from("<iiii", data, p)
            if soff & 0xFFFFFFFF == 0xFFFFFFFF:
                break
            p += _SEG
            if 0 <= sy < h and sx >= 0 and slen > 0:
                n = min(slen, w - sx)
                if n > 0 and p + n <= end:
                    canvas[sy * w + sx:sy * w + sx + n] = data[p:p + n]
            if p + slen > end:
                break
            p += slen
    else:
        return None
    return w, h, bytes(canvas)


# ---------------------------------------------------------------- PNG output

def write_png(path, w, h, rgba):
    """Write RGBA8888 as a PNG. Filter 0 on every scanline; libpng picks
    different filters, so files are not byte-identical to the C extractor's
    output, but the decoded pixels are."""
    raw = bytearray()
    stride = w * 4
    for y in range(h):
        raw.append(0)
        raw += rgba[y * stride:(y + 1) * stride]

    def chunk(tag, payload):
        return (struct.pack(">I", len(payload)) + tag + payload
                + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
           + chunk(b"IEND", b""))
    with open(path, "wb") as fh:
        fh.write(png)


# ------------------------------------------------------------- image dumpers

# The intro/credits screens ship their own palettes (INTRO_Credits loads
# POGPAL_DAT for APOGEE_PIC and so on); the default palette garbles them.
ALT_PALETTES = {
    "APOGEE_PIC": "POGPAL_DAT",
    "CYGNUS_PIC": "CYGPAL_DAT",
    "TAIWARN_PIC": "TAIPAL_DAT",
}


def dump_pics(lib, outdir, pal):
    """_PIC items -> sprites/NNNN_NAME.png.

    The sequence number counts every _PIC item including the zero-length
    stubs that are skipped, because the game indexes sprites by that
    position. It therefore depends on which edition of the GLB you have.
    """
    outdir.mkdir(parents=True, exist_ok=True)
    seq = written = 0
    for it in lib.items:
        if not it.name.endswith("_PIC"):
            continue
        seq += 1
        if it.size == 0:
            continue
        use = pal
        alt = ALT_PALETTES.get(it.name)
        if alt:
            src = lib.by_name(alt)
            if src:
                use = palette_rgba(src.data())
        r = rasterize(it.data())
        if r is None:
            continue
        w, h, px = r
        write_png(str(outdir / f"{seq:04d}_{it.name}.png"), w, h,
                  indexed_to_rgba(px, use))
        written += 1
    return written


def dump_blks(lib, outdir, pal):
    """_BLK items (bullets, explosions, lasers) -> bullets/NAME_NN.png.

    Consecutive items sharing a name are animation frames; the counter
    resets whenever the name changes, mirroring the game's
    `curlib->item + frame` access.
    """
    outdir.mkdir(parents=True, exist_ok=True)
    prev, frame, written = None, 0, 0
    for it in lib.items:
        if not it.name.endswith("_BLK") or it.size == 0:
            continue
        if it.name == prev:
            frame += 1
        else:
            prev, frame = it.name, 0
        r = rasterize(it.data())
        if r is None:
            continue
        w, h, px = r
        write_png(str(outdir / f"{it.name}_{frame:02d}.png"), w, h,
                  indexed_to_rgba(px, pal))
        written += 1
    return written


def dump_tiles(lib, outdir, pal):
    """Terrain tiles -> tiles/g{N}/NNNN.png.

    The tile graphics are anonymous items sitting between STARTG{N}TILES and
    ENDG{N}TILES label markers. Indices are 0-based from the item after the
    start marker, so they line up with TILE.C's startflat[g] + map.flats --
    which means skipped entries still consume an index.
    """
    total = 0
    for g in range(1, 5):
        start = lib.by_name(f"STARTG{g}TILES")
        end = lib.by_name(f"ENDG{g}TILES")
        if start is None or end is None or end.index <= start.index:
            continue
        arc = start._archive
        gdir = outdir / f"g{g}"
        gdir.mkdir(parents=True, exist_ok=True)
        first = start.index + 1
        for idx in range(first, end.index):
            it = arc.items[idx]
            if it.size == 0 or it.size < _PIC_HDR:
                continue
            r = rasterize(it.data())
            if r is None:
                continue
            w, h, px = r
            write_png(str(gdir / f"{idx - first:04d}.png"), w, h,
                      indexed_to_rgba(px, pal))
            total += 1
    return total


AGX_W, AGX_H = 320, 200
AGX_BYTES = AGX_W * AGX_H


def _agx_delta(buf, canvas):
    """Apply one ANIM_Render delta: records of
    (term, _, dst_offset, length) + `length` payload bytes, term 0 ends."""
    p, avail = 0, len(buf)
    for _ in range(4096):
        if avail < 8:
            return False
        term, _pad, dst, length = struct.unpack_from("<HHHH", buf, p)
        if term == 0:
            return True
        p += 8
        avail -= 8
        if avail < length:
            return False
        if dst + length > AGX_BYTES:
            if dst >= AGX_BYTES:
                return True
            length = AGX_BYTES - dst
        canvas[dst:dst + length] = buf[p:p + length]
        p += length
        avail -= length
    return True


def dump_agx(lib, outdir, pal6):
    """Cutscene frames -> agx/NAME_NN.png plus the raw NAME_NN.bin.

    Each _AGX item is a delta over the previous frame of the same name, so
    item order matters. Frame 0 fills the canvas with the item's first byte
    (the background colour) and deltas from byte 1 -- mirroring MOVIE_Play.
    Unlike sprites, index 0 is opaque here: these are full-screen backdrops.
    """
    outdir.mkdir(parents=True, exist_ok=True)
    opaque = bytearray(palette_rgba(pal6))
    for i in range(256):
        opaque[i * 4 + 3] = 255
    opaque = bytes(opaque)

    seqs = {}
    written = 0
    for it in lib.items:
        if not it.name.endswith("_AGX") or it.size == 0:
            continue
        mem = it.data()
        seq = seqs.setdefault(it.name, {"frame": 0, "canvas": bytearray(AGX_BYTES)})
        canvas = seq["canvas"]
        if seq["frame"] == 0:
            canvas[:] = bytes([mem[0]]) * AGX_BYTES
            ok = _agx_delta(mem[1:], canvas) if len(mem) > 1 else True
        else:
            ok = _agx_delta(mem, canvas)
        if not ok:
            continue
        stem = outdir / f"{it.name}_{seq['frame']:02d}"
        write_png(str(stem) + ".png", AGX_W, AGX_H,
                  indexed_to_rgba(bytes(canvas), opaque))
        with open(str(stem) + ".bin", "wb") as fh:
            fh.write(mem)
        seq["frame"] += 1
        written += 1
    return written


# ------------------------------------------------------------- data dumpers
# The JSON below is written by hand rather than via json.dumps so it stays
# byte-identical to the C extractor's fprintf output.

def dump_text(lib, outdir):
    """_TXT items -> text/NAME.txt, raw bytes."""
    outdir.mkdir(parents=True, exist_ok=True)
    n = 0
    for it in lib.items:
        if not it.name.endswith("_TXT") or it.size == 0:
            continue
        (outdir / f"{it.name}.txt").write_bytes(it.data())
        n += 1
    return n


def dump_flats(lib, outdir):
    """FLATSG{N}_ITM -> flats/NAME.json (tile destructibility table).

    Record layout (SOURCE/MAP.H): int32 linkflat, int16 bonus, int16 bounty.

    The C extractor writes these to the output root; the Godot project loads
    them from assets/flats/, so they go straight there.
    """
    outdir.mkdir(parents=True, exist_ok=True)
    n = 0
    for it in lib.items:
        if not (it.name.endswith("_ITM") and it.name.upper().startswith("FLATSG")):
            continue
        if it.size < 8:
            continue
        data = it.data()
        count = len(data) // 8
        rows = []
        for s in range(count):
            link, bonus, bounty = struct.unpack_from("<ihh", data, s * 8)
            rows.append(f'    {{"linkflat": {link}, "bonus": {bonus}, '
                        f'"bounty": {bounty}}}')
        body = (f'{{\n  "name": "{it.name}",\n  "num_flats": {count},\n'
                f'  "flats": [\n' + ",\n".join(rows) + "\n  ]\n}\n")
        (outdir / f"{it.name}.json").write_text(body)
        n += 1
    return n


MAP_ROWS, MAP_COLS = 150, 9
MAP_SIZE = MAP_ROWS * MAP_COLS
_MAZE_HDR = 12 + MAP_SIZE * 4   # sizerec, spriteoff, numsprites, map[1350]
_CSPRITE = 24


def dump_levels(lib, outdir):
    """_MAP items -> levels/NAME.json.

    MAZELEVEL header (5412 bytes) then numsprites CSPRITE records of 24 bytes.
    """
    outdir.mkdir(parents=True, exist_ok=True)
    n = 0
    for it in lib.items:
        if not it.name.endswith("_MAP") or it.size < _MAZE_HDR:
            continue
        d = it.data()
        sizerec, spriteoff, numsprites = struct.unpack_from("<IIi", d)
        if numsprites < 0:
            numsprites = 0
        if len(d) < _MAZE_HDR + numsprites * _CSPRITE:
            numsprites = (len(d) - _MAZE_HDR) // _CSPRITE
        tiles = []
        for t in range(MAP_SIZE):
            flats, fgame = struct.unpack_from("<hh", d, 12 + t * 4)
            tiles.append(f'    {{"flats": {flats}, "fgame": {fgame}}}')
        sprites = []
        for s in range(numsprites):
            link, slib, x, y, game, level = struct.unpack_from(
                "<iiiiiI", d, _MAZE_HDR + s * _CSPRITE)
            sprites.append(f'    {{"link": {link}, "slib": {slib}, "x": {x}, '
                           f'"y": {y}, "game": {game}, "level": {level}}}')
        body = (f'{{\n  "name": "{it.name}",\n  "size": {MAP_SIZE},\n'
                f'  "rows": {MAP_ROWS},\n  "cols": {MAP_COLS},\n'
                f'  "sizerec": {sizerec},\n  "spriteoff": {spriteoff},\n'
                f'  "numsprites": {numsprites},\n'
                f'  "tiles": [\n' + ",\n".join(tiles) + "\n  ],\n"
                f'  "sprites": [\n' + ",\n".join(sprites) + "\n  ]\n}\n")
        (outdir / f"{it.name}.json").write_text(body)
        n += 1
    return n


MAX_GUNS, MAX_FLIGHT = 24, 30
_SPRITE_REC = 528

# Field order matches SOURCE/MAP.H's SPRITE struct, read positionally.
_SPRITE_INTS = (
    "bonus", "exptype", "shotspace", "ground", "suck", "frame_rate",
    "num_frames", "countdown", "rewind", "animtype", "shadow", "bossflag",
    "hits", "money", "shootstart", "shootcnt", "shootframe", "movespeed",
    "numflight", "repos", "flighttype", "numguns", "numengs", "sfx", "song",
)
_SPRITE_ARRAYS = (
    ("shoot_type", MAX_GUNS), ("engx", MAX_GUNS), ("engy", MAX_GUNS),
    ("englx", MAX_GUNS), ("shootx", MAX_GUNS), ("shooty", MAX_GUNS),
    ("flightx", MAX_FLIGHT), ("flighty", MAX_FLIGHT),
)


def _json_str(raw):
    """C's emit_json_string: stop at NUL, escape " and \\, drop non-printables."""
    out = ['"']
    for b in raw:
        if b == 0:
            break
        c = chr(b)
        if c == '"':
            out.append('\\"')
        elif c == "\\":
            out.append("\\\\")
        elif 0x20 <= b < 0x7F:
            out.append(c)
    out.append('"')
    return "".join(out)


def dump_sprite_meta(lib, outdir):
    """SPRITE{N}_ITM -> sprites_meta/NAME.json (enemy/ship definitions).

    528 bytes per record: iname[16], item (u32), 25 int32 scalars, then
    eight int16 arrays.
    """
    outdir.mkdir(parents=True, exist_ok=True)
    n = 0
    for it in lib.items:
        if not (it.name.endswith("_ITM") and it.name.upper().startswith("SPRITE")):
            continue
        if it.size < _SPRITE_REC:
            continue
        d = it.data()
        count = len(d) // _SPRITE_REC
        recs = []
        for s in range(count):
            base = s * _SPRITE_REC
            lines = ["  {", f"    \"iname\": {_json_str(d[base:base + 16])},"]
            item, = struct.unpack_from("<I", d, base + 16)
            lines.append(f'    "item": {item},')
            vals = struct.unpack_from("<25i", d, base + 20)
            for key, v in zip(_SPRITE_INTS, vals):
                lines.append(f'    "{key}": {v},')
            off = base + 120
            for i, (key, cnt) in enumerate(_SPRITE_ARRAYS):
                arr = struct.unpack_from(f"<{cnt}h", d, off)
                off += cnt * 2
                tail = "," if i < len(_SPRITE_ARRAYS) - 1 else ""
                lines.append(f'    "{key}": [' + ", ".join(str(v) for v in arr)
                             + f"]{tail}")
            lines.append("  }")
            recs.append("\n".join(lines))
        body = (f'{{\n  "name": "{it.name}",\n  "num_sprites": {count},\n'
                f'  "sprites": [\n' + ",\n".join(recs) + "\n  ]\n}\n")
        (outdir / f"{it.name}.json").write_text(body)
        n += 1
    return n


_DEMO_REC = 12


def dump_demos(lib, outdir):
    """_REC items -> demos/NAME.json (attract-mode input recordings).

    Record 0 is a header: playerpic = record count, px = game, py = wave.
    """
    outdir.mkdir(parents=True, exist_ok=True)
    n = 0
    for it in lib.items:
        if not it.name.endswith("_REC") or it.size < _DEMO_REC:
            continue
        d = it.data()
        total = len(d) // _DEMO_REC
        game, wave, max_play = struct.unpack_from("<hhh", d, 4)
        max_play &= 0xFFFF
        if max_play <= 0 or max_play > total:
            max_play = total
        rows = []
        for r in range(1, min(max_play, total)):
            b = d[r * _DEMO_REC:r * _DEMO_REC + 4]
            px, py, pic = struct.unpack_from("<hhh", d, r * _DEMO_REC + 4)
            rows.append(f'    {{"frame": {r - 1}, "b1": {b[0]}, "b2": {b[1]}, '
                        f'"b3": {b[2]}, "b4": {b[3]}, "px": {px}, "py": {py}, '
                        f'"playerpic": {pic}}}')
        body = ('{\n  "header": { "max_play": %d, "demo_game": %d,'
                ' "demo_wave": %d },\n  "records": [\n' % (max_play, game, wave)
                + ",\n".join(rows) + "\n  ]\n}\n")
        (outdir / f"{it.name}.json").write_text(body)
        n += 1
    return n


SWIN_SIZE, SFIELD_SIZE = 120, 148

_FLD_OPT = {0: "FLD_OFF", 1: "FLD_TEXT", 2: "FLD_BUTTON", 3: "FLD_INPUT",
            4: "FLD_MARK", 5: "FLD_CLOSE", 6: "FLD_DRAGBAR", 11: "FLD_VIEWAREA"}


def _name16(raw):
    """copy_name16: 16 bytes, trailing spaces/NULs trimmed, cut at first NUL."""
    s = raw[:16].rstrip(b" \0")
    z = s.find(b"\0")
    if z >= 0:
        s = s[:z]
    return s.decode("latin-1")


def _swd_text(raw):
    out = ['"']
    for b in raw:
        c = chr(b)
        if c == '"':
            out.append('\\"')
        elif c == "\\":
            out.append("\\\\")
        elif c == "\n":
            out.append("\\n")
        elif c == "\r":
            out.append("\\r")
        elif c == "\t":
            out.append("\\t")
        elif b < 0x20 or b == 0x7F:
            out.append("\\u%04x" % b)
        else:
            out.append(c)
    out.append('"')
    return "".join(out)


def dump_swd(lib, outdir):
    """_SWD items -> swd/NAME.json (menu window/field definitions).

    Layout: a 120-byte SWIN header, then numflds x 148-byte SFIELD records at
    fldofs, with inline NUL-terminated label text at an offset relative to
    each field's own struct.
    """
    outdir.mkdir(parents=True, exist_ok=True)
    n = 0
    i32 = lambda d, o: struct.unpack_from("<i", d, o)[0]
    u32 = lambda d, o: struct.unpack_from("<I", d, o)[0]
    for it in lib.items:
        if not it.name.endswith("_SWD") or it.size == 0:
            continue
        d = it.data()
        if len(d) < SWIN_SIZE:
            continue
        numflds, fldofs = i32(d, 96), i32(d, 76)
        if not (0 <= numflds <= 256):
            continue
        if fldofs < 0 or fldofs + numflds * SFIELD_SIZE > len(d):
            continue
        head = (
            '{\n  "name": "%s",\n  "version": %d,\n  "swdsize": %d,\n'
            '  "window": {\n    "id": %d,\n    "type": %d,\n    "name": "%s",\n'
            '    "item_name": "%s",\n    "item": %u,\n    "picflag": %d,\n'
            '    "lock": %d,\n    "firstfld": %d,\n    "opt": %d,\n'
            '    "color": %d,\n    "x": %d, "y": %d, "lx": %d, "ly": %d,\n'
            '    "shadow": %d,\n    "arrowflag": %d, "display": %d,\n'
            '    "fldofs": %d, "txtofs": %d, "numflds": %d\n  },\n'
        ) % (it.name, i32(d, 0), i32(d, 4), i32(d, 24), i32(d, 28),
             _name16(d[32:48]), _name16(d[48:64]), u32(d, 64), i32(d, 68),
             i32(d, 72), i32(d, 84), i32(d, 88), i32(d, 92), i32(d, 100),
             i32(d, 104), i32(d, 108), i32(d, 112), i32(d, 116), i32(d, 8),
             i32(d, 12), fldofs, i32(d, 80), numflds)

        fields = []
        for f in range(numflds):
            b = fldofs + f * SFIELD_SIZE
            opt = i32(d, b + 0)
            opt_s = _FLD_OPT.get(opt)
            lines = ["    {", f'      "index": {f},',
                     f'      "id": {i32(d, b + 4)},',
                     f'      "opt": "{opt_s}",' if opt_s else f'      "opt": {opt},',
                     '      "x": %d, "y": %d, "lx": %d, "ly": %d,' % (
                         i32(d, b + 124), i32(d, b + 128),
                         i32(d, b + 132), i32(d, b + 136)),
                     f'      "hotkey": {u32(d, b + 8)},',
                     f'      "kbflag": {i32(d, b + 12)},',
                     f'      "input_opt": {i32(d, b + 24)},',
                     f'      "bstatus": {i32(d, b + 28)},',
                     f'      "name": "{_name16(d[b + 32:b + 48])}",',
                     f'      "item_name": "{_name16(d[b + 48:b + 64])}",',
                     f'      "item": {u32(d, b + 64)},',
                     f'      "font_name": "{_name16(d[b + 68:b + 84])}",',
                     f'      "fontid": {u32(d, b + 84)},',
                     f'      "fontbasecolor": {i32(d, b + 88)},',
                     f'      "maxchars": {i32(d, b + 92)},',
                     f'      "picflag": {i32(d, b + 96)},',
                     '      "color": %d, "lite": %d,' % (i32(d, b + 100), i32(d, b + 104)),
                     '      "mark": %d, "saveflag": %d, "shadow": %d, "selectable": %d,' % (
                         i32(d, b + 108), i32(d, b + 112),
                         i32(d, b + 116), i32(d, b + 120))]
            txtoff = u32(d, b + 140)
            tail = f'      "txtoff": {txtoff}'
            if txtoff != 0:
                start = b + txtoff
                if start < len(d):
                    end = d.find(b"\0", start)
                    end = len(d) if end < 0 else end
                    if end > start:
                        tail += ',\n      "text": ' + _swd_text(d[start:end])
            lines.append(tail)
            fields.append("\n".join(lines) + "\n    }")
        body = head + '  "fields": [\n' + ",\n".join(fields) + "\n  ]\n}\n"
        (outdir / f"{it.name}.json").write_text(body)
        n += 1
    return n


FONT_HEADER_BYTES = 772   # int32 height + 256*u16 charofs + 256*u8 width
EMPTY_OFS = 0xFFFF


def _palette_json(pal6, name):
    parts = []
    for i in range(256):
        r, g, b = pal6[i * 3], pal6[i * 3 + 1], pal6[i * 3 + 2]
        parts.append("%u, %u, %u" % (((r << 2) | (r >> 4)) & 0xFF,
                                     ((g << 2) | (g >> 4)) & 0xFF,
                                     ((b << 2) | (b >> 4)) & 0xFF))
    return '{\n  "format": "rgb8",\n  "colors": [' + ", ".join(parts) + "]\n}\n"


def dump_fonts(lib, outdir):
    """_FNT items -> fonts/NAME.png glyph atlas + NAME.json metrics, plus a
    JSON copy of every 768-byte palette (_DAT), with PALETTE_DAT additionally
    written as palette.json.

    Atlas pixels are white with the raw glyph byte as alpha; the runtime
    colours them by sampling alpha into palette[basecolor - 1 + alpha].
    """
    outdir.mkdir(parents=True, exist_ok=True)
    n = 0
    wrote_default = False
    for it in lib.items:
        if not it.name.endswith("_DAT") or it.size != 768:
            continue
        pal6 = it.data()
        if it.name.upper() == "PALETTE_DAT" and not wrote_default:
            (outdir / "palette.json").write_text(_palette_json(pal6, "palette"))
            wrote_default = True
            n += 1
        (outdir / f"{it.name}.json").write_text(_palette_json(pal6, it.name))
        n += 1

    for it in lib.items:
        if not it.name.endswith("_FNT") or it.size < FONT_HEADER_BYTES:
            continue
        d = it.data()
        height, = struct.unpack_from("<i", d)
        if not (0 < height <= 256):
            continue
        charofs = struct.unpack_from("<256H", d, 4)
        widths = d[4 + 512:4 + 512 + 256]
        pixels = d[FONT_HEADER_BYTES:]

        placed = []
        total_w = 0
        for c in range(256):
            ofs = charofs[c]
            if ofs == EMPTY_OFS:
                continue
            w = widths[c]
            if w <= 0 or ofs + w * height > len(pixels):
                continue
            placed.append((c, ofs, w, total_w))
            total_w += w
        if total_w <= 0:
            continue

        rgba = bytearray(total_w * height * 4)
        for _c, ofs, w, x0 in placed:
            for row in range(height):
                base = (row * total_w + x0) * 4
                gp = ofs + row * w
                for col in range(w):
                    i = base + col * 4
                    rgba[i] = rgba[i + 1] = rgba[i + 2] = 255
                    rgba[i + 3] = pixels[gp + col]
        write_png(str(outdir / f"{it.name}.png"), total_w, height, bytes(rgba))

        glyphs = ",\n".join(f'    "{c}": {{"x": {x0}, "w": {w}}}'
                            for c, _o, w, x0 in placed)
        (outdir / f"{it.name}.json").write_text(
            f'{{\n  "name": "{it.name}",\n  "height": {height},\n'
            f'  "fontspacing": 1,\n  "atlas_width": {total_w},\n'
            f'  "glyphs": {{\n' + glyphs + "\n  }\n}\n")
        n += 2
    return n


DMX_HDR, DMX_BIAS, DMX_DIGITAL = 24, 32, 3


def dump_sounds(lib, outdir):
    """Digital sound effects -> sounds/NAME_FX.wav.

    Each zero-length _FX label is followed by five device-specific items;
    index +4 is the Sound Blaster digital patch. Samples are unsigned 8-bit
    PCM, converted to signed 16-bit.
    """
    outdir.mkdir(parents=True, exist_ok=True)
    items = lib.items
    n = 0
    for i, label in enumerate(items):
        if label.size != 0 or not label.name.upper().endswith("_FX"):
            continue
        if i + 4 >= len(items):
            continue
        it = items[i + 4]
        if it.size < DMX_HDR:
            continue
        d = it.data()
        kind, rate, datalen = struct.unpack_from("<HHI", d)
        if kind != DMX_DIGITAL or datalen < DMX_BIAS:
            continue
        nsamples = datalen - DMX_BIAS
        if nsamples > len(d) - DMX_HDR or not (4000 <= rate <= 48000):
            continue
        if nsamples == 0:
            continue
        pcm = bytearray()
        for s in d[DMX_HDR:DMX_HDR + nsamples]:
            pcm += struct.pack("<h", (s - 128) * 256)
        data_size = len(pcm)
        hdr = (b"RIFF" + struct.pack("<I", 36 + data_size) + b"WAVE"
               + b"fmt " + struct.pack("<IHHIIHH", 16, 1, 1, rate,
                                       rate * 2, 2, 16)
               + b"data" + struct.pack("<I", data_size))
        (outdir / f"{label.name}.wav").write_bytes(hdr + bytes(pcm))
        n += 1
    return n


# ------------------------------------------------------------------- music
# Port of apodmx/MUS2MID.C (Ben Ryves' mus2mid). Raptor stores music as Doom
# MUS; Godot needs standard MIDI.
#
# Rate note: the C extractor calls this with the DMX library default of 140,
# but Raptor initialises DMX at 70 Hz (SOURCE/FX.C:1076 DMX_Init(70, ...)).
# The MThd division is rate/2, so 140 yields 70 and plays everything at
# double speed; 70 yields the correct 35.

NUM_CHANNELS = 16
MIDI_PERCUSSION_CHAN = 9
MUS_PERCUSSION_CHAN = 15

_CONTROLLER_MAP = (0x00, 0x20, 0x01, 0x07, 0x0A, 0x0B, 0x5B, 0x5D,
                   0x40, 0x43, 0x78, 0x7B, 0x7E, 0x7F, 0x79)


class _MidiOut:
    def __init__(self):
        self.buf = bytearray()
        self.queued = 0

    def time(self):
        """Variable-length delta time; resets the queue after writing."""
        t = self.queued
        buffer = t & 0x7F
        while True:
            t >>= 7
            if t == 0:
                break
            buffer <<= 8
            buffer |= (t & 0x7F) | 0x80
        while True:
            self.buf.append(buffer & 0xFF)
            if buffer & 0x80:
                buffer >>= 8
            else:
                break
        self.queued = 0

    def event(self, *payload):
        self.time()
        self.buf += bytes(payload)


class Mus2Mid:
    """Converter holding the state the C implementation keeps in globals.

    channelvelocities is NOT reset between files in the C extractor (it is a
    static array), so a single instance must convert every track in GLB order
    to reproduce its output.
    """

    def __init__(self):
        self.velocities = [127] * NUM_CHANNELS

    def convert(self, mus, rate=70, adlibhack=0):
        if len(mus) < 8 or mus[:4] != b"MUS\x1a":
            return None
        scorestart, = struct.unpack_from("<H", mus, 6)
        p = scorestart
        out = _MidiOut()
        chan_map = [-1] * NUM_CHANNELS

        def allocate():
            result = max(chan_map) + 1
            if result == MIDI_PERCUSSION_CHAN:
                result += 1
            return result

        def midi_channel(mus_chan):
            if mus_chan == MUS_PERCUSSION_CHAN:
                return MIDI_PERCUSSION_CHAN
            if chan_map[mus_chan] == -1:
                chan_map[mus_chan] = allocate()
                out.event(0xB0 | chan_map[mus_chan], 0x7B, 0)
            return chan_map[mus_chan]

        hit_end = False
        while not hit_end:
            while not hit_end:
                if p >= len(mus):
                    return None
                desc = mus[p]; p += 1
                ch = midi_channel(desc & 0x0F)
                ev = desc & 0x70

                if ev == 0x00:                      # release key
                    key = mus[p]; p += 1
                    out.event(0x80 | ch, key & 0x7F, 0)
                elif ev == 0x10:                    # press key
                    key = mus[p]; p += 1
                    if key & 0x80:
                        self.velocities[ch] = mus[p] & 0x7F; p += 1
                    out.event(0x90 | ch, key & 0x7F, self.velocities[ch] & 0x7F)
                elif ev == 0x20:                    # pitch wheel
                    key = mus[p]; p += 1
                    wheel = key * 64
                    out.event(0xE0 | ch, wheel & 0x7F, (wheel >> 7) & 0x7F)
                elif ev == 0x30:                    # system event
                    cnum = mus[p]; p += 1
                    if cnum < 10 or cnum > 14:
                        return None
                    if cnum == 14 and adlibhack:
                        out.event(0xB0 | ch, 0x78, 0)
                        w = 128 * 64
                        out.event(0xE0 | ch, w & 0x7F, (w >> 7) & 0x7F)
                        out.event(0xB0 | ch, 0x0A, 64)
                    else:
                        out.event(0xB0 | ch, _CONTROLLER_MAP[cnum], 0)
                elif ev == 0x40:                    # change controller
                    cnum = mus[p]; p += 1
                    cval = mus[p]; p += 1
                    if cnum == 0:
                        out.event(0xC0 | ch, cval & 0x7F)
                    else:
                        if cnum < 1 or cnum > 9:
                            return None
                        v = 0x7F if cval & 0x80 else cval
                        out.event(0xB0 | ch, _CONTROLLER_MAP[cnum], v)
                elif ev == 0x60:                    # score end
                    hit_end = True
                else:
                    return None

                if desc & 0x80:
                    break

            if not hit_end:
                delay = 0
                while True:
                    if p >= len(mus):
                        return None
                    w = mus[p]; p += 1
                    delay = delay * 128 + (w & 0x7F)
                    if not (w & 0x80):
                        break
                out.queued += delay

        out.event(0xFF, 0x2F, 0x00)

        head = (b"MThd" + struct.pack(">IHHH", 6, 0, 1, rate // 2)
                + b"MTrk" + struct.pack(">I", len(out.buf)))
        return head + bytes(out.buf)


def dump_music(lib, outdir, rate=70):
    """_MUS items -> music/NAME.mid (converted) or copied through if already
    MIDI. One converter instance for the whole run: see Mus2Mid."""
    outdir.mkdir(parents=True, exist_ok=True)
    conv = Mus2Mid()
    n = 0
    for it in lib.items:
        if not it.name.endswith("_MUS") or it.size < 4:
            continue
        d = it.data()
        if d[:4] == b"MThd":
            (outdir / f"{it.name}.mid").write_bytes(d)
            n += 1
            continue
        mid = conv.convert(d, rate=rate)
        if mid is None:
            continue
        (outdir / f"{it.name}.mid").write_bytes(mid)
        n += 1
    return n


# ------------------------------------------------------------------- driver

import argparse
import os
import subprocess
import sys
from pathlib import Path


def find_glbs(d):
    """The DOS distribution ships uppercase names; some installers and
    archive tools lowercase them, so accept either."""
    found = []
    for n in (0, 1):
        for cand in (d / f"FILE000{n}.GLB", d / f"file000{n}.glb"):
            if cand.is_file():
                found.append(cand)
                break
        else:
            raise SystemExit(f"error: FILE000{n}.GLB not found in {d}")
    return found


def extract(glb_dir, assets, rate=70):
    glb0, glb1 = find_glbs(glb_dir)
    lib = Library(str(glb0), str(glb1))
    pal_item = lib.by_name("PALETTE_DAT")
    if pal_item is None:
        raise SystemExit("error: PALETTE_DAT missing -- is this a Raptor GLB?")
    pal6 = pal_item.data()
    pal = palette_rgba(pal6)

    assets.mkdir(parents=True, exist_ok=True)
    counts = [
        ("sprites", dump_pics(lib, assets / "sprites", pal)),
        ("bullets", dump_blks(lib, assets / "bullets", pal)),
        ("tiles", dump_tiles(lib, assets / "tiles", pal)),
        ("agx", dump_agx(lib, assets / "agx", pal6)),
        ("fonts", dump_fonts(lib, assets / "fonts")),
        ("text", dump_text(lib, assets / "text")),
        ("levels", dump_levels(lib, assets / "levels")),
        ("flats", dump_flats(lib, assets / "flats")),
        ("sprites_meta", dump_sprite_meta(lib, assets / "sprites_meta")),
        ("demos", dump_demos(lib, assets / "demos")),
        ("swd", dump_swd(lib, assets / "swd")),
        ("sounds", dump_sounds(lib, assets / "sounds")),
        ("music", dump_music(lib, assets / "music", rate=rate)),
    ]
    for name, n in counts:
        print(f"  {name:<13} {n}")
    return counts


def render_music(root):
    """Hand off to tools/render_music.sh: the authentic Apogee OPL2 FM sound
    comes from libADLMIDI, which the script builds on first run."""
    script = root / "tools" / "render_music.sh"
    if not script.is_file():
        print(f"warning: {script} missing; skipping music render",
              file=sys.stderr)
        return False
    print("==> rendering music (OPL2 FM via libADLMIDI)")
    try:
        subprocess.run([str(script)], check=True)
    except subprocess.CalledProcessError as exc:
        raise SystemExit(
            f"error: music render failed (exit {exc.returncode}).\n"
            "It needs cmake, a C++ compiler and ffmpeg. Install those and "
            "re-run, or pass --skip-music to leave the game without music.")
    return True


def main(argv=None):
    ap = argparse.ArgumentParser(
        prog="tools/extract_assets.py",
        description="Regenerate assets/ from your own Raptor GLB files.")
    ap.add_argument("glb_dir", nargs="?", default=os.environ.get("RAPTOR_GLB_DIR"),
                    help="directory holding FILE0000.GLB and FILE0001.GLB")
    ap.add_argument("--skip-music", action="store_true",
                    help="extract only; don't render assets/music/*.ogg")
    ap.add_argument("--rate", type=int, default=70,
                    help="DMX music rate; MThd division is rate/2 (default: 70, "
                         "the value Raptor actually uses)")
    ap.add_argument("-o", "--output", default=None,
                    help="output directory (default: <repo>/assets)")
    args = ap.parse_args(argv)

    if not args.glb_dir:
        ap.error("no GLB directory given (or set $RAPTOR_GLB_DIR)")
    glb_dir = Path(args.glb_dir).expanduser()
    if not glb_dir.is_dir():
        raise SystemExit(f"error: not a directory: {glb_dir}")

    root = Path(__file__).resolve().parent.parent
    assets = Path(args.output).expanduser() if args.output else root / "assets"

    print(f"==> extracting into {assets}")
    extract(glb_dir, assets, rate=args.rate)

    if args.skip_music:
        print("==> skipping music render (--skip-music); "
              "run tools/render_music.sh later for audio")
    else:
        render_music(root)

    total = sum(1 for _ in assets.rglob("*") if _.is_file())
    print(f"[extract_assets] done -- {total} files in {assets}")


if __name__ == "__main__":
    main()
