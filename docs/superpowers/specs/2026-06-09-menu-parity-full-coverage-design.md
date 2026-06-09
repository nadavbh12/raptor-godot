# Menu parity — full coverage design (2026-06-09)

## Goal

Test scenarios that pass through **every** interactive menu element in the game,
each verified for C↔Godot parity via the per-keypress **menu-event** stream
(`RAPTOR_MENU_OUT` → `parity_diff.py --menu`). The authoritative surface is the
catalog in `docs/playbooks/parity/menu-surface-catalog.json`:

- **12 menus, 120 interactive elements.**
- 60 keyboard-drivable today; 18 need mouse-click; 2 need slider-drag; 2 need text-entry.
- **47 elements are not yet implemented on the Godot side** (`godot_*` blockers) —
  reaching parity on those means *building the feature first*, not just a test.

This is a multi-phase project. Each phase delivers committed, CI-gated scenarios.

## Current state (the tracer bullet, already landed and verified)

- C records one row per menu keypress via `raptor_parity_menu_event`
  (hook in `WIN_MainMenu`'s `SWD_Dialog` loop, `WINDOWS.C:2033`, reading the
  global `active_field` and emitting `field-1`).
- Godot mirrors via `ParityEmitter.EmitMenuEvent` (called from
  `PlaythroughDriver` after `MenuStateMachine.HandleInput`).
- `parity_diff.py --menu` compares: `win`/`selected_item`/`event_index`/`kind`
  exact, `screen`/`scancode`/`x`/`y`/`button` advisory, 100% threshold.
- `tests/run_menu_event_parity.sh <name>` runs the loop; the one landed scenario
  is `menu_main_nav` (Down/Down/Up/Return), byte-identical C↔Godot.

## Hard constraints (learned the hard way)

1. **30-frame key gaps are mandatory.** C menu input is wall-clock-paced
   (`framecount`-gated `SDL_PushEvent` racing `SWD_Dialog`'s busy-spin); keys a
   few frames apart register 0/1/2× nondeterministically. ~30 frames (~0.4 s,
   human keypress pace) makes the C golden stable. Every script obeys this.
2. **No ad-hoc windowed C runs.** The C binary needs a real SDL renderer, so each
   run steals window focus. All golden captures are **batched** into one
   unattended sweep the user kicks off when away — never interactive one-offs.
3. `src/Sim/` rules still apply (no `delta`/`_Process`/engine-RNG/wall-clock).
   Harness/recorder code lives in `src/Test/`, `src/View/`, and `port/` (C side).

## Architecture decisions

### A. Recording coverage (Phase 0 — prerequisite for every non-main-menu scenario)

The SWD windowing library is **precompiled** (no source: `active_field` is
assigned nowhere in `SOURCE/`). So a single shared hook inside `SWD_Dialog` is
impossible. Instead, add the same hook pattern already proven in `WIN_MainMenu`
at **each `SWD_Dialog` call site**. The 10 sites and their screens:

| Site | Function | Screen (`g_win_state`) |
|---|---|---|
| `WINDOWS.C:2034` | `WIN_MainMenu` | MENU (1) — **already hooked** |
| `WINDOWS.C:237` | `WIN_Opts` | inherits MENU |
| `WINDOWS.C:569` | `WIN_AskBool` | inherits parent |
| `WINDOWS.C:687` | `WIN_AskDiff` | inherits MENU |
| `WINDOWS.C:784` | `WIN_Register` | inherits MENU |
| `WINDOWS.C:1121` | `WIN_Hangar` | HANGAR (5) |
| `WINDOWS.C:1434` | `WIN_ShipComp` | inherits (UNKNOWN/HANGAR) |
| `STORE.C:378` | `STORE_Enter` | STORE (6) |
| `HELP.C:89` | `HELP_Win` | HELP (3) / ORDER (4) |
| `LOADSAVE.C:505` | `RAP_LoadWin` | inherits MENU |

```c
{ extern void raptor_parity_menu_event(int,int,int,int,int,int);
  extern int active_field;
  if (dlg.keypress != SC_NONE)
     raptor_parity_menu_event(0, active_field, dlg.keypress, 0,0,0); }
```

- **No screen-argument change needed** (simplification found during planning).
  The 6 screens whose label matters (MENU/CREDITS/HELP/ORDER/HANGAR/STORE) already
  set `g_win_state`; the sub-dialogs (Opts/AskBool/AskDiff/Register/Load/ShipComp)
  inherit their parent's `g_win_state`, and **Godot mirrors the same inheritance**
  (those are flags on top of `WinState`, not distinct states), so both sides emit
  the same `win` for the same keypress. Parity holds; granularity (e.g. labelling
  Options vs Menu distinctly) is unnecessary — scenarios disambiguate by event
  order + `selected_item`.
- **Godot mirror:** `EmitMenuEvent` reads `Menu.State.ToParityString()`; confirm
  the effective-state mapping equals C's `g_win_state` for each screen used in a
  scenario (verified in the batched C capture).
- **Faithful emit semantics** (deferred from the tracer bullet, needed now):
  emit `win` = the *pre-transition* state, `selected_item` = the *post-nav*
  highlight. Each key → exactly one `HandleInput` → one emit (mirrors C's
  one-emit-per-loop-iteration; no double-emit across sub-dialogs).
- **Popup dismiss caveat:** `WIN_Msg`/`WIN_Pause`/`WIN_Order`/`WIN_Credits` use an
  any-key wait, **not** `SWD_Dialog`, so the per-site hook doesn't see them. Their
  dismiss keypress is handled in Phase 1's `popup_*` scenario design (either a
  dedicated hook or excluded from the event stream on both sides).

### B. Determinism hardening

Pin or exclude frame-count/RNG-driven menu state from the recorder: the
DEMO_DELAY attract timer, `HANG_PIC random(3)` portrait flicker, and
`WIN_Msg/Order/Credits` `IMS_WaitTimed` timeouts. These must not perturb the
event stream under 30-frame pacing.

**Audit result (Phase 0.4, no code change):** the menu RNG/timers do not perturb
the event stream. `random(3)`@WINDOWS.C:1123 is the Hangar portrait flicker
(cosmetic, never touches `active_field`/`dlg.keypress`); `IMS_WaitTimed` drives
the any-key popup waits (Msg/Order/Credits — see popup caveat); `DEMO_DELAY`=4000
frames is the attract-mode timeout, and every scenario script (~500 frames at
30-frame gaps) stays far under it, so attract mode never fires.

**Reachability (Phase 0.4):** deep screens are reached keyboard-only.
`PilotCreationFlow` advances on `Return` even with empty name/callsign, so
`NEW → Return → Return → Return` walks Register → callsign → AskDiff → (accept) →
Hangar, and Hangar→SUPPLIES→Store / Hangar→MISSION→ShipComp follow. No text entry
(Phase 2) is needed to *reach* any screen — only to test actual typing. The lone
exception is `load_navigate_and_load`, which needs an existing pilot to load: that
needs a committed fixture **and** reconciling C's binary `CHAR%04u.FIL` format
with Godot's JSON save format, so it is deferred to a Phase 1b.

**Save-environment hermeticity (found while landing Phase 0.3).** Menu paths
branch on which pilot saves exist — e.g. Return on the main-menu LOAD item goes
to the LoadMission panel when `CHAR*.FIL` files exist, or the "No Pilots" message
when none do, and those report *different* `selected_item` values. C reads
`CHAR%04u.FIL` from its save path; Godot reads `RAPTOR_SAVE_DIR` (falling back to
a sibling probe that finds dosraptor's `CHAR000*.FIL`). The harness MUST pin an
isolated save dir on **both** sides so replay matches capture:
`run_menu_event_parity.sh` sets `RAPTOR_SAVE_DIR` to an empty dir by default, or
to a committed per-scenario fixture at `tests/parity/menu_fixtures/<name>/` when
one exists (e.g. `load_navigate_and_load` needs a fixed pilot set). The batch
capture tool must run C against the matching save dir.

### C. Input-modality script commands (extend both playthrough engines)

| Command | C side (`port/platform/playthrough.c`) | Godot side (`src/Test/Playthrough.cs` + `PlaythroughDriver`) |
|---|---|---|
| `chord A B` | hold two scancodes across one tick (Alt+X) | press two actions same tick |
| `type TEXT` | inject per-char `g_ascii` keydowns + `SC_BACKSPACE`, `I_TOUPPER` | feed chars to pilot-create text entry (already implemented in Godot) |
| `click X,Y` | pointer + `PTR_B1` pulse → `SWD_CheckMouse` (currently a stub) | `MenuStateMachine.HandlePointerClick(x,y)` (exists; needs more branches) |
| `drag X,Y→X2` | per-frame `PTR_X` capture for absolute volume (builds on click) | slider set (needs Godot slider drag) |

`mouse`/`click` are currently **stubs on both sides**.

### D. Godot feature build-out (47 elements)

Implement the missing menu features so scenarios can reach parity. Grouped:
`MAIN_RETURN` (in-game menu return), **Alt+X quit** (all screens), OPTS pointer
icons, REG id-pic cycle (kbd `SC_ALT`/`SC_CTRL` + mouse), `HANG_MAIN_MENU` /
`HANG_QSAVE`, `LOAD_DEL` (delete pilot), **SHIPCOMP** game/sector selection +
secret lights + cheats + per-sector node select, `WIN_Pause`, `WIN_Order`,
the `WIN_LoadComp` level-load progress screen. Each lands with its scenario.

## Phases (each ends with committed scenarios + green CI)

- **Phase 0 — Recording coverage.** Per-screen C hooks + `screen` arg; Godot
  per-screen emit + faithful semantics; determinism hardening. Exit: a keypress
  in *any* screen produces a correct, stable menu-event row on both sides.
- **Phase 1 — 16 keyboard scenarios** (no further deps). Covers ~60 elements
  (main menu, options volume/detail, difficulty, hangar nav, store buy/sell,
  load nav, help paging, sector auto-confirm, askbool, popups). Scripts +
  goldens + PASS.
- **Phase 2 — `type` command → `register_create_pilot`.** Harness-only on the C
  side (Godot text entry already exists). Unlocks the pilot-create → difficulty →
  hangar spine.
- **Phase 3 — `chord` + Godot Alt+X.** Unlocks the 7 Alt-X-quit scenarios.
- **Phase 4 — `click`/`drag` + Godot `HandlePointerClick` branches.** Mouse-only
  paths (LOAD cancel, REG view-areas/idpic-mouse, store hover, slider arbitrary
  values, askbool dragbar).
- **Phase 5 — remaining Godot features** (SHIPCOMP games/secret/cheats/sector,
  pause, order, load-progress, LOAD_DEL, hangar mainmenu/qsave, idpic, opts
  pointer icons) + their scenarios. Closes to 120/120.

## Golden capture (batched, no-windows-friendly)

`tools/capture_menu_goldens.sh` runs the C binary once per scenario back-to-back,
unattended, writing each `tests/parity/c_menu_goldens/<name>.menu.ndjson`. The
user runs it when away from the screen; goldens are committed so the per-scenario
Godot test never needs C again. Each golden is verified stable (re-run hash
match) before commit.

## Testing & layout

- Shared script: `dosraptor/tests/scripts/<name>.txt` (30-frame gaps).
- Committed C golden: `tests/parity/c_menu_goldens/<name>.menu.ndjson`.
- Per-scenario: `tests/run_menu_event_parity.sh <name>` → `parity_diff.py --menu`
  (100%).
- CI: a sweep runner over all scenario names; fail if any < 100%.
- Coverage tracking: a checklist mapping all 120 catalog elements → covering
  scenario; track % as phases land.

## Out of scope (for now)

- Per-event **pixel** parity (the `dump`/`RAPTOR_SHOT_DIR` infra exists and is
  used for debugging, but pixel diffs are not part of the event-parity gate).
- Real interactive "record yourself" capture-to-script tooling (stamping events
  with framecount to reconstruct a replayable script) — a possible later add.
