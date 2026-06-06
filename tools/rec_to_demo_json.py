#!/usr/bin/env python3
"""Convert a C RAPTOR_INPUT_LOG (v2) capture into per-wave Godot demo JSON.

The C port (DEMO_Think, SOURCE/INPUT.C) writes a 4-byte "RIL2" magic followed by
one 18-byte RECORD per gameplay iteration, continuously across waves:

    magic: "RIL2"
    struct { u8 b1,b2,b3,b4, dirs, pad; i16 gax,gay,px,py,playerpic,fil; }  # 18B LE

vs the old v1 (12 bytes, no magic): b1-b4 + px,py,playerpic,fil. v2 adds the raw
directional INPUT (`dirs` bits: 1=L 2=R 4=U 8=D) and the per-iter movement delta
(`gax`/`gay` = g_addx/g_addy) so the Godot replay can RECOMPUTE movement instead
of forcing position — px/py/playerpic become the parity oracle.

`fil` carries the current wave (0-based). We split the stream on wave changes and
emit one Godot demo file per wave segment (the Godot demo replay is per-wave).

The loadout the player flew per wave is captured separately by the C port in
RAPTOR_LOADOUT_LOG (one JSON line per Do_Game enter: wave/game/score/sweapon/diff
/objs). We merge each real wave segment with its loadout line, in order.

Output matches src/Test/DemoReplay.cs:
  {header:{max_play,demo_game,demo_wave,loadout:{...}},
   records:[{frame,b1..b4,dirs,gax,gay,px,py,playerpic}]}
The G1 campaign is demo_game 0.

Usage: rec_to_demo_json.py <input.rec> <out_dir> [loadout.json]
"""
import json
import os
import struct
import sys

MAGIC = b"RIL2"
RECORD_SIZE = 18
RECORD_FMT = "<6B6h"  # b1,b2,b3,b4,dirs,pad, gax,gay,px,py,playerpic,fil

# Drop sub-second blips (wave transitions flicker the wave field for a few iters).
MIN_SEGMENT = 35


def read_records(path):
    data = open(path, "rb").read()
    if data[:4] != MAGIC:
        raise SystemExit(
            f"{path}: missing 'RIL2' magic — this is an old v1 capture (no "
            f"directional input). Re-record with the current C build."
        )
    body = data[4:]
    n = len(body) // RECORD_SIZE
    return [struct.unpack_from(RECORD_FMT, body, i * RECORD_SIZE) for i in range(n)]


def read_loadouts(path):
    """Loadout lines in emission order (one per Do_Game enter)."""
    if not path or not os.path.exists(path):
        return []
    out = []
    for line in open(path):
        line = line.strip()
        if line.startswith("{"):
            out.append(json.loads(line))
    return out


def split_segments(recs):
    segments, cur_wave, seg = [], None, []
    for r in recs:
        w = r[11]  # fil
        if w != cur_wave:
            if seg:
                segments.append((cur_wave, seg))
            cur_wave, seg = w, []
        seg.append(r)
    if seg:
        segments.append((cur_wave, seg))
    return segments


def main() -> int:
    if len(sys.argv) not in (3, 4):
        print(__doc__)
        return 2
    inp, outdir = sys.argv[1], sys.argv[2]
    loadout_path = sys.argv[3] if len(sys.argv) == 4 else os.path.join(
        os.path.dirname(inp), "loadout.json")

    recs = read_records(inp)
    loadouts = read_loadouts(loadout_path)
    segments = [(w, s) for (w, s) in split_segments(recs) if len(s) >= MIN_SEGMENT]

    os.makedirs(outdir, exist_ok=True)
    written = []
    for idx, (wave, seg) in enumerate(segments):
        # Match the i-th real segment to the i-th loadout line (both in play order).
        loadout = loadouts[idx] if idx < len(loadouts) else None
        if loadout is not None and loadout.get("wave") != int(wave):
            print(f"  WARN: segment {idx} wave={int(wave)} but loadout wave="
                  f"{loadout.get('wave')} — order mismatch?")
        header = {"max_play": len(seg) + 1, "demo_game": 0, "demo_wave": int(wave), "v": 2}
        if loadout is not None:
            header["loadout"] = loadout
        demo = {
            "header": header,
            "records": [
                {"frame": i, "b1": r[0], "b2": r[1], "b3": r[2], "b4": r[3],
                 "dirs": r[4], "gax": r[6], "gay": r[7],
                 "px": r[8], "py": r[9], "playerpic": r[10]}
                for i, r in enumerate(seg)
            ],
        }
        fn = os.path.join(outdir, f"wave{int(wave) + 1:02d}.json")
        k = 1
        while os.path.exists(fn):  # wave recurs (death + retry) → suffix
            fn = os.path.join(outdir, f"wave{int(wave) + 1:02d}_{k}.json")
            k += 1
        json.dump(demo, open(fn, "w"))
        written.append((fn, int(wave) + 1, len(seg), loadout is not None))

    for fn, wave, frames, has_lo in written:
        lo = "" if has_lo else " (NO loadout)"
        print(f"  {os.path.basename(fn)}: wave {wave}, {frames} frames{lo}")
    print(f"{len(written)} wave segment(s) -> {outdir}; {len(loadouts)} loadout line(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
