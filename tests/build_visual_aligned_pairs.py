#!/usr/bin/env python3
"""Pair Godot frames with closest dense C frames by visual similarity.

Use this only after dense C capture exists. It intentionally pairs by pixels,
so it is a diagnostic for temporal alignment and frame selection. It should not
replace simulation/object parity checks.
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
from dataclasses import asdict, dataclass
from pathlib import Path

from PIL import Image, ImageChops, ImageStat


@dataclass(frozen=True)
class Pair:
    pair_id: str
    godot_frame: str
    c_frame: str
    score: float


def frame_key(path: Path) -> int:
    match = re.search(r"(\d+)", path.stem)
    return int(match.group(1)) if match else 0


def collect(path: Path) -> list[Path]:
    return sorted(path.glob("*.png"), key=frame_key)


def load(path: Path, size: tuple[int, int], crop: tuple[int, int, int, int] | None) -> Image.Image:
    image = Image.open(path).convert("RGB")
    if crop:
        image = image.crop(crop)
    if image.size != size:
        image = image.resize(size, Image.Resampling.BILINEAR)
    return image


def score(left: Image.Image, right: Image.Image) -> float:
    stat = ImageStat.Stat(ImageChops.difference(left, right))
    return sum(stat.mean) / len(stat.mean)


def parse_crop(value: str | None) -> tuple[int, int, int, int] | None:
    if not value:
        return None
    parts = [int(part) for part in value.split(",")]
    if len(parts) != 4:
        raise argparse.ArgumentTypeError("crop must be x0,y0,x1,y1")
    return parts[0], parts[1], parts[2], parts[3]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--c-dir", required=True, type=Path)
    parser.add_argument("--g-dir", required=True, type=Path)
    parser.add_argument("--out-dir", required=True, type=Path)
    parser.add_argument("--size", default="80x50")
    parser.add_argument("--crop", type=parse_crop)
    parser.add_argument("--stride", type=int, default=1, help="Use every Nth C frame.")
    parser.add_argument("--max-godot-frames", type=int, default=0, help="0 means all.")
    args = parser.parse_args()

    width, height = [int(part) for part in args.size.lower().split("x", 1)]
    c_frames = collect(args.c_dir)[:: max(1, args.stride)]
    g_frames = collect(args.g_dir)
    if args.max_godot_frames > 0:
        g_frames = g_frames[: args.max_godot_frames]
    if not c_frames or not g_frames:
        raise SystemExit(f"empty frame set: c={len(c_frames)} g={len(g_frames)}")

    refs = [(path, load(path, (width, height), args.crop)) for path in c_frames]

    out_c = args.out_dir / "c"
    out_g = args.out_dir / "g"
    if out_c.exists():
        shutil.rmtree(out_c)
    if out_g.exists():
        shutil.rmtree(out_g)
    out_c.mkdir(parents=True)
    out_g.mkdir(parents=True)

    pairs: list[Pair] = []
    for index, g_path in enumerate(g_frames):
        g_image = load(g_path, (width, height), args.crop)
        best_score, best_path = min(
            ((score(g_image, c_image), c_path) for c_path, c_image in refs),
            key=lambda item: item[0],
        )
        pair_id = f"{index:04d}"
        shutil.copy(best_path, out_c / f"{pair_id}.png")
        shutil.copy(g_path, out_g / f"{pair_id}.png")
        pairs.append(Pair(
            pair_id=pair_id,
            godot_frame=str(g_path),
            c_frame=str(best_path),
            score=best_score,
        ))

    (args.out_dir / "visual_alignment_pairs.json").write_text(
        json.dumps([asdict(pair) for pair in pairs], indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"visual_align=1 c_frames={len(c_frames)} g_frames={len(g_frames)} pairs={len(pairs)}")
    if pairs:
        print(f"first pair: {pairs[0].godot_frame} -> {pairs[0].c_frame} score={pairs[0].score:.3f}")
        print(f"last  pair: {pairs[-1].godot_frame} -> {pairs[-1].c_frame} score={pairs[-1].score:.3f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
