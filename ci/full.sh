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
echo "[ci/full] PASS"
