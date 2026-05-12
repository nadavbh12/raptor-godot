#!/usr/bin/env python3
# tests/pair_frames.py
#
# Build a side-by-side image sequence from two screenshot dumps (C dosraptor,
# Godot port) for visual parity comparison. Pairs each Godot frame to the
# closest C frame by mission-relative frame number, with a tunable temporal
# offset to compensate for C's NAVCOM transition + GFX_FadeIn that has no
# equivalent in the Godot view layer.
#
# Defaults are calibrated for mission_start.txt; override via CLI flags if
# the script changes.
"""
Pair C and Godot screenshot dumps for side-by-side video.

Usage:
  python3 pair_frames.py \
      --c-dir   /tmp/c_1s \
      --g-dir   /tmp/godot_1s \
      --out-dir /tmp/pair_seq \
      [--anchor 2038] [--offset 144] [--skip-until-c 200]

Each Godot frame at mission_fc=X is paired with the C frame nearest mission_fc=X+offset.
Frames where the paired C side is still mid-fade-in (mfc < skip-until-c) are dropped.
"""

import argparse
import os
import re
import shutil
import sys

# Sequence numbers in the C extraction's filenames at which labeled mission
# events fire. The Godot/C playthrough script is mission_start.txt:
#   wait 700; dump 05_after_sector_select  -> mission_fc = 700
#   wait 700; dump 06_more                 -> mission_fc = 1400
#   wait 700; dump 07_more                 -> mission_fc = 2100
# When the C extractor was last run with RAPTOR_DUMP_EVERY=1 these landed at
# the seq numbers below. They are an interpolation anchor — if you re-extract
# C with a different DUMP_EVERY, re-derive these from the labeled filenames.
C_LABELS_FC = {559: 700, 794: 1400, 1028: 2100}


def c_seq_to_mfc(seq):
    """Linear interpolation between known C label sequence numbers."""
    if seq in C_LABELS_FC:
        return C_LABELS_FC[seq]
    labels = sorted(C_LABELS_FC)
    for i in range(len(labels) - 1):
        if labels[i] <= seq <= labels[i + 1]:
            t = (seq - labels[i]) / (labels[i + 1] - labels[i])
            return C_LABELS_FC[labels[i]] + t * (C_LABELS_FC[labels[i + 1]] - C_LABELS_FC[labels[i]])
    # Extrapolate using the rate from the last labeled segment.
    rate = (C_LABELS_FC[labels[-1]] - C_LABELS_FC[labels[-2]]) / (labels[-1] - labels[-2])
    if seq > labels[-1]:
        return C_LABELS_FC[labels[-1]] + (seq - labels[-1]) * rate
    return C_LABELS_FC[labels[0]] - (labels[0] - seq) * rate


def find_nearest(frames, target):
    """Binary search for the frame whose mfc is closest to target."""
    lo, hi = 0, len(frames) - 1
    while lo < hi:
        mid = (lo + hi) // 2
        if frames[mid][0] < target:
            lo = mid + 1
        else:
            hi = mid
    candidates = [frames[lo]]
    if lo > 0:
        candidates.append(frames[lo - 1])
    return min(candidates, key=lambda x: abs(x[0] - target))


def load_c_frames(c_dir, min_seq=302):
    """Read C png filenames, derive mission_fc per file via label interpolation.

    min_seq drops menu-navigation frames before the mission begins. seq 302 is
    just after the sector_select label (301) in our extraction; earlier
    sequences belong to menu transitions and have no mission gameplay.
    """
    out = []
    for f in sorted(os.listdir(c_dir)):
        if not f.endswith('.png'):
            continue
        m = re.match(r'^(\d{5})_', f)
        if not m:
            continue
        seq = int(m.group(1))
        if seq < min_seq:
            continue
        fc = c_seq_to_mfc(seq)
        if fc < 0:
            continue
        out.append((fc, os.path.join(c_dir, f)))
    out.sort()
    return out


def load_g_frames(g_dir, anchor):
    """Godot dump filenames embed abs SimClock.Frame; subtract anchor for mfc."""
    out = []
    for f in sorted(os.listdir(g_dir)):
        if not f.endswith('.png'):
            continue
        m = re.match(r'^fc(\d+)_', f)
        if not m:
            continue
        mfc = int(m.group(1)) - anchor
        if mfc < 0:
            continue
        out.append((mfc, os.path.join(g_dir, f)))
    out.sort()
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--c-dir',   default='/tmp/c_1s')
    ap.add_argument('--g-dir',   default='/tmp/godot_1s')
    ap.add_argument('--out-dir', default='/tmp/pair_seq')
    # Defaults calibrated empirically against mission_start.txt.
    ap.add_argument('--anchor', type=int, default=2038,
                    help='Godot SimClock.Frame when MISSION_1 begins '
                         '(= the abs frame of the first labeled mission dump - mission_fc).')
    ap.add_argument('--offset', type=int, default=144,
                    help='Sim-frame shift applied to the C side. Compensates for C\'s '
                         'NAVCOM transition + GFX_FadeIn that has no Godot equivalent. '
                         'Positive: each Godot frame at mfc=X is paired with C at mfc=X+offset.')
    ap.add_argument('--skip-until-c', type=int, default=200,
                    help='Drop Godot frames whose paired C frame would still be in fade-in '
                         '(C mfc < this threshold). C\'s palette fade lasts ~120-150 frames.')
    args = ap.parse_args()

    out_c = os.path.join(args.out_dir, 'c')
    out_g = os.path.join(args.out_dir, 'g')
    os.makedirs(out_c, exist_ok=True)
    os.makedirs(out_g, exist_ok=True)
    for d in (out_c, out_g):
        for f in os.listdir(d):
            os.remove(os.path.join(d, f))

    c_frames = load_c_frames(args.c_dir)
    g_frames = load_g_frames(args.g_dir, args.anchor)
    if not c_frames or not g_frames:
        print(f"error: empty frame set (c={len(c_frames)}, g={len(g_frames)})", file=sys.stderr)
        sys.exit(1)

    c_max = c_frames[-1][0]
    g_frames = [(fc, p) for (fc, p) in g_frames
                if fc + args.offset >= args.skip_until_c and fc + args.offset <= c_max]

    pairs = []
    for gfc, gp in g_frames:
        cfc, cp = find_nearest(c_frames, gfc + args.offset)
        pairs.append((gfc, gp, cfc, cp))

    for i, (_, gp, _, cp) in enumerate(pairs):
        shutil.copy(cp, os.path.join(out_c, f'{i:04d}.png'))
        shutil.copy(gp, os.path.join(out_g, f'{i:04d}.png'))

    print(f"anchor={args.anchor}  offset={args.offset}  "
          f"skip_until_c={args.skip_until_c}")
    print(f"C source:   {len(c_frames)} mission frames "
          f"({c_frames[0][0]:.0f}..{c_frames[-1][0]:.0f})")
    print(f"G source:   {len(g_frames)} (kept after trim)")
    print(f"pairs:      {len(pairs)}")
    if pairs:
        print(f"first pair: G mfc={pairs[0][0]} ↔ C mfc≈{pairs[0][2]:.0f}")
        print(f"last  pair: G mfc={pairs[-1][0]} ↔ C mfc≈{pairs[-1][2]:.0f}")


if __name__ == '__main__':
    main()
