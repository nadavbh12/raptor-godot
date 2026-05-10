#!/usr/bin/env python3
"""
Compare two parity output files (NDJSON, schema in tests/parity/schema.json).
Per spec §7.4: per-field tolerances, ≥95% checkpoint pass for overall PASS.

Usage:
    parity_diff.py --c-golden <path> --godot-out <path>

Exit codes:
    0  PASS   ≥95% of checkpoints satisfy tolerances on every required field.
    1  FAIL   first divergent checkpoint shown; exit non-zero.
    2  ERROR  unable to read/parse one of the inputs.
"""
import argparse
import json
import sys
from typing import Iterable


# Per-field tolerance functions. Each returns True if a is "close enough" to b.
def _exact(a, b): return a == b
def _abs(tol):
    return lambda a, b: abs(int(a) - int(b)) <= tol
def _abs_or_pct(abs_tol, pct_tol):
    """Pass if within absolute OR within percent (whichever is larger)."""
    def cmp(a, b):
        a, b = int(a), int(b)
        diff = abs(a - b)
        if diff <= abs_tol:
            return True
        if b == 0:
            return diff == 0
        return diff <= abs(b) * pct_tol
    return cmp

# obj_hash is exact-but-advisory: mismatches are logged, not counted as failures.
TOLERANCES = {
    "fc":       _exact,
    "win":      _exact,
    "player_x": _abs(8),
    "player_y": _abs(8),
    "score":    _abs_or_pct(100, 0.15),
    "shield":   _abs(2),
    "enemies":  _abs(1),
    "pbullets": _abs_or_pct(0, 0.20),
    "ebullets": _abs_or_pct(0, 0.25),
}
ADVISORY_FIELDS = {"obj_hash"}

PASS_PCT = 0.95


def read_ndjson(path: str) -> list[dict]:
    out = []
    with open(path) as f:
        for i, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                out.append(json.loads(line))
            except json.JSONDecodeError as e:
                print(f"ERROR: {path}:{i}: bad JSON: {e}", file=sys.stderr)
                sys.exit(2)
    return out


def compare(c: list[dict], g: list[dict]) -> tuple[int, int, list[str]]:
    """
    Returns (n_total, n_passed, diagnostics).
    Iterates by index — fc-misalignment is reported as a divergence.
    """
    diags: list[str] = []
    n = max(len(c), len(g))
    if not n:
        return (0, 0, ["no checkpoints in either file"])
    n_passed = 0
    for i in range(n):
        if i >= len(c):
            diags.append(f"#{i}: godot has extra checkpoint {g[i].get('fc','?')}, no C counterpart")
            continue
        if i >= len(g):
            diags.append(f"#{i}: C has extra checkpoint {c[i].get('fc','?')}, no godot counterpart")
            continue

        cc, gg = c[i], g[i]
        ok = True
        for field, cmp in TOLERANCES.items():
            if field not in cc:
                diags.append(f"#{i} fc={cc.get('fc')}: C output missing field '{field}'")
                ok = False; continue
            if field not in gg:
                diags.append(f"#{i} fc={cc.get('fc')}: godot output missing field '{field}'")
                ok = False; continue
            if not cmp(cc[field], gg[field]):
                diags.append(f"#{i} fc={cc.get('fc')}: {field} c={cc[field]} godot={gg[field]} (out of tolerance)")
                ok = False
        # Advisory: check obj_hash, log mismatches, don't count as failure.
        for field in ADVISORY_FIELDS:
            if field in cc and field in gg and cc[field] != gg[field]:
                diags.append(f"#{i} fc={cc.get('fc')}: {field} c={cc[field]} godot={gg[field]} (advisory mismatch)")
        if ok:
            n_passed += 1
    return (n, n_passed, diags)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--c-golden", required=True)
    p.add_argument("--godot-out", required=True)
    p.add_argument("--max-diags", type=int, default=10,
                   help="max diagnostic lines to print before truncating")
    args = p.parse_args()

    c = read_ndjson(args.c_golden)
    g = read_ndjson(args.godot_out)
    n_total, n_passed, diags = compare(c, g)

    # Special case: if BOTH inputs are empty, that's a vacuous PASS but
    # it almost certainly means a misconfigured run — fail loudly.
    if n_total == 0:
        print("FAIL: both inputs empty — likely a misconfigured run", file=sys.stderr)
        return 1

    pct = n_passed / n_total
    print(f"checkpoints: {n_total}, passed: {n_passed} ({pct:.1%})")
    if diags:
        for d in diags[: args.max_diags]:
            print(f"  {d}")
        if len(diags) > args.max_diags:
            print(f"  ... and {len(diags) - args.max_diags} more")

    if pct >= PASS_PCT:
        print("PASS")
        return 0
    print(f"FAIL: pass rate {pct:.1%} < {PASS_PCT:.0%}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
