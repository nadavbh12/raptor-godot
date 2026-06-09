# Menu Parity — Phase 0 + 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record menu-input events on every menu screen (not just the main menu) and land the 16 keyboard-drivable test scenarios, each verified C↔Godot at 100% via `parity_diff.py --menu`.

**Architecture:** Per-`SWD_Dialog`-call-site hooks on the C side (the SWD library is precompiled, so no single shared hook); a faithful per-screen emitter on the Godot side. Because C goldens are captured in one batched windowed sweep (user away), the flow is **capture-once → align-Godot-offline**: build the C hooks + Godot emitter + scripts, capture all 16 C goldens in one batch (ground truth for each screen's `active_field`), then align Godot's per-screen `selected_item` to the captured goldens with no further C runs.

**Tech stack:** C (dosraptor `port/`, `SOURCE/`), C# (Godot `src/Test`, `src/Sim`), xUnit (`godot --headless`), Python comparator, bash runners.

**Spec:** `docs/superpowers/specs/2026-06-09-menu-parity-full-coverage-design.md`
**Catalog:** `docs/playbooks/parity/menu-surface-catalog.json`

**Hard rules:** 30-frame key gaps in every script (C wall-clock nondeterminism); no ad-hoc windowed C runs (batch only); `src/Sim/` stays free of `delta`/`_Process`/engine-RNG/wall-clock.

---

## File structure

- **C hooks (dosraptor):** `SOURCE/WINDOWS.C` (6 new sites), `SOURCE/STORE.C`, `SOURCE/HELP.C`, `SOURCE/LOADSAVE.C` (1 each). No new files; `port/platform/parity.{c,h}` unchanged (existing `raptor_parity_menu_event` is reused as-is).
- **Godot emitter:** `src/Test/ParityEmitter.cs` (`EmitMenuEvent` gains screen + per-screen selected_item), `src/Test/PlaythroughDriver.cs` (capture pre-transition state).
- **Godot recorder tests:** `tests/Test/MenuEventEmitterTests.cs` (new xUnit file).
- **Scripts (shared, dosraptor):** `tests/scripts/menu_*.txt`, `*_*.txt` — 16 files.
- **Goldens (raptor-godot):** `tests/parity/c_menu_goldens/<name>.menu.ndjson` — 16 files (from batch).
- **Capture + runner:** `tools/capture_menu_goldens.sh` (new), `tests/run_menu_event_parity.sh` (exists), `tests/run_menu_event_sweep.sh` (new).

---

## Phase 0 — Recording coverage (all screens)

### Task 0.1: Hook the 6 remaining WINDOWS.C SWD_Dialog sites

**Files:**
- Modify: `dosraptor/SOURCE/WINDOWS.C` at sites `237` (WIN_Opts), `569` (WIN_AskBool), `687` (WIN_AskDiff), `784` (WIN_Register), `1121` (WIN_Hangar), `1434` (WIN_ShipComp).

- [ ] **Step 1: Confirm each site is the post-`SWD_Dialog` line.** Run:
  `grep -n 'SWD_Dialog *( *&' dosraptor/SOURCE/WINDOWS.C` → expect lines 237,569,687,784,1121,1434,2034. (2034 already hooked.) Line numbers may have shifted; re-confirm the owning function with `grep -nE 'WIN_(Opts|AskBool|AskDiff|Register|Hangar|ShipComp|MainMenu) *\('`.

- [ ] **Step 2: Insert the hook block immediately after each `SWD_Dialog ( &dlg );`** (identical to the WIN_MainMenu hook). For each of the 6 sites add:

```c
   { extern void raptor_parity_menu_event(int,int,int,int,int,int);
     extern int active_field;
     if ( dlg.keypress != SC_NONE )
        raptor_parity_menu_event ( 0, active_field, dlg.keypress, 0, 0, 0 ); }
```

  Note: each loop uses a local `dlg` (`SWD_Dialog ( &dlg )`), so `dlg.keypress` is in scope. `active_field` is the global SWD highlight.

- [ ] **Step 3: Build the C reference.** Run (no game window — compile only):
  `cd dosraptor && make 2>&1 | tail -5` (or the project's build command). Expected: clean build, binary exports `_raptor_parity_menu_event`.

- [ ] **Step 4: Commit (dosraptor).**
```bash
cd dosraptor && git add SOURCE/WINDOWS.C
git commit -m "parity: record menu events in Opts/AskBool/AskDiff/Register/Hangar/ShipComp loops"
```

### Task 0.2: Hook STORE.C, HELP.C, LOADSAVE.C SWD_Dialog sites

**Files:**
- Modify: `dosraptor/SOURCE/STORE.C:378` (STORE_Enter), `dosraptor/SOURCE/HELP.C:89` (HELP_Win), `dosraptor/SOURCE/LOADSAVE.C:505` (RAP_LoadWin).

- [ ] **Step 1: Locate each site.** Run:
  `for f in STORE HELP LOADSAVE; do grep -n 'SWD_Dialog *( *&' dosraptor/SOURCE/$f.C; done`

- [ ] **Step 2: Insert the same hook block** after each `SWD_Dialog ( &dlg );` (verbatim from Task 0.1 Step 2).

- [ ] **Step 3: Build.** `cd dosraptor && make 2>&1 | tail -5`. Expected: clean.

- [ ] **Step 4: Commit (dosraptor).**
```bash
cd dosraptor && git add SOURCE/STORE.C SOURCE/HELP.C SOURCE/LOADSAVE.C
git commit -m "parity: record menu events in Store/Help/Load dialog loops"
```

### Task 0.3: Godot emitter — pre-transition win + per-screen selected_item

The Godot emitter must mirror C's hook: emit `win` from the state **before** the keypress's transition, and `selected_item` as the **screen-appropriate** highlighted index (C reports `active_field-1` for whichever window is active). Today `EmitMenuEvent` always reads `Menu.CurrentItem` (main-menu only) and post-transition `Menu.State`.

**Files:**
- Modify: `src/Test/ParityEmitter.cs` (worker `EmitMenuEvent`), `src/Test/PlaythroughDriver.cs` (capture pre-state).
- Test: `tests/Test/MenuEventEmitterTests.cs` (new).

- [ ] **Step 1: Add a pure helper `EffectiveSelectedItem` on `MenuStateMachine`** that returns the highlighted index for the current screen/sub-dialog. Add to `src/Sim/MenuStateMachine.cs`:

```csharp
/// <summary>The highlighted index C's active_field-1 reports for the
/// currently-active menu window. Per-screen so menu-event parity holds
/// outside the main menu. Verified against captured C goldens (Phase 0).</summary>
public int EffectiveSelectedItem()
{
    if (_inAskBool)        return _askBoolYes ? 0 : 1;
    if (_options.Active)   return _options.Field;
    if (_loadMission.Active) return _loadMission.SelectedIndex;
    if (_pilotCreate.Active) return _pilotCreate.DifficultyField;  // 0 until AskDiff
    return State switch
    {
        WinState.Hangar => _hangar.Position,
        WinState.Store  => Store?.CurrentItem ?? 0,
        WinState.Help   => _help.PageIndex,
        _               => CurrentItem,   // Menu / Unknown (sector) / Credits
    };
}
```
  (If `_pilotCreate.DifficultyField` / `Store.CurrentItem` accessors don't exist, expose them as `internal int` getters in the same step — minimal additions, no behavior change.)

- [ ] **Step 2: Write the failing recorder test.** Create `tests/Test/MenuEventEmitterTests.cs`:

```csharp
using Raptor.Sim;
using Raptor.Test;
using Xunit;

public class MenuEventEmitterTests
{
    // Main-menu nav still reports CurrentItem (regression guard for the tracer bullet).
    [Fact]
    public void MainMenu_down_reports_highlight_1()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Down", 1);
        Assert.Equal(WinState.Menu, m.State);
        Assert.Equal(1, m.EffectiveSelectedItem());
    }

    // Hangar nav reports the hangar cursor, not the stale main-menu item.
    [Fact]
    public void Hangar_reports_hangar_position()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.CompleteMission(10);                 // -> Hangar, position 1 (SUPPLIES)
        Assert.Equal(WinState.Hangar, m.State);
        Assert.Equal(1, m.EffectiveSelectedItem());
    }
}
```

- [ ] **Step 3: Run, expect FAIL** (`EffectiveSelectedItem` undefined / Hangar case wrong):
  `dotnet test --filter MenuEventEmitterTests 2>&1 | tail -15`. Expected: compile error or assertion fail.

- [ ] **Step 4: Implement** Step 1's helper (and any accessor getters) until both tests pass.
  `dotnet test --filter MenuEventEmitterTests 2>&1 | tail -8`. Expected: PASS.

- [ ] **Step 5: Wire pre-transition `win` + `EffectiveSelectedItem` into the emit path.** In `src/Test/ParityEmitter.cs`, change the worker `EmitMenuEvent` to accept the pre-state and selected item:

```csharp
public void EmitMenuEvent(string action, string winBefore, int selectedItem)
{
    if (_menuOut == null) return;
    int scancode = action switch { "Down"=>80, "Up"=>72, "Return"=>28, "Escape"=>1, _=>0 };
    var line = string.Create(CultureInfo.InvariantCulture,
        $"{{\"type\":\"menu\",\"event_index\":{_menuEventIndex},\"kind\":\"key\",\"win\":\"{winBefore}\",\"screen\":{ScreenInt(winBefore)},\"selected_item\":{selectedItem},\"scancode\":{scancode},\"x\":0,\"y\":0,\"button\":0}}");
    _menuOut.WriteLine(line);
    _menuEventIndex++;
}
private static int ScreenInt(string win) => win switch {
    "MENU"=>1,"CREDITS"=>2,"HELP"=>3,"ORDER"=>4,"HANGAR"=>5,"STORE"=>6,_=>0 };
```
  Update the Node forwarder: `public void EmitMenuEvent(string a, string w, int s) => _worker.EmitMenuEvent(a, w, s);`

- [ ] **Step 6: Capture pre-state in `PlaythroughDriver._PhysicsProcess`:**

```csharp
if (_pendingKey != null && _menu != null)
{
    string key = _pendingKey;
    _pendingKey = null;
    string winBefore = _menu.State.ToParityString();   // pre-transition
    _menu.HandleInput(key, Sim.SimClock.Frame);
    int sel = _menu.EffectiveSelectedItem();            // post-nav highlight
    _emitter?.EmitMenuEvent(key, winBefore, sel);
}
```

- [ ] **Step 7: Build + full unit run.** `dotnet build raptor.csproj --nologo -v q && dotnet test 2>&1 | tail -8`. Expected: build clean, all tests pass.

- [ ] **Step 8: Regression — the tracer bullet still passes headless.** Run the existing `menu_main_nav` Godot replay (headless, no C) and confirm 4 rows `selected_item=1,2,1,1`, all `win=MENU`:
```bash
RAPTOR_PLAYTHROUGH="$PWD/../dosraptor/tests/scripts/menu_main_nav.txt" \
RAPTOR_MENU_OUT=/tmp/g.ndjson RAPTOR_TEST_FAST=1 RAPTOR_DETERMINISTIC_RNG=1 \
godot --path . --headless --quit-after 600 --audio-driver Dummy >/dev/null 2>&1
python3 tests/comparator/parity_diff.py --menu \
  --c-golden tests/parity/c_menu_goldens/menu_main_nav.menu.ndjson --godot-out /tmp/g.ndjson
```
  Expected: `PASS` (4/4). Then commit (raptor-godot):
```bash
git add src/Test/ParityEmitter.cs src/Test/PlaythroughDriver.cs src/Sim/MenuStateMachine.cs tests/Test/MenuEventEmitterTests.cs
git commit -m "test(menu): per-screen menu-event emit (pre-transition win + effective highlight)"
```

### Task 0.4: Determinism hardening note

- [ ] **Step 1: Audit RNG/timer-driven menu state.** Run:
  `grep -nE 'random *\(|DEMO_DELAY|IMS_WaitTimed' dosraptor/SOURCE/WINDOWS.C` and confirm none of these perturb `active_field` or `dlg.keypress` (they drive cosmetics: HANG_PIC flicker, attract timeout). If any feed the event stream, exclude them. Document findings inline in the spec's section B. (No code change expected; this is a guard verified by the batch run's stability re-check.)

---

## Phase 1 — 16 keyboard scenarios

### Task 1.1: Author the 16 shared scripts

**Files:** create under `dosraptor/tests/scripts/` (each read by both C and Godot). All use 30-frame gaps and optional `dump` lines. Names match the catalog's `proposed_scenarios`.

- [ ] **Step 1: Write each script.** The 16: `menu_nav_all_buttons`, `menu_help_f1`, `menu_options_volume_detail_exit`, `register_help_f1`, `askdiff_select_each_difficulty`, `askdiff_abort`, `hangar_nav_mission_supplies`, `hangar_help_save`, `store_buy_sell_navigate`, `store_help_f1`, `load_navigate_and_load`, `help_paging_keys`, `help_onscreen_buttons`, `shipcomp_auto_confirm_sector`, `askbool_yes_no_quit_save`, `popup_winmsg_credits_dismiss`.

  Pattern (example `menu_options_volume_detail_exit.txt` — navigates into Options, adjusts both volumes, toggles detail, exits):
```
# Options: enter from main menu, nav rows, +/- volumes, toggle detail, exit.
# 30-frame gaps mandatory (C wall-clock input nondeterminism).
wait 30
key Down            # NEW->LOAD
wait 30
key Down            # LOAD->OPTIONS
wait 30
key Return          # enter Options
wait 30
key Down            # to Music row
wait 30
key Right           # music +8
wait 30
key Left            # music -8
wait 30
key Down            # to FX row
wait 30
key Right           # fx +8
wait 30
key Up              # back to detail row
wait 30
key Return          # toggle detail
wait 30
key Escape          # exit Options (-> persists, back to menu)
wait 40
quit
```
  Author the other 15 by walking each menu's catalog elements (see `menu-surface-catalog.json` `covers` lists). Reaching the deeper screens requires getting there first: e.g. hangar/store/askdiff scenarios begin with the new-pilot or load path. Keep each scenario’s keys to the catalog elements it claims to cover; one Return/Escape per real screen transition.

- [ ] **Step 2: Lint the scripts** (only known verbs, every `key`/`wait` well-formed):
  `for s in dosraptor/tests/scripts/{menu_nav_all_buttons,menu_help_f1,...}.txt; do echo "== $s =="; grep -vE '^\s*(#|$|wait [0-9]+|key [A-Za-z0-9]+|dump [A-Za-z0-9_]+|down |up |quit)' "$s" && echo "  BAD LINE ^" || echo "  ok"; done`
  Expected: every script `ok`.

- [ ] **Step 3: Commit (dosraptor).**
```bash
cd dosraptor && git add tests/scripts/*.txt
git commit -m "test(menu): 16 keyboard menu-parity scripts (30-frame gaps)"
```

### Task 1.2: Batch capture tool + sweep runner

**Files:** create `tools/capture_menu_goldens.sh`, `tests/run_menu_event_sweep.sh`.

- [ ] **Step 1: Write `tools/capture_menu_goldens.sh`** — runs the C binary once per scenario back-to-back (unattended), writes each golden, and re-runs each once to assert byte-stability before keeping it:

```bash
#!/usr/bin/env bash
# Batch-capture all menu-event C goldens. Run when AWAY from the screen:
# each scenario launches a windowed C run (focus steal). Verifies stability.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
DOS="${DOSRAPTOR:-$(cd "$REPO/.." && pwd)/dosraptor}"
CBIN="${CBIN:-$DOS/build/raptor.app/Contents/MacOS/raptor}"
SCRIPTS="${SCRIPTS:-$(cd "$DOS/tests/scripts" && ls menu_*.txt register_*.txt askdiff_*.txt hangar_*.txt store_*.txt load_*.txt help_*.txt shipcomp_*.txt popup_*.txt 2>/dev/null | sed 's/\.txt$//')}"
OUT="$REPO/tests/parity/c_menu_goldens"
for name in $SCRIPTS; do
  s="$DOS/tests/scripts/$name.txt"; [ -f "$s" ] || { echo "skip $name (no script)"; continue; }
  a="$(mktemp)"; b="$(mktemp)"
  for f in "$a" "$b"; do
    SDL_AUDIODRIVER=dummy RAPTOR_SKIPINTRO=1 RAPTOR_PLAYTHROUGH="$s" RAPTOR_MENU_OUT="$f" \
      timeout 120 "$CBIN" >/dev/null 2>&1 || true
  done
  if cmp -s "$a" "$b"; then cp "$a" "$OUT/$name.menu.ndjson"; echo "OK   $name ($(wc -l <"$a") rows)";
  else echo "UNSTABLE $name — widen waits"; fi
  rm -f "$a" "$b"
done
```

- [ ] **Step 2: Write `tests/run_menu_event_sweep.sh`** — runs Godot once per scenario (headless) and compares against the committed golden:

```bash
#!/usr/bin/env bash
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
fail=0
for g in "$REPO"/tests/parity/c_menu_goldens/*.menu.ndjson; do
  name="$(basename "$g" .menu.ndjson)"
  if ! "$REPO/tests/run_menu_event_parity.sh" "$name" >/tmp/sweep_$name.log 2>&1; then
    echo "FAIL $name"; tail -4 /tmp/sweep_$name.log; fail=1
  else echo "PASS $name"; fi
done
exit $fail
```

- [ ] **Step 3: `chmod +x` both, commit (raptor-godot).**
```bash
chmod +x tools/capture_menu_goldens.sh tests/run_menu_event_sweep.sh
git add tools/capture_menu_goldens.sh tests/run_menu_event_sweep.sh
git commit -m "test(menu): batch C-golden capture + Godot sweep runner"
```

### Task 1.3: BATCH CAPTURE (user-gated — run when away)

- [ ] **Step 1:** With dosraptor built (Tasks 0.1–0.2) and scripts committed, the user runs:
  `tools/capture_menu_goldens.sh` (≈16 windowed C runs back-to-back). Each prints `OK <name> (<rows>)` or `UNSTABLE`.
- [ ] **Step 2:** For any `UNSTABLE` scenario, widen its waits (35–40) and re-capture that one. Goal: all `OK`.
- [ ] **Step 3:** Commit the goldens (raptor-godot).
```bash
git add tests/parity/c_menu_goldens/*.menu.ndjson
git commit -m "test(menu): capture 16 keyboard-scenario C goldens"
```

### Task 1.4: Align Godot to goldens + green sweep

- [ ] **Step 1: Run the sweep.** `tests/run_menu_event_sweep.sh 2>&1 | tail -25`. Expect some FAILs where Godot's `selected_item`/`win` per screen doesn't yet match C's captured `active_field`.
- [ ] **Step 2: For each FAIL, read the diff** (`run_menu_event_parity.sh <name>` prints both streams). Adjust `MenuStateMachine.EffectiveSelectedItem()` (per-screen index offset/order) and/or the screen mapping so Godot matches the **captured** golden. No C re-run needed.
- [ ] **Step 3: Add a unit test** in `MenuEventEmitterTests.cs` pinning each newly-aligned screen's index (so the alignment can't regress). Repeat 1–3 until the sweep is all `PASS`.
- [ ] **Step 4: Full unit run + sweep green.** `dotnet test 2>&1 | tail -5 && tests/run_menu_event_sweep.sh`. Expected: all tests pass, sweep all `PASS`.
- [ ] **Step 5: Commit (raptor-godot).**
```bash
git add src/Sim/MenuStateMachine.cs tests/Test/MenuEventEmitterTests.cs
git commit -m "test(menu): align Godot per-screen highlight to C goldens; 16 scenarios green"
```

---

## Self-Review

**Spec coverage (Phase 0 + 1 portion):**
- Recording coverage (spec A) → Tasks 0.1, 0.2 (C hooks), 0.3 (Godot per-screen emit). ✓
- Determinism hardening (spec B) → Task 0.4. ✓
- 16 keyboard scenarios (spec Phase 1) → Tasks 1.1–1.4. ✓
- Batched golden capture (spec "Golden capture") → Tasks 1.2, 1.3. ✓
- Testing/layout (spec "Testing & layout") → `run_menu_event_parity.sh` per scenario + `run_menu_event_sweep.sh` CI. ✓
- Phases 2–5 (type/chord/click/Godot features) are **out of scope for this plan** — separate plans, authored after Phase 1 lands.

**External-dependency gate:** the real C binary + real Godot binary are exercised un-mocked by `run_menu_event_parity.sh` / `run_menu_event_sweep.sh` (the tracer-bullet tests). xUnit `MenuEventEmitterTests` covers the pure recorder logic without mocking the state machine. No happy-path test is skipped or marked.

**Placeholder scan:** the 16 scripts are listed by name with one fully-worked example; Task 1.1 Step 1 directs authoring the rest from the catalog `covers` lists (the catalog provides the exact per-scenario element list — not a placeholder, a data source). All code steps show code.

**Type consistency:** `EffectiveSelectedItem()` defined in Task 0.3 Step 1, used in 0.3 Step 6; `EmitMenuEvent(action, winBefore, selectedItem)` defined in 0.3 Step 5, called in 0.3 Step 6. `ScreenInt` maps the same 6 labels the C `g_win_state` uses.

**Known risk:** per-screen `active_field` numbering may not align 1:1 with Godot indices for some screens — explicitly handled by the capture-once → align-offline flow (Task 1.4), not assumed away.
