# Missing View-Layer Cosmetics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement seven deferred View-layer cosmetic effects (muzzle flash, scanner/boss-health bar, megabomb flash+anim, ground-explosion scroll drift, boss low-health smoke, ES_LASER beam, super-shield HUD) so the Godot port matches the DOS C original, without changing any sim/parity behavior.

**Architecture:** All effects are View-only and parity-inert. A shared `ViewEffects` list holds short-lived cosmetic anims (same name+age model the renderer already uses for explosions), spawned by `DebugRenderer._Draw()` from sim state it already reads. Nothing enters `WaveController._explosions`, the end-wave gate, the NDJSON checkpoint schema, or draws sim RNG. Pure logic is extracted into testable static helpers (matching the existing `HudShieldBar`/`HudScannerIndicator` pattern); the thin Godot drawing is verified visually + by the parity gate staying byte-identical.

**Tech Stack:** C# (.NET 8), Godot 4.6.2 Mono, xUnit + FsCheck (`[Property]`) for tests. Sim rules: no `delta`/`_Process`/engine-RNG/wall-clock in `src/Sim/`.

**Spec:** `docs/superpowers/specs/2026-06-01-missing-view-cosmetics-design.md`

---

## Environment prelude (run once per shell)

```bash
export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"
export DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec
export DOTNET_ROLL_FORWARD=Major
cd /Users/nadavb/dev/raptor-godot
```

**Build:** `dotnet build raptor.csproj --nologo --verbosity minimal`
**Test (all):** `cd tests && dotnet test --nologo` (then `cd ..`)
**Test (one):** `cd tests && dotnet test --nologo --filter "FullyQualifiedName~<TestClass>" ; cd ..`
**Parity gate (per scenario):** `bash tests/run_l2a.sh <scenario>` — the 12-scenario set is `mission_start mission_long full_demo menu_demo death_wave1 death_wave2 death_wave4 death_wave5 death_wave6 death_wave7 death_wave8 death_wave9`. "Byte-identical to baseline" = each scenario's score matches the values recorded in the session state (4 CI PASS at 100/99.1/100/100; 8 death waves 100% gameplay). Run serially — never in parallel (SIGABRT; see session-state Known Traps).

> **Parity discipline:** Because every feature here is View-only, the standing acceptance check after each feature is: build + full unit suite green, AND the relevant parity scenarios stay byte-identical. `#5` (boss smoke) is the only feature reading boss/`hits` state — re-run the **full** 12-scenario gate after it.

---

## File Structure

**New files:**
- `src/View/ViewEffects.cs` — cosmetic-anim list (pure logic + tiny state); spawned by the renderer.
- `src/View/HudSuperShieldIndicator.cs` — pure helper: super-shield HUD icon positions.
- `tests/ViewEffectsTests.cs`
- `tests/HudScannerIndicatorTests.cs` (if absent — add the damage-bar tests here)
- `tests/HudSuperShieldIndicatorTests.cs`
- `tests/CosmeticAssetPresenceTests.cs` — tracer tests asserting required sprite PNGs exist on disk.
- `tests/parity/scripts/*.parity.txt` trigger scripts (authored later, for visual capture only — not CI).

**Modified files:**
- `src/View/HudScannerIndicator.cs` — add `Box` record + `BuildDamage(int)`.
- `src/View/DebugRenderer.cs` — spawn/draw `ViewEffects`; damage-bar branch in `DrawScannerHud`; ground-drift offset; ES_LASER column; super-shield HUD draw.
- `src/Sim/WaveController.cs` — `GetBaseDamage()`; read-only per-tick muzzle list; read-only one-shot megabomb-detonation flag.
- `src/Sim/Shots/PlayerShooter.cs` — record muzzle positions per shot.
- `src/Sim/Shots/ShotDoneDispatcher.cs` — signal megabomb detonation to the View flag.
- `src/Sim/Enemy/SpriteMeta.cs` — surface `bossflag` (and confirm base `Hits`).
- `dosraptor/tools/extract_assets/main.c` — tolerate + strip trailing `//` in `_PIC`/`_BLK` names (Task 7).
- `assets/sprites/SMSHIELD_PIC.png` — added by re-extraction (Task 7).

---

## Task 0: ViewEffects cosmetic-anim infrastructure

Shared by Tasks 1, 3, 5. A pure list of cosmetic anims keyed to the game-loop iteration clock (same age model as `WaveController.AnimationAge`).

**Files:**
- Create: `src/View/ViewEffects.cs`
- Test: `tests/ViewEffectsTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/ViewEffectsTests.cs
using System.Linq;
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class ViewEffectsTests
{
    [Fact]
    public void Spawned_effect_is_active_for_its_frame_span_then_pruned()
    {
        var fx = new ViewEffects();
        fx.Spawn("GUNSTR_BLK", totalFrames: 4, x: 100, y: 50, spawnIter: 10, ground: false);

        // age 0..3 visible
        Assert.Single(fx.Active(10));
        Assert.Equal(0, fx.Active(10).Single().Frame);
        Assert.Equal(3, fx.Active(13).Single().Frame);
        // age 4 == past end → pruned, nothing active
        Assert.Empty(fx.Active(14));
    }

    [Fact]
    public void Active_reports_position_and_family_unchanged()
    {
        var fx = new ViewEffects();
        fx.Spawn("SHIPGLOW_BLK", totalFrames: 4, x: 7, y: 9, spawnIter: 0, ground: true);
        var e = fx.Active(1).Single();
        Assert.Equal("SHIPGLOW_BLK", e.Family);
        Assert.Equal(7, e.X);
        Assert.Equal(9, e.Y);
        Assert.True(e.Ground);
        Assert.Equal(1, e.Frame);
    }

    [Fact]
    public void Prune_removes_expired_so_the_list_does_not_grow_unbounded()
    {
        var fx = new ViewEffects();
        fx.Spawn("GUNSTR_BLK", 4, 0, 0, spawnIter: 0, ground: false);
        fx.Prune(currentIter: 100);
        Assert.Equal(0, fx.Count);
    }
}
```

- [ ] **Step 2: Run to verify failure**

`cd tests && dotnet test --nologo --filter "FullyQualifiedName~ViewEffectsTests" ; cd ..`
Expected: FAIL (ViewEffects not defined).

- [ ] **Step 3: Implement `ViewEffects`**

```csharp
// src/View/ViewEffects.cs
using System.Collections.Generic;

namespace Raptor.View;

/// View-only cosmetic animations (muzzle flash, megabomb glow, boss smoke).
/// Parity-inert: never enters WaveController._explosions, the end-wave gate,
/// or the NDJSON schema. Age is measured against the game-loop iteration clock,
/// mirroring WaveController.AnimationAge.
internal sealed class ViewEffects
{
    public readonly record struct Anim(string Family, int TotalFrames, int X, int Y, int SpawnIter, bool Ground);
    public readonly record struct ActiveAnim(string Family, int Frame, int X, int Y, bool Ground);

    private readonly List<Anim> _anims = new();

    public int Count => _anims.Count;

    public void Spawn(string family, int totalFrames, int x, int y, int spawnIter, bool ground)
        => _anims.Add(new Anim(family, totalFrames, x, y, spawnIter, ground));

    public IEnumerable<ActiveAnim> Active(int currentIter)
    {
        foreach (var a in _anims)
        {
            int frame = currentIter - a.SpawnIter;
            if (frame >= 0 && frame < a.TotalFrames)
                yield return new ActiveAnim(a.Family, frame, a.X, a.Y, a.Ground);
        }
    }

    public void Prune(int currentIter)
        => _anims.RemoveAll(a => currentIter - a.SpawnIter >= a.TotalFrames);

    public void Clear() => _anims.Clear();
}
```

- [ ] **Step 4: Run to verify pass**

`cd tests && dotnet test --nologo --filter "FullyQualifiedName~ViewEffectsTests" ; cd ..`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/View/ViewEffects.cs tests/ViewEffectsTests.cs
git commit -m "View: add ViewEffects cosmetic-anim list (Task 0)"
```

---

## Task 1: Muzzle flash (GUNSTR_BLK)

### Task 1a: record muzzle positions in the sim (read-only, parity-inert)

`PlayerShooter.Shoot` already computes each bullet's spawn `(x, y)` — the muzzle. Accumulate them in a read-only list the View can read; `WaveController` clears it once per tick before shooting.

**Files:**
- Modify: `src/Sim/Shots/PlayerShooter.cs`
- Test: `tests/PlayerShooterMuzzleTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/PlayerShooterMuzzleTests.cs
using System.Collections.Generic;
using Raptor.Sim;
using Raptor.Sim.Shots;
using Xunit;

namespace RaptorTests;

public class PlayerShooterMuzzleTests
{
    [Fact]
    public void ForwardGuns_records_two_muzzle_positions_at_gun1_offsets()
    {
        var inv = new Inventory();
        var shooter = new PlayerShooter(inv);
        var sink = new List<BulletLogic>();
        shooter.ClearMuzzles();

        int pic = 3; // neutral
        shooter.Shoot(ObjType.ForwardGuns, playerCx: 160, playerCy: 180, playerPic: pic, sink: sink);

        Assert.Equal(2, shooter.Muzzles.Count);
        Assert.Equal((160 + GunOffsets.OGun1[pic], 180), (shooter.Muzzles[0].X, shooter.Muzzles[0].Y));
        Assert.Equal((160 - GunOffsets.OGun1[pic] - 1, 180), (shooter.Muzzles[1].X, shooter.Muzzles[1].Y));
    }

    [Fact]
    public void ClearMuzzles_resets_the_list()
    {
        var shooter = new PlayerShooter(new Inventory());
        var sink = new List<BulletLogic>();
        shooter.Shoot(ObjType.ForwardGuns, 160, 180, 3, sink);
        shooter.ClearMuzzles();
        Assert.Empty(shooter.Muzzles);
    }
}
```

- [ ] **Step 2: Run to verify failure**

`cd tests && dotnet test --nologo --filter "FullyQualifiedName~PlayerShooterMuzzleTests" ; cd ..`
Expected: FAIL (Muzzles/ClearMuzzles not defined).

- [ ] **Step 3: Implement the muzzle accumulator**

In `src/Sim/Shots/PlayerShooter.cs`, add near the class fields:

```csharp
    public readonly record struct MuzzlePos(int X, int Y);
    private readonly List<MuzzlePos> _muzzles = new();

    /// Read-only muzzle positions recorded since the last ClearMuzzles().
    /// View-only / parity-inert — never checkpointed.
    public IReadOnlyList<MuzzlePos> Muzzles => _muzzles;
    public void ClearMuzzles() => _muzzles.Clear();
    private void RecordMuzzle(int x, int y) => _muzzles.Add(new MuzzlePos(x, y));
```

Then, in `Shoot(...)`, immediately after each `sink.Add(BulletLogic.PlayerStraight(spawnX: SX, spawnY: SY, ...))`, add `RecordMuzzle(SX, SY);` using the same `spawnX`/`spawnY` expressions already present (ForwardGuns, PlasmaGuns, MicroMissile, MissilePods, and the remaining S_AIR/S_GROUND cases). Example for ForwardGuns:

```csharp
            int mxR = playerCx + GunOffsets.OGun1[pic];
            sink.Add(BulletLogic.PlayerStraight(
                spawnX: mxR, spawnY: playerCy,
                initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
            RecordMuzzle(mxR, playerCy);
            NextRandom(rng, lib.NumFrames, "forward.frame.l");
            int mxL = playerCx - GunOffsets.OGun1[pic] - 1;
            sink.Add(BulletLogic.PlayerStraight(
                spawnX: mxL, spawnY: playerCy, /* …unchanged… */
                initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
            RecordMuzzle(mxL, playerCy);
```

(Keep all existing `NextRandom`/`ConsumeRandomPitchSound` calls in place and unchanged — they preserve the RNG stream.)

- [ ] **Step 4: Run to verify pass**

`cd tests && dotnet test --nologo --filter "FullyQualifiedName~PlayerShooterMuzzleTests" ; cd ..`
Expected: PASS.

- [ ] **Step 5: Wire `ClearMuzzles` into the tick**

In `src/Sim/WaveController.cs`, at the start of the per-tick input/shoot phase (immediately before player shooting is processed), call `PlayerShooter.ClearMuzzles()`. Add a read-only passthrough so the View can reach it:

```csharp
    public IReadOnlyList<PlayerShooter.MuzzlePos> MuzzlesThisTick => PlayerShooter.Muzzles;
```

(Use the existing `PlayerShooter` instance field name in WaveController; if it is named e.g. `_shooter`, expose `_shooter.Muzzles` / call `_shooter.ClearMuzzles()`.)

- [ ] **Step 6: Verify parity unaffected**

Build, run the unit suite, then run two firing scenarios:
```bash
dotnet build raptor.csproj --nologo --verbosity minimal
cd tests && dotnet test --nologo ; cd ..
bash tests/run_l2a.sh mission_long
bash tests/run_l2a.sh full_demo
```
Expected: suite green; `mission_long` 99.1%, `full_demo` 100% — byte-identical to baseline (muzzle recording adds no RNG draw and no checkpoint state).

- [ ] **Step 7: Commit**

```bash
git add src/Sim/Shots/PlayerShooter.cs src/Sim/WaveController.cs tests/PlayerShooterMuzzleTests.cs
git commit -m "Sim: record per-tick player muzzle positions (read-only, parity-inert) (Task 1a)"
```

### Task 1b: spawn + draw GUNSTR_BLK in the View

**Files:**
- Modify: `src/View/DebugRenderer.cs`

- [ ] **Step 1: Add a ViewEffects field + per-sim-tick muzzle spawn**

In `DebugRenderer`, add `private readonly ViewEffects _effects = new();` and a guard frame `private int _lastMuzzleFrame = -1;`. In `_Draw()`, after `_wave` is confirmed non-null and before HUD drawing, add:

```csharp
        // Spawn muzzle-flash cosmetics once per sim tick.
        if (_lastMuzzleFrame != SimClock.Frame)
        {
            _lastMuzzleFrame = SimClock.Frame;
            int spawnIter = _wave.GameLoopIter;
            foreach (var m in _wave.MuzzlesThisTick)
                _effects.Spawn("GUNSTR_BLK", totalFrames: 4, x: m.X, y: m.Y, spawnIter: spawnIter, ground: false);
        }
        _effects.Prune(_wave.GameLoopIter);
```

- [ ] **Step 2: Draw active effects**

Add a `DrawViewEffects()` method and call it from `_Draw()` in world-draw order (after enemies/bullets, before HUD):

```csharp
    private void DrawViewEffects()
    {
        if (_wave == null) return;
        foreach (var e in _effects.Active(_wave.GameLoopIter))
        {
            var tex = LoadBlkFrame(e.Family, e.Frame);
            if (tex == null) continue;
            DrawTexture(tex, new Vector2(e.X - (int)tex.GetWidth() / 2, e.Y - (int)tex.GetHeight() / 2));
        }
    }
```

- [ ] **Step 3: Build + visually confirm + parity check**

```bash
dotnet build raptor.csproj --nologo --verbosity minimal
bash tests/run_l2a.sh full_demo   # must stay 100% (View-only)
```
Visual confirmation deferred to the capture step (Task 8). Expected: build clean, `full_demo` byte-identical.

- [ ] **Step 4: Commit**

```bash
git add src/View/DebugRenderer.cs
git commit -m "View: render GUNSTR_BLK muzzle flash via ViewEffects (Task 1b)"
```

---

## Task 2: Scanner / boss-health bar

### Task 2a: surface bossflag + base hits, add GetBaseDamage

`ENEMY_GetBaseDamage` (ENEMY.C:1270) averages `(hits*100)/maxHits` over on-screen bosses (`bossflag`, `y+hly >= 0`), returning 0 when none.

**Files:**
- Modify: `src/Sim/Enemy/SpriteMeta.cs`, `src/Sim/WaveController.cs`
- Test: `tests/EnemyBaseDamageTests.cs`

- [ ] **Step 1: Surface `bossflag` in SpriteMeta**

In `src/Sim/Enemy/SpriteMeta.cs`, add (and remove `bossflag` from the "not consumed" comment):

```csharp
    [System.Text.Json.Serialization.JsonPropertyName("bossflag")]
    public bool BossFlag { get; set; }
```

Confirm the base/max hit count is available as `Hits` on `SpriteMeta` (the JSON `hits` field). If it is exposed under a different name, use that name in `GetBaseDamage` below. (If `EnemyLogic` does not expose `Meta`, add `public SpriteMeta Meta => …` passthrough or a `public bool IsBoss => Meta.BossFlag;` + `public int MaxHits => Meta.Hits;`.)

- [ ] **Step 2: Write the failing test**

```csharp
// tests/EnemyBaseDamageTests.cs
using Raptor.Sim;
using Xunit;

namespace RaptorTests;

public class EnemyBaseDamageTests
{
    [Fact]
    public void No_bosses_on_screen_yields_zero()
    {
        Assert.Equal(0, WaveController.ComputeBaseDamage(System.Array.Empty<(bool boss, int y, int hly, int hits, int maxHits)>()));
    }

    [Fact]
    public void Single_boss_returns_health_percent()
    {
        var rows = new[] { (boss: true, y: 10, hly: 12, hits: 50, maxHits: 100) };
        Assert.Equal(50, WaveController.ComputeBaseDamage(rows));
    }

    [Fact]
    public void Offscreen_top_boss_is_excluded()
    {
        var rows = new[] { (boss: true, y: -30, hly: 12, hits: 50, maxHits: 100) };
        Assert.Equal(0, WaveController.ComputeBaseDamage(rows));
    }

    [Fact]
    public void Non_boss_is_ignored()
    {
        var rows = new[] { (boss: false, y: 10, hly: 12, hits: 50, maxHits: 100) };
        Assert.Equal(0, WaveController.ComputeBaseDamage(rows));
    }

    [Fact]
    public void Two_bosses_average_their_percents()
    {
        var rows = new[]
        {
            (boss: true, y: 10, hly: 12, hits: 100, maxHits: 100), // 100%
            (boss: true, y: 10, hly: 12, hits: 50,  maxHits: 100), // 50%
        };
        Assert.Equal(75, WaveController.ComputeBaseDamage(rows));
    }
}
```

- [ ] **Step 3: Run to verify failure**

`cd tests && dotnet test --nologo --filter "FullyQualifiedName~EnemyBaseDamageTests" ; cd ..`
Expected: FAIL (ComputeBaseDamage not defined).

- [ ] **Step 4: Implement the pure core + the live query**

In `src/Sim/WaveController.cs`:

```csharp
    /// Pure mirror of ENEMY_GetBaseDamage (ENEMY.C:1270): average health-% of
    /// on-screen bosses (y+hly >= 0), or 0 when none. Integer arithmetic matches C.
    internal static int ComputeBaseDamage(IEnumerable<(bool boss, int y, int hly, int hits, int maxHits)> rows)
    {
        int total = 0, nums = 0;
        foreach (var r in rows)
        {
            if (!r.boss) continue;
            if (r.y + r.hly < 0) continue;
            if (r.maxHits <= 0) continue;
            total += (r.hits * 100) / r.maxHits;
            nums++;
        }
        return nums > 0 ? total / nums : 0;
    }

    /// Live boss-health value driving the scanner bar (View reads this).
    public int GetBaseDamage()
        => ComputeBaseDamage(System.Linq.Enumerable.Select(GetEnemies(),
            e => (e.IsBoss, e.Y, e.HalfH, e.Hits, e.MaxHits)));
```

(Use the existing enemy-enumeration accessor; if it is not named `GetEnemies()`, substitute the actual one. `e.IsBoss`/`e.MaxHits` come from Step 1.)

- [ ] **Step 5: Run to verify pass**

`cd tests && dotnet test --nologo --filter "FullyQualifiedName~EnemyBaseDamageTests" ; cd ..`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Sim/Enemy/SpriteMeta.cs src/Sim/WaveController.cs src/Sim/Enemy/EnemyLogic.cs tests/EnemyBaseDamageTests.cs
git commit -m "Sim: ComputeBaseDamage (boss-health %) + surface bossflag/maxHits (Task 2a)"
```

### Task 2b: HudScannerIndicator.BuildDamage pure helper

**Files:**
- Modify: `src/View/HudScannerIndicator.cs`
- Test: `tests/HudScannerIndicatorTests.cs`

- [ ] **Step 1: Write the failing tests (incl. the Damage-bar-fidelity property)**

```csharp
// tests/HudScannerIndicatorTests.cs
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class HudScannerIndicatorTests
{
    [Fact]
    public void Damage_bar_draws_frame_then_fill_with_c_geometry()
    {
        var boxes = HudScannerIndicator.BuildDamage(damage: 60).ToArray();
        Assert.Equal(2, boxes.Length);
        // frame: ColorBox(109, MAP_BOTTOM+9=191, 102, 8, 74)
        Assert.Equal(new HudScannerIndicator.Box(109, 191, 102, 8, 74), boxes[0]);
        // fill: ColorBox(110, MAP_BOTTOM+10=192, damage, 6, 68)
        Assert.Equal(new HudScannerIndicator.Box(110, 192, 60, 6, 68), boxes[1]);
    }

    [Fact]
    public void Zero_damage_builds_no_boxes()
        => Assert.Empty(HudScannerIndicator.BuildDamage(0));

    // Property "Damage-bar fidelity" (spec §Properties): for any damage value the
    // fill width equals the raw damage (no clamp, as C draws it), frame is fixed,
    // and the damage branch is non-empty iff damage > 0.
    [Property(MaxTest = 50)]
    public Property Damage_bar_fill_width_equals_raw_damage()
    {
        return Prop.ForAll(Gen.Choose(0, 200).ToArbitrary(), dmg =>
        {
            var boxes = HudScannerIndicator.BuildDamage(dmg).ToArray();
            if (dmg <= 0) return boxes.Length == 0;
            return boxes.Length == 2
                && boxes[0] == new HudScannerIndicator.Box(109, 191, 102, 8, 74)
                && boxes[1] == new HudScannerIndicator.Box(110, 192, dmg, 6, 68);
        });
    }
}
```

- [ ] **Step 2: Run to verify failure**

`cd tests && dotnet test --nologo --filter "FullyQualifiedName~HudScannerIndicatorTests" ; cd ..`
Expected: FAIL (Box/BuildDamage not defined).

- [ ] **Step 3: Implement `Box` + `BuildDamage`**

Append to `src/View/HudScannerIndicator.cs` inside the class:

```csharp
    public readonly record struct Box(int X, int Y, int W, int H, int PaletteIndex);

    /// Mirrors OBJECTS.C:644-647 (boss-health/scanner bar). MAP_BOTTOM=182 →
    /// frame at +9 (191), fill at +10 (192). Width = raw damage value (no clamp).
    public static IEnumerable<Box> BuildDamage(int damage)
    {
        if (damage <= 0) yield break;
        yield return new Box(109, 191, 102, 8, 74);
        yield return new Box(110, 192, damage, 6, 68);
    }
```

- [ ] **Step 4: Run to verify pass**

`cd tests && dotnet test --nologo --filter "FullyQualifiedName~HudScannerIndicatorTests" ; cd ..`
Expected: PASS (3 tests incl. the property).

- [ ] **Step 5: Commit**

```bash
git add src/View/HudScannerIndicator.cs tests/HudScannerIndicatorTests.cs
git commit -m "View: HudScannerIndicator.BuildDamage boss-health bar + property test (Task 2b)"
```

### Task 2c: wire the damage branch into DrawScannerHud

**Files:**
- Modify: `src/View/DebugRenderer.cs`

- [ ] **Step 1: Branch on GetBaseDamage**

Replace the body of `DrawScannerHud()` so the damage bar draws when `GetBaseDamage() > 0`, else the existing idle lines:

```csharp
    private void DrawScannerHud()
    {
        if (_wave == null || !_wave.HasSecretsDetector) return;
        int dmg = _wave.GetBaseDamage();
        if (dmg > 0)
        {
            foreach (var b in HudScannerIndicator.BuildDamage(dmg))
                DrawRect(new Rect2(b.X, b.Y, b.W, b.H), ScannerPaletteColor(b.PaletteIndex));
        }
        else
        {
            foreach (var line in HudScannerIndicator.BuildIdle(_scannerState.CurrentDpos))
                DrawRect(new Rect2(line.X, line.Y, 1, line.Height), ScannerPaletteColor(line.PaletteIndex));
        }
        if (_lastScannerFrame != SimClock.Frame)
        {
            _scannerState.AfterSimTick();
            _lastScannerFrame = SimClock.Frame;
        }
        else _scannerState.AfterRenderFrame();
    }
```

- [ ] **Step 2: Build + parity check**

```bash
dotnet build raptor.csproj --nologo --verbosity minimal
bash tests/run_l2a.sh full_demo
```
Expected: build clean; `full_demo` byte-identical (full_demo has no on-screen boss / no scanner held → idle branch unchanged).

- [ ] **Step 3: Commit**

```bash
git add src/View/DebugRenderer.cs
git commit -m "View: scanner HUD draws boss-health bar when GetBaseDamage>0 (Task 2c)"
```

---

## Task 3: MegaBomb flash + A_SUPER_SHIELD anim

### Task 3a: one-shot detonation signal (read-only, parity-inert)

**Files:**
- Modify: `src/Sim/WaveController.cs`, `src/Sim/Shots/ShotDoneDispatcher.cs`
- Test: `tests/MegaBombSignalTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/MegaBombSignalTests.cs
using Raptor.Sim;
using Xunit;

namespace RaptorTests;

public class MegaBombSignalTests
{
    [Fact]
    public void Detonation_flag_raises_once_and_consumes()
    {
        var wc = WaveController.CreateForTest(); // existing test factory; else minimal ctor seam
        Assert.False(wc.ConsumeMegaBombDetonated());
        wc.SignalMegaBombDetonated();
        Assert.True(wc.ConsumeMegaBombDetonated());
        Assert.False(wc.ConsumeMegaBombDetonated()); // consumed
    }
}
```

(If `WaveController` cannot be instantiated under xUnit — per session-state, `new WaveController()` fails outside Godot — extract the flag into a tiny pure holder `MegaBombFlash` with `Signal()`/`Consume()` and test that instead; WaveController delegates to it. Prefer the pure holder.)

- [ ] **Step 2: Run to verify failure**

`cd tests && dotnet test --nologo --filter "FullyQualifiedName~MegaBombSignalTests" ; cd ..`
Expected: FAIL.

- [ ] **Step 3: Implement the one-shot flag**

Pure holder `src/Sim/Shots/MegaBombFlash.cs`:

```csharp
namespace Raptor.Sim.Shots;

/// One-shot, read-only View signal for megabomb detonation. Parity-inert:
/// not part of any checkpoint. Set in the sim, consumed by the renderer.
public sealed class MegaBombFlash
{
    private bool _pending;
    public void Signal() => _pending = true;
    public bool Consume() { bool p = _pending; _pending = false; return p; }
}
```

In `WaveController`: add `private readonly MegaBombFlash _megaFlash = new();`, expose `public void SignalMegaBombDetonated() => _megaFlash.Signal();` and `public bool ConsumeMegaBombDetonated() => _megaFlash.Consume();`. In `ShotDoneDispatcher` where megabomb detonation is handled (the existing clear-bullets/damage-all branch), invoke the WaveController callback (thread a `System.Action onMegaBombDetonated` into the dispatcher, or call `wave.SignalMegaBombDetonated()` if the dispatcher holds a reference). Test the pure holder directly.

- [ ] **Step 4: Run to verify pass + parity**

```bash
cd tests && dotnet test --nologo --filter "FullyQualifiedName~MegaBombSignalTests" ; cd ..
dotnet build raptor.csproj --nologo --verbosity minimal
bash tests/run_l2a.sh full_demo
```
Expected: PASS; `full_demo` byte-identical (signal adds no sim state).

- [ ] **Step 5: Commit**

```bash
git add src/Sim/Shots/MegaBombFlash.cs src/Sim/WaveController.cs src/Sim/Shots/ShotDoneDispatcher.cs tests/MegaBombSignalTests.cs
git commit -m "Sim: one-shot megabomb-detonation View signal (Task 3a)"
```

### Task 3b: View flash + screen-shake + SHIPGLOW_BLK

**Files:**
- Modify: `src/View/DebugRenderer.cs`

- [ ] **Step 1: Drive a fade overlay + shake + glow anim**

In `_Draw()`, once per sim tick (reuse the `SimClock.Frame` guard), if `_wave.ConsumeMegaBombDetonated()`, start a fade: set `_megaFadeIter = _wave.GameLoopIter` and spawn the glow:

```csharp
        if (_lastMuzzleFrame == SimClock.Frame /* already advanced above */
            && _wave.ConsumeMegaBombDetonated())
        {
            _megaFadeStartIter = _wave.GameLoopIter;
            _effects.Spawn("SHIPGLOW_BLK", totalFrames: 4, x: 160, y: 100, spawnIter: _wave.GameLoopIter, ground: false);
        }
```

(Move `ConsumeMegaBombDetonated` to run exactly once per sim tick — alongside the muzzle spawn block in Task 1b, inside the same `_lastMuzzleFrame != SimClock.Frame` body, so it consumes once per tick.)

- [ ] **Step 2: Draw the flash overlay (white-out → fade back) + shake**

Add fields `private int _megaFadeStartIter = int.MinValue; private const int MegaFadeFrames = 8;` and a draw helper called at the very end of `_Draw()` (over everything):

```csharp
    private void DrawMegaBombFlash()
    {
        if (_wave == null) return;
        int age = _wave.GameLoopIter - _megaFadeStartIter;
        if (age < 0 || age >= MegaFadeFrames) return;
        // White-out toward (63,60,60)/63 ≈ (1,0.95,0.95), strongest at age 0, easing out.
        float t = 1f - (age / (float)MegaFadeFrames);
        DrawRect(new Rect2(0, 0, 320, 200), new Color(1f, 0.95f, 0.95f, 0.85f * t));
    }
```

Screen shake: while `age < MegaFadeFrames`, offset the world draw by a small deterministic jitter (e.g. `int sx = (age % 2 == 0) ? 2 : -2;`) applied to the tile/world transform. Keep shake View-only; do not touch sim coordinates. (Exact magnitude/curve is cosmetic — tune visually in Task 8.)

- [ ] **Step 3: Build + parity check**

```bash
dotnet build raptor.csproj --nologo --verbosity minimal
bash tests/run_l2a.sh full_demo
```
Expected: build clean; `full_demo` byte-identical (no megabomb in full_demo).

- [ ] **Step 4: Commit**

```bash
git add src/View/DebugRenderer.cs
git commit -m "View: megabomb white-out flash + shake + SHIPGLOW_BLK anim (Task 3b)"
```

---

## Task 4: Ground-explosion scroll drift

GROUND-type explosions drift +1px in Y per frame while scrolling (ANIMS.C:418). Applied as a render-time offset only.

**Files:**
- Modify: `src/View/DebugRenderer.cs`
- Test: `tests/GroundDriftTests.cs`

- [ ] **Step 1: Write the failing test (pure offset helper)**

```csharp
// tests/GroundDriftTests.cs
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class GroundDriftTests
{
    [Theory]
    [InlineData(0, true, 0)]
    [InlineData(3, true, 3)]   // +1px per frame of age while scrolling
    [InlineData(3, false, 0)]  // no drift when not scrolling
    public void Ground_drift_offset_is_age_when_scrolling(int age, bool scrolling, int expected)
        => Assert.Equal(expected, GroundExplosionDrift.YOffset(age, scrolling));
}
```

- [ ] **Step 2: Run to verify failure**

`cd tests && dotnet test --nologo --filter "FullyQualifiedName~GroundDriftTests" ; cd ..`
Expected: FAIL.

- [ ] **Step 3: Implement the helper**

```csharp
// src/View/GroundExplosionDrift.cs
namespace Raptor.View;

internal static class GroundExplosionDrift
{
    /// ANIMS.C:418 — GROUND anims gain +1px Y per frame while scroll_flag is set.
    /// Cumulative drift over the anim's life == its age in frames when scrolling.
    public static int YOffset(int age, bool scrolling) => scrolling ? age : 0;
}
```

- [ ] **Step 4: Run to verify pass**

`cd tests && dotnet test --nologo --filter "FullyQualifiedName~GroundDriftTests" ; cd ..`
Expected: PASS.

- [ ] **Step 5: Apply in DrawExplosions**

In `DebugRenderer.DrawExplosions()`, for the default (non-smoke, non-spark) explosion branch, when the explosion family is a GROUND type (`GEXPLO_BLK`/`BOOM_PIC`/`SPLAT_BLK`/`BIGSPLAT_BLK`/`EXPLO2_BLK`/`FLARE_PIC`/`SPARKLE_PIC` — the GROUND-registered anims), add `GroundExplosionDrift.YOffset(frame, _wave.IsScrolling)` to the draw Y:

```csharp
        int driftY = IsGroundFamily(family) ? GroundExplosionDrift.YOffset(frame, _wave.IsScrolling) : 0;
        DrawTexture(tex, new Vector2(ex.X - dw / 2, ex.Y - dh / 2 + driftY));
```

Add `private static bool IsGroundFamily(string f) => f is "GEXPLO_BLK" or "BOOM_PIC" or "SPLAT_BLK" or "BIGSPLAT_BLK" or "EXPLO2_BLK" or "FLARE_PIC" or "SPARKLE_PIC";`. Use the existing scroll-state accessor on `WaveController` for `IsScrolling` (if absent, expose a read-only `public bool IsScrolling => …` mirroring C's `scroll_flag`; it is already tracked for tile scrolling).

- [ ] **Step 6: Build + parity check**

```bash
dotnet build raptor.csproj --nologo --verbosity minimal
bash tests/run_l2a.sh full_demo
bash tests/run_l2a.sh mission_long
```
Expected: build clean; both byte-identical (drift is a View render offset; explosion sim state unchanged).

- [ ] **Step 7: Commit**

```bash
git add src/View/GroundExplosionDrift.cs src/View/DebugRenderer.cs src/Sim/WaveController.cs tests/GroundDriftTests.cs
git commit -m "View: ground-explosion scroll drift (+1px/frame) (Task 4)"
```

---

## Task 5: Boss low-health smoke

For each `bossflag` enemy with `Hits < 50`, every other frame (`gl_cnt & 2`), spawn `SMFLAK_BLK` at a deterministic offset `(X + W/2, Y + H/2)`.

**Files:**
- Create: `src/View/BossSmoke.cs`
- Modify: `src/View/DebugRenderer.cs`
- Test: `tests/BossSmokeTests.cs`

- [ ] **Step 1: Write the failing tests (incl. Deterministic-positions + Spawn-once properties)**

```csharp
// tests/BossSmokeTests.cs
using FsCheck;
using FsCheck.Xunit;
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class BossSmokeTests
{
    [Fact]
    public void Spawns_only_when_boss_low_and_on_even_gl_cnt_bit()
    {
        // gl_cnt & 2 nonzero for gl_cnt in {2,3,6,7,...}
        Assert.True(BossSmoke.ShouldSpawn(hits: 49, glCnt: 2));
        Assert.False(BossSmoke.ShouldSpawn(hits: 49, glCnt: 1)); // 1 & 2 == 0
        Assert.False(BossSmoke.ShouldSpawn(hits: 50, glCnt: 2)); // not low enough
    }

    [Fact]
    public void Offset_is_half_width_half_height()
        => Assert.Equal((100 + 16, 40 + 12), BossSmoke.SpawnPoint(x: 100, y: 40, width: 32, height: 24));

    // Property "Deterministic positions" (spec §Properties): offset is exactly
    // (width/2, height/2) for any bounds — no RNG, reproducible.
    [Property(MaxTest = 50)]
    public Property Spawn_point_is_exactly_half_bounds()
    {
        return Prop.ForAll(
            Gen.Choose(0, 320).ToArbitrary(), Gen.Choose(0, 200).ToArbitrary(),
            Gen.Choose(1, 64).ToArbitrary(), Gen.Choose(1, 64).ToArbitrary(),
            (x, y, w, h) =>
            {
                var (sx, sy) = BossSmoke.SpawnPoint(x, y, w, h);
                return sx == x + w / 2 && sy == y + h / 2;
            });
    }
}
```

- [ ] **Step 2: Run to verify failure**

`cd tests && dotnet test --nologo --filter "FullyQualifiedName~BossSmokeTests" ; cd ..`
Expected: FAIL.

- [ ] **Step 3: Implement the pure helper**

```csharp
// src/View/BossSmoke.cs
namespace Raptor.View;

/// ENEMY.C:1076-1085 — bosses with hits<50 emit A_SMALL_AIR_EXPLO (SMFLAK_BLK)
/// every other frame (gl_cnt & 2) at a within-bounds offset. C uses
/// random(width)/random(height); under deterministic RNG that is width/2,height/2,
/// computed here with no RNG draw.
internal static class BossSmoke
{
    public static bool ShouldSpawn(int hits, int glCnt) => hits < 50 && (glCnt & 2) != 0;
    public static (int X, int Y) SpawnPoint(int x, int y, int width, int height)
        => (x + width / 2, y + height / 2);
}
```

- [ ] **Step 4: Run to verify pass**

`cd tests && dotnet test --nologo --filter "FullyQualifiedName~BossSmokeTests" ; cd ..`
Expected: PASS (incl. property).

- [ ] **Step 5: Spawn in the View (once per sim tick)**

In `DebugRenderer._Draw()`, inside the per-sim-tick block (the `_lastMuzzleFrame != SimClock.Frame` body), iterate enemies and spawn smoke:

```csharp
            int glCnt = _wave.GameLoopIter;
            foreach (var e in _wave.GetEnemies())
            {
                if (!e.IsBoss) continue;
                if (!BossSmoke.ShouldSpawn(e.Hits, glCnt)) continue;
                var (sx, sy) = BossSmoke.SpawnPoint(e.X, e.Y, e.Meta.Width, e.Meta.Height);
                _effects.Spawn("SMFLAK_BLK", totalFrames: 6, x: sx, y: sy, spawnIter: _wave.GameLoopIter, ground: false);
            }
```

(`SMFLAK_BLK` has ≥6 extracted frames; use the registered count. Reuse the same enemy accessor/`IsBoss`/`Meta` from Task 2a.)

- [ ] **Step 6: Build + FULL parity gate (this feature reads boss/hits state)**

```bash
dotnet build raptor.csproj --nologo --verbosity minimal
cd tests && dotnet test --nologo ; cd ..
for s in mission_start mission_long full_demo menu_demo death_wave1 death_wave2 death_wave4 death_wave5 death_wave6 death_wave7 death_wave8 death_wave9; do
  echo "== $s =="; bash tests/run_l2a.sh "$s"
done
```
Expected: suite green; all 12 scenarios byte-identical to baseline. (No-input death waves never damage a boss below 50, so smoke never fires there; the spawn reads state but adds no sim mutation or RNG.)

- [ ] **Step 7: Commit**

```bash
git add src/View/BossSmoke.cs src/View/DebugRenderer.cs tests/BossSmokeTests.cs
git commit -m "View: boss low-health smoke via ViewEffects (Task 5)"
```

---

## Task 6: ES_LASER beam column

### Task 6a: BLOCKING dependency check — resolve the `lashit` impact sprite

**Files:**
- Test: `tests/CosmeticAssetPresenceTests.cs`

- [ ] **Step 1: Resolve `lashit`**

Search the extracted atlas and the C source for the impact sprite ES_LASER draws (`lashit[curframe-1]`, ESHOT.C:567):

```bash
ls assets/sprites assets/bullets | grep -iE "LASHIT|LASHT|LAHIT|LSHIT"
grep -rniE "lashit" /Users/nadavb/dev/dosraptor/SOURCE
```
Record the actual GLB item name behind `lashit` (read its declaration/`GLB_GetItemID` in ESHOT.C). If a matching PNG exists, note its family name for Step 2's draw code. **If no such sprite is extracted, document the drop in the spec's "Validated dependencies" and skip the impact-sprite line in Task 6b (column + power still render).** Do not silently omit it.

- [ ] **Step 2: Add an un-mocked tracer test for the ES_LASER sprites**

```csharp
// tests/CosmeticAssetPresenceTests.cs  (add to the file from Task 1/7 tracer)
using System.IO;
using System.Linq;
using Godot; // ProjectSettings — runs under `dotnet test` via the Godot SDK ref
using Xunit;

namespace RaptorTests;

public class CosmeticAssetPresenceTests
{
    private static string Bullets => ProjectSettings.GlobalizePath("res://assets/bullets");
    private static bool HasFamily(string dir, string family)
        => Directory.GetFiles(dir, $"{family}_*.png").Any();

    [Fact]
    public void Es_laser_column_sprites_exist()
    {
        Assert.True(HasFamily(Bullets, "ELASER_BLK"));
        Assert.True(HasFamily(Bullets, "ELASEPOW_BLK"));
    }
}
```

(If `ProjectSettings` is unavailable in plain `dotnet test`, resolve the path via the repo root: `Path.Combine(RepoRoot, "assets/bullets")` using a `RepoRoot` helper that walks up from `AppContext.BaseDirectory` to the dir containing `raptor.csproj`. Keep it un-mocked — real filesystem.)

- [ ] **Step 3: Run + commit**

```bash
cd tests && dotnet test --nologo --filter "FullyQualifiedName~CosmeticAssetPresenceTests" ; cd ..
git add tests/CosmeticAssetPresenceTests.cs
git commit -m "test: tracer — ES_LASER column sprites present; resolve lashit (Task 6a)"
```

### Task 6b: render the column

**Files:**
- Create: `src/View/LaserBeam.cs`
- Modify: `src/View/DebugRenderer.cs`
- Test: `tests/LaserBeamTests.cs`

- [ ] **Step 1: Write the failing test (pure column geometry)**

```csharp
// tests/LaserBeamTests.cs
using System.Linq;
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class LaserBeamTests
{
    [Fact]
    public void Column_steps_by_three_from_y_to_y2()
    {
        var ys = LaserBeam.ColumnYs(y: 10, y2: 22).ToArray();
        Assert.Equal(new[] { 10, 13, 16, 19 }, ys); // < y2, step 3
    }

    [Fact]
    public void Empty_when_y2_at_or_below_y()
        => Assert.Empty(LaserBeam.ColumnYs(20, 20));
}
```

- [ ] **Step 2: Run to verify failure / implement**

```csharp
// src/View/LaserBeam.cs
using System.Collections.Generic;

namespace Raptor.View;

/// ESHOT.C:558-573 — ES_LASER draws ELASER_BLK every 3px from shot.y down to
/// move.y2 (exclusive), ELASEPOW_BLK at the gun, and the impact sprite at
/// move.y2-8 (clamped on-screen).
internal static class LaserBeam
{
    public static IEnumerable<int> ColumnYs(int y, int y2)
    {
        for (int loop = y; loop < y2; loop += 3) yield return loop;
    }
}
```

Run: `cd tests && dotnet test --nologo --filter "FullyQualifiedName~LaserBeamTests" ; cd ..` — expect FAIL then PASS.

- [ ] **Step 3: Special-case ES_LASER in the enemy-bullet draw**

In `DebugRenderer`, where enemy bullets are drawn, detect ES_LASER bullets (expose `IsLaser` + `Y2`/`CurFrame` on the enemy-shot the View renders; the laser sim already tracks these — surface read-only accessors). For each laser shot draw: `ELASER_BLK[curframe-1]` at each `LaserBeam.ColumnYs(shot.Y, shot.Y2)`; `ELASEPOW_BLK[curframe-1]` at `(shot.X, shot.Y)`; and the impact sprite (from 6a) at `(shot.X - w/4, shot.Y2 - 8)` when `0 < y < 200`. Use `LoadBlkFrame`. If `lashit` was unresolved in 6a, omit only the impact line.

- [ ] **Step 4: Build + parity check + commit**

```bash
dotnet build raptor.csproj --nologo --verbosity minimal
bash tests/run_l2a.sh death_wave8   # laser never spawns (dies first) → must stay 100%
git add src/View/LaserBeam.cs src/View/DebugRenderer.cs src/Sim tests/LaserBeamTests.cs
git commit -m "View: ES_LASER beam column rendering (Task 6b)"
```

---

## Task 7: Super-shield HUD counter

### Task 7a: BLOCKING — fix the extractor + re-extract SMSHIELD_PIC

`SMSHIELD_PIC` is GLB item i=966, sz=270, stored as `"SMSHIELD_PIC//"`; the extractor's strict `_PIC` suffix skips it.

**Files:**
- Modify: `dosraptor/tools/extract_assets/main.c`
- Add: `assets/sprites/SMSHIELD_PIC.png` (regenerated)
- Test: `tests/CosmeticAssetPresenceTests.cs`

- [ ] **Step 1: Make the suffix check tolerant + sanitize the name**

In `tools/extract_assets/main.c`, add a helper that strips a trailing run of non-identifier chars, and use a sanitized copy for both the suffix test and the output filename. In `extract_sprites` (and `extract_blks`) replace the raw `name` use:

```c
/* Trim a trailing run of non-[A-Za-z0-9_] chars (some GLB names carry a
 * stray "//", e.g. "SMSHIELD_PIC//"). */
static void sanitize_item_name(char *s) {
    size_t n = strlen(s);
    while (n > 0) {
        char c = s[n-1];
        if ((c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='_') break;
        s[--n] = '\0';
    }
}
```

After `GLB_GetItemInfo(i, name, ...)` succeeds, call `sanitize_item_name(name);` before the `name_has_suffix(name, "_PIC")` test and before composing the `%04d_%s.png` path. (Apply the identical change in `extract_blks` for `_BLK`.)

- [ ] **Step 2: Rebuild + re-extract + verify the sprite appears (CLI tracer)**

```bash
make -C /Users/nadavb/dev/dosraptor/build extract_assets
OUT=/Users/nadavb/.claude/jobs/0b2e4de6/tmp/extract_smshield
rm -rf "$OUT"; mkdir -p "$OUT"
( cd /Users/nadavb/dev/dosraptor && ./build/tools/extract_assets/extract_assets FILE0000.GLB FILE0001.GLB "$OUT" )
ls "$OUT/sprites" | grep SMSHIELD   # MUST list NNNN_SMSHIELD_PIC.png
```
Expected: a `*_SMSHIELD_PIC.png` is produced. Copy it into the repo (the port loads by name, so the prefix is irrelevant):
```bash
cp "$OUT"/sprites/*_SMSHIELD_PIC.png /Users/nadavb/dev/raptor-godot/assets/sprites/
```

- [ ] **Step 3: Add the un-mocked asset tracer test**

Append to `tests/CosmeticAssetPresenceTests.cs`:

```csharp
    private static string Sprites => ProjectSettings.GlobalizePath("res://assets/sprites");

    [Fact]
    public void SuperShield_hud_icon_is_extracted()
        => Assert.True(Directory.GetFiles(Sprites, "*_SMSHIELD_PIC.png").Any());

    [Fact]
    public void Cosmetic_anim_families_are_extracted()
    {
        Assert.True(HasFamily(Bullets, "GUNSTR_BLK"));
        Assert.True(HasFamily(Bullets, "SHIPGLOW_BLK"));
        Assert.True(HasFamily(Bullets, "SMFLAK_BLK"));
    }
```

- [ ] **Step 4: Run tracer + commit (godot repo + dosraptor repo)**

```bash
cd tests && dotnet test --nologo --filter "FullyQualifiedName~CosmeticAssetPresenceTests" ; cd ..
# godot repo: the new sprite + tracer
git add assets/sprites/*_SMSHIELD_PIC.png tests/CosmeticAssetPresenceTests.cs
git commit -m "assets: extract SMSHIELD_PIC; tracer for cosmetic sprites (Task 7a)"
# dosraptor repo: the extractor fix
( cd /Users/nadavb/dev/dosraptor && git add tools/extract_assets/main.c && \
  git commit -m "extract_assets: tolerate+strip trailing non-identifier chars in item names (e.g. SMSHIELD_PIC//)" )
```

### Task 7b: HudSuperShieldIndicator pure helper

**Files:**
- Create: `src/View/HudSuperShieldIndicator.cs`
- Test: `tests/HudSuperShieldIndicatorTests.cs`

- [ ] **Step 1: Write the failing tests (incl. Icon-count-exactness property)**

```csharp
// tests/HudSuperShieldIndicatorTests.cs
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class HudSuperShieldIndicatorTests
{
    [Fact]
    public void Icons_at_map_left_plus_2_spaced_13_on_row_1()
    {
        var ps = HudSuperShieldIndicator.Build(3).ToArray();
        Assert.Equal(3, ps.Length);
        Assert.Equal(new HudSuperShieldIndicator.Position(18, 1), ps[0]); // MAP_LEFT(16)+2
        Assert.Equal(new HudSuperShieldIndicator.Position(31, 1), ps[1]);
        Assert.Equal(new HudSuperShieldIndicator.Position(44, 1), ps[2]);
    }

    [Fact]
    public void Zero_count_builds_nothing() => Assert.Empty(HudSuperShieldIndicator.Build(0));

    // Property "Icon-count exactness" (spec §Properties).
    [Property(MaxTest = 50)]
    public Property Icon_count_equals_input_and_x_is_left2_plus_13i()
    {
        return Prop.ForAll(Gen.Choose(0, 30).ToArbitrary(), n =>
        {
            var ps = HudSuperShieldIndicator.Build(n).ToArray();
            if (ps.Length != n) return false;
            for (int i = 0; i < n; i++)
                if (ps[i].X != 18 + 13 * i || ps[i].Y != 1) return false;
            return true;
        });
    }
}
```

- [ ] **Step 2: Run to verify failure / implement**

```csharp
// src/View/HudSuperShieldIndicator.cs
using System.Collections.Generic;

namespace Raptor.View;

/// OBJECTS.C:663-672 — one SMSHIELD_PIC per owned super-shield, x = MAP_LEFT+2,
/// stepping +13, on row y=1.
internal static class HudSuperShieldIndicator
{
    public readonly record struct Position(int X, int Y);
    private const int MapLeft = 16; // SOURCE/MAP.H

    public static IEnumerable<Position> Build(int count)
    {
        int x = MapLeft + 2;
        for (int i = 0; i < count; i++) { yield return new Position(x, 1); x += 13; }
    }
}
```

Run: `cd tests && dotnet test --nologo --filter "FullyQualifiedName~HudSuperShieldIndicatorTests" ; cd ..` — FAIL then PASS.

- [ ] **Step 3: Commit**

```bash
git add src/View/HudSuperShieldIndicator.cs tests/HudSuperShieldIndicatorTests.cs
git commit -m "View: HudSuperShieldIndicator positions + property test (Task 7b)"
```

### Task 7c: draw the super-shield HUD

**Files:**
- Modify: `src/View/DebugRenderer.cs`

- [ ] **Step 1: Draw icons from the inventory count**

In the HUD-drawing section of `_Draw()` (where other HUD elements draw), add:

```csharp
    private void DrawSuperShieldHud()
    {
        if (_wave == null) return;
        int count = _wave.Inventory.GetTotal(ObjType.SuperShield);
        if (count <= 0) return;
        var tex = LoadSprite("SMSHIELD_PIC");
        if (tex == null) return;
        foreach (var p in HudSuperShieldIndicator.Build(count))
            DrawTexture(tex, new Vector2(p.X, p.Y));
    }
```

Call `DrawSuperShieldHud()` from `_Draw()` alongside the other HUD draws. (`_wave.Inventory` is the canonical inventory; if exposed under another name, use it. `ObjType.SuperShield` = 15.)

- [ ] **Step 2: Build + parity check**

```bash
dotnet build raptor.csproj --nologo --verbosity minimal
bash tests/run_l2a.sh full_demo
bash tests/run_l2a.sh menu_demo
```
Expected: build clean; both byte-identical (no super-shield owned in CI scenarios → count 0 → nothing drawn).

- [ ] **Step 3: Commit**

```bash
git add src/View/DebugRenderer.cs
git commit -m "View: super-shield HUD icon counter (Task 7c)"
```

---

## Task 8: Visual capture for human review

Author per-feature trigger scripts and produce side-by-side C/Godot diffs (closes the original request). These are **visual-review artifacts, not CI**.

**Files:**
- Create: `tests/parity/scripts/megabomb_probe.parity.txt`, `scanner_probe.parity.txt`, `boss_probe.parity.txt`, plus reuse `full_demo` (muzzle flash) and a super-shield-pickup probe; an ES_LASER cheat script for #6.

- [ ] **Step 1: Author trigger scripts (both C + Godot share the playthrough format)**

For each: a deterministic playthrough that exercises the cosmetic — megabomb: enter a wave with a megabomb in inventory and fire it (`down <megabomb key>`); scanner: hold the scanner with a boss/enemies on screen; boss-smoke: damage a boss below 50 hits; super-shield: pick up a super-shield bonus; muzzle flash: any forward-guns firing run (`full_demo` already fires). Follow `docs/playbooks/parity/scenario-authoring.md`. (Fire key is `A` — see session-state Known Traps, not Ctrl.)

- [ ] **Step 2: Capture side-by-side (serial; C grabs the mouse)**

```bash
SCENARIO_ID=full_demo bash tests/run_visual_audit.sh full_demo   # muzzle flash
# repeat per scenario id after registering each in tests/parity/scenarios/scenarios.json
```
Open each `dumps/visual_audit/<id>/<stamp>/index.html` and review the C-vs-Godot frames + diffs. Iterate any cosmetic that looks wrong (timing/position) — these are View-only, so re-tuning is safe.

- [ ] **Step 3: Send the artifacts to the user**

Surface the key side-by-side frames (composite C | Godot | diff) for each feature for sign-off. Do **not** commit generated `dumps/` artifacts.

---

## Self-Review

**1. Spec coverage** — every spec feature maps to a task: #1→Task 1a/1b; #2→Task 2a/2b/2c; #3→Task 3a/3b; #4→Task 4; #5→Task 5; #6→Task 6a/6b; #7→Task 7a/7b/7c. Shared infra→Task 0. Verification→Task 8. ✅

**2. Property coverage** (spec §Properties):
- *Parity-inert* (all 7) — realized as the per-task parity-gate verification step (build + `run_l2a.sh` byte-identical), full 12-scenario gate after Task 5. This is a system-level invariant verified by the existing gate harness, not a unit `@given`. ✅
- *Spawn-once* — `ViewEffects` spawn/active/prune semantics (Task 0 tests); the per-sim-tick `_lastMuzzleFrame`/`SimClock.Frame` guard ensures one spawn per event (muzzle/megabomb/boss). Boss-smoke cadence has the `[Property]` in Task 5. ✅
- *Damage-bar fidelity* — `[Property]` in Task 2b. ✅
- *Icon-count exactness* — `[Property]` in Task 7b. ✅
- *Deterministic positions* — `[Property]` in Task 5. ✅

**3. External-dependency tracer coverage** (spec §Validated dependencies):
- Cosmetic sprite families (GUNSTR_BLK, SHIPGLOW_BLK, SMFLAK_BLK) — un-mocked filesystem tracer, Task 7a Step 3. ✅
- ES_LASER sprites (ELASER_BLK, ELASEPOW_BLK) — un-mocked tracer, Task 6a Step 2. ✅
- `SMSHIELD_PIC` extraction — CLI tracer (run extractor, assert PNG) Task 7a Step 2 + filesystem tracer Step 3. ✅
- `lashit` (UNVERIFIED) — blocking resolution task 6a Step 1, with documented drop path. ✅
- Parity gate (`run_l2a.sh`) — exercised un-mocked in every feature's verification step. ✅

**4. Placeholder scan** — no TBD/TODO; each code step has complete code; commands have expected output. The two intentional "use the actual accessor if named differently" notes (enemy enumeration, inventory field, scroll flag) point at existing symbols the implementer confirms by grep, not invented APIs. ✅

**5. Type consistency** — `ViewEffects.Spawn(family,totalFrames,x,y,spawnIter,ground)` / `Active(currentIter)→ActiveAnim{Family,Frame,X,Y,Ground}` used consistently in Tasks 1b/3b/5. `HudScannerIndicator.Box(X,Y,W,H,PaletteIndex)`, `HudSuperShieldIndicator.Position(X,Y)`, `BossSmoke.ShouldSpawn/SpawnPoint`, `GroundExplosionDrift.YOffset`, `LaserBeam.ColumnYs` all match their call sites. `WaveController` additions (`MuzzlesThisTick`, `GetBaseDamage`, `ComputeBaseDamage`, `SignalMegaBombDetonated`/`ConsumeMegaBombDetonated`, `IsScrolling`, `GetEnemies`/`Inventory`) referenced consistently. ✅

## Execution Handoff

(Provided after the plan is saved — see chat.)
