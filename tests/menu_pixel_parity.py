#!/usr/bin/env python3
"""Compare labeled C/Godot menu screenshots and write inspectable diff images.

The comparator is intentionally simple:
  - locate matching dump labels in a C screenshot dir and a Godot screenshot dir
  - normalize both images to 320x200 RGB
  - count pixels whose max channel delta exceeds --tolerance
  - write a red diff heatmap and a C | Godot | diff panel for each label
  - write summary.json for automation

This is meant for menu/SWD windows, where static art can eventually converge to
pixel-level parity. Use --max-mismatch-pct as a ratchet, not as a permanent
excuse for drift.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import asdict, dataclass
from pathlib import Path

from PIL import Image, ImageChops


@dataclass(frozen=True)
class LabelResult:
    label: str
    c_path: str
    godot_path: str
    diff_path: str
    panel_path: str
    mismatch_pixels: int
    total_pixels: int
    mismatch_pct: float
    mean_abs_delta: float
    max_abs_delta: int
    passed: bool


def c_labels(path: Path) -> dict[str, Path]:
    out: dict[str, Path] = {}
    for file in path.iterdir():
        if file.suffix.lower() != ".png":
            continue
        match = re.match(r"^\d{5}_(.+)\.png$", file.name)
        if match:
            out[match.group(1)] = file
    return out


def godot_labels(path: Path) -> dict[str, Path]:
    out: dict[str, Path] = {}
    for file in path.iterdir():
        if file.suffix.lower() != ".png":
            continue
        match = re.match(r"^fc\d+_label_(.+?)(?:_p\d+)?\.png$", file.name)
        if match and "_p" not in file.stem:
            out[match.group(1)] = file
    return out


def load_normalized(path: Path) -> Image.Image:
    img = Image.open(path).convert("RGB")
    if img.size != (320, 200):
        img = img.resize((320, 200), Image.Resampling.NEAREST)
    return img


def compare_label(
    label: str,
    c_path: Path,
    godot_path: Path,
    out_dir: Path,
    tolerance: int,
    max_mismatch_pct: float,
) -> LabelResult:
    c_img = load_normalized(c_path)
    g_img = load_normalized(godot_path)
    delta = ImageChops.difference(c_img, g_img)
    pixels = list(delta.getdata())

    mismatch = 0
    total_delta = 0
    max_delta = 0
    heat = Image.new("RGB", delta.size)
    heat_pixels = []
    for r, g, b in pixels:
        d = max(r, g, b)
        max_delta = max(max_delta, d)
        total_delta += r + g + b
        if d > tolerance:
            mismatch += 1
            heat_pixels.append((min(255, d), 0, 0))
        else:
            heat_pixels.append((0, 0, 0))
    heat.putdata(heat_pixels)

    total = delta.size[0] * delta.size[1]
    mismatch_pct = mismatch * 100.0 / total
    mean_abs_delta = total_delta / (total * 3)
    passed = mismatch_pct <= max_mismatch_pct

    safe = re.sub(r"[^A-Za-z0-9_.-]+", "_", label)
    diff_path = out_dir / f"{safe}.diff.png"
    panel_path = out_dir / f"{safe}.panel.png"
    heat.save(diff_path)

    panel = Image.new("RGB", (960, 200), (0, 0, 0))
    panel.paste(c_img, (0, 0))
    panel.paste(g_img, (320, 0))
    panel.paste(heat, (640, 0))
    panel.save(panel_path)

    return LabelResult(
        label=label,
        c_path=str(c_path),
        godot_path=str(godot_path),
        diff_path=str(diff_path),
        panel_path=str(panel_path),
        mismatch_pixels=mismatch,
        total_pixels=total,
        mismatch_pct=mismatch_pct,
        mean_abs_delta=mean_abs_delta,
        max_abs_delta=max_delta,
        passed=passed,
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--c-dir", required=True, type=Path)
    parser.add_argument("--godot-dir", required=True, type=Path)
    parser.add_argument("--out-dir", required=True, type=Path)
    parser.add_argument("--tolerance", type=int, default=8)
    parser.add_argument("--max-mismatch-pct", type=float, default=40.0)
    parser.add_argument("--label", action="append", default=[],
                        help="Compare only this label. May be repeated.")
    parser.add_argument("--no-fail", action="store_true",
                        help="Always exit 0 after writing artifacts.")
    args = parser.parse_args()

    args.out_dir.mkdir(parents=True, exist_ok=True)
    c = c_labels(args.c_dir)
    g = godot_labels(args.godot_dir)
    labels = sorted(set(c) & set(g))
    if args.label:
        wanted = set(args.label)
        labels = [label for label in labels if label in wanted]

    if not labels:
        print("menu_pixel_parity: no common labels", file=sys.stderr)
        print(f"C labels: {sorted(c)}", file=sys.stderr)
        print(f"Godot labels: {sorted(g)}", file=sys.stderr)
        return 2

    results = [
        compare_label(label, c[label], g[label], args.out_dir,
                      args.tolerance, args.max_mismatch_pct)
        for label in labels
    ]
    summary = {
        "tolerance": args.tolerance,
        "max_mismatch_pct": args.max_mismatch_pct,
        "passed": all(r.passed for r in results),
        "labels": [asdict(r) for r in results],
    }
    (args.out_dir / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")

    for result in results:
        status = "PASS" if result.passed else "FAIL"
        print(
            f"{status} {result.label}: "
            f"mismatch={result.mismatch_pct:.2f}% "
            f"mean_delta={result.mean_abs_delta:.2f} "
            f"max_delta={result.max_abs_delta} "
            f"panel={result.panel_path}"
        )

    if args.no_fail:
        return 0
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
