#!/usr/bin/env bash
# End-to-end parity smoke: launches Godot headless, captures parity output,
# validates against the JSON schema. Returns 0 on success, 1 on any failure.
#
# Note on --quit-after: this counts main-loop iterations, not physics ticks.
# In headless mode each iteration can run many physics steps, so we need a
# large value (~1000) to reliably cross the first fc=70 checkpoint.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$(mktemp -d -t raptor_parity_smoke)"
trap 'rm -rf "$OUT"' EXIT

if ! command -v godot >/dev/null 2>&1; then
    echo "[parity_smoke] godot not on PATH; skipping (install Godot 4.6.2 .NET to run)" >&2
    exit 0
fi
if ! command -v dotnet >/dev/null 2>&1; then
    echo "[parity_smoke] dotnet not on PATH; skipping" >&2
    exit 0
fi

export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(dirname "$(realpath "$(command -v dotnet)")")")}"

# Resolve the real binary path.  Running Godot via a symlink on macOS can
# trigger a slow Gatekeeper re-scan of the app bundle; using the resolved
# path avoids that and keeps startup time well under 5 seconds.
GODOT_BIN="$(realpath "$(command -v godot)")"

# Build the C# project so the assembly is up to date.
cd "$REPO"
dotnet build raptor.csproj --nologo --verbosity quiet >/dev/null

# Run for 1000 main-loop iterations so we reliably cross the fc=70 checkpoint.
# (--quit-after counts main-loop frames, not physics ticks; physics runs at 70Hz
# with up to 8 steps per frame, so 80 iterations isn't always enough.)
RAPTOR_PARITY_OUT="$OUT/out.parity.txt" \
"$GODOT_BIN" --path "$REPO" --headless --quit-after 1000 \
    >"$OUT/godot_stdout.log" 2>&1 || {
    echo "[parity_smoke] FAIL: godot run failed; log:" >&2
    tail -20 "$OUT/godot_stdout.log" >&2
    exit 1
}

if [[ ! -s "$OUT/out.parity.txt" ]]; then
    echo "[parity_smoke] FAIL: parity output is empty" >&2
    echo "[parity_smoke] godot log:" >&2
    tail -20 "$OUT/godot_stdout.log" >&2
    exit 1
fi

python3 - <<EOF
import json, jsonschema, sys
schema = json.load(open("$REPO/tests/parity/schema.json"))
lines = [l.strip() for l in open("$OUT/out.parity.txt") if l.strip()]
if not lines:
    print("[parity_smoke] FAIL: parity file has no lines", file=sys.stderr)
    sys.exit(1)
for i, line in enumerate(lines):
    jsonschema.validate(json.loads(line), schema)
print(f"[parity_smoke] PASS ({len(lines)} checkpoint(s) validated)")
EOF
