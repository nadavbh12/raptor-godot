#!/usr/bin/env python3
"""Faithful C-vs-Godot F_REPEAT flight parity check (finding #2).

Ports the C enemy-flight Bresenham (SOURCE/ENEMY.C MoveEobj + SOURCE/RAP.C
InitMobj/MoveMobj + the F_REPEAT done block) and the Godot EnemyLogic flight, and
runs both from an identical spawn to show where they diverge.

Root cause: at a ping-pong reversal C re-targets the SAME waypoint the boss is
already on — a ZERO-LENGTH segment. C's InitMobj leaves addx=1, maxloop=delx+1=1,
so MoveMobj/MoveEobj DRIFT the boss (an authentic C quirk). Godot special-cased
zero-length to a no-op (maxloop=0, moveDone=true, no drift), producing a "clean"
ping-pong that diverges from C at the first reversal (the iter-~4537 boss bug).

CORRECTION (2026-06-07 v2): the SOLE Godot deviation is the zero-length special
case in InitBresenhamForTarget. Both C MoveEobj (ENEMY.C:149-150) AND Godot
MoveEobjSteps (line 377) carry `if maxloop < 1: done = TRUE` after the loop — so
that check is FAITHFUL and must stay (an earlier note here wrongly claimed C only
sets done at maxloop==0, and the model below omitted it). The faithful fix removes
ONLY the special case; every flight function is then byte-identical to C.

Set FIXED=True to model the corrected Godot (no zero-length special case; keeps the
maxloop<1 done check): then C and Godot match for thousands of ticks.

Usage: python3 tools/flight_parity_check.py
"""
FLIGHTX = [0, 64, 64, 8, -8, -64, -64, -4]   # SHIP10G1 waypoints
FLIGHTY = [-72, -32, -8, 32, 32, -8, -32, -70]
NUMFLIGHT, REPOS, SPEED = 8, 0, 2
SX, SY, MAPY = 160, 88, 0   # spawnX, 100-halfY (Height=24), mapY
FIXED = True


def c_init_mobj(m):
    m['done'] = False; m['addx'] = 1; m['addy'] = 1
    m['delx'] = m['x2'] - m['x']; m['dely'] = m['y2'] - m['y']
    if m['delx'] < 0: m['delx'] = -m['delx']; m['addx'] = -m['addx']
    if m['dely'] < 0: m['dely'] = -m['dely']; m['addy'] = -m['addy']
    if m['delx'] >= m['dely']: m['err'] = -(m['dely'] >> 1); m['maxloop'] = m['delx'] + 1
    else: m['err'] = (m['delx'] >> 1); m['maxloop'] = m['dely'] + 1


def c_move_mobj(m):
    if m['maxloop'] == 0: m['done'] = True; return
    if m['delx'] >= m['dely']:
        m['x'] += m['addx']; m['err'] += m['dely']
        if m['err'] > 0: m['y'] += m['addy']; m['err'] -= m['delx']
    else:
        m['y'] += m['addy']; m['err'] += m['delx']
        if m['err'] > 0: m['x'] += m['addx']; m['err'] -= m['dely']
    m['maxloop'] -= 1


def c_move_eobj(m, speed):
    if speed == 0: return 0
    horiz = m['delx'] >= m['dely']
    while speed:
        speed -= 1; m['maxloop'] -= 1
        if m['maxloop'] == 0: m['done'] = True; return speed
        if horiz:
            m['x'] += m['addx']; m['err'] += m['dely']
            if m['err'] > 0: m['y'] += m['addy']; m['err'] -= m['delx']
        else:
            m['y'] += m['addy']; m['err'] += m['delx']
            if m['err'] > 0: m['x'] += m['addx']; m['err'] -= m['dely']
    if m['maxloop'] < 1: m['done'] = True   # ENEMY.C:149-150 (was omitted)
    return speed


def c_spawn():
    m = {'x': SX, 'y': MAPY, 'x2': SX + FLIGHTX[0], 'y2': SY + FLIGHTY[0]}
    c_init_mobj(m); c_move_mobj(m)
    return {'m': m, 'movepos': 1, 'edir': 'F'}


def c_tick(st):
    m = st['m']; speed = c_move_eobj(m, SPEED)
    if m['done']:
        m['x'] = m['x2']; m['y'] = m['y2']
        m['x2'] = SX + FLIGHTX[st['movepos']]; m['y2'] = SY + FLIGHTY[st['movepos']]
        c_init_mobj(m); c_move_mobj(m); c_move_eobj(m, speed)
        if st['edir'] == 'F':
            st['movepos'] += 1
            if st['movepos'] >= NUMFLIGHT: st['edir'] = 'B'; st['movepos'] = NUMFLIGHT - 1
        else:
            st['movepos'] -= 1
            if st['movepos'] <= REPOS: st['edir'] = 'F'; st['movepos'] = REPOS
    return (m['x'], m['y'])


def g_init_bres(g, fx, fy, tx, ty):
    g['mx'] = fx; g['my'] = fy; g['tgtX'] = tx; g['tgtY'] = ty
    if not FIXED and fx == tx and fy == ty:
        g['addX'] = g['addY'] = 0; g['delX'] = g['delY'] = 0
        g['err'] = 0; g['maxloop'] = 0; g['moveDone'] = True; return
    g['addX'] = 1; g['addY'] = 1; g['delX'] = tx - fx; g['delY'] = ty - fy
    if g['delX'] < 0: g['delX'] = -g['delX']; g['addX'] = -1
    if g['delY'] < 0: g['delY'] = -g['delY']; g['addY'] = -1
    if g['delX'] >= g['delY']: g['err'] = -(g['delY'] >> 1); g['maxloop'] = g['delX'] + 1
    else: g['err'] = (g['delX'] >> 1); g['maxloop'] = g['delY'] + 1
    g['moveDone'] = (g['maxloop'] == 0)


def g_bres_step(g):
    if g['delX'] >= g['delY']:
        g['mx'] += g['addX']; g['err'] += g['delY']
        if g['err'] > 0: g['my'] += g['addY']; g['err'] -= g['delX']
    else:
        g['my'] += g['addY']; g['err'] += g['delX']
        if g['err'] > 0: g['mx'] += g['addX']; g['err'] -= g['delY']


def g_move_eobj(g, speed):
    if speed <= 0: return 0
    while speed > 0:
        speed -= 1; g['maxloop'] -= 1
        if g['maxloop'] == 0: g['moveDone'] = True; return speed
        g_bres_step(g)
    if g['maxloop'] < 1: g['moveDone'] = True   # FAITHFUL: matches C; NOT removed by the fix
    return speed


def g_move_mobj(g):
    if g['maxloop'] == 0: g['moveDone'] = True; return
    g_bres_step(g); g['maxloop'] -= 1


def g_advance(g):
    if g['flightStep'] > 0 and g['flightIdx'] >= NUMFLIGHT: g['flightStep'] = -1; g['flightIdx'] = NUMFLIGHT - 1
    elif g['flightStep'] < 0 and g['flightIdx'] <= REPOS: g['flightStep'] = 1; g['flightIdx'] = REPOS
    ntx = SX + FLIGHTX[g['flightIdx']]; nty = SY + FLIGHTY[g['flightIdx']]
    g['flightIdx'] += g['flightStep']
    g_init_bres(g, g['mx'], g['my'], ntx, nty)


def g_spawn():
    g = {'flightIdx': 0, 'flightStep': 1}
    g_init_bres(g, SX, MAPY, SX + FLIGHTX[0], SY + FLIGHTY[0])
    g_move_mobj(g); g['flightIdx'] = 1
    return g


def g_tick(g):
    leftover = g_move_eobj(g, SPEED)
    if g['moveDone']:
        g['mx'] = g['tgtX']; g['my'] = g['tgtY']
        g_advance(g); g_move_mobj(g); g_move_eobj(g, leftover)
    return (g['mx'], g['my'])


def run(ticks=2000):
    """Return (first_divergence_tick_or_None, c_positions[1..ticks])."""
    C, G = c_spawn(), g_spawn()
    div, golden = None, []
    for t in range(1, ticks + 1):
        c, g = c_tick(C), g_tick(G)
        golden.append(c)
        if c != g and div is None:
            div = t
    return div, golden


if __name__ == "__main__":
    import sys
    FIXED = False
    div_buggy, _ = run()
    FIXED = True
    div_fixed, golden = run()
    print(f"FIXED=False (special case present): "
          + (f"diverges at tick {div_buggy}" if div_buggy else "MATCH (no bug?!)"))
    print(f"FIXED=True  (special case removed): "
          + ("C and Godot MATCH for 2000 ticks" if div_fixed is None
             else f"diverge at tick {div_fixed}"))
    if "--golden" in sys.argv:
        n = 180
        flat = [v for xy in golden[:n] for v in xy]
        print(f"\n// golden C (x,y) for ticks 1..{n} (SHIP10G1 F_REPEAT, speed 2)")
        print(",".join(str(v) for v in flat))
