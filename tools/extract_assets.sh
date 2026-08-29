#!/usr/bin/env bash
# Regenerate assets/ from your own copy of the original Raptor game data.
#
# This repository ships NO game content. Raptor's art, audio, maps and text are
# the property of their rights holders and are not redistributed here. You must
# supply FILE0000.GLB and FILE0001.GLB from a legitimate copy of the game; this
# script extracts them into the assets/ layout the Godot project expects.
#
# Usage:
#   tools/extract_assets.sh <dir-containing-GLBs> [--skip-music]
#
#   $RAPTOR_GLB_DIR  may be set instead of passing the directory.
#   $DOSRAPTOR       path to the dosraptor repo (default: ../dosraptor).
#   --skip-music     extract only; don't render assets/music/*.ogg.
#
# Requires: cmake, a C toolchain, libpng (for dosraptor's extractor).
# Music rendering additionally needs ffmpeg and a C++ toolchain; see
# tools/render_music.sh.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOSRAPTOR="${DOSRAPTOR:-$(cd "$ROOT/.." && pwd)/dosraptor}"
SKIP_MUSIC=0
GLB_DIR="${RAPTOR_GLB_DIR:-}"

for arg in "$@"; do
    case "$arg" in
        --skip-music) SKIP_MUSIC=1 ;;
        -h|--help)    sed -n '2,18p' "$0"; exit 0 ;;
        *)            GLB_DIR="$arg" ;;
    esac
done

die() { echo "[extract_assets] $*" >&2; exit 1; }

[[ -n "$GLB_DIR" ]] || die "no GLB directory given. Usage: tools/extract_assets.sh <dir-containing-GLBs>"
[[ -d "$GLB_DIR" ]] || die "not a directory: $GLB_DIR"

# The GLB filenames are uppercase in the DOS distribution but some installers
# and archive tools lowercase them; accept either.
find_glb() {
    local n="$1" f
    for f in "$GLB_DIR/FILE000$n.GLB" "$GLB_DIR/file000$n.glb"; do
        [[ -f "$f" ]] && { echo "$f"; return 0; }
    done
    die "FILE000$n.GLB not found in $GLB_DIR"
}
GLB0="$(find_glb 0)"
GLB1="$(find_glb 1)"

[[ -d "$DOSRAPTOR" ]] || die "dosraptor repo not found at $DOSRAPTOR (set \$DOSRAPTOR). Clone https://github.com/nadavbh12/dosraptor"

# ---- 1. Build dosraptor's extractor -------------------------------------
EXTRACTOR="$DOSRAPTOR/build/tools/extract_assets/extract_assets"
if [[ ! -x "$EXTRACTOR" ]]; then
    echo "==> building extract_assets in $DOSRAPTOR/build"
    cmake -S "$DOSRAPTOR" -B "$DOSRAPTOR/build" -DCMAKE_BUILD_TYPE=Release
    cmake --build "$DOSRAPTOR/build" --target extract_assets -j
fi
[[ -x "$EXTRACTOR" ]] || die "extractor missing after build: $EXTRACTOR"

# ---- 2. Extract ---------------------------------------------------------
echo "==> extracting into $ROOT/assets"
mkdir -p "$ROOT/assets"
"$EXTRACTOR" "$GLB0" "$GLB1" "$ROOT/assets"

# ---- 3. Normalise layout ------------------------------------------------
# The extractor writes FLATSG*_ITM.json to the output root, but the Godot
# project loads it from assets/flats/. Move it into place.
shopt -s nullglob
flats=("$ROOT"/assets/FLATSG*_ITM.json)
if [[ ${#flats[@]} -gt 0 ]]; then
    mkdir -p "$ROOT/assets/flats"
    mv "${flats[@]}" "$ROOT/assets/flats/"
    echo "==> moved ${#flats[@]} FLATS*_ITM.json into assets/flats/"
fi
shopt -u nullglob

# ---- 4. Correct the MIDI tempo -----------------------------------------
# dosraptor's extractor converts MUS via mus2mid(rate=140) — the DMX library
# default (apodmx/DMX.C: mus_rate=140), NOT the rate Raptor actually uses.
# Raptor calls DMX_Init(70, ...) (dosraptor SOURCE/FX.C:1076), so the correct
# MThd division is 35, not 70. Left uncorrected, every track plays 2x too fast.
echo "==> correcting MIDI tempo (MThd division 70 -> 35)"
patched=0; already=0
shopt -s nullglob
for mid in "$ROOT"/assets/music/*.mid; do
    [[ "$(head -c 4 "$mid")" == "MThd" ]] || die "not a MIDI file: $mid"
    div="$(xxd -s 12 -l 2 -p "$mid")"
    case "$div" in
        0046) printf '\x00\x23' | dd of="$mid" bs=1 seek=12 conv=notrunc status=none
              patched=$((patched + 1)) ;;
        0023) already=$((already + 1)) ;;
        *)    die "$mid: unexpected MThd division 0x$div (expected 0046 or 0023). The extractor's mus2mid rate may have changed — check tools/render_music.sh." ;;
    esac
done
shopt -u nullglob
[[ $((patched + already)) -gt 0 ]] || die "no MIDI files found in assets/music — extraction produced nothing?"
echo "    $patched corrected, $already already correct"

# ---- 5. Render the .ogg Godot actually plays ----------------------------
# Godot cannot play .mid natively, so music requires this step.
if [[ "$SKIP_MUSIC" -eq 1 ]]; then
    echo "==> skipping music render (--skip-music). Run tools/render_music.sh for audio."
else
    echo "==> rendering music (OPL2 FM via libADLMIDI); this builds a synth on first run"
    "$ROOT/tools/render_music.sh"
fi

echo "[extract_assets] done — assets/ regenerated ($(find "$ROOT/assets" -type f | wc -l | tr -d ' ') files)"
