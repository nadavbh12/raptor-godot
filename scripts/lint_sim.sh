#!/usr/bin/env bash
# Enforce sim-layer discipline (CLAUDE.md rule #1, spec §4.2) in src/Sim/:
#   - no `_Process` override (sim ticks via `_PhysicsProcess`)
#   - never read the frame timestep (sim counts `SimClock.Frame`)
#   - no engine RNG (`GD.Rand*` / `Mathf.Rand*`) — use the per-wave RNG instance
#   - no wall-clock time (`Time`/`OS` `GetTicks*`/`GetUnix*`)
#
# This is a SEMANTIC check: tools/SimLint parses each file with Roslyn, so the
# matches are real C# constructs, not text. The word "delta" in a comment or an
# unrelated variable (e.g. a UI volume-adjustment `int delta`) is NOT flagged —
# only actual frame-time / RNG / wall-clock usage is. Whitelist a specific line
# with the EXACT uppercase token `LINT-OK` in a comment on that line; the marker
# is case-sensitive so a casual lowercase mention can't bypass the lint.
#
# Exits non-zero on any violation (or if the analyzer's own self-test fails).

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJ="$ROOT/tools/SimLint"
SIM_DIR="$ROOT/src/Sim"

if [[ ! -d "$SIM_DIR" ]]; then
    echo "[lint_sim] $SIM_DIR not found"
    exit 0  # not yet created; not a failure
fi

# Build the analyzer once (quietly), then run it from the built dll so the only
# stdout is the lint result.
dotnet build "$PROJ/SimLint.csproj" --configuration Release --verbosity quiet --nologo
DLL="$PROJ/bin/Release/net8.0/SimLint.dll"

# Verify the analyzer itself (embedded fixtures) before trusting its scan.
dotnet "$DLL" --self-test
dotnet "$DLL" "$SIM_DIR"
