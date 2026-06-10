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
# Each scenario runs in a hermetic temp dir (GLB symlinks + SETUP.INI + the
# scenario's pilot fixture). Your real saved pilots in the dosraptor dir are
# never read or modified. See the staging block below for why.
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
askbool_yes_no_quit_save \
popup_winmsg_credits_dismiss \
load_navigate_and_load}"

if [[ ! -x "$CBIN" ]]; then
    echo "[capture] ERROR: C binary not found/executable: $CBIN" >&2
    echo "[capture] build it with: (cd $DOS && cmake --build build)" >&2
    exit 2
fi
mkdir -p "$OUT"
echo "[capture] C binary: $CBIN"
echo "[capture] capturing ${SCRIPTS} " | tr ' ' '\n' | head -1

# Hermetic pilot state. C does NOT honor RAPTOR_SAVE_DIR; it reads pilots as
# CHAR%04u.FIL from its working dir, and sys_main's locate_assets_and_chdir()
# checks cwd FIRST for FILE0000.GLB (only walking to the exe dir if cwd lacks
# it). So we run C from a staged temp dir holding GLB symlinks + a SETUP.INI copy
# (for key bindings) + the scenario's pilot fixture (if any) — C stays in that
# dir and its CHAR lookup is hermetic. The user's REAL CHAR*.FIL in $DOS are
# never read or touched. A scenario with no fixture → empty dir → "No Pilots".
shopt -s nullglob
glbs=("$DOS"/FILE*.GLB)
shopt -u nullglob
if [[ ${#glbs[@]} -eq 0 ]]; then
    echo "[capture] ERROR: no FILE*.GLB in $DOS — C cannot boot without its assets" >&2
    exit 2
fi

ok=0; bad=0
for name in $SCRIPTS; do
    s="$DOS/tests/scripts/$name.txt"
    if [[ ! -f "$s" ]]; then echo "[capture] skip $name (no script)"; continue; fi

    # Stage the hermetic run dir for this scenario.
    run="$(mktemp -d)"
    for g in "${glbs[@]}"; do ln -s "$g" "$run/$(basename "$g")"; done
    if [[ -f "$DOS/SETUP.INI" ]]; then cp "$DOS/SETUP.INI" "$run/SETUP.INI"; cp "$DOS/SETUP.INI" "$run/setup.ini"; fi
    fix="$REPO/tests/parity/menu_fixtures/$name"
    if [[ -d "$fix" ]]; then cp "$fix"/CHAR*.FIL "$run"/ 2>/dev/null || true; fi

    a="$(mktemp)"; b="$(mktemp)"
    for f in "$a" "$b"; do
        ( cd "$run" && SDL_AUDIODRIVER=dummy RAPTOR_SKIPINTRO=1 \
          RAPTOR_PLAYTHROUGH="$s" RAPTOR_MENU_OUT="$f" \
            timeout 120 "$CBIN" >/dev/null 2>&1 ) || true
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
    rm -rf "$run"
done
echo "[capture] done: $ok ok, $bad needs-attention. Goldens in $OUT"
echo "[capture] commit with: git add tests/parity/c_menu_goldens/*.menu.ndjson && git commit"
[[ "$bad" -eq 0 ]]
