#!/usr/bin/env bash
# tests/replay_live_menu_recording.sh <menu.ndjson>
#
# Diff a LIVE C menu recording (menu.ndjson, captured by tools/record_c_benchmark.sh
# via RAPTOR_MENU_OUT) against Godot: convert it to a Godot playthrough script, replay
# that headless, and compare Godot's menu-event stream back to the recording.
#
#   tools/record_c_benchmark.sh        # produces <bench>/menu.ndjson (the C golden)
#   tests/replay_live_menu_recording.sh <bench>/menu.ndjson
#
# Exit 0 iff every menu event matches (win/selected_item/event_index/kind).
# The converter exits non-zero if the recording has events it cannot replay (mouse
# clicks, or scancodes not yet mapped) — we surface that and stop, rather than diff a
# knowingly-incomplete script.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
GODOT_BIN="${GODOT_BIN:-$HOME/.local/bin/godot}"
MENU="${1:?usage: replay_live_menu_recording.sh <menu.ndjson>}"
[ -s "$MENU" ] || { echo "menu recording not found / empty: $MENU" >&2; exit 2; }

OUT="$(mktemp -d)"
SAVE="$(mktemp -d)"
trap 'rm -rf "$OUT" "$SAVE"' EXIT

# 1. Convert the recorded menu events -> a Godot playthrough script.
if ! python3 "$REPO/tools/ndjson_to_menu_playthrough.py" \
        --input "$MENU" --output "$OUT/replay.txt"; then
    echo "[replay-menu] converter reported unreplayable events (above) — fix the" >&2
    echo "[replay-menu] mapping or re-record keyboard-only, then retry." >&2
    exit 1
fi

# 2. Replay headless, emitting Godot's menu-event stream. Hermetic empty save dir so
#    the LOAD menu doesn't branch on stray pilot files (must match the recording's
#    save state — record with an empty save dir for a clean diff).
env RAPTOR_PLAYTHROUGH="$OUT/replay.txt" \
    RAPTOR_MENU_OUT="$OUT/godot.menu.ndjson" \
    RAPTOR_SAVE_DIR="$SAVE" \
    RAPTOR_TEST_FAST=1 \
    RAPTOR_DETERMINISTIC_RNG=1 \
    "$GODOT_BIN" --path "$REPO" --headless \
        --quit-after "${RAPTOR_MAX_FRAMES:-4000}" --audio-driver Dummy \
        >"$OUT/godot.log" 2>&1 || true

[ -s "$OUT/godot.menu.ndjson" ] || {
    echo "[replay-menu] Godot produced no menu events — see $OUT/godot.log" >&2
    cp "$OUT/godot.log" /tmp/replay_menu_godot.log 2>/dev/null || true
    exit 1
}

# 3. Diff Godot's replay against the live recording.
python3 "$REPO/tests/comparator/parity_diff.py" --menu \
    --c-golden "$MENU" --godot-out "$OUT/godot.menu.ndjson"
