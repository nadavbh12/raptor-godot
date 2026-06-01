# Missing View-Layer Cosmetics — Design

_Date: 2026-06-01. Status: approved design, pre-plan._

## Goal

Implement the deferred View-layer cosmetic effects from the parity backlog so the
Godot port visually matches the DOS C original in features that were previously
skipped. All work is **rendering only**; none of it may change sim state, the
parity checkpoint schema, or the 12-scenario parity gate.

Scope was chosen by the user (full set) after a dependency-verification pass that
materially corrected two items (see Validated Dependencies). Seven features:

1. Muzzle flash (the visible `A_PLAYER_SHOOT`/`GUNSTR_BLK` anim — **not** the `g_flash` ship-swap)
2. Scanner damage bar
3. MegaBomb full-screen flash + `A_SUPER_SHIELD` anim
4. Ground-explosion scroll drift
5. Boss low-health smoke
6. ES_LASER vertical-column beam
7. Super-shield HUD counter (gated on an extractor fix)

## Background

The C authority is `dosraptor/SOURCE/*.C`. The port renders in `src/View/`
(`DebugRenderer._Draw()` is the per-frame entry; it already holds a
`WaveController` reference and iterates `_wave.GetExplosions()`, drawing each at
`age = GameLoopIter - StartIter`). Sim explosions live in
`WaveController._explosions` and **gate the end-wave sequence** (wave ends when no
enemies and no explosions remain), so they are parity-relevant. The port already
models only enemy-death explosions there; other C anims (sparks, muzzle flashes)
are not modeled — establishing the precedent that cosmetic-only anims stay out of
the sim.

## Architecture & Parity Principle

All seven effects are **View-only and parity-inert**:

- A new View-side cosmetic-effects list (owned by `DebugRenderer`, e.g. a
  `ViewEffects` helper) holds short-lived anims with the same `(spriteName,
  startFrame, x, y)` + age model the renderer already uses for explosions. It is
  ticked by the View's own frame counter and drawn in `_Draw()`.
- Effects are **spawned by observing sim state the View already reads** (player
  fire events, enemy positions/health, megabomb-detonation events, scroll state).
  They never enter `WaveController._explosions`, never affect the end-wave gate,
  never appear in the NDJSON checkpoint schema, and never draw an RNG value from
  the sim.
- Any positional "randomness" is the deterministic `x/2` form (e.g. boss-smoke
  offset = `width/2, height/2`), computed in the View without touching sim RNG.
- The small sim-side reads the View needs (below) are **read-only queries / a
  per-tick event list that is not checkpointed** — they expose existing state, they
  do not add hashed or compared state.

**Acceptance bar:** the full 12-scenario parity gate (4 CI: `mission_start`,
`mission_long`, `full_demo`, `menu_demo`; 8 death waves) stays **byte-identical to
baseline** after every feature.

### Sim-side reads to add (read-only, non-checkpointed)

- **#1 fire signal:** the shooter exposes a per-tick list of muzzle offsets fired
  this tick (e.g. `playerCx ± OGun1/OGun2[pic]`, cleared each tick). The View spawns
  one `GUNSTR_BLK` anim per offset. (`GunOffsets.OGun1/OGun2` already exist.)
- **#2 base damage:** a port `EnemyLogic.GetBaseDamage()` mirroring
  `ENEMY_GetBaseDamage()` (ENEMY.C:1270), returning the current damage value that
  drives the scanner bar width.
- **#5 boss state:** surface `bossflag` from `SpriteMeta` (currently a comment
  only, SpriteMeta.cs:60) and the enemy `hits` value, so the View can test
  `bossflag && hits < 50`.

## Per-Feature Design

### 1. Muzzle flash (`GUNSTR_BLK`)
The visible flash is the `A_PLAYER_SHOOT` anim (= `GUNSTR_BLK`, anim id 16, 4
frames), spawned at the gun on each forward/secondary-gun shot (SHOTS.C:679, 696,
850, 866). The View spawns a `GUNSTR_BLK` cosmetic anim at each muzzle offset
reported by the shooter for that tick.

**Explicitly out of scope:** the `g_flash` ship-frame swap (RAP.C:1060/1077). In
episode 1 — the only shipped episode — `RAP_GetShipPic` sets `lightflag=TRUE`, so
`curship[loop+7] == curship[loop]`; the swap is a visual no-op. It only differs in
ep2 wave8 / ep3 wave3 (unshipped). Implementing it would render nothing.

### 2. Scanner damage bar (OBJECTS.C:640-655)
When the player holds `S_DETECT` and `GetBaseDamage() > 0`, draw two filled boxes
in the HUD: a frame `ColorBox(109, MAP_BOTTOM+9, 102, 8, color74)` and a fill
`ColorBox(110, MAP_BOTTOM+10, dmg, 6, color68)` where `dmg` is the base-damage
value (bar width). When `GetBaseDamage() == 0`, the existing idle bouncing-`VLine`
state (already implemented in `HudScannerIndicator`) is kept. This completes the
existing idle-only indicator by adding the `BuildDamage()` branch. Colors 74/68
resolve via `HudPalette`; coordinates use the port's existing HUD layout.

### 3. MegaBomb flash + `A_SUPER_SHIELD` (SHOTS.C:1273 / RAP.C:1127-1138)
On megabomb detonation (already detected in `ShotDoneDispatcher`), the sim raises a
read-only one-shot View event. The View then plays a full-screen palette-style
white-out flash toward `(63,60,60)` followed by a short return, a brief
screen-shake offset for the flash duration, and an `A_SUPER_SHIELD` anim
(= `SHIPGLOW_BLK`, 4 frames). Exact frame counts are cosmetic and tuned visually.

### 4. Ground-explosion scroll drift (ANIMS.C:418)
While the map is scrolling, GROUND-type explosions drift `+1px` in Y per frame. The
port applies this as a **render-time Y offset** on GROUND explosions only (computed
from the explosion's age and the scroll state) — it does not mutate the sim
explosion's stored position.

### 5. Boss low-health smoke (ENEMY.C:1076-1085)
For each enemy with `bossflag` whose `hits < 50`, on every other View frame
(`gl_cnt & 2`) spawn a `SMFLAK_BLK` anim (= `A_SMALL_AIR_EXPLO`, anim id 6) at a
deterministic offset within the boss bounds (`x + width/2, y + height/2`). C uses
`random(width)/random(height)`; under determinism that is `width/2, height/2`,
computed directly in the View with no RNG draw.

### 6. ES_LASER beam column (ESHOT.C:558-573)
For each enemy shot of `type == ES_LASER`, draw: the `ELASER_BLK` column from
`shot.y` down to `shot.move.y2` stepping by 3px; `ELASEPOW_BLK[curframe-1]` at the
gun; and the `lashit[curframe-1]` impact sprite at `move.y2 - 8` (clamped to
on-screen). Never visible in normal play (secret-cheat-gated, MAP8G1 only) — value
is completeness; included at user request.

### 7. Super-shield HUD counter (OBJECTS.C:663-672)
When `Inventory.GetTotal(S_SUPER_SHIELD) > 0`, draw `SMSHIELD_PIC` at
`(MAP_LEFT+2 + 13·i, 1)` for `i` in `[0, GetTotal)`.

**Prerequisite — extractor fix:** `SMSHIELD_PIC` is GLB item i=966, 270 bytes, but
its stored name is `"SMSHIELD_PIC//"`. The extractor's strict `_PIC` suffix check
(`tools/extract_assets/main.c` `extract_sprites`) silently skips it. Fix: tolerate
a trailing non-identifier run in the suffix test **and** strip it before composing
the output filename, so the sprite writes as `SMSHIELD_PIC.png`. Re-extract and add
`SMSHIELD_PIC.png` to `assets/sprites/`. The port resolves sprites **by item name**,
so the `pic_seq` numeric-prefix shift from including new items does not break any
existing reference.

## Properties / Invariants

- **Parity-inert**: *Domain:* all 7 features. After each feature the 12-scenario
  parity gate is byte-identical to the recorded baseline; no sim RNG draw is added;
  no cosmetic anim enters `_explosions` or changes wave-end timing or the NDJSON
  schema.
- **Spawn-once**: *Domain:* muzzle flash (#1), megabomb (#3), boss-smoke (#5).
  Exactly one anim is spawned per discrete trigger event — one per reported muzzle
  offset per tick, one flash per detonation, one smoke per `gl_cnt & 2` tick per
  qualifying boss — with no duplicate spawns within a single frame.
- **Damage-bar fidelity**: *Domain:* scanner bar (#2). Rendered fill width equals
  the `GetBaseDamage()` value (drawn raw, as C does — no port-side clamp; its
  natural range is `[0, 102]` given the 102-wide frame box); the idle bouncing-line
  branch renders iff `GetBaseDamage() == 0`. If `GetBaseDamage()`'s C range is found
  to exceed 102, the port matches C's raw behavior rather than clamping.
- **Icon-count exactness**: *Domain:* super-shield HUD (#7). Number of
  `SMSHIELD_PIC` icons drawn equals `Inventory.GetTotal(S_SUPER_SHIELD)`; icon `i`
  is at x `MAP_LEFT + 2 + 13·i`.
- **Deterministic positions**: *Domain:* boss-smoke (#5). Spawn offset is exactly
  `(width/2, height/2)` — no RNG, reproducible frame-to-frame.

## Validated Dependencies

Verified this session by reading C source and the extracted atlas, and by re-running
the extractor:

- **Extracted, confirmed present:** `GUNSTR_BLK` (4f), `SHIPGLOW_BLK` (4f),
  `SMFLAK_BLK` (6+f), `ELASER_BLK` (4f), `ELASEPOW_BLK` (4f), `GEXPLO_BLK`.
- **Procedural, no sprite:** scanner damage bar (`GFX_ColorBox`).
- **Sprite-load by name:** the port's `IHost.LoadSprite(itemName)` resolves by item
  name, so re-extraction prefix shifts are safe.
- **`SMSHIELD_PIC` (#7):** exists in GLB (item i=966, sz=270) but stored as
  `"SMSHIELD_PIC//"`; skipped by the extractor's strict suffix. Fix is small and
  verified by instrumenting the extractor (debug confirmed `suffix_pic=0` for that
  name). Re-extraction is the unblock path.
- **Sim reads to add (read-only, parity-inert):** `EnemyLogic.GetBaseDamage()`
  (mirror ENEMY.C:1270 — not yet present), `bossflag` from `SpriteMeta` (comment
  only today) + enemy `hits`, a per-tick muzzle-offset signal from the shooter.
- **UNVERIFIED — `lashit` sprite (#6):** no `LASHIT*` found in the atlas. The plan
  must carry a blocking check (un-skipped) that resolves the impact sprite before
  building #6; if it cannot be resolved, #6 renders the column + power only and the
  impact sprite is dropped (documented, not silently).

## Verification

Each feature is validated two ways:

1. **Pure-logic unit tests** where math is involved: scanner bar width/clamp and
   idle-vs-damage branch (#2); super-shield icon count and x positions (#7);
   muzzle-offset spawn list (#1); boss-smoke cadence (`gl_cnt & 2`) and deterministic
   offset (#5); ground-drift Y offset (#4).
2. **Side-by-side visual capture** (closes the original request): per-feature
   trigger scripts authored for both C and Godot — a megabomb-fire run (#3), a
   scanner-with-enemies-alive run (#2), a boss-damage run (#5), the wave-8 laser
   cheat (#6), a super-shield-pickup run (#7), and any forward-guns run (#1) — then
   `tests/run_visual_audit.sh` (or `build_compare_video.sh`) produces aligned C/Godot
   frames + diffs for human review. These features are not parity-scored (View
   layer), so visual review + the green parity gate is the acceptance signal.

The full 12-scenario parity gate is re-run after each feature, and especially after
#5 (the only feature that reads boss/`hits` state), to confirm byte-identical
output.

## Implementation Order

Safe/visible first, risky/niche last:

1. **#1 muzzle flash**, **#2 scanner bar**, **#4 ground drift** — visible,
   View-only, low risk.
2. **#3 megabomb flash + anim**.
3. **#7 super-shield HUD** — preceded by the extractor fix + re-extraction.
4. **#5 boss low-health smoke** — re-run the full parity gate afterward.
5. **#6 ES_LASER beam** — niche; resolve the `lashit` dependency first.

## Out of Scope

- The `g_flash` ship-frame swap (invisible in episode 1; see #1).
- Shadow detail-level gating (port renders shadows correctly; the `opt_detail`
  gate is moot since the port runs at full detail).
- Any change to sim state, the parity checkpoint schema, or the seeded/deterministic
  RNG behavior.
- Episodes 2-4 assets (only G1 ships in `FILE0001.GLB`).
