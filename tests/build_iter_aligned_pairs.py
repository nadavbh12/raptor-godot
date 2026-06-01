#!/usr/bin/env python3
"""Build C/Godot screenshot pairs by matching game-loop iteration.

For each Godot screenshot, find the nearest Godot parity row by relative
framecount, read its game-loop iter, then select the C parity row with the same
iter and the closest dumped C screenshot to that C row's relative framecount.

This is a renderer-diagnosis pairing mode: it deliberately ignores wall-clock
framecount cadence drift and asks "what does the same simulated iteration look
like in C and Godot?"
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


def read_parity(path: Path) -> list[dict[str, int]]:
    rows: list[dict[str, int]] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.startswith("{"):
            continue
        row = json.loads(line)
        if row.get("win") == "MISSION_1":
            rows.append({
                "fc": int(row["fc"]),
                "iter": int(row["iter"]),
                "score": int(row.get("score", 0)),
                "shield": int(row.get("shield", 0)),
                "enemies": int(row.get("enemies", 0)),
                "pbullets": int(row.get("pbullets", 0)),
                "ebullets": int(row.get("ebullets", 0)),
            })
    rows.sort(key=lambda row: row["fc"])
    return rows


def nearest_by_fc(rows: list[dict[str, int]], fc: int) -> dict[str, int]:
    fcs = [row["fc"] for row in rows]
    idx = bisect.bisect_left(fcs, fc)
    candidates = []
    if idx < len(rows):
        candidates.append(rows[idx])
    if idx:
        candidates.append(rows[idx - 1])
    if not candidates:
        raise SystemExit("empty parity rows")
    return min(candidates, key=lambda row: abs(row["fc"] - fc))


def rows_by_iter(rows: list[dict[str, int]]) -> dict[int, dict[str, int]]:
    return {row["iter"]: row for row in rows}


def nearest_by_iter(rows: list[dict[str, int]], iter_value: int) -> dict[str, int]:
    iters = [row["iter"] for row in rows]
    idx = bisect.bisect_left(iters, iter_value)
    candidates = []
    if idx < len(rows):
        candidates.append(rows[idx])
    if idx:
        candidates.append(rows[idx - 1])
    if not candidates:
        raise SystemExit("empty parity rows")
    return min(candidates, key=lambda row: abs(row["iter"] - iter_value))


def read_godot_shot_map(g_dir: Path) -> dict[str, dict[str, int]]:
    shot_map = g_dir / "shot_map.tsv"
    if not shot_map.exists():
        return {}
    rows: dict[str, dict[str, int]] = {}
    for line in shot_map.read_text(encoding="utf-8").splitlines()[1:]:
        # Godot C# int formatting on a Hebrew/RTL system locale prefixes negative
        # values with a bidi mark (LRM U+200E / RLM U+200F), which breaks int().
        # Strip them defensively (the writer is also fixed to use InvariantCulture).
        parts = [p.replace("‎", "").replace("‏", "").strip()
                 for p in line.split("\t")]
        if len(parts) != 9:
            continue
        file_name, saved_fc, drawn_fc, drawn_iter, score, shield, enemies, pbullets, ebullets = parts
        rows[file_name] = {
            "fc": int(drawn_fc),
            "iter": int(drawn_iter),
            "score": int(score),
            "shield": int(shield),
            "enemies": int(enemies),
            "pbullets": int(pbullets),
            "ebullets": int(ebullets),
            "saved_fc": int(saved_fc),
        }
    return rows


def read_c_frames(c_dir: Path, c_start_abs: int) -> list[dict[str, object]]:
    frame_map = c_dir / "frame_map.tsv"
    if not frame_map.exists():
        raise SystemExit(f"missing C frame map: {frame_map}")
    frames: list[dict[str, object]] = []
    for line in frame_map.read_text(encoding="utf-8").splitlines()[1:]:
        parts = line.split("\t")
        if len(parts) == 6:
            seq, abs_fc, present_count, game_iter, label, file_name = parts
        else:
            seq, abs_fc, present_count, label, file_name = parts
            game_iter = "-1"
        png = c_dir / file_name.replace(".bmp", ".png")
        if png.exists():
            abs_value = int(abs_fc)
            frames.append({
                "seq": int(seq),
                "abs": abs_value,
                "rel": abs_value - c_start_abs,
                "present_count": int(present_count),
                "game_iter": int(game_iter),
                "label": label,
                "path": png,
            })
    frames.sort(key=lambda frame: int(frame["rel"]))
    return frames


def nearest_c_frame(c_frames: list[dict[str, object]], rel_fc: int) -> dict[str, object]:
    rels = [int(frame["rel"]) for frame in c_frames]
    idx = bisect.bisect_left(rels, rel_fc)
    candidates = []
    if idx < len(c_frames):
        candidates.append(c_frames[idx])
    if idx:
        candidates.append(c_frames[idx - 1])
    if not candidates:
        raise SystemExit("empty C frame set")
    return min(candidates, key=lambda frame: abs(int(frame["rel"]) - rel_fc))


def nearest_c_frame_by_iter(c_frames: list[dict[str, object]], iter_value: int) -> dict[str, object] | None:
    frames = [frame for frame in c_frames if int(frame.get("game_iter", -1)) >= 0]
    if not frames:
        return None
    iters = [int(frame["game_iter"]) for frame in frames]
    idx = bisect.bisect_left(iters, iter_value)
    candidates = []
    if idx < len(frames):
        candidates.append(frames[idx])
    if idx:
        candidates.append(frames[idx - 1])
    return min(candidates, key=lambda frame: abs(int(frame["game_iter"]) - iter_value))


def read_godot_frames(g_dir: Path, g_start_abs: int) -> list[dict[str, object]]:
    frames: list[dict[str, object]] = []
    for path in sorted(g_dir.glob("*.png")):
        match = GODOT_FRAME_RE.search(path.name)
        if not match:
            continue
        abs_value = int(match.group(1))
        frames.append({"abs": abs_value, "rel": abs_value - g_start_abs, "path": path})
    return frames


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--c-dir", required=True, type=Path)
    parser.add_argument("--g-dir", required=True, type=Path)
    parser.add_argument("--out-dir", required=True, type=Path)
    parser.add_argument("--max-godot-frames", type=int, default=0)
    args = parser.parse_args()

    c_start = mission_start_abs(args.c_dir / "positions.txt")
    g_start = mission_start_abs(args.g_dir / "positions.txt")
    c_frames = read_c_frames(args.c_dir, c_start)
    c_rows = read_parity(args.c_dir / "parity.txt")
    g_rows = read_parity(args.g_dir / "parity.txt")
    c_by_iter = rows_by_iter(c_rows)
    g_frames = read_godot_frames(args.g_dir, g_start)
    g_shot_map = read_godot_shot_map(args.g_dir)
    if args.max_godot_frames > 0:
        g_frames = g_frames[: args.max_godot_frames]

    out_c = args.out_dir / "c"
    out_g = args.out_dir / "g"
    if args.out_dir.exists():
        shutil.rmtree(args.out_dir)
    out_c.mkdir(parents=True)
    out_g.mkdir(parents=True)

    pairs = []
    for g_frame in g_frames:
        mapped = g_shot_map.get(Path(str(g_frame["path"])).name)
        if mapped is not None and mapped["iter"] >= 0:
            g_row = {k: mapped[k] for k in ["fc", "iter", "score", "shield", "enemies", "pbullets", "ebullets"]}
            c_row = nearest_by_iter(c_rows, g_row["iter"])
        else:
            g_row = nearest_by_fc(g_rows, int(g_frame["rel"]))
            c_row = c_by_iter.get(g_row["iter"])
            if c_row is None:
                continue
        c_frame = nearest_c_frame_by_iter(c_frames, g_row["iter"]) or nearest_c_frame(c_frames, c_row["fc"])
        pair_id = f"{len(pairs):04d}"
        shutil.copy2(c_frame["path"], out_c / f"{pair_id}.png")
        shutil.copy2(g_frame["path"], out_g / f"{pair_id}.png")
        pairs.append({
            "pair_id": pair_id,
            "godot_frame": str(g_frame["path"]),
            "godot_abs": int(g_frame["abs"]),
            "godot_rel": int(g_frame["rel"]),
            "godot_parity": g_row,
            "godot_shot_map": mapped,
            "c_frame": str(c_frame["path"]),
            "c_abs": int(c_frame["abs"]),
            "c_rel": int(c_frame["rel"]),
            "c_parity": c_row,
            "c_frame_iter": int(c_frame.get("game_iter", -1)),
            "c_frame_delta": int(c_frame["rel"]) - c_row["fc"],
        })

    (args.out_dir / "iter_alignment_pairs.json").write_text(
        json.dumps(pairs, indent=2) + "\n",
        encoding="utf-8",
    )
    print(
        f"iter_align=1 c_frames={len(c_frames)} g_frames={len(g_frames)} "
        f"pairs={len(pairs)} c_start={c_start} g_start={g_start}"
    )
    if pairs:
        print(
            f"first pair: {pairs[0]['godot_frame']} -> {pairs[0]['c_frame']} "
            f"iter={pairs[0]['godot_parity']['iter']}"
        )
        print(
            f"last  pair: {pairs[-1]['godot_frame']} -> {pairs[-1]['c_frame']} "
            f"iter={pairs[-1]['godot_parity']['iter']}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
