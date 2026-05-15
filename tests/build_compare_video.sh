#!/usr/bin/env bash
# tests/build_compare_video.sh
#
# Build a side-by-side comparison video of C dosraptor vs the Godot port for a
# given playthrough script. Anchors both implementations on mission_fc and
# applies the empirically-tuned C↔Godot temporal offset.
#
# Outputs:
#   /tmp/pair_seq/c/NNNN.png  /tmp/pair_seq/g/NNNN.png  (paired frame sequences)
#   /tmp/c.mp4                /tmp/g.mp4                (per-side videos)
#   /tmp/sidebyside.mp4                                 (final hstack output)
#
# Inputs (env):
#   DOSRAPTOR             path to dosraptor repo (default: ../dosraptor)
#   SCRIPT_NAME           playthrough script name (default: mission_start)
#   OFFSET                C-side shift in sim frames (default: 144)
#   ANCHOR                Godot mission-start abs SimClock.Frame (default: 2038)
#   SKIP_UNTIL_C          drop pairs where C is mid-fade-in below this mfc (default: 200)
#   FPS                   output framerate (default: 24)
#   REUSE_C, REUSE_GODOT  set to 1 to skip re-extracting that side
#   GODOT_BIN             Godot executable (default: command -v godot)

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
DOSRAPTOR="${DOSRAPTOR:-$(cd "$REPO/.." && pwd)/dosraptor}"
SCRIPT_NAME="${SCRIPT_NAME:-mission_start}"
SCRIPT_PATH="$DOSRAPTOR/tests/scripts/${SCRIPT_NAME}.txt"

OFFSET="${OFFSET:-144}"
ANCHOR="${ANCHOR:-2038}"
SKIP_UNTIL_C="${SKIP_UNTIL_C:-200}"
FPS="${FPS:-24}"

C_DIR=/tmp/c_1s
G_DIR=/tmp/godot_1s
PAIR_DIR=/tmp/pair_seq
mkdir -p "$C_DIR" "$G_DIR"

# 1. Extract C frames (every present = every iter ≈ 3 sim fc).
if [[ "${REUSE_C:-0}" -ne 1 ]]; then
    if [[ ! -f "$SCRIPT_PATH" ]]; then
        echo "[compare] ERROR: script not found: $SCRIPT_PATH" >&2
        exit 2
    fi
    echo "[compare] extracting C frames ($SCRIPT_NAME)..."
    CBIN="$DOSRAPTOR/build/raptor.app/Contents/MacOS/raptor"
    rm -f "$C_DIR"/*.bmp "$C_DIR"/*.png "$C_DIR"/parity.txt "$C_DIR"/log.txt
    SDL_AUDIODRIVER=dummy \
    RAPTOR_SKIPINTRO=1 \
    RAPTOR_PLAYTHROUGH="$SCRIPT_PATH" \
    RAPTOR_PARITY_OUT="$C_DIR/parity.txt" \
    RAPTOR_DUMP_DIR="$C_DIR" \
    RAPTOR_DUMP_EVERY=1 \
    timeout 120 "$CBIN" >"$C_DIR/log.txt" 2>&1 || true
    echo "[compare]   BMPs: $(ls "$C_DIR"/*.bmp 2>/dev/null | wc -l | xargs)"
    # Convert BMP -> PNG in parallel (sips, 8 concurrent).
    # macOS ships bash 3 which lacks `wait -n`; poll the job count instead.
    for f in "$C_DIR"/*.bmp; do
        sips -s format png "$f" --out "${f%.bmp}.png" >/dev/null 2>&1 &
        while [[ $(jobs -r -p | wc -l) -ge 8 ]]; do sleep 0.05; done
    done
    wait
    rm -f "$C_DIR"/*.bmp
    echo "[compare]   PNGs: $(ls "$C_DIR"/*.png | wc -l | xargs)"
fi

# 2. Extract Godot frames (dense: every 5 sim frames).
if [[ "${REUSE_GODOT:-0}" -ne 1 ]]; then
    echo "[compare] extracting Godot frames..."
    rm -f "$G_DIR"/*.png "$G_DIR"/parity.txt "$G_DIR"/log.txt
    GODOT_BIN="${GODOT_BIN:-$(command -v godot)}"
    GODOT_BIN="$(realpath "$GODOT_BIN")"
    RAPTOR_PLAYTHROUGH="$SCRIPT_PATH" \
    RAPTOR_PARITY_OUT="$G_DIR/parity.txt" \
    RAPTOR_TEST_FAST=1 \
    RAPTOR_SHOT_DIR="$G_DIR" \
    RAPTOR_SHOT_EVERY_FC=5 \
    "$GODOT_BIN" --path "$REPO" \
        --audio-driver Dummy \
        --position 99999,99999 --resolution 320x200 \
        --quit-after 20000 >"$G_DIR/log.txt" 2>&1
    echo "[compare]   frames: $(ls "$G_DIR"/*.png | wc -l | xargs)"
fi

# 3. Pair by mission_fc + tuned offset.
echo "[compare] pairing frames (offset=$OFFSET anchor=$ANCHOR skip=$SKIP_UNTIL_C)..."
python3 "$REPO/tests/pair_frames.py" \
    --c-dir "$C_DIR" --g-dir "$G_DIR" --out-dir "$PAIR_DIR" \
    --offset "$OFFSET" --anchor "$ANCHOR" --skip-until-c "$SKIP_UNTIL_C"

# 4. Encode each side then hstack.
echo "[compare] encoding videos @ ${FPS}fps..."
rm -f /tmp/c.mp4 /tmp/g.mp4 /tmp/sidebyside.mp4
ffmpeg -y -hide_banner -loglevel error \
    -framerate "$FPS" -i "$PAIR_DIR/c/%04d.png" \
    -vf "scale=640:400:flags=neighbor" \
    -c:v libx264 -pix_fmt yuv420p -preset fast /tmp/c.mp4
ffmpeg -y -hide_banner -loglevel error \
    -framerate "$FPS" -i "$PAIR_DIR/g/%04d.png" \
    -vf "scale=640:400:flags=neighbor" \
    -c:v libx264 -pix_fmt yuv420p -preset fast /tmp/g.mp4
ffmpeg -y -hide_banner -loglevel error \
    -i /tmp/c.mp4 -i /tmp/g.mp4 \
    -filter_complex "[0:v][1:v]hstack" \
    -c:v libx264 -pix_fmt yuv420p -preset fast /tmp/sidebyside.mp4

duration=$(ffprobe -v error -show_entries format=duration -of csv=p=0 /tmp/sidebyside.mp4)
echo "[compare] done: /tmp/sidebyside.mp4 (${duration}s)"
