# Deterministic Parity + Death-Scenario Baseline — Design

_Date: 2026-05-30. Status: approved (design), pending spec review._

## Motivation

Parity testing currently relies on matching the per-wave RNG **seed** between the
DOS C build and the Godot port so that `random()`-driven behaviour lines up. This
is fragile:

- The seed mapping is inconsistent. C seeds `1024 * game_wave[cur_game]` (RAP.C:836,
  0-based per episode); Godot gameplay seeds `1024 * waveNum` (1-based), an apparent
  off-by-one for the same map. The Godot **demo** path seeds 0-based, which is why
  `full_demo` aligns but a scripted gameplay scenario on a denser wave would not.
- Matching seeds only gets you matching *streams*; any divergence in the **number**
  of `random()` draws (e.g. the MiniGun/`snd-patch` desync fixed in `deab7ca`)
  cascades through the rest of the stream.

We want a new **death scenario** baseline — start mid-episode-1, hold fire without
dodging until the player dies — to validate the death sequence (the only path that
exercises death-explosion / death-prelude / body-crash logic). Seed-matching makes
that scenario impractical.

The game is mostly deterministic; `random()` drives a small minority of behaviour.
So instead of matching seeds, we make the RNG itself deterministic and identical on
both sides.

## Goals

- A blanket deterministic-RNG mode: `random(N) → N/2` (integer, `floor`) on both the
  C and Godot side, gated behind the existing test flag. Test-only; production keeps
  real RNG.
- Migrate **all** parity scripts to this mode; regenerate every golden; drop the
  now-meaningless seed plumbing.
- A new `death_wave<N>` scenario (episode-1 waves 2–9) that captures the player's
  death sequence and compares C vs Godot.

## Non-goals

- Episodes 2–4: Godot has only G1 (`FILE0001.GLB`) assets; out of scope.
- Detecting RNG-stream-*count* bugs in parity. This mode deliberately makes them
  inert (accepted tradeoff). Existing RNG fixes remain — they are still correct for
  the real (non-deterministic) game.
- Changing production RNG behaviour in either build.

## Design

> **Correction (2026-05-30, during execution):** A1 below assumed C needed a new
> `random()` patch under `RAPTOR_TEST_DETERMINISTIC`. In fact `GFX/types.h` already
> defines `random(x) → raptor_random(x)` returning `x>>1` when `RAPTOR_DETERMINISTIC_RNG`
> is set — the **same flag** Godot's `DeterministicRandom` and `SHOTS.C`/`ENEMY.C` use.
> So **no C patch is required**; the foundation is just *setting `RAPTOR_DETERMINISTIC_RNG=1`*
> during capture + L2a. The flag of record is `RAPTOR_DETERMINISTIC_RNG` (not
> `RAPTOR_TEST_DETERMINISTIC`, which remains clock-only). The plan reflects this.

### Part A — Deterministic-RNG migration (foundation)

**A1. C patch (`dosraptor`).** Today `random(x)` is `#define random(x) (rand()%x)`
(dos_compat.h:215) and `RAPTOR_TEST_DETERMINISTIC` only makes the clock deterministic
(gfx_sdl.c:75). Route `random` through a single function:

```c
int raptor_random(int x);   // returns g_deterministic ? (x>0 ? x/2 : 0) : (rand()%x)
#define random(x) raptor_random(x)
```

`g_deterministic` is the existing flag. This one chokepoint covers all ~100 call
sites. The `SHOTS_DeterministicMiniGun` special-case (SHOTS.C:24, 788–805) becomes
redundant — `random(width)→width/2 == hlx` reproduces its aim formula — and is
removed. The C patch is committed in `dosraptor` separately.

The Godot equivalent collapses the same way: `PickRandomEnemy` drawing the midpoint
index (`visible[count/2]`) equals the existing `PickMiddleEnemy`, so the
`DeterministicMiniGun` branch in `PlayerShooter` is removed in favour of the single
chokepoint.

**A2. Godot audit + chokepoint.** Funnel every RNG draw through one deterministic
helper that returns `maxValue/2` when the flag is set (today only draws via
`DeterministicRandom.NextOrMidpoint` do). Audit and convert every other site:
`WaveController.Rng.*`, `_shooterRng.*`, `BulletLogic`/`EnemyLogic` direct `.Next()`,
etc. The flag is unified so one env var drives the whole port.

**A3. Regenerate goldens; retire seed sweep.** Regenerate every script golden
(`mission_start`, `mission_long`, `full_demo`, `menu_demo`, plus the new death
scenario) in deterministic mode via `dosraptor/tests/parity_capture.sh` (two
byte-identical runs per script = determinism gate). Retire the 100-seed sweep
(`tests/parity/seeds/`, the `RAPTOR_RNG_SEED_OVERRIDE` capture loop) — meaningless
once `random()` ignores the seed. The `1024*wave` seeding code itself becomes inert
and may be left in place or simplified opportunistically; no need to rip it out
(avoid churn / risk on the hot path).

### Part B — Death scenario (depends on A)

**B1. `RAPTOR_START_WAVE=N` env hook.** C: after the new-game reset (RAP.C:1715–1719)
set `game_wave[cur_game] = N-1`, so the existing `RAP_LoadMap` (LOADSAVE.C:371,
`MAP{game_wave+1}G{cur_game+1}_MAP`) loads `MAP<N>G1`. Godot mirror sets the gameplay
wave so `WaveController.LoadWave` loads `MAP<N>G1_MAP.json`. With deterministic RNG
the seed is irrelevant, so no seed reconciliation is needed.

**B2. `death_wave<N>.txt` script.** Reuse the existing menu-nav prefix (new pilot →
difficulty → sector select → Do_Game), then `down <fire-key>` with no movement and
`wait`/`checkpoint` markers until shield reaches 0 and the death sequence completes,
then `quit`. The wave and (optionally) difficulty are chosen empirically so a
stationary firing player reliably dies in a bounded window; the C run is the oracle
for "does it die, and when".

**B3. Golden + comparison.** Capture the C golden in deterministic mode; run Godot;
compare with the existing L2a comparator (`tests/comparator/parity_diff.py`). Register
the scenario in the coverage matrix and `scenarios.json` per the extension rule.

### Data flow

```
RAPTOR_TEST_DETERMINISTIC=1 (+ RAPTOR_START_WAVE=N for death)
        │
        ├─ C build:    random(x) → x/2   ─┐
        └─ Godot:      every draw → x/2  ─┤→ identical sim behaviour
                                          │→ identical NDJSON checkpoints
                                          └→ parity_diff.py: PASS
```

## Properties

- **Order-independence**: `random(N)` returns a value depending only on `N` (= `N/2`,
  integer floor), never on how many draws preceded it. *Domain:* every RNG call site
  in deterministic mode, both builds.
- **Seed-independence**: in deterministic mode the full checkpoint stream is identical
  for any seed or no seed. *Domain:* whole-run output, both builds.
- **Run-determinism**: same script + deterministic mode ⇒ byte-identical golden across
  repeated runs. *Domain:* `parity_capture.sh` / `golden_capture.sh` output.
- **Cross-implementation equivalence**: for a given script in deterministic mode, C and
  Godot emit identical checkpoints (the parity goal; residual diffs are genuine
  non-RNG logic bugs). *Domain:* per-script golden vs Godot output.
- **Production isolation**: with the flag unset, RNG behaviour in both builds is
  unchanged (real `rand()` / `RandomNumberGenerator`). *Domain:* default (non-test) runs.

## Validated dependencies

- C `random(x)` = `#define random(x) (rand()%x)` — verified (dos_compat.h:215).
- Godot `DeterministicRandom.NextOrMidpoint` returns `maxValue/2` when enabled — verified.
- C map naming `MAP{game_wave[cur_game]+1}G{cur_game+1}_MAP` — verified (LOADSAVE.C:371).
- C seed `1024 * game_wave[cur_game]` — verified (RAP.C:836); becomes inert.
- Godot `WaveController.LoadWave` → `MAP{waveNum}G1_MAP.json` — verified
  (MazeLevelLoader.cs:79).
- **UNVERIFIED — all-Godot-draws-funnelled:** that every Godot RNG site can be routed
  through one deterministic chokepoint with none bypassing it. Resolve with an audit +
  a grep-gate test.
- **UNVERIFIED — C↔Godot byte-equivalence under determinism:** that blanket `N/2`
  actually yields matching checkpoints (no other nondeterminism source). Resolve with a
  tracer: apply the C patch, regenerate one golden, diff against Godot before building
  the rest.

## Risks / tradeoffs

- **Loss of RNG-count-bug detection** (accepted). Mitigation: the fixes already landed
  stay; if desired later, a single seeded scenario can be re-added as a canary.
- **Non-RNG nondeterminism** (uninitialised memory, pointer-dependent order) could break
  byte-equivalence. Mitigation: the 2-run determinism gate in capture, plus the tracer.
- **Godot draws bypassing the chokepoint** would silently diverge. Mitigation: audit +
  a test asserting no direct RNG calls outside the chokepoint.
- **Death not reliably reached** on the chosen wave. Mitigation: the C run is the oracle;
  pick wave/difficulty empirically; bound the script with a max-wait + `quit`.

## Testing

- Tracer (blocking): C patch → regenerate `mission_start` golden → confirm 2-run
  determinism → diff vs Godot deterministic output.
- Per-script L2a PASS after regeneration (expect ↑ toward 100%).
- Godot unit tests: deterministic chokepoint returns `N/2`; a guard test that no RNG
  site bypasses it.
- New `death_wave<N>` L2a PASS.
- `ci/full.sh` green.

## Consequences to record on completion

- Close deferred RNG-cluster tasks (#4 body-crash, #5 death-explosion, #6 death-prelude,
  spark ordering) — non-issues under determinism.
- Remove seed-sweep references from docs/CI; note in state.md that parity no longer
  relies on seed-matching (seeding code left inert).
