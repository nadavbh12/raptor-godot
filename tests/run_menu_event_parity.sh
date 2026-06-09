#!/usr/bin/env bash
# tests/run_menu_event_parity.sh [OPTIONS] <script-name>
#
# Run a single menu-EVENT parity test: launch Godot with a playthrough script,
# emit one row per processed menu input event (RAPTOR_MENU_OUT), and compare
# the stream against the committed C golden via parity_diff.py --menu.
#
# This is the keyboard/menu analogue of run_l2a.sh (which covers in-game
# checkpoints). The golden is produced by the C reference's per-keypress hook
# in WIN_MainMenu's SWD_Dialog loop (raptor_parity_menu_event).
#
# Arguments:
#   <script-name>  Base name (no extension), e.g. "menu_main_nav".
#
# Options:
#   --keep-output  Do not delete the temp dir; print its path for inspection.
#   --regen-c      Regenerate the committed C golden from the C binary first
#                  (requires $DOSRAPTOR build). Use after changing the script.
#
# Environment:
#   DOSRAPTOR      Path to the dosraptor repo (default: ../dosraptor).
#   CBIN           C binary (default: $DOSRAPTOR/build/raptor.app/.../raptor).
#
# Exit codes:
#   0  PASS
#   1  FAIL (comparator failed or godot run failed)
#   2  Error (missing files / tools)
#
# NOTE: menu scripts MUST leave generous (~30-frame) gaps between keys. C paces
# menu input by the wall-clock DOS timer while SWD_Dialog consumes SDL events in
# a wall-clock busy-spin; keys a few frames apart race that spin and register
# nondeterministically. ~30 frames (human keypress pace) makes the C golden
# stable. See tests/scripts (dosraptor) menu_main_nav.txt header.

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
KEEP_OUTPUT=0
REGEN_C=0
NAME="menu_main_nav"

for arg in "$@"; do
    case "$arg" in
        --keep-output) KEEP_OUTPUT=1 ;;
        --regen-c)     REGEN_C=1 ;;
        -*) echo "unknown option: $arg" >&2; exit 2 ;;
        *) NAME="$arg" ;;
    esac
done

DOSRAPTOR="${DOSRAPTOR:-$(cd "$REPO/.." && pwd)/dosraptor}"
SCRIPT="$DOSRAPTOR/tests/scripts/$NAME.txt"
GOLDEN="$REPO/tests/parity/c_menu_goldens/$NAME.menu.ndjson"
CBIN="${CBIN:-$DOSRAPTOR/build/raptor.app/Contents/MacOS/raptor}"

if [[ ! -f "$SCRIPT" ]]; then
    echo "[menu_event] ERROR: script not found: $SCRIPT" >&2
    exit 2
fi

if ! command -v godot >/dev/null 2>&1; then
    echo "[menu_event] ERROR: godot not on PATH" >&2
    exit 2
fi
GODOT_BIN="$(realpath "$(command -v godot)")"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(dirname "$(realpath "$(command -v dotnet)")")")}"

OUT="$(mktemp -d -t raptor_menu_event)"
if [[ "$KEEP_OUTPUT" -eq 0 ]]; then
    trap 'rm -rf "$OUT"' EXIT
else
    echo "[menu_event] keeping output dir: $OUT"
fi

echo "[menu_event] script: $SCRIPT"
echo "[menu_event] golden: $GOLDEN"

# Optionally regenerate the committed C golden from the C reference binary.
# C's menu input is wall-clock-paced; a single run with the ~30-frame-gap
# script is deterministic (verified byte-identical across runs).
if [[ "$REGEN_C" -eq 1 ]]; then
    if [[ ! -x "$CBIN" ]]; then
        echo "[menu_event] ERROR: --regen-c needs C binary: $CBIN" >&2
        exit 2
    fi
    echo "[menu_event] regenerating C golden from $CBIN ..."
    SDL_AUDIODRIVER=dummy RAPTOR_SKIPINTRO=1 \
    RAPTOR_PLAYTHROUGH="$SCRIPT" \
    RAPTOR_MENU_OUT="$GOLDEN" \
    timeout 120 "$CBIN" >"$OUT/c.log" 2>&1 || true
    if [[ ! -s "$GOLDEN" ]]; then
        echo "[menu_event] FAIL: C produced no golden; see $OUT/c.log" >&2
        tail -20 "$OUT/c.log" >&2
        exit 1
    fi
fi

if [[ ! -f "$GOLDEN" ]]; then
    echo "[menu_event] ERROR: golden not found: $GOLDEN (run with --regen-c)" >&2
    exit 2
fi

echo "[menu_event] building..."
dotnet build "$REPO/raptor.csproj" --nologo --verbosity quiet 2>&1

echo "[menu_event] running godot..."
env RAPTOR_PLAYTHROUGH="$SCRIPT" \
    RAPTOR_MENU_OUT="$OUT/godot.menu.ndjson" \
    RAPTOR_TEST_FAST=1 \
    RAPTOR_DETERMINISTIC_RNG=1 \
    "$GODOT_BIN" --path "$REPO" --headless \
        --quit-after "${RAPTOR_MAX_FRAMES:-2000}" \
        --audio-driver Dummy \
        >"$OUT/godot.log" 2>&1 || {
    echo "[menu_event] FAIL: godot run failed; see $OUT/godot.log"
    tail -30 "$OUT/godot.log" >&2
    exit 1
}

if [[ ! -s "$OUT/godot.menu.ndjson" ]]; then
    echo "[menu_event] FAIL: godot produced no menu events (empty file)"
    tail -30 "$OUT/godot.log" >&2
    exit 1
fi

echo "[menu_event] godot output:"
cat "$OUT/godot.menu.ndjson"
echo ""
echo "[menu_event] C golden:"
cat "$GOLDEN"
echo ""

echo "[menu_event] running comparator (--menu)..."
python3 "$REPO/tests/comparator/parity_diff.py" --menu \
    --c-golden "$GOLDEN" \
    --godot-out "$OUT/godot.menu.ndjson"
