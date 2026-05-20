#!/usr/bin/env bash
# Generate a C/Godot visual parity audit report for a manifest scenario.

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
SCENARIO_ID="${1:-${SCENARIO_ID:-full_demo}}"
MANIFEST="$REPO/tests/parity/scenarios/scenarios.json"
STAMP="$(date +%Y%m%d_%H%M%S)"
OUT_ROOT="${OUT_ROOT:-$REPO/dumps/visual_audit/$SCENARIO_ID/$STAMP}"

if [[ -z "${DOTNET_ROOT:-}" && -d /opt/homebrew/opt/dotnet@8/libexec ]]; then
    export DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec
fi

read -r SCRIPT_NAME C_PLAYTHROUGH G_PLAYTHROUGH < <(
python3 - "$MANIFEST" "$SCENARIO_ID" <<'PY'
import json
import os
import sys

manifest, scenario_id = sys.argv[1], sys.argv[2]
data = json.load(open(manifest, encoding="utf-8"))
for scenario in data["scenarios"]:
    if scenario["id"] == scenario_id:
        playthrough = scenario["playthrough"]
        c_playthrough = scenario.get("c_playthrough", playthrough)
        g_playthrough = scenario.get("godot_playthrough", playthrough)
        print(
            os.path.splitext(os.path.basename(playthrough))[0],
            c_playthrough,
            g_playthrough,
        )
        break
else:
    raise SystemExit(f"scenario not found: {scenario_id}")
PY
)

mkdir -p "$OUT_ROOT"
echo "[visual_audit] scenario: $SCENARIO_ID"
echo "[visual_audit] output: $OUT_ROOT"

GODOT_QUIT_AFTER="${GODOT_QUIT_AFTER:-500000}" \
LABEL_ALIGN=1 \
SCRIPT_NAME="$SCRIPT_NAME" \
C_SCRIPT_PATH="$REPO/$C_PLAYTHROUGH" \
G_SCRIPT_PATH="$(cd "$REPO/.." && pwd)/dosraptor/$G_PLAYTHROUGH" \
"$REPO/tests/build_compare_video.sh"

if [[ "${VISUAL_ALIGN:-0}" -eq 1 ]]; then
    echo "[visual_audit] rebuilding pairs by dense visual alignment..."
    if [[ -n "${VISUAL_ALIGN_STRIDE:-}" ]]; then
        python3 "$REPO/tests/build_visual_aligned_pairs.py" \
            --c-dir /tmp/c_1s \
            --g-dir /tmp/godot_1s \
            --out-dir /tmp/pair_seq \
            --stride "$VISUAL_ALIGN_STRIDE"
    else
        python3 "$REPO/tests/build_visual_aligned_pairs.py" \
            --c-dir /tmp/c_1s \
            --g-dir /tmp/godot_1s \
            --out-dir /tmp/pair_seq
    fi
fi

if [[ "${FRAMECOUNT_ALIGN:-0}" -eq 1 ]]; then
    echo "[visual_audit] rebuilding pairs by normalized framecount alignment..."
    python3 "$REPO/tests/build_framecount_aligned_pairs.py" \
        --c-dir /tmp/c_1s \
        --g-dir /tmp/godot_1s \
        --out-dir /tmp/pair_seq \
        --offset "${FRAMECOUNT_OFFSET:--160}"
fi

if [[ "${ITER_ALIGN:-0}" -eq 1 ]]; then
    echo "[visual_audit] rebuilding pairs by game-loop iteration alignment..."
    python3 "$REPO/tests/build_iter_aligned_pairs.py" \
        --c-dir /tmp/c_1s \
        --g-dir /tmp/godot_1s \
        --out-dir /tmp/pair_seq
fi

mkdir -p "$OUT_ROOT/pairs"
rm -rf "$OUT_ROOT/pairs/c" "$OUT_ROOT/pairs/g"
cp -R /tmp/pair_seq/c "$OUT_ROOT/pairs/c"
cp -R /tmp/pair_seq/g "$OUT_ROOT/pairs/g"
if [[ -f /tmp/pair_seq/visual_alignment_pairs.json ]]; then
    cp /tmp/pair_seq/visual_alignment_pairs.json "$OUT_ROOT/pairs/visual_alignment_pairs.json"
fi
if [[ -f /tmp/pair_seq/framecount_alignment_pairs.json ]]; then
    cp /tmp/pair_seq/framecount_alignment_pairs.json "$OUT_ROOT/pairs/framecount_alignment_pairs.json"
fi
if [[ -f /tmp/pair_seq/iter_alignment_pairs.json ]]; then
    cp /tmp/pair_seq/iter_alignment_pairs.json "$OUT_ROOT/pairs/iter_alignment_pairs.json"
fi

mkdir -p "$OUT_ROOT/raw"
rm -rf "$OUT_ROOT/raw/c" "$OUT_ROOT/raw/godot"
cp -R /tmp/c_1s "$OUT_ROOT/raw/c"
cp -R /tmp/godot_1s "$OUT_ROOT/raw/godot"

python3 "$REPO/tests/visual_audit.py" \
    --pairs-dir "$OUT_ROOT/pairs" \
    --out-dir "$OUT_ROOT" \
    --scenario-id "$SCENARIO_ID"

echo "[visual_audit] done: $OUT_ROOT/index.html"
