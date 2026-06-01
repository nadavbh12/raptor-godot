# Cross-Repo Visual-Capture Infrastructure (7 View Cosmetics) — Implementation Plan

> **For agentic workers:** Execution is controller-run (serial, interactive mouse-grab C captures cannot be delegated to subagents). Code hooks use TDD; parity-safety rides on the existing 12-scenario L2a gate. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Produce true, frame-aligned C-vs-Godot side-by-side+diff images for all 7 View cosmetics on branch `feat/view-cosmetics`, so the user can eyeball-review them before merge.

**Architecture:** Reuse the existing visual-audit pipeline (`tests/build_compare_video.sh` → `build_iter_aligned_pairs.py` → `visual_audit.py`). Both C and Godot child processes inherit exported env vars, so captures are driven by setting envs. Clean diffs come from `ITER_ALIGN=1` (pairs frames by identical game-loop iteration → only rendering differs) + `RAPTOR_DETERMINISTIC_RNG=1` (same iter = same sim state both sides). **No manual anchor recalibration (#27) is needed** — ITER_ALIGN sidesteps temporal offset.

The 5 cosmetics not reachable in `full_demo` need trigger state forced on **both** binaries. Trigger surface:
- **Reach any wave 1–9:** `RAPTOR_START_WAVE` (already exists, both binaries).
- **Survive to the boss (invuln):** C godmode via `S_HOST=CASTLE` (already exists, RAP.C:1414). Godot has **no** godmode path → new `RAPTOR_GODMODE` hook.
- **Own items (detect / super-shield / megabomb):** new `RAPTOR_GRANT` hook (both binaries): C `OBJS_Add`, Godot `Inventory.Add`.
- **Boss at hits<50 (smoke):** new `RAPTOR_BOSS_LOWHP=<n>` hook (both): clamp a boss enemy's hits at spawn.
- **ES_LASER secret beam (wave 8):** best-effort — investigate secret-enemy gating; may stay marginal.

**Parity-safety (the non-negotiable invariant):** every new hook is **env-gated and off by default**. The 12 parity scenarios never set these envs, so their behavior is byte-identical. Proven by re-running the full L2a gate (Task 5). New hooks live behind a single `getenv`/`GetEnvironment` check each; no default code path changes.

**Boss waves (verified from extracted map data):** every wave 1–9 spawns exactly one boss-flagged sprite (slib 28–34/110–113/128–130, hits 300–750), most at map top row (appears late in the wave). With invuln the player survives to it.

**Tech Stack:** Godot 4.6.2 Mono / C# (`src/Sim`, `src/View`); C dosraptor (`SOURCE/*.C`, `port/platform`); Python/PIL for compositing; existing bash audit harness.

---

## Cosmetic → trigger matrix

| # | Cosmetic | Source ref | Trigger | C-vs-Godot feasibility |
|---|---|---|---|---|
| 1 | Muzzle flash (GUNSTR_BLK) | SHOTS.C:679/696/850/866 | `full_demo` (ForwardGuns) | easy |
| 2 | Ground-explosion scroll drift | ANIMS.C:418 | `full_demo` ground explosions | easy (subtle 1px) |
| 3 | Super-shield HUD counter | OBJECTS.C:663-672 | `RAPTOR_GRANT=supershield` | medium |
| 4 | Scanner / boss-health bar | OBJECTS.C:640-655, ENEMY.C:1270 | `RAPTOR_GRANT=detect` + boss wave + invuln | medium |
| 5 | MegaBomb white-out flash | RAP.C:1127, SHOTS.C:1273 | `RAPTOR_GRANT=megabomb` + fire-megabomb script | medium |
| 6 | Boss low-health smoke (SMFLAK_BLK) | ENEMY.C:1076-1085 | boss wave + invuln + `RAPTOR_BOSS_LOWHP` | medium-hard |
| 7 | ES_LASER beam column | ESHOT.C:558-573 | wave 8 + secret enemy + invuln | hard (best-effort) |

---

## Task 0: Validate the ITER_ALIGN pipeline (no code) — delivers cosmetics #1, #2

**Files:** none (uses existing harness).

- [ ] Build the C# project (headless) so Godot has current assemblies.
- [ ] Export `RAPTOR_DETERMINISTIC_RNG=1`; run `ITER_ALIGN=1 tests/run_visual_audit.sh full_demo`.
- [ ] Open the iter-aligned report; confirm same-iter C/Godot pairs are clean (sim matches, only rendering differs). If not clean, ITER_ALIGN is insufficient → fall back to `VISUAL_ALIGN=1` and note it.
- [ ] Find the iters where (a) a forward-gun shot fires (muzzle flash) and (b) a ground explosion is mid-scroll. Composite C | Godot | diff crops (zoomed on the gun / explosion). Send to user.

**Gate:** muzzle flash + ground drift visible and aligned in C-vs-Godot crops.

## Task 1: Godot invuln hook (`RAPTOR_GODMODE`)

**Files:** Modify `src/Sim/WaveController.cs` (`ApplyPlayerDamage`, ~1439); Test `tests/...` (a pure-helper test if extractable, else rely on the parity gate + a smoke check).

- [ ] TDD where unit-testable: gate damage to 0 when `RAPTOR_GODMODE=1`. Mirror C semantics (RAP.C:560/619 — no death, no low-shield special loss). Read the env once (cache in a field at ctor like `_quitAfterDeath`).
- [ ] Build + test. The env is off in all parity scenarios → inert.

## Task 2: Godot grant hook (`RAPTOR_GRANT`)

**Files:** Modify `src/Sim/WaveController.cs` (wave-init / `SetupDemoPlayer`); Test `tests/InventoryTests.cs` or new.

- [ ] Parse `RAPTOR_GRANT` (comma list of `detect|supershield|megabomb`) → `Inventory.Add(ObjType.Detect|SuperShield|MegaBomb)` at wave init. Map names → ObjType (Detect=17, SuperShield=15, MegaBomb=11).
- [ ] Unit-test the name→ObjType parse (pure helper). Build + test. Off by default → inert.

## Task 3: Godot boss-lowhp hook (`RAPTOR_BOSS_LOWHP`)

**Files:** Modify `src/Sim/WaveController.cs` (after `_enemies.Add(new EnemyLogic(...))`, ~687); `src/Sim/Enemy/EnemyLogic.cs` (add a debug `SetHits`/clamp); Test.

- [ ] When `RAPTOR_BOSS_LOWHP=<n>` set and a spawned enemy `IsBoss`, clamp its hits to `n` (keep `MaxHits` so the boss-health-% stays sensible — pick `n` so `hits<50` for smoke and the bar shows a low %).
- [ ] Unit-test the clamp helper. Build + test. Off by default → inert.

## Task 4: C grant + boss-lowhp hooks; rebuild dosraptor

**Files:** Modify dosraptor `SOURCE/RAP.C` (wave-load) or `SOURCE/OBJECTS.C` / `SOURCE/ENEMY.C` (spawn) — env-gated, mirroring the Godot semantics.

- [ ] `RAPTOR_GRANT` → `OBJS_Add(type)` at the same wave-load point the existing `RAPTOR_START_WAVE` uses (LOADSAVE.C / RAP.C), once per wave.
- [ ] `RAPTOR_BOSS_LOWHP` → clamp a boss enemy's `hits` when it spawns (ENEMY.C spawn path, gated on the sprite's bossflag).
- [ ] Rebuild the C binary (`dosraptor/build/...`). Verify it still runs a normal capture.

## Task 5: Parity gate — prove the hooks are inert

- [ ] Run the full 12-scenario L2a gate (serial, ~18–20 min) with **no** new envs set. Expect byte-identical to baseline: mission_start 100%, mission_long 99.1%, full_demo 100%, menu_demo 100%, death_wave1/2/4-9 100%, (death_wave3 98.7% if run). Any drift = a hook leaked into the default path → fix before capturing.

## Task 6: Capture the 5 forced cosmetics (controller-run, serial mouse-grab)

For each: export the trigger envs + `RAPTOR_DETERMINISTIC_RNG=1` + `ITER_ALIGN=1`, run `build_compare_video.sh` with the boss-wave/no-input playthrough, iter-align, locate the cosmetic frame.

- [ ] #3 super-shield HUD — `RAPTOR_GRANT=supershield`, any wave (HUD is always painted).
- [ ] #4 scanner bar — `RAPTOR_GRANT=detect` + `RAPTOR_START_WAVE=<boss wave>` + godmode/invuln; capture when the boss is on screen.
- [ ] #5 megabomb flash — `RAPTOR_GRANT=megabomb` + a playthrough that fires the megabomb key; capture the white-out frame.
- [ ] #6 boss smoke — boss wave + invuln + `RAPTOR_BOSS_LOWHP` (hits<50); capture when boss on screen.
- [ ] #7 ES_LASER — wave 8 + invuln + secret-enemy trigger; capture iter ~828 if reachable. **Best-effort; report if marginal.**

## Task 7: Compose + deliver + record

- [ ] Composite C | Godot | diff for all 7 cosmetics; send to user with per-cosmetic notes (and honest feasibility caveats for #6/#7).
- [ ] Update `~/.agent-state/raptor-godot--main/state.md` (Session Log + Current Status).
- [ ] After user review: `superpowers:finishing-a-development-branch` to merge `feat/view-cosmetics` → main (includes the now-reverted/kept capture hooks — keep them, they're parity-inert test infra).

---

## Self-review notes
- **Spec coverage:** all 7 cosmetics have a trigger row + a capture task. #6/#7 flagged best-effort.
- **Parity-safety:** Task 5 is the gate; every hook is a single env check, off by default.
- **YAGNI:** reuses existing godmode (`S_HOST`), start-wave, and audit harness; only 3 Godot + 2 C tiny hooks are new. No new capture tooling beyond a PIL composite snippet.
- **Risk:** ES_LASER secret-enemy gating (#7) and getting a boss into the capture window late in a wave (#4/#6) are the real unknowns; both are reported honestly rather than silently dropped.
