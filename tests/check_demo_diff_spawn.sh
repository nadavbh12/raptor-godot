#!/usr/bin/env bash
# tests/check_demo_diff_spawn.sh
#
# Regression guard for the wave-1 iter-0 EXTRA-ENEMY parity bug (Godot 3 vs C 2).
#
# Root cause: the demo-replay path applied difficulty AFTER loading the map. In C
# RAP_SetPlayerDiff() always runs BEFORE RAP_LoadMap() (INPUT.C:70 -> :270), so the
# difficulty filter is established before the iter-0 spawn. Godot's
# ApplyPendingDemoStart inverted this: LoadWave()/DoInitialSpawn() consumed the
# stale _curDiff (default 24 = EASY|MED, or 56 in the legacy branch) BEFORE
# SetupDemoPlayer() -> SetPlayerDiff(loadout.Diff). At any mask with the MED bit
# set, MAP1G1 sprite idx0 (x=4,y=139,level=4=MED) spawns as a 3rd enemy; at the
# ROOKIE mask (EB_EASY=8) it is filtered (16 & 8 == 0) -> exactly 2.
#
# This replays a committed minimal ROOKIE/DIFF_1 demo and asserts the iter-0
# enemy count is 2 (the C ground truth from benchmarks/bench_20260606_141540).
# Pre-fix this is 3 (FAIL); post-fix 2 (PASS).
#
# Sim-state only (parity NDJSON), so it runs fully headless — no display needed.
#
# Exit codes: 0 = PASS, 1 = enemy-count regression, 2 = inconclusive (the run
# never reached gameplay — infra, not a verdict).
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
GODOT_BIN="${GODOT_BIN:-$(command -v godot)}"
GODOT_BIN="$(realpath "$GODOT_BIN")"

DEMO="$REPO/tests/parity/demos/rookie_diff1.json"
TRIGGER="$REPO/tests/demo_trigger_iter0.txt"
[ -f "$DEMO" ]    || { echo "[demo_diff_spawn] missing demo fixture: $DEMO"; exit 2; }
[ -f "$TRIGGER" ] || { echo "[demo_diff_spawn] missing trigger: $TRIGGER"; exit 2; }

GDIR="$(mktemp -d)"
trap 'rm -rf "$GDIR"' EXIT
OUT="$GDIR/parity.ndjson"

echo "[demo_diff_spawn] headless demo replay -> $GDIR"
RAPTOR_PLAYTHROUGH="$TRIGGER" \
RAPTOR_DEMO_PATH="$DEMO" \
RAPTOR_PARITY_OUT="$OUT" \
RAPTOR_TEST_FAST=1 \
"$GODOT_BIN" --headless --path "$REPO" \
    --audio-driver Dummy \
    --quit-after 4000 >"$GDIR/run.log" 2>&1 || true

[ -f "$OUT" ] || {
    echo "[demo_diff_spawn] INCONCLUSIVE: no parity output (run never started). Not a verdict:"
    tail -8 "$GDIR/run.log" 2>/dev/null | sed 's/^/                /'
    exit 2
}

python3 - "$OUT" <<'PY'
import json, sys
rows = []
with open(sys.argv[1]) as f:
    for line in f:
        line = line.strip()
        if not line:
            continue
        try:
            rows.append(json.loads(line))
        except json.JSONDecodeError:
            continue

mission0 = [r for r in rows
            if str(r.get("win", "")).startswith("MISSION") and r.get("iter") == 0]
if not mission0:
    print("[demo_diff_spawn] INCONCLUSIVE: replay never reached MISSION iter 0 "
          "(no such checkpoint) — run likely stalled. Not a verdict.")
    sys.exit(2)

enemies = mission0[0].get("enemies")
EXPECTED = 2   # C ground truth at wave-1 iter-0, ROOKIE/DIFF_1
if enemies == EXPECTED:
    print(f"[demo_diff_spawn] PASS: iter-0 enemies = {enemies} (== C)")
    sys.exit(0)
print(f"[demo_diff_spawn] FAIL: iter-0 enemies = {enemies}, expected {EXPECTED} "
      f"(C spawns 2 at ROOKIE; the demo path applied difficulty after the map load)")
sys.exit(1)
PY
