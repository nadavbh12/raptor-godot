# Deterministic Parity + Death-Scenario Baseline — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make parity testing fully deterministic (`random(N) → N/2` on both the DOS C build and the Godot port), regenerate all goldens, and add a death-scenario baseline that starts mid-episode-1 and validates the player death sequence.

**Architecture:** A single test-only flag routes every `random()` call to the midpoint of its bound on both sides — order-independent, seed-independent, identical across implementations. This removes seed-matching and RNG draw-order desync as parity concerns. A `RAPTOR_START_WAVE` env hook lets a scripted playthrough begin at any episode-1 wave, where a hold-fire/no-dodge script drives a deterministic death.

**Tech Stack:** C (dosraptor, SDL2), C# / Godot 4.6.2 (.NET 8), xUnit + FsCheck for tests, bash capture scripts, Python parity comparator.

**Spec:** `docs/superpowers/specs/2026-05-30-deterministic-parity-and-death-scenario-design.md`

**Environment notes (read before running anything):**
- Build/test env: `export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH" DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec DOTNET_ROLL_FORWARD=Major`
- Godot build: `dotnet build raptor.csproj --nologo -v q`; tests: `dotnet test tests/RaptorTests.csproj --nologo -v q`
- C repo: `/Users/nadavb/dev/dosraptor`; binary `build/raptor.app/Contents/MacOS/raptor`; rebuild: `cmake --build build`
- **Golden regeneration runs the real C binary, which GRABS THE MOUSE/KEYBOARD for ~1 min/script (headless impossible — needs an accelerated renderer). Never run two C sessions in parallel. Confirm with the user before each capture run.**

---

## File structure

- `dosraptor/port/platform/dos_compat.h` — `random(x)` macro → route through `raptor_random`.
- `dosraptor/port/platform/parity.c` (or a small new `det_rng.c`) — `raptor_random()` definition + `g_deterministic` accessor.
- `dosraptor/SOURCE/SHOTS.C` — remove the now-redundant `SHOTS_DeterministicMiniGun` special-case.
- `dosraptor/SOURCE/RAP.C` — `RAPTOR_START_WAVE` env hook at new-game reset.
- `src/Sim/DeterministicRandom.cs` — the Godot chokepoint; flag unification.
- `src/Sim/Shots/PlayerShooter.cs` — drop the `DeterministicMiniGun` branch (redundant under the chokepoint).
- `src/Sim/WaveController.cs` — `RAPTOR_START_WAVE` mirror; ensure all draws use the chokepoint.
- `tests/DeterministicRandomTests.cs` (new) — chokepoint property + example tests.
- `tests/run_l2a.sh`, `dosraptor/tests/parity_capture.sh` — set the deterministic flag.
- `dosraptor/tests/scripts/death_wave<N>.txt` (new) — death playthrough.
- `tests/parity/scripts/*.parity.txt` — regenerated goldens (+ new `death_wave<N>.parity.txt`).
- `tests/parity/scenarios/{coverage.md,scenarios.json}` — register the death scenario.

---

## Phase 0 — Tracer: verify the EXISTING C deterministic RNG (BLOCKING)

**Discovery (2026-05-30):** the C repo already has the chokepoint. `GFX/types.h`:
```c
static inline int raptor_random(int x) {           // #define random(x) raptor_random(x)
   if (x <= 0) return 0;
   if (getenv("RAPTOR_DETERMINISTIC_RNG") == "1") return x >> 1;   // = x/2, order-independent
   return rand() % x;
}
```
So `random()` is **already** blanket-deterministic-capable, gated on `RAPTOR_DETERMINISTIC_RNG` — the *same* flag Godot's `DeterministicRandom.Enabled` reads, and that `SHOTS.C:30` / `ENEMY.C:69` key off. **No C code patch is needed.** The foundation is just *setting the flag* during capture and Godot L2a. This task only verifies the two UNVERIFIED spec dependencies: (a) C is byte-deterministic run-to-run under the flag, (b) C↔Godot agree under it.

### Task 0: Tracer — C self-determinism + C↔Godot diff under `RAPTOR_DETERMINISTIC_RNG`

**Files:** none changed (verification only).

- [ ] **Step 1: C self-determinism gate (un-mocked tracer).** Two deterministic runs must be byte-identical. **Ask the user before running — grabs the mouse.**

```bash
cd /Users/nadavb/dev/dosraptor
BIN=build/raptor.app/Contents/MacOS/raptor
for r in A B; do RAPTOR_PLAYTHROUGH=tests/scripts/mission_start.txt RAPTOR_PARITY_OUT=/tmp/det_$r.txt \
  RAPTOR_TEST_DETERMINISTIC=1 RAPTOR_DETERMINISTIC_RNG=1 timeout 120 "$BIN" >/dev/null 2>&1 || true; done
diff -q /tmp/det_A.txt /tmp/det_B.txt && echo "DETERMINISTIC OK" || { echo "NONDETERMINISTIC — STOP"; diff /tmp/det_A.txt /tmp/det_B.txt | head; }
```

Expected: `DETERMINISTIC OK`. If not, a non-RNG nondeterminism source exists — **stop and investigate** (the whole approach depends on this).

- [ ] **Step 2: C↔Godot diff under the flag.** Run Godot `mission_start` with `RAPTOR_DETERMINISTIC_RNG=1` and diff against the C run from Step 1 (`/tmp/det_A.txt`). Mismatches here are either Godot draws not yet honoring the flag (Phase 1 audit fixes them) or a real non-RNG divergence. Record the diff; it informs Phase 1.

- [ ] **Step 3: No commit** (verification only). Proceed to Phase 1.

---

## Phase 1 — Godot deterministic chokepoint (complete + prove)

### Task 1: Property + example tests for the chokepoint

**Files:**
- Test: `tests/DeterministicRandomTests.cs` (new)
- Under test: `src/Sim/DeterministicRandom.cs`

`DeterministicRandom.Enabled` currently reads `RAPTOR_DETERMINISTIC_RNG`. We will keep that env var but the tests pin the invariants. Because `Enabled` reads an env var, add an internal seam so tests are hermetic.

- [ ] **Step 1: Add a test seam.** In `src/Sim/DeterministicRandom.cs`, replace the property with an overridable field defaulting to the env check:

```csharp
internal static bool? Override;   // test seam; null => read env
public static bool Enabled =>
    Override ?? (Environment.GetEnvironmentVariable("RAPTOR_DETERMINISTIC_RNG") == "1");
```

- [ ] **Step 2: Write the failing tests.**

```csharp
using System;
using FsCheck;
using FsCheck.Xunit;
using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

public class DeterministicRandomTests : IDisposable
{
    public DeterministicRandomTests() => DeterministicRandom.Override = true;   // deterministic mode
    public void Dispose() => DeterministicRandom.Override = null;

    // Property: order-independence — value depends only on the bound, never on
    // how many draws preceded it (spec: Order-independence).
    [Property(MaxTest = 50)]
    public Property NextOrMidpoint_is_order_independent()
    {
        return Prop.ForAll(
            Gen.Choose(1, 4096).ToArbitrary(),               // bound N > 0
            Gen.Choose(0, 50).ToArbitrary(),                 // number of preceding draws
            (n, preceding) =>
            {
                var rng = new Random(12345);
                for (int i = 0; i < preceding; i++) DeterministicRandom.NextOrMidpoint(rng, 7, 3);
                return DeterministicRandom.NextOrMidpoint(rng, n, 0) == n / 2;
            });
    }

    // Property: the deterministic value is exactly floor(N/2) (matches C raptor_random).
    [Property(MaxTest = 50)]
    public Property NextOrMidpoint_returns_floor_half()
    {
        return Prop.ForAll(Gen.Choose(1, 100000).ToArbitrary(),
            n => DeterministicRandom.NextOrMidpoint(null, n, 0) == n / 2);
    }

    [Fact]
    public void Production_isolation_uses_real_rng_when_disabled()
    {
        DeterministicRandom.Override = false;   // production mode
        var a = new Random(99); var b = new Random(99);
        // With the flag off, the helper must defer to the supplied RNG, not the midpoint.
        Assert.Equal(b.Next(1000), DeterministicRandom.NextOrMidpoint(a, 1000, 0));
    }
}
```

- [ ] **Step 3: Run to verify the order/midpoint props pass and isolation compiles/fails as appropriate.**

Run: `dotnet test tests/RaptorTests.csproj --nologo -v q --filter "FullyQualifiedName~DeterministicRandomTests"`
Expected: the two `[Property]` tests PASS already (NextOrMidpoint returns `maxValue/2`); `Production_isolation` PASS. If `Override` seam not added, build fails — add it (Step 1).

- [ ] **Step 4: Commit.**

```bash
git add tests/DeterministicRandomTests.cs src/Sim/DeterministicRandom.cs
git commit -m "Pin DeterministicRandom chokepoint invariants (order-independence, floor N/2, production isolation)"
```

### Task 2: Audit guard — no RNG draw bypasses the chokepoint

**Files:**
- Test: `tests/DeterministicRandomTests.cs` (append)

- [ ] **Step 1: Write a guard test** asserting the sim has no direct RNG draws outside the chokepoint. This is a source-scan test (the audit surface is `DeterministicRandom.NextOrMidpoint`, used by `PlayerShooter.NextRandom` and `ShotDoneDispatcher`).

```csharp
[Fact]
public void No_sim_rng_draw_bypasses_the_chokepoint()
{
    // Every RNG draw in src/Sim must go through DeterministicRandom.NextOrMidpoint.
    // Forbid raw .Next(/.Randf(/.RandiRange( outside DeterministicRandom.cs.
    string simRoot = System.IO.Path.Combine(FindRepoRoot(), "src", "Sim");
    var offenders = new System.Collections.Generic.List<string>();
    foreach (var f in System.IO.Directory.EnumerateFiles(simRoot, "*.cs", System.IO.SearchOption.AllDirectories))
    {
        if (f.EndsWith("DeterministicRandom.cs") || f.EndsWith("LegacyRandom.cs")) continue;
        int ln = 0;
        foreach (var line in System.IO.File.ReadLines(f))
        {
            ln++;
            string t = line.TrimStart();
            if (t.StartsWith("//")) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"\.(Next|Randf|RandfRange|RandiRange)\s*\("))
                offenders.Add($"{f}:{ln}: {line.Trim()}");
        }
    }
    Assert.True(offenders.Count == 0, "Direct RNG draws bypass the chokepoint:\n" + string.Join("\n", offenders));
}

private static string FindRepoRoot()
{
    var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
    while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "raptor.csproj"))) d = d.Parent;
    return d?.FullName ?? throw new System.IO.DirectoryNotFoundException("raptor.csproj not found");
}
```

- [ ] **Step 2: Run it.** Run: `dotnet test ... --filter "FullyQualifiedName~No_sim_rng_draw_bypasses"`. Expected: PASS (current code already funnels through the chokepoint). If it FAILS, route each offender through `PlayerShooter.NextRandom`/`NextOrMidpoint` and re-run.

- [ ] **Step 3: Commit.** `git add tests/DeterministicRandomTests.cs && git commit -m "Guard: no sim RNG draw bypasses the deterministic chokepoint"`

### Task 3: Remove the redundant DeterministicMiniGun branch

**Files:**
- Modify: `src/Sim/Shots/PlayerShooter.cs` (the `WeaponType.MiniGun` case + `DeterministicMiniGun`/`PickMiddleEnemy`)
- Modify: `dosraptor/SOURCE/SHOTS.C` (remove `SHOTS_DeterministicMiniGun` special-case, lines ~24, 788–805)

Under the chokepoint, `PickRandomEnemy` drawing the midpoint index equals `PickMiddleEnemy`, and `random(width)→width/2 == hlx` reproduces the C deterministic aim. The special-cases are dead weight.

- [ ] **Step 1: Godot — collapse the MiniGun branch** so it always uses `PickRandomEnemy(enemies, rng)` and the `mini.aim.x/y` draws (which return midpoints in deterministic mode). Delete `DeterministicMiniGun`, `PickMiddleEnemy`, and the `deterministic ? ... : ...` fork. Keep the existing aim draws.

- [ ] **Step 2: Run the full Godot suite.** Run: `dotnet test tests/RaptorTests.csproj --nologo -v q`. Expected: PASS (the MiniGun tests assert behaviour the chokepoint still produces). Fix any test that referenced the removed members.

- [ ] **Step 3: C — remove `SHOTS_DeterministicMiniGun`** branching in `SHOTS.C` so the MiniGun uses plain `random(...)` (now deterministic via the macro). Rebuild C: `cd /Users/nadavb/dev/dosraptor && cmake --build build 2>&1 | tail -3`.

- [ ] **Step 4: Commit both repos.**

```bash
git add src/Sim/Shots/PlayerShooter.cs && git commit -m "Remove redundant DeterministicMiniGun branch (chokepoint covers it)"
cd /Users/nadavb/dev/dosraptor && git add -A && git commit -m "Remove SHOTS_DeterministicMiniGun special-case (random() now deterministic via macro)"
```

---

## Phase 2 — `RAPTOR_START_WAVE` wave-start hook

### Task 4: C wave-start env hook

**Files:**
- Modify: `dosraptor/SOURCE/RAP.C` (the new-game reset, ~lines 1715–1719)

- [ ] **Step 1: Add the hook** right after `game_wave[*]=0`:

```c
{
    const char *sw = getenv("RAPTOR_START_WAVE");   /* 1-based wave within episode 1 */
    if (sw && *sw) {
        int w = atoi(sw);
        if (w >= 1 && w <= 9) game_wave[cur_game] = w - 1;   /* RAP_LoadMap uses game_wave+1 -> MAP<w>G1 */
    }
}
```

- [ ] **Step 2: Build + smoke.** `cd /Users/nadavb/dev/dosraptor && cmake --build build 2>&1 | tail -3`. Then (ask user — mouse) run `mission_start` with `RAPTOR_START_WAVE=3` and confirm the parity output's `win` shows MISSION and the run is non-empty:

```bash
RAPTOR_PLAYTHROUGH=tests/scripts/mission_start.txt RAPTOR_PARITY_OUT=/tmp/w3.txt \
  RAPTOR_TEST_DETERMINISTIC=1 RAPTOR_DETERMINISTIC_RNG=1 RAPTOR_START_WAVE=3 timeout 120 build/raptor.app/Contents/MacOS/raptor >/dev/null 2>&1 || true
head -3 /tmp/w3.txt; wc -l /tmp/w3.txt
```

Expected: non-empty checkpoints (the run entered Do_Game at wave 3's map).

- [ ] **Step 3: Commit (dosraptor).** `git add -A && git commit -m "Add RAPTOR_START_WAVE env hook (start at episode-1 wave N)"`

### Task 5: Godot `RAPTOR_START_WAVE` mirror

**Files:**
- Modify: `src/Sim/WaveController.cs` (where `_pendingGameNum`/`_waveNum` are resolved before `LoadWave`)
- Test: `tests/WaveControllerTests.cs` (append)

- [ ] **Step 1: Write the failing test** for the wave resolver. Extract the env read into a pure helper:

```csharp
[Theory]
[InlineData(null, 1, 1)]    // no override -> default wave
[InlineData("3", 1, 3)]     // override wins
[InlineData("9", 1, 9)]
[InlineData("0", 1, 1)]     // out of range -> default
[InlineData("10", 1, 1)]    // out of range -> default
public void StartWave_resolves_override(string? env, int defaultWave, int expected)
    => Assert.Equal(expected, WaveController.ResolveStartWave(env, defaultWave));
```

- [ ] **Step 2: Run — expect FAIL** (`ResolveStartWave` undefined). Run: `dotnet test ... --filter "FullyQualifiedName~StartWave_resolves_override"`.

- [ ] **Step 3: Implement** in `WaveController.cs`:

```csharp
internal static int ResolveStartWave(string? env, int defaultWave)
{
    if (int.TryParse(env, out int w) && w >= 1 && w <= 9) return w;
    return defaultWave;
}
```

Then call it where the gameplay wave is chosen: `_waveNum = ResolveStartWave(OS.GetEnvironment("RAPTOR_START_WAVE"), _pendingGameNum + 1);` before `LoadWave(_waveNum)`.

- [ ] **Step 4: Run — expect PASS.** Build Godot, run the filter again.

- [ ] **Step 5: Commit.** `git add src/Sim/WaveController.cs tests/WaveControllerTests.cs && git commit -m "Mirror RAPTOR_START_WAVE in Godot WaveController"`

---

## Phase 3 — Wire deterministic flag + regenerate goldens

### Task 6: Run L2a in deterministic mode

**Files:**
- Modify: `tests/run_l2a.sh` (add the Godot deterministic env)
- Modify: `dosraptor/tests/parity_capture.sh`, `golden_capture.sh` — add `RAPTOR_DETERMINISTIC_RNG=1` to the C run env (they set `RAPTOR_TEST_DETERMINISTIC=1` for the clock, but RNG determinism is gated on the separate `RAPTOR_DETERMINISTIC_RNG` flag).

- [ ] **Step 1: Set the Godot flag** in `run_l2a.sh` alongside `RAPTOR_TEST_FAST=1`: add `RAPTOR_DETERMINISTIC_RNG=1 \` to the Godot launch env block. Add the same env to the C capture lines in `parity_capture.sh` / `golden_capture.sh` (dosraptor).

- [ ] **Step 2: Commit.** `git add tests/run_l2a.sh && git commit -m "Run L2a Godot side in deterministic-RNG mode"`

### Task 7: Regenerate all goldens (mouse-grab C run)

**Files:**
- Modify: `tests/parity/scripts/{mission_start,mission_long,full_demo,menu_demo}.parity.txt`

- [ ] **Step 1: Ask the user** to authorize the C capture (grabs mouse ~1 min/script).

- [ ] **Step 2: Regenerate to temp, verify determinism, diff, then stage.** For each game script run twice (per `golden_capture.sh` practice) and confirm byte-identical, then copy over the committed golden:

```bash
cd /Users/nadavb/dev/dosraptor; BIN=build/raptor.app/Contents/MacOS/raptor
GDIR=/Users/nadavb/dev/raptor-godot/tests/parity/scripts
for s in mission_start mission_long full_demo menu_demo; do
  for r in A B; do RAPTOR_PLAYTHROUGH=tests/scripts/$s.txt RAPTOR_PARITY_OUT=/tmp/g_$s.$r \
    RAPTOR_TEST_DETERMINISTIC=1 RAPTOR_DETERMINISTIC_RNG=1 timeout 120 "$BIN" >/dev/null 2>&1 || true; done
  if diff -q /tmp/g_$s.A /tmp/g_$s.B >/dev/null; then cp /tmp/g_$s.A "$GDIR/$s.parity.txt"; echo "$s OK"; else echo "$s NONDETERMINISTIC"; fi
done
```

Expected: all `OK`.

- [ ] **Step 3: Verify all L2a PASS** against the new goldens (deterministic both sides):

```bash
cd /Users/nadavb/dev/raptor-godot
export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH" DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec DOTNET_ROLL_FORWARD=Major
for s in mission_start mission_long full_demo menu_demo; do printf "%-14s " "$s:"; bash tests/run_l2a.sh "$s" 2>&1 | grep -E "pass rate|^PASS|^FAIL" | tail -1; done
```

Expected: all PASS (rates should rise toward 100%). If any newly FAILs, it's a genuine non-RNG logic divergence now exposed — record it as a follow-up finding (do NOT mask it by reverting determinism).

- [ ] **Step 4: Commit the regenerated goldens.** `git add tests/parity/scripts/*.parity.txt && git commit -m "Regenerate goldens in deterministic-RNG mode"`

---

## Phase 4 — Death scenario

### Task 8: Author the death script + capture its golden

**Files:**
- Create: `dosraptor/tests/scripts/death_wave<N>.txt`
- Create: `tests/parity/scripts/death_wave<N>.parity.txt`

- [ ] **Step 1: Pick the wave empirically (C is the oracle).** Starting from a copy of `mission_start.txt`'s menu-nav prefix, append hold-fire + no-move + long wait. The fire key is `lctrl` (verify against `playthrough.c` scancode map / existing in-game scripts). Draft `death_wave3.txt`:

```
# menu nav prefix (copy from mission_start.txt up to the sector-select Return)
# ... (new pilot TEST, difficulty, hangar, sector select -> Do_Game) ...
down lctrl          # hold fire
checkpoint death_pre
wait 4000           # do not move; take hits until shield hits 0 and death plays out
checkpoint death_post
quit
```

- [ ] **Step 2: Run it in C (ask user — mouse), at increasing difficulty/wave until the player actually dies** within the wait window. Confirm the parity output contains the death (shield reaches 0; checkpoints continue through the death sequence):

```bash
cd /Users/nadavb/dev/dosraptor; BIN=build/raptor.app/Contents/MacOS/raptor
RAPTOR_PLAYTHROUGH=tests/scripts/death_wave3.txt RAPTOR_PARITY_OUT=/tmp/death.txt \
  RAPTOR_TEST_DETERMINISTIC=1 RAPTOR_DETERMINISTIC_RNG=1 RAPTOR_START_WAVE=3 timeout 120 "$BIN" >/dev/null 2>&1 || true
grep -o '"shield":[0-9]*' /tmp/death.txt | tail -5   # expect shield -> 0
```

Adjust wave (`RAPTOR_START_WAVE`) / difficulty in the script until death is reliable; bake the chosen `RAPTOR_START_WAVE` into the L2a runner for this script (Task 9 wires it). Confirm determinism with a second run (byte-identical).

- [ ] **Step 3: Stage the golden + script.** Copy `/tmp/death.txt` → `tests/parity/scripts/death_wave<N>.parity.txt`; commit the `.txt` script in dosraptor.

```bash
cp /tmp/death.txt /Users/nadavb/dev/raptor-godot/tests/parity/scripts/death_wave3.parity.txt
cd /Users/nadavb/dev/dosraptor && git add tests/scripts/death_wave3.txt && git commit -m "Add death_wave3 playthrough (hold fire, no dodge, until death)"
```

### Task 9: Wire the death scenario into L2a + verify Godot matches

**Files:**
- Modify: `tests/run_l2a.sh` (pass `RAPTOR_START_WAVE` for `death_wave*` scripts)
- Modify: `tests/parity/scenarios/{coverage.md,scenarios.json}`

- [ ] **Step 1: Teach `run_l2a.sh`** to export `RAPTOR_START_WAVE=<N>` when `NAME` matches `death_wave<N>` (parse the trailing number). Keep it a one-liner near the env block.

- [ ] **Step 2: Run the death scenario L2a.** Run: `bash tests/run_l2a.sh death_wave3`. Expected: PASS. If it FAILs, the diff localizes a genuine death-path logic divergence (now deterministic, so it's a real bug, not RNG) — fix per the parity fix-workflow, or record as a finding.

- [ ] **Step 3: Register the scenario** — add a `death_wave3` row to `coverage.md` and an entry to `scenarios.json` (C source refs: RAP.C death prelude, INTRO.C death movie; expected dump categories: parity checkpoints) per the extension rule.

- [ ] **Step 4: Commit.** `git add tests/run_l2a.sh tests/parity/scripts/death_wave3.parity.txt tests/parity/scenarios/ && git commit -m "Add death_wave3 L2a death-sequence baseline"`

---

## Phase 5 — Retire seed sweep + housekeeping

### Task 10: Retire the seed sweep and update docs/CI/state

**Files:**
- Modify: `dosraptor/tests/parity_capture.sh` (drop the 100-seed loop)
- Delete: `tests/parity/seeds/*.parity.txt` (no longer meaningful)
- Modify: `ci/full.sh` (add `death_wave3`; ensure deterministic flag), `docs/playbooks/parity/README.md` (note seed-matching retired), `tests/parity/scenarios/coverage.md`

- [ ] **Step 1: Remove the seed-sweep loop** from `parity_capture.sh` and the `RAPTOR_RNG_SEED_OVERRIDE` capture; `git rm tests/parity/seeds/*.parity.txt`.

- [ ] **Step 2: Add `death_wave3`** (and the deterministic flag, if not already implied) to `ci/full.sh`'s L2a list.

- [ ] **Step 3: Run `ci/full.sh`.** Expected: build, full xUnit, all L2 scripts incl. `death_wave3` green.

- [ ] **Step 4: Update `~/.agent-state/raptor-godot--main/state.md`** — parity no longer relies on seed-matching; close deferred RNG-cluster tasks (#4 body-crash, #5 death-explosion, #6 death-prelude, spark ordering) as non-issues under determinism; record the death-scenario baseline.

- [ ] **Step 5: Commit.** `git add -A && git commit -m "Retire seed sweep; add death_wave3 to CI; document deterministic parity"` (and the dosraptor `parity_capture.sh` change in its repo).

---

## Self-Review

**Spec coverage:**
- A1 C patch → Task 0. A2 Godot chokepoint/audit → Tasks 1, 2. Minigun special-case removal → Task 3. A3 regenerate + retire seed sweep → Tasks 7, 10.
- B1 RAPTOR_START_WAVE → Tasks 4, 5. B2 death script → Task 8. B3 golden + comparison + registration → Task 9.
- Properties: Order-independence → Task 1 (`NextOrMidpoint_is_order_independent`, `@Property`). floor(N/2) → Task 1 (`NextOrMidpoint_returns_floor_half`). Production isolation → Task 1 (`Production_isolation_uses_real_rng_when_disabled`). Run-determinism → realized as the 2-run capture gate (Tasks 0 Step 5, 7 Step 2, 8 Step 2). Cross-impl equivalence → realized as the L2a PASS gate (Tasks 7 Step 3, 9 Step 2). Seed-independence → covered transitively by order-independence (value ignores the stream/seed) + the L2a PASS without seed-matching.
- External dependencies (tracer-bullet, un-mocked, un-skipped): C binary determinism → Task 0 Step 5 (real binary, not mocked). RAPTOR_START_WAVE map load → Task 4 Step 2 (real binary). Godot↔C equivalence → Task 7 Step 3 / Task 9 Step 2 (real L2a). C `random()` macro behaviour → Task 0 (exercised by the real run).

**Placeholder scan:** `<N>` is an intentional parameter resolved empirically in Task 8 (default 3); all code steps contain real code. No TBD/TODO.

**Type consistency:** `ResolveStartWave(string?, int)`, `DeterministicRandom.NextOrMidpoint(Random?, int, int)`, `DeterministicRandom.Override`, `raptor_random(int)`, `raptor_is_deterministic(void)` — names consistent across tasks.

**Gaps:** Seed-independence has no standalone `@given` test (it's a whole-run property, not a unit invariant); it is covered by order-independence at the unit level and the L2a PASS at the integration level. Acceptable — no unit seam exists for "whole run ignores seed" without running the sim.
