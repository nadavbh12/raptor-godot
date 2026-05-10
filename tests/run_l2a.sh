#!/usr/bin/env bash
# tests/run_l2a.sh [OPTIONS] <script-name>
#
# Run a single L2a parity test: launch Godot with a playthrough script,
# compare the output against the C golden.
#
# Arguments:
#   <script-name>  Base name (no extension), e.g. "credits"
#
# Options:
#   --keep-output  Do not delete the temp dir; print its path for inspection.
#
# Environment:
#   DOSRAPTOR      Path to the dosraptor repo (default: ../dosraptor)
#
# Exit codes:
#   0  PASS
#   1  FAIL (comparator failed or godot run failed)
#   2  Error (missing files)

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
KEEP_OUTPUT=0
NAME="credits"

for arg in "$@"; do
    case "$arg" in
        --keep-output) KEEP_OUTPUT=1 ;;
        -*) echo "unknown option: $arg" >&2; exit 2 ;;
        *) NAME="$arg" ;;
    esac
done

DOSRAPTOR="${DOSRAPTOR:-$(cd "$REPO/.." && pwd)/dosraptor}"
SCRIPT="$DOSRAPTOR/tests/scripts/$NAME.txt"
GOLDEN="$REPO/tests/parity/scripts/$NAME.parity.txt"

if [[ ! -f "$SCRIPT" ]]; then
    echo "[run_l2a] ERROR: script not found: $SCRIPT" >&2
    exit 2
fi
if [[ ! -f "$GOLDEN" ]]; then
    echo "[run_l2a] ERROR: golden not found: $GOLDEN" >&2
    exit 2
fi

OUT="$(mktemp -d -t raptor_l2a)"
if [[ "$KEEP_OUTPUT" -eq 0 ]]; then
    trap 'rm -rf "$OUT"' EXIT
else
    echo "[run_l2a] keeping output dir: $OUT"
fi

echo "[run_l2a] script: $SCRIPT"
echo "[run_l2a] golden: $GOLDEN"
echo "[run_l2a] output: $OUT/godot.parity.txt"

if ! command -v godot >/dev/null 2>&1; then
    echo "[run_l2a] ERROR: godot not on PATH" >&2
    exit 2
fi

# Resolve the real binary path. On macOS, using the symlink can trigger a slow
# Gatekeeper re-scan of the app bundle; using the resolved path avoids that
# and keeps startup time well under 5 seconds.
GODOT_BIN="$(realpath "$(command -v godot)")"
echo "[run_l2a] godot: $GODOT_BIN"

export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(dirname "$(realpath "$(command -v dotnet)")")")}"

# Build first so Godot doesn't compile on first run.
echo "[run_l2a] building..."
dotnet build "$REPO/raptor.csproj" --nologo --verbosity quiet 2>&1

# Calculate max frames: allow enough for the script plus a generous buffer.
# credits.txt total: wait 60 + (key+wait)x4 + key + wait 200 + key + wait 100 = ~385 frames.
# Add buffer: 1000 frames. At 70fps fast mode, this finishes in under a second.
MAX_FRAMES=2000

echo "[run_l2a] running godot..."
RAPTOR_PLAYTHROUGH="$SCRIPT" \
RAPTOR_PARITY_OUT="$OUT/godot.parity.txt" \
RAPTOR_TEST_FAST=1 \
"$GODOT_BIN" --path "$REPO" --headless --quit-after $MAX_FRAMES \
    >"$OUT/godot.log" 2>&1 || {
    echo "[run_l2a] FAIL: godot run failed; see $OUT/godot.log"
    tail -30 "$OUT/godot.log" >&2
    exit 1
}

if [[ ! -s "$OUT/godot.parity.txt" ]]; then
    echo "[run_l2a] FAIL: godot produced no output (empty parity file)"
    echo "[run_l2a] godot log tail:"
    tail -30 "$OUT/godot.log" >&2
    exit 1
fi

echo "[run_l2a] godot output:"
cat "$OUT/godot.parity.txt"
echo ""

echo "[run_l2a] C golden:"
cat "$GOLDEN"
echo ""

echo "[run_l2a] running comparator..."
python3 "$REPO/tests/comparator/parity_diff.py" \
    --c-golden "$GOLDEN" \
    --godot-out "$OUT/godot.parity.txt"
