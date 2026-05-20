#!/usr/bin/env bash
# Record an interactive C dosraptor play session with visual and object-dump
# artifacts suitable for later Godot comparison.

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
DOSRAPTOR="${DOSRAPTOR:-$(cd "$REPO/.." && pwd)/dosraptor}"
CBIN="${CBIN:-$DOSRAPTOR/build/raptor.app/Contents/MacOS/raptor}"

RUN_ID="${1:-manual_c_$(date +%Y%m%d_%H%M%S)}"
OUT_ROOT="${OUT_ROOT:-$REPO/dumps/c_seed_runs}"
OUT_DIR="$OUT_ROOT/$RUN_ID"
FRAMES_DIR="$OUT_DIR/frames"

DUMP_EVERY="${DUMP_EVERY:-5}"
ENCODE_MP4="${ENCODE_MP4:-1}"
KEEP_BMPS="${KEEP_BMPS:-0}"
FPS="${FPS:-24}"

if [[ ! -x "$CBIN" ]]; then
    echo "[c-seed] ERROR: C binary not executable: $CBIN" >&2
    echo "[c-seed] Build dosraptor first, or pass CBIN=/path/to/raptor." >&2
    exit 2
fi

mkdir -p "$FRAMES_DIR"

cat >"$OUT_DIR/README.md" <<EOF
# C Seed Run: $RUN_ID

Interactive C dosraptor capture for visual parity seeding.

- Created: $(date -u +"%Y-%m-%dT%H:%M:%SZ")
- C repo: $DOSRAPTOR
- Binary: $CBIN
- Frame dump cadence: every $DUMP_EVERY presents
- Frames: frames/
- Parity stream: parity.ndjson
- Position/object stream: positions.txt
- Bullet/enemy stream: bullets.txt
- Bonus stream: bonus.txt
- Process log: c.log

After the game exits, this script writes manifest.json and, when ENCODE_MP4=1,
attempts to encode c_capture.mp4 from the captured frame sequence.
EOF

cat >"$OUT_DIR/manifest.json" <<EOF
{
  "id": "$RUN_ID",
  "kind": "interactive-c-seed",
  "status": "running",
  "created_utc": "$(date -u +"%Y-%m-%dT%H:%M:%SZ")",
  "dosraptor": "$DOSRAPTOR",
  "binary": "$CBIN",
  "dump_every": $DUMP_EVERY,
  "artifacts": {
    "frames_dir": "frames",
    "parity": "parity.ndjson",
    "positions": "positions.txt",
    "bullets": "bullets.txt",
    "bonus": "bonus.txt",
    "log": "c.log",
    "video": "c_capture.mp4"
  }
}
EOF

echo "[c-seed] Run id: $RUN_ID"
echo "[c-seed] Output: $OUT_DIR"
echo "[c-seed] Launching C dosraptor interactively."
echo "[c-seed] Play normally. Quit the game when done; post-processing starts after exit."
echo "[c-seed] Frame cadence: RAPTOR_DUMP_EVERY=$DUMP_EVERY. Set DUMP_EVERY=1 for dense capture."

set +e
(
    cd "$DOSRAPTOR"
    RAPTOR_SKIPINTRO="${RAPTOR_SKIPINTRO:-1}" \
    RAPTOR_DUMP_DIR="$FRAMES_DIR" \
    RAPTOR_DUMP_EVERY="$DUMP_EVERY" \
    RAPTOR_DUMP_KEY="${RAPTOR_DUMP_KEY:-1}" \
    RAPTOR_PARITY_OUT="$OUT_DIR/parity.ndjson" \
    RAPTOR_POS_DUMP="$OUT_DIR/positions.txt" \
    RAPTOR_BULLET_DUMP="$OUT_DIR/bullets.txt" \
    RAPTOR_BONUS_DUMP="$OUT_DIR/bonus.txt" \
    "$CBIN"
) >"$OUT_DIR/c.log" 2>&1
RC=$?
set -e

BMP_COUNT=$(find "$FRAMES_DIR" -maxdepth 1 -name '*.bmp' -type f | wc -l | tr -d ' ')
PNG_COUNT=$(find "$FRAMES_DIR" -maxdepth 1 -name '*.png' -type f | wc -l | tr -d ' ')

if [[ "$ENCODE_MP4" -eq 1 && "$BMP_COUNT" -gt 0 ]]; then
    if command -v ffmpeg >/dev/null 2>&1; then
        echo "[c-seed] Encoding c_capture.mp4 from $BMP_COUNT BMP frames..."
        ffmpeg -y -hide_banner -loglevel error \
            -framerate "$FPS" -pattern_type glob -i "$FRAMES_DIR/*.bmp" \
            -vf "scale=640:400:flags=neighbor" \
            -c:v libx264 -pix_fmt yuv420p -preset fast "$OUT_DIR/c_capture.mp4" || true
    else
        echo "[c-seed] ffmpeg not found; skipping MP4 encode." >&2
    fi
fi

if [[ "$KEEP_BMPS" -eq 0 && "$BMP_COUNT" -gt 0 ]]; then
    if command -v sips >/dev/null 2>&1; then
        echo "[c-seed] Converting BMP frames to PNG and removing BMPs..."
        while IFS= read -r f; do
            sips -s format png "$f" --out "${f%.bmp}.png" >/dev/null 2>&1 &
            while [[ $(jobs -r -p | wc -l) -ge 8 ]]; do sleep 0.05; done
        done < <(find "$FRAMES_DIR" -maxdepth 1 -name '*.bmp' -type f | sort)
        wait
        find "$FRAMES_DIR" -maxdepth 1 -name '*.bmp' -type f -delete
        BMP_COUNT=0
        PNG_COUNT=$(find "$FRAMES_DIR" -maxdepth 1 -name '*.png' -type f | wc -l | tr -d ' ')
    else
        echo "[c-seed] sips not found; keeping BMP frames." >&2
    fi
fi

count_lines() {
    local path="$1"
    if [[ ! -f "$path" ]]; then
        echo 0
        return
    fi
    wc -l <"$path" | tr -d ' '
}

PARITY_LINES=$(count_lines "$OUT_DIR/parity.ndjson")
POSITION_LINES=$(count_lines "$OUT_DIR/positions.txt")
BULLET_LINES=$(count_lines "$OUT_DIR/bullets.txt")
BONUS_LINES=$(count_lines "$OUT_DIR/bonus.txt")
VIDEO_PRESENT=false
if [[ -f "$OUT_DIR/c_capture.mp4" ]]; then
    VIDEO_PRESENT=true
fi

python3 - "$OUT_DIR/manifest.json" "$RC" "$BMP_COUNT" "$PNG_COUNT" "$PARITY_LINES" "$POSITION_LINES" "$BULLET_LINES" "$BONUS_LINES" "$VIDEO_PRESENT" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
data = json.loads(path.read_text())
data["status"] = "complete" if sys.argv[2] == "0" else "exited-nonzero"
data["exit_code"] = int(sys.argv[2])
data["completed_utc"] = __import__("datetime").datetime.now(__import__("datetime").timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
data["counts"] = {
    "bmp_frames": int(sys.argv[3]),
    "png_frames": int(sys.argv[4]),
    "parity_lines": int(sys.argv[5]),
    "position_lines": int(sys.argv[6]),
    "bullet_lines": int(sys.argv[7]),
    "bonus_lines": int(sys.argv[8]),
}
data["artifacts"]["video_present"] = sys.argv[9] == "true"
path.write_text(json.dumps(data, indent=2) + "\n")
PY

echo "[c-seed] Done."
echo "[c-seed] Output: $OUT_DIR"
echo "[c-seed] Frames: BMP=$BMP_COUNT PNG=$PNG_COUNT"
echo "[c-seed] Lines: parity=$PARITY_LINES positions=$POSITION_LINES bullets=$BULLET_LINES bonus=$BONUS_LINES"
if [[ "$VIDEO_PRESENT" == "true" ]]; then
    echo "[c-seed] Video: $OUT_DIR/c_capture.mp4"
fi
exit "$RC"
