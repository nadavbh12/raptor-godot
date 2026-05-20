#!/usr/bin/env python3
"""Search a dense reference frame dump for the closest visual matches.

This is intentionally simple and deterministic: use the dense C frame dump as
the reference, compare selected Godot frames against it, and report the best
matching C filenames. It is a temporal-alignment diagnostic, not a parity
verdict.
"""

from __future__ import annotations

import argparse
import json
import re
from dataclasses import asdict, dataclass
from pathlib import Path

from PIL import Image, ImageChops, ImageStat


@dataclass(frozen=True)
class Match:
    query: str
    reference: str
    score: float
    rank: int


def frame_key(path: Path) -> int:
    match = re.search(r"(\d+)", path.stem)
    return int(match.group(1)) if match else 0


def load_compare_image(path: Path, size: tuple[int, int], crop: tuple[int, int, int, int] | None) -> Image.Image:
    image = Image.open(path).convert("RGB")
    if crop:
        image = image.crop(crop)
    if image.size != size:
        image = image.resize(size, Image.Resampling.BILINEAR)
    return image


def mean_abs_difference(left: Image.Image, right: Image.Image) -> float:
    diff = ImageChops.difference(left, right)
    stat = ImageStat.Stat(diff)
    return sum(stat.mean) / len(stat.mean)


def parse_crop(value: str | None) -> tuple[int, int, int, int] | None:
    if not value:
        return None
    parts = [int(part) for part in value.split(",")]
    if len(parts) != 4:
        raise argparse.ArgumentTypeError("crop must be x0,y0,x1,y1")
    x0, y0, x1, y1 = parts
    if x1 <= x0 or y1 <= y0:
        raise argparse.ArgumentTypeError("crop must have positive width and height")
    return x0, y0, x1, y1


def collect_frames(path: Path) -> list[Path]:
    frames = sorted(path.glob("*.png"), key=frame_key)
    if not frames:
        frames = sorted(path.glob("*.bmp"), key=frame_key)
    return frames


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference-dir", required=True, type=Path, help="Dense frame directory, usually C.")
    parser.add_argument("--query", action="append", type=Path, help="Specific frame to match. May be repeated.")
    parser.add_argument("--query-dir", type=Path, help="Directory of query frames. Used if --query is omitted.")
    parser.add_argument("--out", type=Path, help="Optional JSON output path.")
    parser.add_argument("--top", type=int, default=5)
    parser.add_argument("--stride", type=int, default=1, help="Use every Nth reference frame.")
    parser.add_argument("--size", default="80x50", help="Comparison size, e.g. 80x50.")
    parser.add_argument("--crop", type=parse_crop, help="Optional crop before resizing: x0,y0,x1,y1.")
    args = parser.parse_args()

    width, height = [int(part) for part in args.size.lower().split("x", 1)]
    references = collect_frames(args.reference_dir)[:: max(1, args.stride)]
    if not references:
        raise SystemExit(f"no reference frames found in {args.reference_dir}")

    if args.query:
        queries = args.query
    elif args.query_dir:
        queries = collect_frames(args.query_dir)
    else:
        raise SystemExit("pass --query or --query-dir")
    if not queries:
        raise SystemExit("no query frames found")

    reference_images = [
        (path, load_compare_image(path, (width, height), args.crop))
        for path in references
    ]

    all_matches: list[Match] = []
    for query in queries:
        query_image = load_compare_image(query, (width, height), args.crop)
        scored = [
            (mean_abs_difference(query_image, reference_image), reference_path)
            for reference_path, reference_image in reference_images
        ]
        scored.sort(key=lambda item: item[0])
        for rank, (score, reference_path) in enumerate(scored[: args.top], start=1):
            all_matches.append(Match(
                query=str(query),
                reference=str(reference_path),
                score=score,
                rank=rank,
            ))

    for match in all_matches:
        print(f"{match.rank}\tscore={match.score:.3f}\tquery={match.query}\tref={match.reference}")

    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps([asdict(match) for match in all_matches], indent=2) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
