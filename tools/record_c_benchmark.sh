#!/usr/bin/env bash
# tools/record_c_benchmark.sh — record a live C dosraptor play session as a
# complete Godot-parity benchmark: input (keys + player path), game state
# (objects / score / shields / bullets), every screen frame, and audio.
#
#   ./tools/record_c_benchmark.sh [out_dir]
#
# Play the game; CLOSE THE WINDOW when you're done (or die out). Then it
# compresses everything. Outputs in <out_dir>:
#   demos/wave*.json   per-wave input -> replay in Godot (RAPTOR_DEMO_PATH)
#   parity.ndjson      per-checkpoint game state (objects/score/shields/bullets)
#   video.mkv          every frame, lossless (FFV1) — extract any frame to compare
#   audio.ogg          the exact game audio
#
# RNG is forced deterministic so the same input reproduces in Godot.
set -euo pipefail

DOSRAPTOR="${DOSRAPTOR:-/Users/nadavb/dev/dosraptor}"
GODOT_REPO="$(cd "$(dirname "$0")/.." && pwd)"
CBIN="$DOSRAPTOR/build/raptor.app/Contents/MacOS/raptor"
OUT="${1:-$GODOT_REPO/benchmarks/bench_$(date +%Y%m%d_%H%M%S)}"

[ -x "$CBIN" ] || { echo "C binary not found/executable: $CBIN" >&2; exit 2; }
mkdir -p "$OUT/frames"
# MUST be absolute: the C binary chdir's into its asset dir (sys_main.c
# locate_assets_and_chdir), so a relative out-dir would make every RAPTOR_*_LOG
# path resolve against dosraptor/ and silently fail to write.
OUT="$(cd "$OUT" && pwd)"

echo "================================================================"
echo " Recording C play -> $OUT"
echo " Play now. CLOSE THE WINDOW when done. (deterministic RNG is on)"
echo "================================================================"

# Every frame is dumped (RAPTOR_DUMP_EVERY=1) then compressed to lossless FFV1 — the
# BMPs are transient scratch (deleted after compression below). NOTE: that scratch is
# uncompressed and dumped all at once, so a long multi-level run needs several GB free;
# record in chunks (RAPTOR_START_WAVE) if disk is tight. RAPTOR_START_WAVE (1-9) is
# inherited, so you can record a specific level directly, e.g.:
#   RAPTOR_START_WAVE=5 ./tools/record_c_benchmark.sh
RAPTOR_SKIPINTRO=1 \
RAPTOR_DETERMINISTIC_RNG=1 \
RAPTOR_INPUT_LOG="$OUT/input.rec" \
RAPTOR_LOADOUT_LOG="$OUT/loadout.json" \
RAPTOR_AUDIO_PCM="$OUT/audio.pcm" \
RAPTOR_DUMP_DIR="$OUT/frames" \
RAPTOR_DUMP_EVERY=1 \
RAPTOR_PARITY_OUT="$OUT/parity.ndjson" \
"$CBIN" || true

# Fail loudly if the capture wrote nothing usable (e.g. disk full, or the binary
# exited before a wave). The parity oracle + Godot demos come from these two; a
# missing/empty either means the recording is unusable — say so now, not later.
if [ ! -s "$OUT/parity.ndjson" ] || [ ! -s "$OUT/input.rec" ]; then
    echo "!! RECORDING FAILED: missing/empty $OUT/{parity.ndjson,input.rec}." >&2
    echo "!! (disk full? closed before entering a wave?) Nothing to diff — re-record." >&2
    exit 1
fi

echo "=== compressing (this can take a minute) ==="

# Audio: raw PCM (44.1k S16 stereo) -> OGG.
if [ -s "$OUT/audio.pcm" ]; then
    ffmpeg -y -f s16le -ar 44100 -ac 2 -i "$OUT/audio.pcm" \
        -c:a vorbis -strict experimental -q:a 4 "$OUT/audio.ogg" 2>/dev/null \
        && rm -f "$OUT/audio.pcm" \
        && echo "  audio.ogg  ($(du -h "$OUT/audio.ogg" | cut -f1))"
fi

# Pixels: every BMP frame -> lossless FFV1 video (pixel-exact, extractable).
if ls "$OUT/frames"/*_auto.bmp >/dev/null 2>&1; then
    ffmpeg -y -framerate 35 -pattern_type glob -i "$OUT/frames/*_auto.bmp" \
        -c:v ffv1 -level 3 "$OUT/video.mkv" 2>/dev/null \
        && rm -rf "$OUT/frames" \
        && echo "  video.mkv  ($(du -h "$OUT/video.mkv" | cut -f1), lossless every frame)"
fi

# Input: continuous .rec -> per-wave Godot demo JSON.
if [ -s "$OUT/input.rec" ]; then
    python3 "$GODOT_REPO/tools/rec_to_demo_json.py" "$OUT/input.rec" "$OUT/demos"
fi

echo "=== done ==="
du -sh "$OUT" 2>/dev/null
echo "Replay a wave in Godot:  RAPTOR_DEMO_PATH=$OUT/demos/wave01.json  (then the 'D' demo trigger)"
