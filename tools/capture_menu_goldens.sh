#!/usr/bin/env bash
# Batch-capture the menu-event C goldens for the keyboard scenarios.
#
# RUN THIS WHEN AWAY FROM THE SCREEN: each scenario launches a windowed C run
# (the C reference needs a real SDL renderer, so it steals window focus). The
# runs are back-to-back and unattended. Each golden is captured twice and kept
# only if the two runs are byte-identical (menu input must be deterministic at
# the scripts' 30-frame gaps — see the script headers).
#
# Output: tests/parity/c_menu_goldens/<name>.menu.ndjson (committed afterwards).
#
# Env:
#   DOSRAPTOR  path to the C repo (default ../dosraptor)
#   CBIN       C binary (default $DOSRAPTOR/build/raptor.app/Contents/MacOS/raptor)
#   SCRIPTS    space-separated scenario base names (default: the keyboard set)
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
DOS="${DOSRAPTOR:-$(cd "$REPO/.." && pwd)/dosraptor}"
CBIN="${CBIN:-$DOS/build/raptor.app/Contents/MacOS/raptor}"
OUT="$REPO/tests/parity/c_menu_goldens"

# Explicit list (avoids sweeping in non-menu-event scripts like menu_demo.txt).
# load_navigate_and_load and popup_winmsg_credits_dismiss are deferred (Phase 1b).
SCRIPTS="${SCRIPTS:-\
menu_main_nav \
menu_nav_all_buttons \
menu_help_f1 \
menu_options_volume_detail_exit \
register_help_f1 \
askdiff_select_each_difficulty \
askdiff_abort \
hangar_nav_mission_supplies \
hangar_help_save \
store_buy_sell_navigate \
store_help_f1 \
help_paging_keys \
help_onscreen_buttons \
shipcomp_auto_confirm_sector \
askbool_yes_no_quit_save}"

if [[ ! -x "$CBIN" ]]; then
    echo "[capture] ERROR: C binary not found/executable: $CBIN" >&2
    echo "[capture] build it with: (cd $DOS && cmake --build build)" >&2
    exit 2
fi
mkdir -p "$OUT"
echo "[capture] C binary: $CBIN"
echo "[capture] capturing ${SCRIPTS} " | tr ' ' '\n' | head -1

ok=0; bad=0
for name in $SCRIPTS; do
    s="$DOS/tests/scripts/$name.txt"
    if [[ ! -f "$s" ]]; then echo "[capture] skip $name (no script)"; continue; fi
    a="$(mktemp)"; b="$(mktemp)"
    for f in "$a" "$b"; do
        SDL_AUDIODRIVER=dummy RAPTOR_SKIPINTRO=1 \
        RAPTOR_PLAYTHROUGH="$s" RAPTOR_MENU_OUT="$f" \
            timeout 120 "$CBIN" >/dev/null 2>&1 || true
    done
    if [[ ! -s "$a" ]]; then
        echo "[capture] EMPTY  $name (no rows — check the script reaches a hooked screen)"; bad=$((bad+1))
    elif cmp -s "$a" "$b"; then
        cp "$a" "$OUT/$name.menu.ndjson"
        echo "[capture] OK     $name ($(wc -l <"$a" | tr -d ' ') rows)"; ok=$((ok+1))
    else
        echo "[capture] UNSTABLE $name — two runs differ; widen the script's waits"; bad=$((bad+1))
    fi
    rm -f "$a" "$b"
done
echo "[capture] done: $ok ok, $bad needs-attention. Goldens in $OUT"
echo "[capture] commit with: git add tests/parity/c_menu_goldens/*.menu.ndjson && git commit"
[[ "$bad" -eq 0 ]]
