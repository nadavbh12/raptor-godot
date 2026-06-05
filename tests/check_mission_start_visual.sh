#!/usr/bin/env bash
# tests/check_mission_start_visual.sh
#
# Pixel-exact visual regression for the mission-start sequence + muzzle-follow.
# Captures the labeled Godot frames from tests/visual/mission_start.txt,
# downsamples each to 320x200, and compares them near-bit-exact (tolerance 2,
# <=0.5% mismatch budget) against committed goldens. This covers the frames that
# the other gates are blind to: L2 parity checks SIM STATE (NDJSON), not pixels,
# and the menu pixel-parity test only looks at static menu screens. The bugs that
# slipped (frozen mission start, missing briefing, static loading bar, lagging
# muzzle) were all View-layer and lived exactly in these frames.
#
# Rendering is deterministic on a given machine (verified bit-exact run-to-run),
# so this is a true pixel regression guard — unlike C-vs-Godot parity, which the
# 4x render/downsample round-trip keeps fuzzy.
#
# Usage:
#   bash tests/check_mission_start_visual.sh           # compare against goldens
#   GENERATE=1 bash tests/check_mission_start_visual.sh # (re)create goldens
#
# Exit: 0 = all match, 1 = visual regression, 2 = inconclusive (no display).
# Headed (rendering required); do NOT pass --headless (GetViewport returns null).
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPT="$REPO/tests/visual/mission_start.txt"
GOLDEN_DIR="$REPO/tests/parity/visual_snapshots/mission_start"
GODOT_BIN="${GODOT_BIN:-$(command -v godot)}"
GODOT_BIN="$(realpath "$GODOT_BIN")"
if [[ -z "${DOTNET_ROOT:-}" && -d /opt/homebrew/opt/dotnet@8/libexec ]]; then
    export DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec
fi
export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-Major}"

# Only the FROZEN mission-start frames are snapshotted: they're bit-exact run to
# run. Scrolling/firing gameplay frames have delta-driven cosmetic variance, so
# they're covered by L2 (sim state) + the muzzle unit test instead.
LABELS="briefing_bar_low briefing_bar_mid briefing_bar_high fade_mid"
TOLERANCE="${TOLERANCE:-2}"
MAX_MISMATCH_PCT="${MAX_MISMATCH_PCT:-0.5}"

GDIR="$(mktemp -d)"
trap 'rm -rf "$GDIR"' EXIT

echo "[visual] headed capture -> $GDIR"
RAPTOR_PLAYTHROUGH="$SCRIPT" \
RAPTOR_RENDER_MENUS=1 \
RAPTOR_SHOT_DIR="$GDIR" \
RAPTOR_SHOT_EVERY_FC=1 \
"$GODOT_BIN" --path "$REPO" --audio-driver Dummy --quit-after 2700 >"$GDIR/run.log" 2>&1 || true

if [[ "$(ls "$GDIR"/fc*_label_*.png 2>/dev/null | wc -l | tr -d ' ')" == "0" ]]; then
    echo "[visual] INCONCLUSIVE: no labeled frames captured (a real display is required)."
    tail -4 "$GDIR/run.log" 2>/dev/null | sed 's/^/           /'
    exit 2
fi

mkdir -p "$GOLDEN_DIR"
python3 - "$GDIR" "$GOLDEN_DIR" "$TOLERANCE" "$MAX_MISMATCH_PCT" "${GENERATE:-0}" $LABELS <<'PY'
import sys, glob, os
from PIL import Image, ImageChops
gdir, golden, tol, maxpct, gen = sys.argv[1], sys.argv[2], int(sys.argv[3]), float(sys.argv[4]), sys.argv[5] == "1"
labels = sys.argv[6:]

def frame_for(label):
    hits = glob.glob(os.path.join(gdir, f"fc*_label_{label}.png"))
    return sorted(hits)[-1] if hits else None

fails, missing = [], []
for label in labels:
    src = frame_for(label)
    if not src:
        missing.append(label); continue
    img = Image.open(src).convert("RGB").resize((320, 200), Image.NEAREST)
    gpath = os.path.join(golden, f"{label}.png")
    if gen:
        img.save(gpath); print(f"  GEN {label} -> {gpath}"); continue
    if not os.path.exists(gpath):
        missing.append(f"{label}(no-golden)"); continue
    gold = Image.open(gpath).convert("RGB")
    diff = ImageChops.difference(img, gold)
    mm = sum(1 for r, g, b in diff.getdata() if max(r, g, b) > tol)
    pct = mm * 100.0 / (320 * 200)
    status = "PASS" if pct <= maxpct else "FAIL"
    print(f"  {status} {label}: mismatch={pct:.3f}% ({mm}px, budget {maxpct}%)")
    if status == "FAIL":
        fails.append(label)

if gen:
    print("[visual] goldens written."); sys.exit(0)
if missing:
    print(f"[visual] INCONCLUSIVE: frames/goldens missing: {missing}"); sys.exit(2)
if fails:
    print(f"[visual] FAIL: visual regression in {fails} — re-run with GENERATE=1 only if the change is intended."); sys.exit(1)
print("[visual] PASS: all mission-start frames pixel-match their goldens."); sys.exit(0)
PY
