#!/usr/bin/env bash
# Enforce sim-layer discipline (spec §4.2):
# - No `delta` references in src/Sim/
# - No `_process` (only `_PhysicsProcess`)
# - No engine RNG (use the explicit RandomNumberGenerator instance)
# - No wall-clock time
# Exit non-zero on violation; whitelist via inline `// LINT-OK: <reason>`.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SIM_DIR="$REPO_ROOT/src/Sim"

if [[ ! -d "$SIM_DIR" ]]; then
    echo "[lint_sim] $SIM_DIR not found"
    exit 0  # not yet created; not a failure
fi

# Patterns that must not appear in sim code without LINT-OK
PATTERNS=(
    '\bdelta\b'
    '\b_Process\b'
    'GD\.Rand'
    'Mathf\.Rand'
    'Time\.GetUnix'
    'Time\.GetTicks'
    'OS\.GetTicks'
)

VIOLATIONS=0
for pattern in "${PATTERNS[@]}"; do
    while IFS= read -r line; do
        # Skip lines containing the LINT-OK marker
        if [[ "$line" == *"LINT-OK"* ]]; then
            continue
        fi
        echo "[lint_sim] FAIL: $line"
        VIOLATIONS=$((VIOLATIONS + 1))
    done < <(grep -rnE "$pattern" "$SIM_DIR" --include='*.cs' || true)
done

if [[ $VIOLATIONS -gt 0 ]]; then
    echo "[lint_sim] $VIOLATIONS violation(s)"
    exit 1
fi
echo "[lint_sim] OK"
