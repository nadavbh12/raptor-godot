#!/usr/bin/env python3
"""Build C/Godot screenshot pairs by normalized framecount.

The C frame dump records every present plus frame_map.tsv. Godot screenshot
names already include SimClock.Frame as fcNNNNN. This tool subtracts each side's
mission-start absolute frame and chooses the C frame with the closest relative
framecount for each Godot screenshot.
"""

from __future__ import annotations

import argparse
import bisect
import json
import re
import shutil
from pathlib import Path


GODOT_FRAME_RE = re.compile(r"fc(\d+)_")


def mission_start_abs(positions_path: Path) -> int:
    for line in positions_path.read_text(encoding="utf-8").splitlines():
        match = re.match(r"fc=(-?\d+) abs=(-?\d+) win=MISSION_1", line)
        if match:
            rel_fc = int(match.group(1))
            abs_fc = int(match.group(2))
            return abs_fc - rel_fc
    raise SystemExit(f"no MISSION_1 position header found in {positions_path}")


def read_c_frames(c_dir: Path, c_start_abs: int) -> list[dict[str, object]]:
    frame_map = c_dir / "frame_map.tsv"
    if not frame_map.exists():
        raise SystemExit(f"missing C frame map: {frame_map}")

    frames: list[dict[str, object]] = []
    for line in frame_map.read_text(encoding="utf-8").splitlines()[1:]:
        seq, abs_fc, present_count, label, file_name = line.split("\t")
        png = c_dir / file_name.replace(".bmp", ".png")
        if not png.exists():
            continue
        abs_value = int(abs_fc)
        frames.append(
            {
                "seq": int(seq),
                "abs": abs_value,
                "rel": abs_value - c_start_abs,
                "present_count": int(present_count),
                "label": label,
                "path": png,
            }
        )
    frames.sort(key=lambda frame: int(frame["rel"]))
    return frames


def read_godot_frames(g_dir: Path, g_start_abs: int) -> list[dict[str, object]]:
    frames: list[dict[str, object]] = []
    for path in sorted(g_dir.glob("*.png")):
        match = GODOT_FRAME_RE.search(path.name)
        if not match:
            continue
        abs_value = int(match.group(1))
        frames.append({"abs": abs_value, "rel": abs_value - g_start_abs, "path": path})
    return frames


def nearest_c(c_frames: list[dict[str, object]], rel_fc: int) -> dict[str, object]:
    rels = [int(frame["rel"]) for frame in c_frames]
    idx = bisect.bisect_left(rels, rel_fc)
    candidates = []
    if idx < len(c_frames):
        candidates.append(c_frames[idx])
    if idx:
        candidates.append(c_frames[idx - 1])
    if not candidates:
        raise SystemExit("no C frames available")
    return min(candidates, key=lambda frame: abs(int(frame["rel"]) - rel_fc))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--c-dir", required=True, type=Path)
    parser.add_argument("--g-dir", required=True, type=Path)
    parser.add_argument("--out-dir", required=True, type=Path)
    parser.add_argument("--offset", type=int, default=0, help="add to Godot relative framecount before matching C")
    parser.add_argument("--max-godot-frames", type=int, default=0)
    args = parser.parse_args()

    c_start = mission_start_abs(args.c_dir / "positions.txt")
    g_start = mission_start_abs(args.g_dir / "positions.txt")
    c_frames = read_c_frames(args.c_dir, c_start)
    g_frames = read_godot_frames(args.g_dir, g_start)
    if args.max_godot_frames > 0:
        g_frames = g_frames[: args.max_godot_frames]

    out_c = args.out_dir / "c"
    out_g = args.out_dir / "g"
    if args.out_dir.exists():
        shutil.rmtree(args.out_dir)
    out_c.mkdir(parents=True)
    out_g.mkdir(parents=True)

    pairs = []
    for index, g_frame in enumerate(g_frames):
        target_rel = int(g_frame["rel"]) + args.offset
        c_frame = nearest_c(c_frames, target_rel)
        pair_id = f"{index:04d}"
        shutil.copy2(c_frame["path"], out_c / f"{pair_id}.png")
        shutil.copy2(g_frame["path"], out_g / f"{pair_id}.png")
        pairs.append(
            {
                "pair_id": pair_id,
                "godot_frame": str(g_frame["path"]),
                "godot_abs": int(g_frame["abs"]),
                "godot_rel": int(g_frame["rel"]),
                "c_frame": str(c_frame["path"]),
                "c_abs": int(c_frame["abs"]),
                "c_rel": int(c_frame["rel"]),
                "delta": int(c_frame["rel"]) - target_rel,
                "offset": args.offset,
            }
        )

    (args.out_dir / "framecount_alignment_pairs.json").write_text(
        json.dumps(pairs, indent=2) + "\n",
        encoding="utf-8",
    )
    print(
        f"framecount_align=1 c_frames={len(c_frames)} g_frames={len(g_frames)} "
        f"pairs={len(pairs)} c_start={c_start} g_start={g_start} offset={args.offset}"
    )
    if pairs:
        print(
            f"first pair: {pairs[0]['godot_frame']} -> {pairs[0]['c_frame']} "
            f"delta={pairs[0]['delta']}"
        )
        print(
            f"last  pair: {pairs[-1]['godot_frame']} -> {pairs[-1]['c_frame']} "
            f"delta={pairs[-1]['delta']}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
