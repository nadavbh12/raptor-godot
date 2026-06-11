# Mid-Wave Abort + In-Game Main-Menu RETURN — Design

_2026-06-11. Faithful recreation of C's two "leave a campaign-in-progress" mechanisms.
Branch `main` (project norm). C authority: `dosraptor/SOURCE/{RAP.C,WINDOWS.C,INPUT.C}`._

## Goal

Let the player step out of an in-progress campaign and resume, matching the DOS C build:

1. **Mid-wave abort** — press `Esc` *during* a wave → an inline `"Abort Mission ?"` modal over
   the frozen game. **YES** restores the wave-start score and aborts to the landing cutscene →
   hangar (the **same** wave replays). **NO** resumes the wave.
2. **In-game main-menu RETURN** — between waves, the hangar's MAIN MENU button pops to the main
   menu; because a campaign is in progress, the menu shows a **RETURN** item; RETURN (or `Esc`)
   resumes back to the hangar.

Today neither works: `InteractiveInputController._UnhandledInput` blanket-early-returns on all
input while `Menu.InGame` (so `Esc` is dead mid-wave), and `MAIN_RETURN` is a `return false;`
stub (`MenuStateMachine.cs:647`).

## C authority (verified by reading)

- **Abort** — `RAP.C:1185-1222`. `if (KBD_IsKey(SC_ESC))` and `demo_mode == DEMO_OFF`:
  `WIN_AskBool("Abort Mission ?")`. YES → `plr.score = start_score; rval = TRUE; break;`
  (`start_score` is `plr.score` snapshotted at `RAP.C:883`, the score brought into the wave).
  NO → re-init input and continue the loop. The `WIN_AskBool` is a blocking modal — the whole
  game loop is frozen while it is up.
- **Campaign loop** — `WIN_MainLoop` (`WINDOWS.C:1710`) sets `ingameflag = TRUE` and loops
  hangar → ship-comp → `Do_Game()`. `abort_flag = Do_Game()`. After the wave:
  - energy ≤ 0 → `ingameflag = FALSE; INTRO_Death(); return;`
  - `if (abort_flag) { INTRO_Landing(); continue; }` — abort → landing → back to hangar
    **without advancing `game_wave`** (same wave replays).
  - else normal completion → advance wave / difficulty.
- **`ingameflag` ("campaign in progress")** is **broader** than "actively in a wave". It is set
  on campaign start and cleared on death (`WINDOWS.C:1786`), on loading a *different* pilot
  (`MAIN_LOAD` case 1, `WINDOWS.C:2153`), and on quit.
- **In-game main menu** — `WIN_MainMenu`: `if (KBD_Key(SC_ESC) && ingameflag) goto menu_exit;`
  (`WINDOWS.C:2112`) and `case MAIN_RETURN: if (ingameflag) goto menu_exit;` (`WINDOWS.C:2178`).
  `menu_exit` leaves the menu loop → re-enters `WIN_MainLoop` → `WIN_Hangar` (resume at the
  hangar between waves). MAIN_RETURN is only reachable between waves, never mid-wave.

## Godot current state (verified)

- `InteractiveInputController._UnhandledInput` — `if (_menuController?.Menu.InGame == true) return;`
  (`src/Sim/InteractiveInputController.cs:45`) swallows everything in-game.
- `MenuStateMachine`:
  - `InGame` (`:135`) = actively in a wave; set false by `CompleteMission`/`PlayerDied`.
  - `VisibleItemCount => InGame ? ItemCount : ItemCount - 1` (`:56`) — currently gates MAIN_RETURN
    visibility off `InGame`, which is never true while you can reach the menu → RETURN always hidden.
  - Generic AskBool machinery: `_inAskBool`, `_askBoolQuestion`, `_askBoolYes`, `_askBoolOnYes`
    callback; `HandleAskBoolInput` (`:659`); openers `OpenAskBoolSave` (`:701`) /
    `OpenAskBoolQuit` (`:714`). The YES-callback pattern is the idiom to reuse.
  - `CompleteMission(frame, finalWave=false)` (`:276`) → `WinState.Landing` (or `Victory`) → hangar.
    Called by `WaveController.cs:1716`.
  - MAIN_RETURN stub at `:647`; hangar MAIN MENU button → `EnterMenu` (`:826`, wired in `bb7b14e`);
    `EnterMenu` (`:358`) resets sub-dialog state but does not touch `InGame` (or any campaign flag).
- `WaveController`:
  - `Score` (`:200`), `SetScore` (`:204`). No wave-start snapshot today.
  - `_PhysicsProcess` (`:808`) runs the phase order via `_scheduler.Tick()` (`:832`), then
    `_gameLoopIter++`. The freeze gate goes here.
  - The game-enter path (`ApplyPendingGameEnter` / `OnGameEnter`) is where a wave begins — the
    score-snapshot point (Godot equivalent of `RAP.C:883`).

## Design

### Architecture decision: reuse AskBool

The abort prompt reuses the existing `_inAskBool` + `_askBoolOnYes` machinery (same as
`OpenAskBoolSave`/`OpenAskBoolQuit`), not a new pause state machine. Rationale: the nav
(`HandleAskBoolInput`) and render path already exist and are tested; the YES-callback is the
established idiom; interactive abort never runs under the parity harness, so there is no collision
with menu-event parity emission. A dedicated state would duplicate all of this for no benefit.

### Part A — `CampaignActive` flag (faithful `ingameflag`)

New flag on `MenuStateMachine`, **separate from `InGame`**:

- Set `true` on pilot create (`PilotCreationFlow` confirm) and pilot load (`ApplyLoadedPilot`).
- Stays `true` across waves, hangar, store, landing/victory cutscenes, and the main menu.
- Cleared on: `PlayerDied`, loading a *different* pilot, and quit-to-DOS (`OnQuit`).
- Repoint `VisibleItemCount` (`:56`) and MAIN_RETURN visibility from `InGame` → `CampaignActive`.

Cold-launch default is `false`. The captured menu-event goldens start no campaign, so
`CampaignActive` is `false` throughout them and `VisibleItemCount` is byte-unchanged → menu-event
sweep stays 9/9.

### Part B — MAIN_RETURN resume (mechanism 2)

- Fill the `:647` stub: when `CurrentItem == ReturnItemIndex`, `EnterState(WinState.Hangar, …)`
  and reset the hangar cursor to a sensible position (mission). Resumes between-waves at the hangar.
- `Escape` on the **main** menu screen resumes to the hangar **only when `CampaignActive`**
  (`WINDOWS.C:2112`). The guard preserves the existing Credits/Help `Esc`-recovery behavior
  (known trap: a blanket Esc change regressed commit `3048975`).
- Entry point (hangar MAIN MENU → `EnterMenu`) already exists; `EnterMenu` must **not** clear
  `CampaignActive`.

### Part C — Mid-wave abort (mechanism 1)

- **Input** (`InteractiveInputController`): replace the blanket in-game early-return with:
  - If `InGame` and the abort prompt is **not** active and the key is `Esc` → open the abort prompt.
  - If the abort prompt **is** active → route `Left`/`Right`/`Tab`/`Up`/`Down`/`Return`/`Space`/`Escape`
    to `HandleAskBoolInput`; swallow other gameplay keys.
  - Otherwise (in-game, no prompt) → keep swallowing (unchanged).
  - Gated to interactive runs only (the controller is inactive under `RAPTOR_PLAYTHROUGH`).
- **Prompt**: `MenuStateMachine.OpenAskBoolAbort(onYes)` sets `_askBoolQuestion = "Abort Mission ?"`,
  `_inAskBool = true`, `_askBoolOnYes = onYes`. Expose an `AbortPromptActive` read for the freeze gate
  (true while `_inAskBool` was opened for abort and `InGame`).
- **Freeze**: in `WaveController._PhysicsProcess`, immediately after the `!_waveActive` guard
  (`:820`) and **before `_subTick++`** (`:829`), early-return when the abort prompt is active. Neither
  the sub-tick counter, the iter, nor any gameplay phase advances. SimClock continues (harmless —
  interactive-only, parity-inert).
- **Score snapshot**: capture `Score` into `_waveStartScore` in the game-enter path (wave begins).
- **YES** → `WaveController.AbortMission()`: `Score = _waveStartScore;` then
  `_menu.CompleteMission(SimClock.Frame, finalWave: false)` → landing → hangar (same wave; wave/diff
  not advanced). Symmetric with the normal-completion call at `:1716`.
- **NO** → close the prompt; the freeze gate clears → wave resumes with identical state.
- **Render** (View): draw the `"Abort Mission ?"` AskBool overlay over the frozen game. Reuse the
  existing AskBool overlay renderer.

## Properties

- **Score restore**: after a YES abort, `Score == _waveStartScore` (the score brought into the wave).
  *Domain:* `WaveController.AbortMission`.
- **Abort replays wave**: aborting does not advance wave number or difficulty. *Domain:* campaign
  progression (`CompleteMission(finalWave:false)` → landing → hangar, same wave).
- **Resume is inert**: pressing NO leaves all sim state (enemies, bullets, player, score, scroll,
  `_gameLoopIter`) identical to the pre-prompt frame. *Domain:* abort pause/resume.
- **Freeze halts the sim**: while `AbortPromptActive`, no enemy/bullet/player/score/iter mutation
  occurs. *Domain:* `WaveController._PhysicsProcess` gate.
- **RETURN visibility ⟺ CampaignActive**: the main-menu RETURN item is navigable iff a campaign is
  in progress; `VisibleItemCount` wraps over 7 items iff `CampaignActive`, else 6. *Domain:* menu nav.
- **CampaignActive lifecycle**: `false` at cold launch; `true` after create/load; `false` after death,
  loading a different pilot, or quit. *Domain:* `MenuStateMachine` campaign flag.
- **Parity-inert**: no parity checkpoint and no menu-event golden changes. Interactive-only (controller
  off under playthrough); harness scripts never press the abort key and start no campaign in the
  captured menu-event goldens. *Domain:* L2a parity + menu-event sweep.

## Testing

- **Unit (Sim, headless, no Godot Node):**
  - `MenuStateMachine`: `CampaignActive` lifecycle (create/load set; death/load-other/quit clear);
    `VisibleItemCount`/MAIN_RETURN visibility ⟺ `CampaignActive`; RETURN → Hangar; main-menu `Esc`
    resumes only when `CampaignActive` and does not regress Credits/Help Esc-recovery; abort AskBool
    open + YES invokes the callback + NO clears.
  - `WaveController` (via the existing pure-helper/dispatcher pattern where a Node can't be
    instantiated): `_waveStartScore` snapshot; `AbortMission` restores score + routes to
    `CompleteMission(finalWave:false)`; the freeze predicate gates the tick.
  - `InteractiveInputController`: in-game `Esc` opens the abort prompt; while the prompt is up, nav
    keys route to AskBool and gameplay keys are swallowed (controller-layer test, per the standing
    decision that new key bindings must be exercised at the controller layer).
- **Regression gates (must stay green):** full unit suite (currently 786); `tools/SimLint` 0;
  `tests/run_menu_event_sweep.sh` 9/9; the 4 CI L2a scenarios byte-identical. A wave1 exact-diff
  spot-check confirms the freeze gate and score snapshot don't perturb the normal (non-abort) path.
- **Manual (interactive, mouse-grab, user-gated):** press `Esc` mid-wave → "Abort Mission ?" →
  YES lands to hangar with restored score; NO resumes. From the hangar, MAIN MENU → main menu shows
  RETURN → RETURN/Esc resumes to the hangar.

## Validated dependencies

All dependencies are internal to the two repos and were verified by reading the C source and the
Godot code directly (no external CLI/API/library involved):

- `RAP.C:1185-1222` (abort + `start_score` restore) — read.
- `RAP.C:883` (`start_score = plr.score`) — read.
- `WINDOWS.C:1710-1796` (`WIN_MainLoop`, `ingameflag`, abort→Landing→continue) — read.
- `WINDOWS.C:2112` / `:2178` (in-game `Esc` / MAIN_RETURN → `menu_exit`) — read.
- Godot: `InteractiveInputController.cs:42-74`, `MenuStateMachine.cs:{56,276,358,560-657,659-719}`,
  `WaveController.cs:{200,808-835}` — read.

No `UNVERIFIED` items.

## Out of scope

- The separate `P`/`pause` key (inert today) — abort uses `Esc`, matching C; no general pause.
- Demo-mode abort branches (`DEMO_PLAYBACK`/`DEMO_RECORD`, `RAP.C:1215-1221`) — Godot's interactive
  controller is off under the harness, so these never apply.
- Any change to the death path, victory cinematic, or wave-progression model.
