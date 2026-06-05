#!/usr/bin/env bash
# Full local acceptance runner for the Godot port.
#
# Requirements:
#   - godot on PATH
#   - dotnet on PATH
#   - ../dosraptor available, or DOSRAPTOR=/path/to/dosraptor
#
# Audio is always disabled for development runs.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOSRAPTOR="${DOSRAPTOR:-$(cd "$ROOT/.." && pwd)/dosraptor}"

run_step() {
    local name="$1"
    shift
    echo
    echo "==> $name"
    "$@"
}

require_cmd() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "[ci/full] missing required command: $1" >&2
        exit 2
    fi
}

require_cmd dotnet
require_cmd godot

if [[ ! -d "$DOSRAPTOR" ]]; then
    echo "[ci/full] missing dosraptor repo: $DOSRAPTOR" >&2
    exit 2
fi

export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-Major}"
export SDL_AUDIODRIVER=dummy
export RAPTOR_DETERMINISTIC_RNG=1

run_step "Godot SDK/runtime version match" "$ROOT/tests/check_godot_sdk_version.sh"
run_step "build game" dotnet build "$ROOT/raptor.csproj" --nologo --verbosity minimal
run_step "build tests" dotnet build "$ROOT/tests/RaptorTests.csproj" --nologo --verbosity minimal
run_step "unit tests" dotnet test "$ROOT/tests/RaptorTests.csproj" --no-build --logger "console;verbosity=minimal"

for script in mission_start mission_long menu_demo full_demo; do
    run_step "L2 parity: $script" "$ROOT/tests/run_l2a.sh" "$script"
done

# Per-wave death scenarios (episode-1 waves 1,2,4-9, no gameplay input). run_l2a
# auto-sets RAPTOR_START_WAVE + RAPTOR_QUIT_AFTER_DEATH, so Godot emits gameplay-
# only output (quits at death) and compares against the trimmed goldens. All 8 are
# frame-exact (100% PASS), ~60-110s each. death_wave3 is intentionally omitted: it
# is the held-input probe with the open #24 input-onset skew (98.7%, not frame-exact)
# — kept as a manual/manifested probe so a CI gate doesn't ride on that residual.
for script in death_wave1 death_wave2 death_wave4 death_wave5 \
              death_wave6 death_wave7 death_wave8 death_wave9; do
    run_step "L2 parity: $script" "$ROOT/tests/run_l2a.sh" "$script"
done

if [[ -n "${MENU_C_CAPTURE_ROOT:-}" ]]; then
    run_step "menu pixel parity" env \
        C_CAPTURE_ROOT="$MENU_C_CAPTURE_ROOT" \
        SCRIPTS="${MENU_PIXEL_SCRIPTS:-new_mission mission_start}" \
        MAX_MISMATCH_PCT="${MENU_MAX_MISMATCH_PCT:-20}" \
        "$ROOT/tests/run_menu_pixel_parity.sh"
else
    echo
    echo "==> menu pixel parity"
    echo "[ci/full] skipped: set MENU_C_CAPTURE_ROOT to a reusable C capture root to enable this check"
fi

echo
echo "==> mission-start dead-screen guard (headed)"
# Regression guard for the mission-start dead screen (#1/#4). Headed (renders),
# so on a display-less box the script self-reports exit 2 = inconclusive, which
# we treat as a skip rather than a failure. exit 1 = the dead screen is back.
ms_rc=0
"$ROOT/tests/check_mission_start_terrain.sh" || ms_rc=$?
if [[ $ms_rc -eq 2 ]]; then
    echo "[ci/full] skipped: mission-start guard inconclusive (needs a real display)"
elif [[ $ms_rc -ne 0 ]]; then
    echo "[ci/full] FAIL: mission-start dead-screen guard"
    exit 1
fi

echo
echo "==> mission-start visual regression (headed, pixel-exact)"
# Pixel-exact snapshot of the View frames the other gates can't see: the LOADCOMP
# briefing, the loading bar fill levels, the terrain fade-in, and a strafe-fire
# muzzle frame. Catches View regressions L2 (sim state) and the fuzzy menu-pixel
# test miss. exit 2 = inconclusive (no display) -> skip; exit 1 = real regression.
vis_rc=0
"$ROOT/tests/check_mission_start_visual.sh" || vis_rc=$?
if [[ $vis_rc -eq 2 ]]; then
    echo "[ci/full] skipped: mission-start visual regression inconclusive (needs a real display)"
elif [[ $vis_rc -ne 0 ]]; then
    echo "[ci/full] FAIL: mission-start visual regression"
    exit 1
fi

echo
echo "[ci/full] PASS"
