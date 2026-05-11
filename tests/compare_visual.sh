#!/usr/bin/env bash
# tests/compare_visual.sh
#
# Run the same mission_start.txt playthrough on both C dosraptor and our
# Godot port, capturing screenshots at parity-tick boundaries, then build
# side-by-side panels for human inspection.
#
# Inputs:
#   $DOSRAPTOR     — path to dosraptor repo (default ../dosraptor)
# Outputs:
#   /tmp/c_1s/         C frame dumps (PNG, 320x200 native)
#   /tmp/godot_1s/     Godot screenshots (1280x800, 4x sprite render)
#   /tmp/compare/      side-by-side panels (1280x400 each, C left/Godot right)
#
# Comparison strategy: match by visible score on each pair. Score milestones
# bracket MISSION_1 fc ranges and survive minor cadence drift.

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
DOSRAPTOR="${DOSRAPTOR:-$(cd "$REPO/.." && pwd)/dosraptor}"
SCRIPT="$DOSRAPTOR/tests/scripts/mission_start.txt"

CDIR=/tmp/c_1s
GDIR=/tmp/godot_1s
ODIR=/tmp/compare

mkdir -p "$CDIR" "$GDIR" "$ODIR"

# 1. Capture C dumps
echo "[compare] capturing C dosraptor frames..."
CBIN="$DOSRAPTOR/build/raptor.app/Contents/MacOS/raptor"
rm -f "$CDIR"/*.bmp "$CDIR"/*.png
RAPTOR_SKIPINTRO=1 \
RAPTOR_PLAYTHROUGH="$SCRIPT" \
RAPTOR_PARITY_OUT="$CDIR/parity.txt" \
RAPTOR_DUMP_DIR="$CDIR" \
RAPTOR_DUMP_EVERY=23 \
timeout 90 "$CBIN" >"$CDIR/log.txt" 2>&1 || true
for f in "$CDIR"/*.bmp; do
    sips -s format png "$f" --out "${f%.bmp}.png" >/dev/null 2>&1
done

# 2. Capture Godot frames
echo "[compare] capturing Godot frames..."
rm -f "$GDIR"/*.png "$GDIR/parity.txt" "$GDIR/log.txt"
GODOT_BIN="$(realpath "$(command -v godot)")"
export PATH="$HOME/.local/bin:/opt/homebrew/opt/dotnet@8/bin:$PATH"
RAPTOR_PLAYTHROUGH="$SCRIPT" \
RAPTOR_PARITY_OUT="$GDIR/parity.txt" \
RAPTOR_TEST_FAST=1 \
RAPTOR_SHOT_DIR="$GDIR" \
"$GODOT_BIN" --path "$REPO" --quit-after 20000 >"$GDIR/log.txt" 2>&1

# 3. Build matched-score panels
echo "[compare] building panels..."
rm -f "$ODIR"/*.png
make_panel() {
    local c="$1"; local g="$2"; local out="$3"
    [ -f "$c" ] && [ -f "$g" ] && ffmpeg -y -hide_banner -loglevel error \
        -i "$c" -i "$g" \
        -filter_complex "[0:v]scale=640:400:flags=neighbor[c]; [1:v]scale=640:400[g]; [c][g]hstack" \
        "$out"
}

# Score-milestone pairs (C dump → Godot fc):
# C dump 29 (post-sector_select, score 10000) → Godot abs_fc ≈ 2590 (M1 fc=560)
# C dump 33 (auto, score 10350)               → Godot abs_fc ≈ 2730 (M1 fc=700)
# C dump 41 (06_more, score 11050)            → Godot abs_fc ≈ 3290 (M1 fc=1260)
# C dump 52 (07_more, score 11750)            → Godot abs_fc ≈ 4131 (M1 fc=2100)

pick_godot_at() {
    local target=$1
    ls "$GDIR"/fc*.png 2>/dev/null | awk -F'fc|_sec' -v t=$target '
        { d=($2>t)?($2-t):(t-$2); if(d<min || NR==1){min=d; pick=$0} }
        END { print pick }'
}

make_panel "$CDIR/00029_05_after_sector_select.png" "$(pick_godot_at 2590)" "$ODIR/01_score10000_fc560.png"
make_panel "$CDIR/00033_auto.png"                    "$(pick_godot_at 2730)" "$ODIR/02_score10350_fc700.png"
make_panel "$CDIR/00041_06_more.png"                 "$(pick_godot_at 3290)" "$ODIR/03_score11050_fc1260.png"
make_panel "$CDIR/00052_07_more.png"                 "$(pick_godot_at 4131)" "$ODIR/04_score11750_fc2100.png"

echo "[compare] done. panels in $ODIR/"
ls -1 "$ODIR"/*.png
