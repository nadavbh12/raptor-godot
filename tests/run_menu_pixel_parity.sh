#!/usr/bin/env bash
# Capture labeled C and Godot menu screenshots and compare them pixel-by-pixel.
#
# Outputs:
#   dumps/menu_pixel_parity_<timestamp>/
#     c/<script>/*.png
#     godot/<script>/*.png
#     diff/<script>/*.diff.png
#     diff/<script>/*.panel.png
#     diff/<script>/summary.json
#
# Env:
#   DOSRAPTOR             path to C repo (default ../dosraptor)
#   GODOT_BIN             Godot executable (default command -v godot)
#   SCRIPTS               space-separated script names
#   TOLERANCE             per-channel max-delta tolerance (default 8)
#   MAX_MISMATCH_PCT      full-screen mismatch budget (default 40)
#   NO_FAIL               set 1 to always exit 0 after writing diffs
#   OUT_ROOT              output dir override
#   C_CAPTURE_ROOT        reuse existing C captures from $C_CAPTURE_ROOT/<script>
#   REUSE_C               set 1 to reuse $OUT_ROOT/c/<script>
#   LABELS_<script>       optional space-separated label allow-list for script

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
DOSRAPTOR="${DOSRAPTOR:-$(cd "$REPO/.." && pwd)/dosraptor}"
GODOT_BIN="${GODOT_BIN:-$(command -v godot)}"
GODOT_BIN="$(realpath "$GODOT_BIN")"

# Godot Mono's hostfxr needs a .NET 8 runtime. macOS brew often has only
# the latest .NET linked (e.g. 10.x), which makes Godot crash at startup
# with ".NET: Assemblies not found". Point DOTNET_ROOT at the keg-only
# dotnet@8 install when present, otherwise leave it for the caller to set.
if [[ -z "${DOTNET_ROOT:-}" ]] && [[ -d /opt/homebrew/opt/dotnet@8/libexec ]]; then
    export DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec
fi
SCRIPTS="${SCRIPTS:-credits help_f1 new_mission mission_start}"
TOLERANCE="${TOLERANCE:-8}"
MAX_MISMATCH_PCT="${MAX_MISMATCH_PCT:-40}"
STAMP="$(date +%Y%m%d_%H%M%S)"
OUT_ROOT="${OUT_ROOT:-$REPO/dumps/menu_pixel_parity_$STAMP}"
CBIN="$DOSRAPTOR/build/raptor.app/Contents/MacOS/raptor"

mkdir -p "$OUT_ROOT"
dotnet build "$REPO/raptor.csproj" --nologo --verbosity quiet >/dev/null

overall=0
for script in $SCRIPTS; do
    script_path="$DOSRAPTOR/tests/scripts/$script.txt"
    if [[ ! -f "$script_path" ]]; then
        echo "[menu_pixel] missing script: $script_path" >&2
        overall=2
        continue
    fi

    if [[ -n "${C_CAPTURE_ROOT:-}" ]]; then
        c_dir="$C_CAPTURE_ROOT/$script"
    else
        c_dir="$OUT_ROOT/c/$script"
    fi
    g_dir="$OUT_ROOT/godot/$script"
    d_dir="$OUT_ROOT/diff/$script"
    mkdir -p "$c_dir" "$g_dir" "$d_dir"
    if [[ "${REUSE_C:-0}" != "1" && -z "${C_CAPTURE_ROOT:-}" ]]; then
        rm -f "$c_dir"/*
    fi
    rm -f "$g_dir"/* "$d_dir"/*

    if [[ "${REUSE_C:-0}" == "1" || -n "${C_CAPTURE_ROOT:-}" ]]; then
        echo "[menu_pixel] C capture: $script (reusing $c_dir)"
        if ! ls "$c_dir"/*.png >/dev/null 2>&1; then
            echo "[menu_pixel] no reusable C pngs in $c_dir" >&2
            overall=2
            continue
        fi
    else
        echo "[menu_pixel] C capture: $script"
        SDL_AUDIODRIVER=dummy \
        RAPTOR_SKIPINTRO=1 \
        RAPTOR_PLAYTHROUGH="$script_path" \
        RAPTOR_DUMP_DIR="$c_dir" \
        timeout 120 "$CBIN" >"$c_dir/run.log" 2>&1 || true
        for bmp in "$c_dir"/*.bmp; do
            [[ -e "$bmp" ]] || continue
            sips -s format png "$bmp" --out "${bmp%.bmp}.png" >/dev/null 2>&1
            rm -f "$bmp"
        done
    fi

    echo "[menu_pixel] Godot capture: $script"
    # --position pushes the window off-screen; --display-driver headless
    # would prevent the focus steal entirely but disables GetViewport
    # rendering, breaking the screenshots, so we keep a real window.
    # NOTE: a comment between these env vars and the godot command will
    # be joined by the trailing backslash, swallowing every env assignment
    # via `#` — keep comments above the block, not in it.
    RAPTOR_PLAYTHROUGH="$script_path" \
    RAPTOR_TEST_FAST=1 \
    RAPTOR_RENDER_MENUS=1 \
    RAPTOR_SHOT_DIR="$g_dir" \
    RAPTOR_SHOT_BURST=0 \
    "$GODOT_BIN" --path "$REPO" \
        --audio-driver Dummy \
        --position 99999,99999 --resolution 320x200 \
        --quit-after 20000 >"$g_dir/run.log" 2>&1

    echo "[menu_pixel] compare: $script"
    extra=()
    if [[ "${NO_FAIL:-0}" == "1" ]]; then
        extra+=(--no-fail)
    fi
    label_var="LABELS_${script}"
    if [[ -n "${!label_var:-}" ]]; then
        for label in ${!label_var}; do
            extra+=(--label "$label")
        done
    fi
    if ! python3 "$REPO/tests/menu_pixel_parity.py" \
        --c-dir "$c_dir" \
        --godot-dir "$g_dir" \
        --out-dir "$d_dir" \
        --tolerance "$TOLERANCE" \
        --max-mismatch-pct "$MAX_MISMATCH_PCT" \
        "${extra[@]}"; then
        overall=1
    fi
done

echo "[menu_pixel] artifacts: $OUT_ROOT"
exit "$overall"
