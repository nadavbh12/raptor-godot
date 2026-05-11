#!/usr/bin/env python3
"""
Compare position dumps from C and Godot at each parity tick.

C dump format:
  fc=<n> abs=<a> win=<w>
  player x=<x> y=<y> shield=<s> score=<u>
  enemy x=<x> y=<y> sx=<sx> sy=<sy> item=<i>

Godot dump format (same except `enemy slib=<name> x=<x> y=<y> hits=<h>`).

Output: for each MISSION_1 fc, show paired enemies sorted by (x, y),
and any unmatched ones. Player position too.
"""
import argparse, re

def _to_int(s):
    # Godot's negative numbers get a U+200E LRM injected on RTL locales;
    # strip non-ASCII before parsing.
    return int(''.join(ch for ch in s if ch.isascii()))

def parse_dump(path):
    """Parse a position dump into {(win, fc): {'player': (x,y,shield,score),
                                                'enemies': [(x, y)]}}"""
    out = {}
    cur = None
    with open(path) as f:
        for line in f:
            line = line.rstrip('\n')
            if not line:
                cur = None
                continue
            m = re.match(r'^fc=(\d+) abs=\d+ win=(\S+)', line)
            if m:
                cur = (m.group(2), _to_int(m.group(1)))
                out[cur] = {'player': None, 'enemies': []}
                continue
            if cur is None:
                continue
            m = re.match(r'^player x=(\S+) y=(\S+) shield=(\d+) score=(\d+)', line)
            if m:
                out[cur]['player'] = (_to_int(m.group(1)), _to_int(m.group(2)),
                                       _to_int(m.group(3)), _to_int(m.group(4)))
                continue
            m = re.match(r'^enemy.*x=(\S+) y=(\S+)', line)
            if m:
                out[cur]['enemies'].append((_to_int(m.group(1)), _to_int(m.group(2))))
                continue
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--c', required=True)
    ap.add_argument('--godot', required=True)
    ap.add_argument('--win', default='MISSION_1')
    args = ap.parse_args()

    c = parse_dump(args.c)
    g = parse_dump(args.godot)

    c_fcs = sorted([fc for (w, fc) in c if w == args.win])
    g_fcs = sorted([fc for (w, fc) in g if w == args.win])
    common = sorted(set(c_fcs) & set(g_fcs))

    # Summary stats
    total_x_diff = 0
    total_y_diff = 0
    matched = 0
    unmatched_c = 0
    unmatched_g = 0

    print(f"{'fc':>5} {'C':>3}/{'G':>3} {'matched':>8} {'avg|dx|':>8} {'avg|dy|':>8} {'unmatched':>9}")
    for fc in common:
        ce = c[(args.win, fc)]['enemies']
        ge = g[(args.win, fc)]['enemies']
        # Greedy match: pair each C enemy with the closest unmatched Godot enemy.
        # Use sum of |dx|+|dy| as distance.
        used = [False]*len(ge)
        pairs = []
        for (cx, cy) in ce:
            best_i, best_d = -1, 10**9
            for i, (gx, gy) in enumerate(ge):
                if used[i]: continue
                d = abs(cx-gx) + abs(cy-gy)
                if d < best_d:
                    best_d, best_i = d, i
            if best_i >= 0 and best_d < 100:
                used[best_i] = True
                pairs.append(((cx,cy), ge[best_i]))
        n_matched = len(pairs)
        n_unmatched = (len(ce) - n_matched) + (len(ge) - n_matched)
        if pairs:
            adx = sum(abs(a[0]-b[0]) for a,b in pairs)/len(pairs)
            ady = sum(abs(a[1]-b[1]) for a,b in pairs)/len(pairs)
        else:
            adx = ady = 0
        print(f"{fc:>5} {len(ce):>3}/{len(ge):>3} {n_matched:>8} {adx:>8.2f} {ady:>8.2f} {n_unmatched:>9}")
        matched += n_matched
        unmatched_c += len(ce) - n_matched
        unmatched_g += len(ge) - n_matched
        total_x_diff += sum(abs(a[0]-b[0]) for a,b in pairs)
        total_y_diff += sum(abs(a[1]-b[1]) for a,b in pairs)

    print()
    print(f"Total matched pairs:  {matched}")
    print(f"Unmatched C enemies:  {unmatched_c}")
    print(f"Unmatched G enemies:  {unmatched_g}")
    if matched > 0:
        print(f"Avg |dx| over matches: {total_x_diff/matched:.2f}")
        print(f"Avg |dy| over matches: {total_y_diff/matched:.2f}")


if __name__ == '__main__':
    main()
