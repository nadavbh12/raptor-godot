#!/usr/bin/env bash
# Run the Godot menu-event parity test for every committed C golden and report
# pass/fail. This is the CI entry point for menu-event scenarios: it never needs
# the C binary (goldens are committed) — only headless Godot.
#
#   tests/run_menu_event_sweep.sh
#
# Exit 0 iff every scenario PASSes.
set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
shopt -s nullglob
goldens=("$REPO"/tests/parity/c_menu_goldens/*.menu.ndjson)
if [[ ${#goldens[@]} -eq 0 ]]; then
    echo "[sweep] no goldens in tests/parity/c_menu_goldens/ (run tools/capture_menu_goldens.sh)" >&2
    exit 2
fi

fail=0; pass=0
for g in "${goldens[@]}"; do
    name="$(basename "$g" .menu.ndjson)"
    if "$REPO/tests/run_menu_event_parity.sh" "$name" >"/tmp/sweep_$name.log" 2>&1; then
        echo "[sweep] PASS $name"; pass=$((pass+1))
    else
        echo "[sweep] FAIL $name"; tail -5 "/tmp/sweep_$name.log" | sed 's/^/    /'; fail=$((fail+1))
    fi
done
echo "[sweep] $pass passed, $fail failed"
[[ "$fail" -eq 0 ]]
