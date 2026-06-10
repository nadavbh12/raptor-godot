# Menu parity — Phase 1b Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the two deferred menu-event parity scenarios — `popup_winmsg_credits_dismiss` and `load_navigate_and_load` — verified C↔Godot and added to the committed golden set (CI sweep gate, `ci/full.sh`).

**Architecture:** Build-side prep first (no windowed C): add two emit-only C hooks for the any-key popups, author two scripts, commit a 3-pilot save fixture, make the Godot popup-dismiss emit a `selected_item=0` sentinel. Then a single user-gated batched C capture produces the goldens. Then align Godot to the goldens offline and commit. Mirrors the proven Phase 1 flow.

**Tech Stack:** C# (Godot port, src/Sim + src/Test), C (dosraptor reference, SOURCE/ + port/), bash runners (tests/, tools/), python comparator (`parity_diff.py --menu`), xUnit tests.

**Spec:** `docs/superpowers/specs/2026-06-10-menu-parity-phase1b-design.md`

**Env (every shell):**
```bash
export PATH="/opt/homebrew/opt/dotnet@8/bin:$HOME/.local/bin:$PATH"
export DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec
export DOTNET_ROLL_FORWARD=Major
export SDL_AUDIODRIVER=dummy
```
Build Godot C# before tests: `dotnet build raptor.csproj` (tests reference the prebuilt DLL via HintPath — a stale DLL gives phantom CS0103). Build C: `cmake --build /Users/nadavb/dev/dosraptor/build -j`.

---

## File Structure

**raptor-godot (Godot port):**
- `src/Sim/MenuStateMachine.cs` — MODIFY: `Screen.WinMsg` enum value; `EffectiveScreen()` returns it while `_inWinMsg`; `EffectiveSelectedItem()` returns `0` for `_inWinMsg` and `WinState.Credits` (the popup-dismiss sentinel).
- `tests/MenuStateMachineTests.cs` (or a focused new `tests/PopupDismissEmitTests.cs`) — sentinel unit tests.
- `tests/PilotFixtureTests.cs` — CREATE: hermetic round-trip property test + a skipped fixture generator.
- `tests/parity/menu_fixtures/load_navigate_and_load/CHAR0000.FIL`, `CHAR0001.FIL`, `CHAR0002.FIL` — CREATE (committed binaries, authored by the generator).
- `tools/capture_menu_goldens.sh` — MODIFY: add the two scenarios to the default `SCRIPTS` list.
- `tests/parity/c_menu_goldens/popup_winmsg_credits_dismiss.menu.ndjson`, `load_navigate_and_load.menu.ndjson` — CREATE (Phase C, from the capture).

**dosraptor (C reference):**
- `SOURCE/WINDOWS.C` — MODIFY: emit-only `raptor_parity_menu_event` hooks in `WIN_Credits` (~520) and `WIN_Msg` (~161).
- `tests/scripts/popup_winmsg_credits_dismiss.txt`, `tests/scripts/load_navigate_and_load.txt` — CREATE.

---

# PHASE A — Build-side prep (subagent-executable, no windowed C)

## Task 1: 3-pilot save fixture + round-trip property test

**Files:**
- Create: `tests/PilotFixtureTests.cs`
- Create (generated, committed): `tests/parity/menu_fixtures/load_navigate_and_load/CHAR000{0,1,2}.FIL`

- [ ] **Step 1: Write the hermetic round-trip property test**

`tests/PilotFixtureTests.cs`:
```csharp
using System.IO;
using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

public class PilotFixtureTests
{
    // Property: fixture round-trips (oracle). A CHAR####.FIL written by
    // PilotSaveStore.Save loads back via LoadAll with the same name/callsign/
    // score/idPic. Representative cases stand in for a property sweep (the
    // project has no FsCheck/Hypothesis; xUnit [Theory] expresses the invariant).
    [Theory]
    [InlineData("ALPHA", "AL", 0, 1000u)]
    [InlineData("BRAVO", "BR", 1, 25000u)]
    [InlineData("CHARLIE", "CH", 3, 148500u)]
    public void Pilot_save_round_trips_header_fields(string name, string callsign, int idPic, uint score)
    {
        string dir = Path.Combine(Path.GetTempPath(), "raptor_fix_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            int slot = PilotSaveStore.Save(dir, name, callsign, idPic, score);
            Assert.Equal(0, slot); // first write into an empty dir → slot 0
            var all = PilotSaveStore.LoadAll(dir);
            Assert.Single(all);
            Assert.Equal(name, all[0].Name);
            Assert.Equal(callsign, all[0].Callsign);
            Assert.Equal(idPic, all[0].IdPic);
            Assert.Equal(score, all[0].Score);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
```

NOTE: confirm the `PilotSaveSummary` property names (`Name`/`Callsign`/`IdPic`/`Score`) against `src/Sim/PilotSaveStore.cs:9` (the `record PilotSaveSummary(...)`) and adjust if they differ. `Save` returns `int` (the slot).

- [ ] **Step 2: Run the round-trip test, verify it passes**

```bash
dotnet build raptor.csproj --nologo -v quiet
dotnet test tests/RaptorTests.csproj --no-build --filter "FullyQualifiedName~PilotFixtureTests.Pilot_save_round_trips"
```
Expected: PASS (3 cases). If property names differ, fix and re-run.

- [ ] **Step 3: Add the skipped fixture generator**

Append to `tests/PilotFixtureTests.cs` (inside the class):
```csharp
    // Manual fixture author. Run explicitly to (re)generate the committed
    // load_navigate_and_load fixture. Skipped in normal CI — it writes into the
    // repo tree. Deterministic (no RNG/timestamp), so re-running is idempotent.
    [Fact(Skip = "manual: regenerates the committed load fixture; run with --filter")]
    public void Generate_load_navigate_fixture()
    {
        string dir = Path.Combine(RepoRoot(), "tests", "parity", "menu_fixtures", "load_navigate_and_load");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        PilotSaveStore.Save(dir, "ALPHA",   "AL", 0, 1000u);   // CHAR0000
        PilotSaveStore.Save(dir, "BRAVO",   "BR", 1, 25000u);  // CHAR0001
        PilotSaveStore.Save(dir, "CHARLIE", "CH", 3, 148500u); // CHAR0002
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null && !File.Exists(Path.Combine(d.FullName, "raptor.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root (raptor.csproj) not found");
    }
```

- [ ] **Step 4: Generate the fixture files**

```bash
dotnet build raptor.csproj --nologo -v quiet
dotnet test tests/RaptorTests.csproj --no-build \
  --filter "FullyQualifiedName~PilotFixtureTests.Generate_load_navigate_fixture" \
  -- xUnit.MethodDisplay=method
# the [Fact(Skip=...)] is excluded by default; force it:
dotnet test tests/RaptorTests.csproj --no-build \
  --filter "Generate_load_navigate_fixture" -- RunConfiguration.DisableAppDomain=true || true
ls -l tests/parity/menu_fixtures/load_navigate_and_load/
```
Expected: three files `CHAR0000.FIL`, `CHAR0001.FIL`, `CHAR0002.FIL`.

NOTE: a `[Fact(Skip=...)]` is NOT run even when name-filtered. If the filter does not execute it, temporarily change `Skip = ...` to a plain `[Fact]`, run the filter, confirm the three files exist, then restore the `Skip`. Verify the files exist before proceeding either way.

- [ ] **Step 5: Verify the fixture loads back via LoadAll (sanity)**

```bash
cd /Users/nadavb/dev/raptor-godot && dotnet run --project tests/RaptorTests.csproj 2>/dev/null || true
# Quick check: the round-trip test already proves the format; just confirm 3 files of non-zero size:
for f in tests/parity/menu_fixtures/load_navigate_and_load/CHAR000{0,1,2}.FIL; do test -s "$f" && echo "OK $f ($(wc -c <"$f") bytes)"; done
```
Expected: three non-empty `.FIL` files.

- [ ] **Step 6: Commit**

```bash
git add tests/PilotFixtureTests.cs tests/parity/menu_fixtures/load_navigate_and_load/
git commit -m "test(menu): 3-pilot load fixture + round-trip property test"
```

---

## Task 2: Godot popup-dismiss `selected_item=0` sentinel

**Files:**
- Modify: `src/Sim/MenuStateMachine.cs` (`Screen` enum; `EffectiveScreen()`; `EffectiveSelectedItem()`)
- Create: `tests/PopupDismissEmitTests.cs`

Background (verified): the menu-event emitter (`src/Test/PlaythroughDriver.cs:219-233`) computes `selBefore = EffectiveSelectedItem()` BEFORE `HandleInput`, and emits `selBefore` when the key changed `EffectiveScreen()` (cross-screen), else the post-nav value. A Credits/WinMsg dismiss changes screen, so the emit uses `selBefore`. Making `EffectiveSelectedItem()` return `0` while `_inWinMsg`/`Credits`, AND giving `_inWinMsg` a distinct `EffectiveScreen()`, yields `selected_item=0` on dismiss. This does NOT regress `menu_main_nav` (its Return-on-LOAD key is computed with `_inWinMsg=false` at `selBefore`, still `1`).

- [ ] **Step 1: Write the failing unit tests**

`tests/PopupDismissEmitTests.cs`:
```csharp
using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

public class PopupDismissEmitTests
{
    // Property: popup-dismiss sentinel. Credits and WIN_Msg have no cursor →
    // EffectiveSelectedItem() reports 0 while they are showing (matches C's
    // raptor_parity_menu_event(field=1) → selected_item = field-1 = 0).
    [Fact]
    public void WinMsg_reports_selected_item_zero()
    {
        var m = new MenuStateMachine();
        // Reach "No Pilots to Load": LOAD item + Return with an empty save dir.
        m.PilotSaveDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "raptor_empty_" + System.Guid.NewGuid().ToString("N"));
        m.HandleInput("Down", 0);            // highlight LOAD (item 1)
        m.HandleInput("Return", 0);          // → WIN_Msg "No Pilots to Load"
        Assert.True(m.InWinMsg);
        Assert.Equal(0, m.EffectiveSelectedItem());
        Assert.Equal(MenuStateMachine.Screen.WinMsg, m.EffectiveScreen());
    }

    [Fact]
    public void Credits_reports_selected_item_zero()
    {
        var m = new MenuStateMachine();
        m.HandleInput("Down", 0); m.HandleInput("Down", 0);
        m.HandleInput("Down", 0); m.HandleInput("Down", 0);   // highlight CREDITS (item 4)
        m.HandleInput("Return", 0);                            // enter Credits
        Assert.Equal(WinState.Credits, m.State);
        Assert.Equal(0, m.EffectiveSelectedItem());
    }
}
```

NOTE: verify the public surface used here exists: `MenuStateMachine.PilotSaveDirectory` (settable), `InWinMsg` (present, line 167), `EffectiveScreen()`, `EffectiveSelectedItem()`, `Screen` enum (public), `State`, `WinState.Credits`. If `PilotSaveDirectory` is not settable, set it via whatever the class exposes (grep `PilotSaveDirectory` / `RAPTOR_SAVE_DIR` resolution). The CREDITS path requires the main-menu item order to put CREDITS at index 4 — confirm against the existing `menu_nav_all_buttons` golden.

- [ ] **Step 2: Run tests, verify they fail**

```bash
dotnet build raptor.csproj --nologo -v quiet
dotnet test tests/RaptorTests.csproj --no-build --filter "FullyQualifiedName~PopupDismissEmitTests"
```
Expected: FAIL — `Screen.WinMsg` does not exist (compile error) and/or `EffectiveSelectedItem()` returns `CurrentItem` (e.g. 1 / 4) not 0.

- [ ] **Step 3: Add `Screen.WinMsg` to the enum**

In `src/Sim/MenuStateMachine.cs`, find the `Screen` enum (referenced by `EffectiveScreen()` ~line 245-260). Add a `WinMsg` member alongside `Credits`, `Help`, `LoadMission`, etc. (append; do not reorder existing members).

- [ ] **Step 4: Return `Screen.WinMsg` from `EffectiveScreen()` while `_inWinMsg`**

In `EffectiveScreen()` (src/Sim/MenuStateMachine.cs:~245), add near the top (before the `_loadMission`/`State` checks):
```csharp
        if (_inWinMsg) return Screen.WinMsg;
```

- [ ] **Step 5: Return `0` from `EffectiveSelectedItem()` for the popups**

In `EffectiveSelectedItem()` (src/Sim/MenuStateMachine.cs:221), add at the top (before `_inAskBool`):
```csharp
        if (_inWinMsg) return 0;              // popup: no cursor (C field=1 → 0)
```
and change the `switch` default so Credits maps to 0 explicitly:
```csharp
        return State switch
        {
            WinState.Hangar => _hangar.Position,
            WinState.Store  => Store?.CurItem ?? 0,
            WinState.Help   => _help.PageIndex,
            WinState.Credits => 0,            // popup: no cursor (C field=1 → 0)
            _               => CurrentItem,   // Menu / Unknown(sector)
        };
```

- [ ] **Step 6: Run the new tests, verify they pass**

```bash
dotnet build raptor.csproj --nologo -v quiet
dotnet test tests/RaptorTests.csproj --no-build --filter "FullyQualifiedName~PopupDismissEmitTests"
```
Expected: PASS (2).

- [ ] **Step 7: Run the full unit suite + the existing menu sweep — no regressions**

```bash
dotnet test tests/RaptorTests.csproj --no-build
tests/run_menu_event_sweep.sh
```
Expected: full suite green (≥750 + 2 new + 3 from Task 1); sweep still **7/7 PASS** (the sentinel does not change any current golden's rows — verified by reasoning on `menu_main_nav`).

- [ ] **Step 8: Commit**

```bash
git add src/Sim/MenuStateMachine.cs tests/PopupDismissEmitTests.cs
git commit -m "feat(menu): popup dismiss emits selected_item=0 (Credits/WinMsg sentinel)"
```

---

## Task 3: dosraptor C hooks for the any-key popups

**Files:**
- Modify: `/Users/nadavb/dev/dosraptor/SOURCE/WINDOWS.C` (`WIN_Credits` ~520, `WIN_Msg` ~161)

Background: the any-key popups wait in `IMS_WaitTimed` (`GFX/IMSAPI.C:126`), not `SWD_Dialog`, so they never call `raptor_parity_menu_event`. Add one emit-only call per popup, immediately after the wait returns. Mirror the existing hook idiom (e.g. WINDOWS.C:2067 in `WIN_MainMenu`). Both pass `field=1` → `selected_item=0`. `kind=0` (key). Emit-only: no state change.

- [ ] **Step 1: Read the two call sites to get exact line numbers**

```bash
cd /Users/nadavb/dev/dosraptor
grep -n "IMS_WaitTimed" SOURCE/WINDOWS.C
grep -n "raptor_parity_set_win_state\|raptor_parity_menu_event" SOURCE/WINDOWS.C | head
sed -n '140,170p;505,525p' SOURCE/WINDOWS.C
```
Confirm: `WIN_Msg` calls `IMS_WaitTimed(10)`; `WIN_Credits` does `rval = IMS_WaitTimed(25)` then `raptor_parity_set_win_state(0)`.

- [ ] **Step 2: Add the `WIN_Credits` hook**

In `WIN_Credits`, between `rval = IMS_WaitTimed(25);` and the `raptor_parity_set_win_state(0);` line, insert (win_state is still CREDITS here so the row carries `win="CREDITS"`):
```c
        { extern void raptor_parity_menu_event(int,int,int,int,int,int);
          raptor_parity_menu_event ( 0, 1, rval ? 1 : 0, 0, 0, 0 ); }
```

- [ ] **Step 3: Add the `WIN_Msg` hook**

In `WIN_Msg`, capture the wait return and emit before `SWD_DestroyWindow`. Change `IMS_WaitTimed(10);` to `{ int rval = IMS_WaitTimed(10);` and before the destroy add:
```c
          { extern void raptor_parity_menu_event(int,int,int,int,int,int);
            raptor_parity_menu_event ( 0, 1, rval ? 1 : 0, 0, 0, 0 ); }
        }
```
(Match the existing brace/scope style; `WIN_Msg`'s win_state is the caller's — `MENU` while in `WIN_MainMenu` — so the row carries `win="MENU"`.) If `IMS_WaitTimed(10)`'s return is already assigned to a variable, reuse it instead of re-wrapping.

- [ ] **Step 4: Build dosraptor, verify it compiles**

```bash
cmake --build /Users/nadavb/dev/dosraptor/build -j 2>&1 | tail -20
```
Expected: builds clean (no new warnings/errors in WINDOWS.C). Do NOT run the binary (windowed; user-gated).

- [ ] **Step 5: Commit (in the dosraptor repo)**

```bash
cd /Users/nadavb/dev/dosraptor
git add SOURCE/WINDOWS.C
git commit -m "parity(menu): emit menu-event on WIN_Credits/WIN_Msg any-key dismiss"
```

---

## Task 4: dosraptor scenario scripts

**Files:**
- Create: `/Users/nadavb/dev/dosraptor/tests/scripts/popup_winmsg_credits_dismiss.txt`
- Create: `/Users/nadavb/dev/dosraptor/tests/scripts/load_navigate_and_load.txt`

Both use 30-frame gaps (load-bearing — see any existing menu script header). Main-menu item order: 0=NEW, 1=LOAD, 2=OPTS, 3=ORDER, 4=CREDITS, 5=QUIT, 6=RETURN.

- [ ] **Step 1: Write `popup_winmsg_credits_dismiss.txt`**

```text
# popup_winmsg_credits_dismiss: dismiss the any-key popups (WIN_Msg, WIN_Credits).
#
# Empty save dir → Return on LOAD triggers WIN_Msg("No Pilots to Load"); dismiss
# it, then walk to CREDITS, enter, wait past the fade, dismiss. Each dismiss is
# emitted by the new C hooks (WIN_Msg/WIN_Credits raptor_parity_menu_event,
# field=1 → selected_item=0). 30-frame gaps are mandatory (see menu_main_nav.txt).
#
# Expected event walk (8 rows): Down→LOAD(sel=1,MENU); Return→WinMsg(sel=1,MENU);
# dismiss WinMsg(sel=0,MENU); Down→OPTS(2,MENU); Down→ORDER(3,MENU);
# Down→CREDITS(4,MENU); Return→enter Credits(4,MENU); dismiss Credits(0,CREDITS).
wait 30
key Down
wait 30
key Return
wait 60
key Return
wait 30
key Down
wait 30
key Down
wait 30
key Down
wait 30
key Return
wait 200
key Return
wait 60
quit
```

- [ ] **Step 2: Write `load_navigate_and_load.txt`**

```text
# load_navigate_and_load: open the LOAD window with 3 committed pilots, cycle the
# shown pilot (NEXT/PREV with wrap), then LOAD → Hangar.
#
# Fixture: tests/parity/menu_fixtures/load_navigate_and_load/CHAR000{0,1,2}.FIL
# (raptor-godot side; the capture tool points the C binary's save dir at it).
# C RAP_LoadWin is already hooked (LOADSAVE.C:506, active_field-1). The exact
# selected_item sequence is the GOLDEN's to define — align Godot to it in Phase C.
# 30-frame gaps mandatory.
wait 30
key Down
wait 30
key Return
wait 30
key Down
wait 30
key Down
wait 30
key Down
wait 30
key Up
wait 30
key Return
wait 60
quit
```

- [ ] **Step 3: Commit (in the dosraptor repo)**

```bash
cd /Users/nadavb/dev/dosraptor
git add tests/scripts/popup_winmsg_credits_dismiss.txt tests/scripts/load_navigate_and_load.txt
git commit -m "test(menu): Phase 1b scripts (popup dismiss + load navigate)"
```

---

## Task 5: extend the batched capture tool

**Files:**
- Modify: `tools/capture_menu_goldens.sh` (the default `SCRIPTS` list)

- [ ] **Step 1: Add both scenarios to the default list**

In `tools/capture_menu_goldens.sh`, the `SCRIPTS="${SCRIPTS:-\ ...}"` default list currently omits the two Phase 1b scenarios (with a "deferred" comment). Append them and update the comment:
```bash
help_paging_keys \
help_onscreen_buttons \
shipcomp_auto_confirm_sector \
askbool_yes_no_quit_save \
popup_winmsg_credits_dismiss \
load_navigate_and_load}"
```
Remove the stale `# load_navigate_and_load and popup_winmsg_credits_dismiss are deferred (Phase 1b).` line.

- [ ] **Step 2: Syntax-check**

```bash
bash -n tools/capture_menu_goldens.sh && echo "[syntax OK]"
```
Expected: `[syntax OK]`.

NOTE: the load scenario needs the C binary's save dir pointed at the fixture. `capture_menu_goldens.sh` runs the C binary directly (not via `run_menu_event_parity.sh`), so it does NOT currently pin a per-scenario save dir. Before capture, EITHER (a) add per-scenario `RAPTOR_SAVE_DIR` handling to the tool (copy `tests/parity/menu_fixtures/<name>` to a temp dir if it exists, like `run_menu_event_parity.sh:112-115`), OR (b) document that the user must run `load_navigate_and_load` with `RAPTOR_SAVE_DIR` set to the fixture. **Prefer (a)** — implement it in this task so the batched capture is one command. Insert, inside the per-scenario loop, before the two capture runs:
```bash
    fix="$REPO/tests/parity/menu_fixtures/$name"
    if [[ -d "$fix" ]]; then sd="$(mktemp -d)"; cp -R "$fix"/. "$sd"/; else sd="$(mktemp -d)"; fi
```
and add `RAPTOR_SAVE_DIR="$sd"` to BOTH `env`-prefixed C run lines, and `rm -rf "$sd"` after the cmp. Re-run `bash -n`.

- [ ] **Step 3: Commit**

```bash
git add tools/capture_menu_goldens.sh
git commit -m "tools(menu): capture popup + load scenarios; pin fixture save dir"
```

---

# PHASE B — User-gated capture (CHECKPOINT — the user runs this)

## Task 6: batched C capture (windowed; user runs when away)

NOT a subagent task. The C binary needs a real SDL renderer and steals focus (memory `no-windowed-c-binary-runs`). Hand off to the user.

- [ ] **Step 1: Ensure dosraptor is built with the new hooks**

```bash
cmake --build /Users/nadavb/dev/dosraptor/build -j
```

- [ ] **Step 2: User runs the capture (away from the screen)**

```bash
SCRIPTS="popup_winmsg_credits_dismiss load_navigate_and_load" tools/capture_menu_goldens.sh
```
Expected: `[capture] OK popup_winmsg_credits_dismiss (8 rows)` and `[capture] OK load_navigate_and_load (N rows)`, written to `tests/parity/c_menu_goldens/`. If `EMPTY` → the script never reached a hooked screen (check the hooks built / the script). If `UNSTABLE` → widen the script waits.

- [ ] **Step 3: Inspect the goldens (the load one is the oracle for Phase C)**

```bash
cat tests/parity/c_menu_goldens/popup_winmsg_credits_dismiss.menu.ndjson
cat tests/parity/c_menu_goldens/load_navigate_and_load.menu.ndjson
```
Record the `selected_item`/`win`/`event_index` sequence of the LOAD golden — it drives Task 8.

---

# PHASE C — Align offline + commit (subagent/inline, no more C)

## Task 7: align & confirm the popup scenario

**Files:** none expected (the sentinel from Task 2 should already match).

- [ ] **Step 1: Run the popup scenario**

```bash
dotnet build raptor.csproj --nologo -v quiet
tests/run_menu_event_parity.sh popup_winmsg_credits_dismiss
```
Expected: PASS. The 8-row walk should match (sel=0 on both dismiss rows).

- [ ] **Step 2: If it diverges, reconcile**

Likely causes and fixes:
- Event COUNT differs → the script's key→event mapping differs (e.g. C emits the WinMsg-trigger Return AND the dismiss as separate rows; confirm both sides agree). Adjust the script gaps or the Godot emit; re-run.
- `win` on the Credits-dismiss row is `MENU` not `CREDITS` (or vice-versa) → check `MenuStateMachine.State.ToParityString()` timing in `PlaythroughDriver` vs C's `set_win_state` ordering. Fix the side that's wrong per C authority (C sets CREDITS inside `WIN_Credits`, cleared after — the dismiss row carries CREDITS).
- `selected_item` not 0 on a dismiss → re-check Task 2's `EffectiveScreen`/`EffectiveSelectedItem` changes.

- [ ] **Step 3: Commit the golden**

```bash
git add tests/parity/c_menu_goldens/popup_winmsg_credits_dismiss.menu.ndjson
git commit -m "test(menu): popup_winmsg_credits_dismiss C golden (8/8 parity)"
```

## Task 8: align the load scenario (cursor model — the open risk)

**Files:** possibly `src/Sim/MenuStateMachine.cs` (`EffectiveSelectedItem()` LoadMission branch) and/or `src/Sim/LoadMissionPanel.cs`.

- [ ] **Step 1: Run the load scenario**

```bash
tests/run_menu_event_parity.sh load_navigate_and_load
```
Expected: prints both streams + comparator result.

- [ ] **Step 2: Classify the divergence (decision procedure)**

Compare the C golden's `selected_item` sequence to Godot's:
- **Identical** → done, skip to Step 4.
- **Constant offset / simple remap** (C reports the LOAD-window button field `active_field-1`; Godot reports the 0-based pilot index) → align `EffectiveSelectedItem()`'s `_loadMission.Active` branch (line 227) to emit the same value the C golden shows. If C holds `active_field=LOAD_LOAD=5→4` throughout while cycling, return the constant; if it tracks the shown pilot, map `_loadMission.SelectedIndex` accordingly. Add a focused unit test pinning the new mapping. Re-run.
- **Structurally different stream** (different number of rows, or the cursor semantics don't correspond — C's button cursor vs Godot's list cursor produce genuinely different walks) → STOP. This is a real parity gap in the Godot load UI, not an index bug. Do NOT force the golden to pass. Write it up as a finding in `docs/playbooks/parity/` (like the pilot-create bug), update the session state, and bring it to the user as a scoped follow-up (faithful LOAD-window rework). The committed golden waits until the UI matches.

- [ ] **Step 3: If aligned, run the full suite + sweep**

```bash
dotnet test tests/RaptorTests.csproj --no-build
tests/run_menu_event_sweep.sh
```
Expected: full suite green; sweep **9/9 PASS** (or 8/9 with the load scenario tracked as a finding per Step 2's structural branch).

- [ ] **Step 4: Commit the golden (+ any alignment fix)**

```bash
git add tests/parity/c_menu_goldens/load_navigate_and_load.menu.ndjson src/Sim/MenuStateMachine.cs tests/*.cs
git commit -m "test(menu): load_navigate_and_load C golden + LoadMission selected_item align"
```

## Task 9: final verification + state update

- [ ] **Step 1: Confirm the CI menu-event gate covers the new goldens**

```bash
ls tests/parity/c_menu_goldens/*.menu.ndjson | wc -l   # expect 9 (or 8 if load is a finding)
tests/run_menu_event_sweep.sh                          # the ci/full.sh gate runs exactly this
```
Expected: the sweep enumerates and passes all committed goldens (the `ci/full.sh` hard gate, `15b76f6`, runs this verbatim).

- [ ] **Step 2: Update the session state**

Record in `~/.agent-state/raptor-godot--fix-playtest-issues/state.md`: Phase 1b outcome (scenarios green / load finding), HEADs (raptor-godot + dosraptor), and append a Session Log entry.

- [ ] **Step 3: Report to the user**

Summarize: scenarios landed, sweep count, whether the load cursor model was an index offset or surfaced a finding, and what (if anything) remains.

---

## Properties → tests coverage

- **Fixture round-trips (oracle)** → Task 1, `Pilot_save_round_trips_header_fields` ([Theory], 3 cases).
- **Popup-dismiss sentinel** → Task 2, `PopupDismissEmitTests` (WinMsg + Credits → 0).
- **Event-stream equality** + **One event per processed keypress** → Tasks 7/8, the un-mocked `run_menu_event_parity.sh` integration runs (real godot + real C golden + comparator) — also the external-dependency tracer gate.
- **Hermeticity** → exercised by the runner pinning `RAPTOR_SAVE_DIR` (the load scenario uses the fixture; the popup scenario uses an empty dir).
- **C-reference inertness** → structural (emit-only hooks); validated by the existing in-wave + 7/7 menu goldens still passing after the dosraptor rebuild.

## External-dependency tracer gate

The C reference binary + `parity_diff.py --menu` are the external deps. The tracer-bullet is Task 6 (real windowed C capture) + Tasks 7/8 (`run_menu_event_parity.sh` runs real headless godot against the real C golden through the real comparator — no mocks, not skipped, runs in the default sweep / `ci/full.sh`). A scenario is not "done" until its `run_menu_event_parity.sh` passes un-mocked.
