#!/usr/bin/env bash
# tests/check_shield_recharge.sh
#
# Regression guard for the shield-recharge / objuse-flag parity bug.
#
# C OBJS_Think (OBJECTS.C:1385) recharges +1 shield only after CHARGE_SHIELD(96)
# consecutive ticks WITHOUT an object use. But OBJS_Use (firing FORWARD_GUNS when
# BUT_1 is held — RAP.C:1005-1013) sets objuse_flag=TRUE and think_cnt=0 every
# such iter, so OBJS_Think skips that tick. The recorded player fires heavily, so
# in C the counter never reaches 96 and the shield does NOT recharge (stays 75).
# Godot's recharge ignored firing and healed every ~96 iters (76 by iter ~108).
#
# This replays a committed ROOKIE/DIFF_1 demo that holds fire every iter with the
# player stationary, and asserts the shield NEVER rises above its start value
# (75) — i.e. firing suppresses the recharge, matching C. Pre-fix the shield
# climbs to 76+ (FAIL); post-fix it stays <= 75 (PASS).
#
# Sim-state only (parity NDJSON) → fully headless.
#
# Exit codes: 0 = PASS, 1 = recharge-while-firing regression, 2 = inconclusive.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
GODOT_BIN="${GODOT_BIN:-$(command -v godot)}"
GODOT_BIN="$(realpath "$GODOT_BIN")"

DEMO="$REPO/tests/parity/demos/rookie_diff1_firing.json"
TRIGGER="$REPO/tests/demo_trigger.txt"   # full wait; the demo self-terminates at 150 recs
[ -f "$DEMO" ]    || { echo "[shield_recharge] missing demo fixture: $DEMO"; exit 2; }
[ -f "$TRIGGER" ] || { echo "[shield_recharge] missing trigger: $TRIGGER"; exit 2; }

GDIR="$(mktemp -d)"
trap 'rm -rf "$GDIR"' EXIT
OUT="$GDIR/parity.ndjson"

echo "[shield_recharge] headless firing replay -> $GDIR"
# RAPTOR_GODMODE keeps the stationary firing player alive past the would-be first
# recharge (~iter 108); it only suppresses incoming damage (parity-inert), so the
# shield stays at its start value UNLESS the recharge wrongly heals it.
RAPTOR_PLAYTHROUGH="$TRIGGER" \
RAPTOR_DEMO_PATH="$DEMO" \
RAPTOR_PARITY_OUT="$OUT" \
RAPTOR_GODMODE=1 \
RAPTOR_TEST_FAST=1 \
"$GODOT_BIN" --headless --path "$REPO" \
    --audio-driver Dummy \
    --quit-after 2000 >"$GDIR/run.log" 2>&1 || true

[ -f "$OUT" ] || {
    echo "[shield_recharge] INCONCLUSIVE: no parity output. Not a verdict:"
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

mission = [r for r in rows if str(r.get("win", "")).startswith("MISSION")]
# Need to have run far enough that, WITHOUT the objuse fix, a recharge would have
# fired (CHARGE_SHIELD=96 → first heal ~iter 108, the 7th 18-iter checkpoint).
if len(mission) < 7 or max(r.get("iter", 0) for r in mission) < 108:
    print(f"[shield_recharge] INCONCLUSIVE: replay too short "
          f"({len(mission)} MISSION rows, max iter "
          f"{max((r.get('iter',0) for r in mission), default=-1)}) — never reached "
          f"the would-be first recharge (~iter 108). Not a verdict.")
    sys.exit(2)

START = 75
worst = max(mission, key=lambda r: r.get("shield", 0))
if worst.get("shield", 0) <= START:
    print(f"[shield_recharge] PASS: shield never rose above {START} while firing "
          f"(max {worst.get('shield')} @ iter {worst.get('iter')}) — matches C.")
    sys.exit(0)
print(f"[shield_recharge] FAIL: shield rose to {worst.get('shield')} @ iter "
      f"{worst.get('iter')} while firing (start {START}). Firing must suppress the "
      f"recharge (objuse_flag/think_cnt reset) as in C OBJS_Think.")
sys.exit(1)
PY
