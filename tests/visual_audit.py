#!/usr/bin/env python3
"""Generate a compact visual parity audit from paired C/Godot frames."""

from __future__ import annotations

import argparse
import json
import os
import re
from collections import deque
from dataclasses import asdict, dataclass
from pathlib import Path

from PIL import Image, ImageChops


@dataclass(frozen=True)
class Finding:
    frame_id: str
    classification: str
    mismatch_pixels: int
    bbox: list[int]
    c_frame: str
    godot_frame: str
    diff_image: str
    crop_image: str

    @property
    def area(self) -> int:
        return max(0, self.bbox[2] - self.bbox[0]) * max(0, self.bbox[3] - self.bbox[1])


def load_rgb(path: Path) -> Image.Image:
    return Image.open(path).convert("RGB")


def frame_id(path: Path) -> str:
    match = re.match(r"^(\d+)", path.stem)
    return match.group(1) if match else path.stem


def neighbors(x: int, y: int, width: int, height: int):
    if x > 0:
        yield x - 1, y
    if x + 1 < width:
        yield x + 1, y
    if y > 0:
        yield x, y - 1
    if y + 1 < height:
        yield x, y + 1


def clusters(mask: list[bool], width: int, height: int) -> list[tuple[int, int, int, int, int]]:
    seen = [False] * len(mask)
    out: list[tuple[int, int, int, int, int]] = []
    for start, active in enumerate(mask):
        if not active or seen[start]:
            continue
        sx = start % width
        sy = start // width
        queue: deque[tuple[int, int]] = deque([(sx, sy)])
        seen[start] = True
        min_x = max_x = sx
        min_y = max_y = sy
        count = 0
        while queue:
            x, y = queue.popleft()
            count += 1
            min_x = min(min_x, x)
            max_x = max(max_x, x)
            min_y = min(min_y, y)
            max_y = max(max_y, y)
            for nx, ny in neighbors(x, y, width, height):
                idx = ny * width + nx
                if mask[idx] and not seen[idx]:
                    seen[idx] = True
                    queue.append((nx, ny))
        out.append((count, min_x, min_y, max_x + 1, max_y + 1))
    out.sort(reverse=True)
    return out


def expanded_bbox(bbox: tuple[int, int, int, int], width: int, height: int, pad: int) -> tuple[int, int, int, int]:
    x0, y0, x1, y1 = bbox
    return max(0, x0 - pad), max(0, y0 - pad), min(width, x1 + pad), min(height, y1 + pad)


def make_panel(c_img: Image.Image, g_img: Image.Image, diff_img: Image.Image, bbox: tuple[int, int, int, int]) -> Image.Image:
    x0, y0, x1, y1 = bbox
    c_crop = c_img.crop((x0, y0, x1, y1))
    g_crop = g_img.crop((x0, y0, x1, y1))
    d_crop = diff_img.crop((x0, y0, x1, y1))
    scale = max(1, min(8, 256 // max(1, max(c_crop.size))))
    crops = [
        img.resize((img.width * scale, img.height * scale), Image.Resampling.NEAREST)
        for img in (c_crop, g_crop, d_crop)
    ]
    panel = Image.new("RGB", (sum(img.width for img in crops), max(img.height for img in crops)), (0, 0, 0))
    x = 0
    for img in crops:
        panel.paste(img, (x, 0))
        x += img.width
    return panel


def analyze_pair(
    c_path: Path,
    g_path: Path,
    out_dir: Path,
    threshold: int,
    min_cluster_pixels: int,
    max_findings_per_frame: int,
) -> list[Finding]:
    c_img = load_rgb(c_path)
    g_img = load_rgb(g_path)
    if c_img.size != g_img.size:
        g_img = g_img.resize(c_img.size, Image.Resampling.NEAREST)

    delta = ImageChops.difference(c_img, g_img)
    width, height = delta.size
    heat = Image.new("RGB", delta.size)
    heat_pixels = []
    mask = []
    pixel_data = delta.get_flattened_data() if hasattr(delta, "get_flattened_data") else delta.getdata()
    for r, g, b in pixel_data:
        d = max(r, g, b)
        active = d > threshold
        mask.append(active)
        heat_pixels.append((min(255, d), 0, 0) if active else (0, 0, 0))
    heat.putdata(heat_pixels)

    fid = frame_id(g_path)
    diff_path = out_dir / "diffs" / f"{fid}.diff.png"
    heat.save(diff_path)

    findings: list[Finding] = []
    for index, (count, x0, y0, x1, y1) in enumerate(clusters(mask, width, height)):
        if count < min_cluster_pixels:
            continue
        if len(findings) >= max_findings_per_frame:
            break
        crop_bbox = expanded_bbox((x0, y0, x1, y1), width, height, pad=8)
        crop_path = out_dir / "crops" / f"{fid}_{index:02d}.png"
        make_panel(c_img, g_img, heat, crop_bbox).save(crop_path)
        findings.append(Finding(
            frame_id=fid,
            classification="visual-diff",
            mismatch_pixels=count,
            bbox=[x0, y0, x1, y1],
            c_frame=os.path.relpath(c_path, out_dir),
            godot_frame=os.path.relpath(g_path, out_dir),
            diff_image=os.path.relpath(diff_path, out_dir),
            crop_image=os.path.relpath(crop_path, out_dir),
        ))
    return findings


def write_html(path: Path, scenario_id: str, findings: list[Finding]) -> None:
    rows = []
    for finding in findings:
        rows.append(
            "<section>"
            f"<h2>{finding.frame_id} - {finding.classification} - {finding.mismatch_pixels}px</h2>"
            f"<p>bbox: {finding.bbox}</p>"
            f"<img src=\"{finding.crop_image}\" />"
            f"<p><a href=\"{finding.c_frame}\">C frame</a> "
            f"<a href=\"{finding.godot_frame}\">Godot frame</a> "
            f"<a href=\"{finding.diff_image}\">diff</a></p>"
            "</section>"
        )
    body = "\n".join(rows) if rows else "<p>No findings above threshold.</p>"
    path.write_text(
        "<!doctype html><meta charset=\"utf-8\">"
        f"<title>Visual audit: {scenario_id}</title>"
        "<style>body{font-family:sans-serif;background:#111;color:#eee}"
        "section{border-top:1px solid #555;padding:16px}img{image-rendering:pixelated;max-width:100%}"
        "a{color:#8cf}</style>"
        f"<h1>Visual audit: {scenario_id}</h1>{body}\n",
        encoding="utf-8",
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--pairs-dir", required=True, type=Path)
    parser.add_argument("--out-dir", required=True, type=Path)
    parser.add_argument("--scenario-id", required=True)
    parser.add_argument("--threshold", type=int, default=8)
    parser.add_argument("--min-cluster-pixels", type=int, default=8)
    parser.add_argument("--max-findings-per-frame", type=int, default=5)
    parser.add_argument("--max-total-findings", type=int, default=200)
    args = parser.parse_args()

    c_dir = args.pairs_dir / "c"
    g_dir = args.pairs_dir / "g"
    if not c_dir.is_dir() or not g_dir.is_dir():
        raise SystemExit(f"pairs dir must contain c/ and g/: {args.pairs_dir}")

    for subdir in ("diffs", "crops"):
        (args.out_dir / subdir).mkdir(parents=True, exist_ok=True)

    findings: list[Finding] = []
    for g_path in sorted(g_dir.glob("*.png")):
        c_path = c_dir / g_path.name
        if not c_path.exists():
            findings.append(Finding(
                frame_id=frame_id(g_path),
                classification="missing-c-object-dump",
                mismatch_pixels=0,
                bbox=[0, 0, 0, 0],
                c_frame="",
                godot_frame=os.path.relpath(g_path, args.out_dir),
                diff_image="",
                crop_image="",
            ))
            continue
        findings.extend(analyze_pair(
            c_path,
            g_path,
            args.out_dir,
            threshold=args.threshold,
            min_cluster_pixels=args.min_cluster_pixels,
            max_findings_per_frame=args.max_findings_per_frame,
        ))

    frame_area = 320 * 200
    findings.sort(key=lambda f: (
        f.area > frame_area // 2,
        -f.mismatch_pixels,
        -f.area,
    ))
    findings = findings[:args.max_total_findings]
    summary = {
        "scenario_id": args.scenario_id,
        "pairs_dir": str(args.pairs_dir),
        "finding_count": len(findings),
        "findings": [asdict(f) for f in findings],
    }
    args.out_dir.mkdir(parents=True, exist_ok=True)
    (args.out_dir / "findings.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    write_html(args.out_dir / "index.html", args.scenario_id, findings)
    print(f"[visual_audit] findings: {len(findings)}")
    print(f"[visual_audit] report: {args.out_dir / 'index.html'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
