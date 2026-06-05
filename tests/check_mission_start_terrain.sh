#!/usr/bin/env bash
# tests/check_mission_start_terrain.sh
#
# Regression guard for the mission-start "dead screen" (issues #1/#4): at mission
# start the wave must be loaded during the ~1.5s LoadComp deferral, so the View
# can render the briefing (LOADCOMP window) + fade the terrain in — NOT a black
# ship-on-void screen. (The deferral shows the "APPROACHING DESTINATION / BRAVO
# SECTOR / WAVE n" briefing; the terrain then fades up over the post-iter-0 hold.)
#
# Root cause / fix: WaveController.LoadWave (map + DoInitialSpawn) must run in
# OnGameEnter (deferral START), not in ApplyPendingGameEnter (deferral END). When
# it runs early, the on-screen enemies are spawned during the deferral, so the
# captured shot_map.tsv shows enemies > 0 across the iter==-1 deferral window.
# Pre-fix (load late) that window has enemies == 0 and renders black — the bug.
#
# We assert enemies > 0 during the deferral. This is a sim-state proxy for "the
# terrain is on screen", tightly coupled to the fix (DoInitialSpawn lives in
# LoadWave); it was validated to PASS on the fixed build and FAIL on the pre-fix
# build. See the before/after capture in the issue.
#
# Headed capture (rendering required, like run_menu_pixel_parity.sh): we launch a
# real window pushed off-screen. `--headless` disables GetViewport rendering and
# would write no shot_map, so we do NOT use it here.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
DOSRAPTOR="${DOSRAPTOR:-$(cd "$REPO/.." && pwd)/dosraptor}"
SCRIPT="$DOSRAPTOR/tests/scripts/mission_start.txt"
GODOT_BIN="${GODOT_BIN:-$(command -v godot)}"
GODOT_BIN="$(realpath "$GODOT_BIN")"

GDIR="$(mktemp -d)"
trap 'rm -rf "$GDIR"' EXIT

[ -f "$SCRIPT" ] || { echo "[mission_start] missing playthrough script: $SCRIPT"; exit 2; }

echo "[mission_start] headed capture -> $GDIR"
# Keep comments OUT of the env block below: a trailing backslash joins a '#'
# comment onto the next line and silently swallows the env assignments.
RAPTOR_PLAYTHROUGH="$SCRIPT" \
RAPTOR_RENDER_MENUS=1 \
RAPTOR_SHOT_DIR="$GDIR" \
RAPTOR_SHOT_EVERY_FC=1 \
"$GODOT_BIN" --path "$REPO" \
    --audio-driver Dummy \
    --quit-after 2300 >"$GDIR/run.log" 2>&1 || true

[ -f "$GDIR/shot_map.tsv" ] || {
    echo "[mission_start] INCONCLUSIVE: no shot_map.tsv (headed capture produced no"
    echo "                frames — a real display/window is required). Not a verdict:"
    tail -5 "$GDIR/run.log" 2>/dev/null | sed 's/^/                /'
    exit 2
}

python3 - "$GDIR/shot_map.tsv" <<'PY'
import sys
rows = []
with open(sys.argv[1]) as f:
    next(f, None)  # header
    for line in f:
        p = line.rstrip("\n").split("\t")
        if len(p) < 7:
            continue
        try:
            rows.append((int(p[1]), int(p[3]), int(p[6])))  # saved_fc, drawn_iter, enemies
        except ValueError:
            continue

# Exit codes: 0 = PASS, 1 = dead-screen regression, 2 = inconclusive capture
# (infra: the run never reached gameplay) — kept distinct so a flaky headed
# capture can never be mistaken for the bug.
iter0 = [fc for fc, it, _ in rows if it == 0]
if not iter0:
    print("[mission_start] INCONCLUSIVE: capture never reached gameplay "
          "(no drawn_iter==0 frame) — headed run likely stalled. Not a verdict.")
    sys.exit(2)

first = min(iter0)
# The LoadComp deferral is the iter==-1 frames immediately preceding activation
# (LoadCompFrames=107). Look back a little wider than that to be robust.
window = [en for fc, it, en in rows if it == -1 and first - 110 <= fc < first]
mx = max(window) if window else 0

if not window:
    print(f"[mission_start] INCONCLUSIVE: no deferral frames captured before "
          f"fc{first}. Not a verdict.")
    sys.exit(2)

if mx > 0:
    print(f"[mission_start] PASS: terrain/enemies present during the mission-start "
          f"deferral (first_iter0=fc{first}, deferral_frames={len(window)}, "
          f"max_enemies={mx}).")
    sys.exit(0)

print(f"[mission_start] FAIL: DEAD SCREEN — the mission-start deferral "
      f"(first_iter0=fc{first}, {len(window)} frames) shows enemies=0, i.e. the "
      f"wave is not loaded during the LoadComp wait. LoadWave must run in "
      f"WaveController.OnGameEnter, not ApplyPendingGameEnter.")
sys.exit(1)
PY