# Mid-Wave Abort + In-Game Main-Menu RETURN — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the player step out of an in-progress campaign and resume — mid-wave `Esc` → "Abort Mission ?" (YES restores wave-start score + aborts to landing/hangar, same wave replays; NO resumes), and the in-game main menu shows a working RETURN that resumes to the hangar.

**Architecture:** Three slices. (A) A new `CampaignActive` flag on `MenuStateMachine` = C's `ingameflag` (separate from `InGame`), gating MAIN_RETURN visibility. (B) Fill the MAIN_RETURN stub + main-menu `Esc` resume. (C) Mid-wave abort: reuse the existing AskBool machinery for the prompt, freeze the `WaveController` tick while it's up, snapshot/restore the wave-start score, and route the input through a pure decision helper. All View-only/interactive-only → parity-inert.

**Tech Stack:** C# (`net8.0`), Godot 4.6.3 Mono, xUnit 2.9 + FsCheck.Xunit 3.0. Sim rules apply (no `delta`/`_Process`/engine-RNG/wall-clock in `src/Sim/`). Spec: `docs/superpowers/specs/2026-06-11-mid-wave-abort-and-menu-return-design.md`.

**Build/test env (prepend before every build/test):**
```bash
export PATH="/opt/homebrew/opt/dotnet@8/bin:$HOME/.local/bin:$PATH"
export DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec
export DOTNET_ROLL_FORWARD=Major
```
Build: `dotnet build raptor.csproj`. Test: `dotnet test tests/RaptorTests.csproj`. (Build `raptor.csproj` FIRST — HintPath trap; the test project references the built assembly.)

---

## File Structure

- `src/Sim/MenuStateMachine.cs` — add `CampaignActive`, `VisibleCount` helper, `OnAbortMission` event, `OpenAbortPrompt`/`AbortPromptActive`; fill MAIN_RETURN stub + `Esc` resume; clear `_abortPrompt` on resolve/`EnterMenu`. (Tasks 1, 2, 4)
- `src/Sim/WaveController.cs` — `_waveStartScore` snapshot in `OnGameEnter`, `ScoreOnAbort` + `ShouldRunGameTick` pure helpers, `AbortMission`, wire `OnAbortMission`, fold the abort freeze into the tick guard. (Tasks 3, 4)
- `src/Sim/InteractiveInputController.cs` — `InGameInputDecision` enum + `DecideInGameInput` pure helper; rewire `_UnhandledInput` in-game branch. (Task 5)
- `src/View/MenuRenderer.cs` — `DrawAbortPrompt` (public reuse of `DrawAskBoolOverlay`). (Task 6)
- `src/View/DebugRenderer.cs` — draw the abort overlay over the frozen playfield. (Task 6)
- Tests: `tests/MenuStateMachineTests.cs` (Tasks 1, 2, 4), `tests/WaveControllerTests.cs` (Tasks 3, 4), `tests/InteractiveInputDecisionTests.cs` (new, Task 5).

---

## Task 1: `CampaignActive` flag + lifecycle + `VisibleItemCount` repoint

**Files:**
- Modify: `src/Sim/MenuStateMachine.cs:56` (`VisibleItemCount`), `:276-296` (`CompleteMission`/`PlayerDied`), `:570-580` (pilot-create confirm), `:692-699` (`ApplyLoadedPilot`), `:714-719` (`OpenAskBoolQuit`)
- Test: `tests/MenuStateMachineTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `tests/MenuStateMachineTests.cs`:

```csharp
[Fact]
public void CampaignActive_is_false_at_cold_launch()
{
    var m = new MenuStateMachine();
    Assert.False(m.CampaignActive);
    Assert.Equal(MenuStateMachine.ItemCount - 1, m.VisibleItemCount);
}

[Fact]
public void Pilot_create_confirm_sets_CampaignActive_and_shows_RETURN()
{
    var m = new MenuStateMachine();
    m.EnterMenu(0);
    // NEW → name → callsign → difficulty → confirm. Name must be non-empty
    // (gated in 5376b99); difficulty Return confirms.
    m.HandleInput("Return", 0);          // NEW → registration
    m.HandleInput("A", 0);               // type a name char
    m.HandleInput("Return", 0);          // name → callsign
    m.HandleInput("Return", 0);          // callsign → difficulty
    m.HandleInput("Return", 0);          // difficulty confirm → Hangar
    Assert.True(m.CampaignActive);
    Assert.Equal(MenuStateMachine.ItemCount, m.VisibleItemCount);
}

[Fact]
public void PlayerDied_clears_CampaignActive()
{
    var m = new MenuStateMachine();
    m.EnterMenu(0);
    m.SetCampaignActiveForTest(true);
    m.PlayerDied(0);
    Assert.False(m.CampaignActive);
}

[Fact]
public void Quit_clears_CampaignActive()
{
    var m = new MenuStateMachine();
    m.EnterMenu(0);
    m.SetCampaignActiveForTest(true);
    m.HandleInput("Up", 0);              // QUIT (wrap, RETURN hidden until... it's visible now)
    // Navigate explicitly to QUIT to avoid wrap ambiguity while CampaignActive:
    m.CurrentItemForTest = MenuStateMachine.QuitItemIndex;
    m.HandleInput("Return", 0);          // QUIT → EXIT TO DOS AskBool
    m.HandleInput("Return", 0);          // YES (default) → OnQuit
    Assert.False(m.CampaignActive);
}

[Property]
public Property VisibleCount_includes_RETURN_iff_campaign_active()
{
    return Prop.ForAll<bool>(active =>
        MenuStateMachine.VisibleCount(active)
            == (active ? MenuStateMachine.ItemCount : MenuStateMachine.ItemCount - 1));
}
```

Add the FsCheck usings at the top of the file if absent:
```csharp
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build raptor.csproj && dotnet test tests/RaptorTests.csproj --filter "FullyQualifiedName~MenuStateMachineTests"`
Expected: FAIL — `CampaignActive`, `VisibleCount`, `SetCampaignActiveForTest`, `CurrentItemForTest` not defined.

- [ ] **Step 3: Implement the flag, helper, and lifecycle**

In `src/Sim/MenuStateMachine.cs`, replace `VisibleItemCount` (`:56`):
```csharp
public int VisibleItemCount => VisibleCount(CampaignActive);

/// <summary>
/// C `ingameflag`: a campaign is in progress (pilot created/loaded, not yet
/// dead or quit). Broader than <see cref="InGame"/> (= actively ticking a wave).
/// Gates MAIN_RETURN visibility. See spec 2026-06-11-mid-wave-abort-and-menu-return.
/// </summary>
public bool CampaignActive { get; private set; }

/// <summary>Selectable item count: 7 (incl. RETURN) while a campaign is active, else 6.</summary>
internal static int VisibleCount(bool campaignActive) => campaignActive ? ItemCount : ItemCount - 1;

// Test seams (no production caller).
internal void SetCampaignActiveForTest(bool v) => CampaignActive = v;
internal int  CurrentItemForTest { set => CurrentItem = value; }
```

In the pilot-create confirm branch (`:570`, `case PilotCreationFlow.Result.Confirm:`), add before `OnPilotCreated?.Invoke();`:
```csharp
                    CampaignActive = true;
```

In `ApplyLoadedPilot` (`:692`), add after `IdPic = pilot.IdPic;`:
```csharp
        CampaignActive = true;
```

In `PlayerDied` (`:289`), add after `InGame = false;`:
```csharp
        CampaignActive = false;   // C: death → ingameflag = FALSE (WINDOWS.C:1786)
```

In `OpenAskBoolQuit` (`:714`), change the callback to also clear the flag:
```csharp
        _askBoolOnYes = () => { CampaignActive = false; QuitRequested = true; OnQuit?.Invoke(); };
```

Do **not** modify `CompleteMission` (campaign stays active across landing/hangar).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RaptorTests.csproj --filter "FullyQualifiedName~MenuStateMachineTests"`
Expected: PASS (incl. the existing `..._skipping_RETURN_when_not_in_game` tests — cold-launch `CampaignActive` is false, so `VisibleItemCount` is unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/Sim/MenuStateMachine.cs tests/MenuStateMachineTests.cs
git commit -m "feat(menu): CampaignActive flag (C ingameflag) gates MAIN_RETURN visibility"
```

---

## Task 2: MAIN_RETURN resume + main-menu `Esc` resume

**Files:**
- Modify: `src/Sim/MenuStateMachine.cs:647` (RETURN stub), `:650-655` (`Esc` handler in `HandleMenuInput`)
- Test: `tests/MenuStateMachineTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void RETURN_item_resumes_to_Hangar_when_campaign_active()
{
    var m = new MenuStateMachine();
    m.EnterMenu(0);
    m.SetCampaignActiveForTest(true);
    m.CurrentItemForTest = MenuStateMachine.ReturnItemIndex;
    m.HandleInput("Return", 100);
    Assert.Equal(WinState.Hangar, m.State);
}

[Fact]
public void MainMenu_Escape_resumes_to_Hangar_when_campaign_active()
{
    var m = new MenuStateMachine();
    m.EnterMenu(0);
    m.SetCampaignActiveForTest(true);
    m.HandleInput("Escape", 100);
    Assert.Equal(WinState.Hangar, m.State);
}

[Fact]
public void MainMenu_Escape_resets_to_clean_menu_when_no_campaign()
{
    var m = new MenuStateMachine();
    m.EnterMenu(0);                       // CampaignActive == false
    m.HandleInput("Escape", 100);
    Assert.Equal(WinState.Menu, m.State); // existing recovery behavior preserved
}

[Fact]
public void Credits_Escape_still_returns_to_menu_during_campaign()
{
    var m = new MenuStateMachine();
    m.EnterMenu(0);
    m.SetCampaignActiveForTest(true);
    m.CurrentItemForTest = MenuStateMachine.CreditsItemIndex;
    m.HandleInput("Return", 0);           // → Credits
    Assert.Equal(WinState.Credits, m.State);
    m.HandleInput("Escape", 0);           // Credits Esc-recovery must NOT resume to Hangar
    Assert.Equal(WinState.Menu, m.State);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build raptor.csproj && dotnet test tests/RaptorTests.csproj --filter "FullyQualifiedName~MenuStateMachineTests"`
Expected: FAIL — RETURN currently returns false (stays Menu); Esc resets to Menu even when CampaignActive.

- [ ] **Step 3: Implement RETURN + Esc resume**

In `HandleMenuInput`, replace the RETURN stub (`:647-648`):
```csharp
            if (CurrentItem == ReturnItemIndex && CampaignActive)
            {
                // C MAIN_RETURN (WINDOWS.C:2178): ingameflag → menu_exit → WIN_Hangar.
                _hangar.Position = 0;   // resume on the hangar's MISSION slot
                EnterState(WinState.Hangar, currentFrame, reAnchor: true);
                return true;
            }
            return false;
```

Replace the `Esc` handler (`:650-655`):
```csharp
        if (action == "Escape")
        {
            if (CampaignActive)
            {
                // C WINDOWS.C:2112: KBD_Key(SC_ESC) && ingameflag → menu_exit → hangar.
                _hangar.Position = 0;
                EnterState(WinState.Hangar, currentFrame, reAnchor: true);
                return true;
            }
            // No campaign: existing reset-to-clean-menu recovery (commit 3048975).
            _hangar.Position = 2;
            EnterMenu(currentFrame);
            return true;
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RaptorTests.csproj --filter "FullyQualifiedName~MenuStateMachineTests"`
Expected: PASS. (`Credits_Escape...` confirms the Credits state has its own dispatch, so the `HandleMenuInput` Esc edit is main-menu-only.)

- [ ] **Step 5: Commit**

```bash
git add src/Sim/MenuStateMachine.cs tests/MenuStateMachineTests.cs
git commit -m "feat(menu): MAIN_RETURN + main-menu Esc resume to hangar during campaign"
```

---

## Task 3: Wave-start score snapshot + `AbortMission` + score-restore property

**Files:**
- Modify: `src/Sim/WaveController.cs:574` (`OnGameEnter`), `:515` (event wiring), add helpers + `AbortMission`
- Modify: `src/Sim/MenuStateMachine.cs` (add `OnAbortMission` event)
- Test: `tests/WaveControllerTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `tests/WaveControllerTests.cs` (FsCheck usings already present in that file per repo convention; add if missing):

```csharp
[Fact]
public void ScoreOnAbort_returns_wave_start_score_discarding_earned()
{
    Assert.Equal(10000u, WaveController.ScoreOnAbort(waveStartScore: 10000u, currentScore: 25000u));
}

[Property]
public Property ScoreOnAbort_always_restores_wave_start_score()
{
    // Property "Score restore": after abort, Score == wave-start score, regardless
    // of whatever was earned during the wave (currentScore). Domain: WaveController abort path.
    return Prop.ForAll<uint, uint>((waveStart, current) =>
        WaveController.ScoreOnAbort(waveStart, current) == waveStart);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build raptor.csproj && dotnet test tests/RaptorTests.csproj --filter "FullyQualifiedName~WaveControllerTests"`
Expected: FAIL — `ScoreOnAbort` not defined.

- [ ] **Step 3: Implement the snapshot, helper, event, and AbortMission**

In `src/Sim/MenuStateMachine.cs`, near the other public events (`:220`/`:729`), add:
```csharp
/// <summary>Fired when the player confirms YES on the mid-wave "Abort Mission ?" prompt.
/// WaveController subscribes to restore the wave-start score and complete the mission.</summary>
public event System.Action? OnAbortMission;
```

In `src/Sim/WaveController.cs`, add a field near `Score` (`:200`):
```csharp
    // Score the player brought into the current wave (C start_score, RAP.C:883).
    // Snapshotted at game-enter; restored on a mid-wave abort.
    private uint _waveStartScore;
```

At the top of `OnGameEnter` (`:574`, first line of the body):
```csharp
        _waveStartScore = Score;
```

In `_Ready` event wiring (next to `_menu.OnGameEnter += OnGameEnter;` at `:515`):
```csharp
            _menu.OnAbortMission += AbortMission;
```

Add the pure helper and `AbortMission` (place near the other `internal static` helpers, e.g. after `ResolveStartWave`):
```csharp
    /// <summary>
    /// The score after a mid-wave abort: the wave-start score, discarding anything
    /// earned during the wave (C RAP.C:1197 plr.score = start_score). `currentScore`
    /// is taken only to make the discard explicit and testable.
    /// </summary>
    internal static uint ScoreOnAbort(uint waveStartScore, uint currentScore) => waveStartScore;

    /// <summary>
    /// Mid-wave abort confirmed (YES on "Abort Mission ?"). Mirrors the normal
    /// mission-complete path (`:1711-1716`): stop the wave, then complete it as a
    /// non-final wave so the landing cinematic plays → hangar (same wave replays).
    /// Restores the wave-start score first (C abort restores start_score).
    /// </summary>
    internal void AbortMission()
    {
        _waveActive = false;
        Score = ScoreOnAbort(_waveStartScore, Score);
        _menu?.CompleteMission(SimClock.Frame, finalWave: false);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RaptorTests.csproj --filter "FullyQualifiedName~WaveControllerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Sim/WaveController.cs src/Sim/MenuStateMachine.cs tests/WaveControllerTests.cs
git commit -m "feat(abort): wave-start score snapshot + AbortMission (restore + complete)"
```

---

## Task 4: Abort prompt machinery + tick-freeze predicate

**Files:**
- Modify: `src/Sim/MenuStateMachine.cs` (`OpenAbortPrompt`, `AbortPromptActive`, clear `_abortPrompt`), `:659-684` (`HandleAskBoolInput`), `:358-375` (`EnterMenu`)
- Modify: `src/Sim/WaveController.cs:820` (tick guard) + `ShouldRunGameTick` helper
- Test: `tests/MenuStateMachineTests.cs`, `tests/WaveControllerTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `tests/MenuStateMachineTests.cs`:

```csharp
[Fact]
public void OpenAbortPrompt_shows_abort_askbool_and_sets_active()
{
    var m = new MenuStateMachine();
    m.OpenAbortPrompt();
    Assert.True(m.InAskBool);
    Assert.Equal("Abort Mission ?", m.AskBoolQuestion);
    Assert.True(m.AbortPromptActive);
}

[Fact]
public void Abort_prompt_YES_fires_OnAbortMission_and_clears_active()
{
    var m = new MenuStateMachine();
    bool fired = false;
    m.OnAbortMission += () => fired = true;
    m.OpenAbortPrompt();
    m.HandleInput("Return", 0);           // YES is the default selection
    Assert.True(fired);
    Assert.False(m.AbortPromptActive);
    Assert.False(m.InAskBool);
}

[Fact]
public void Abort_prompt_NO_does_not_fire_and_clears_active()
{
    var m = new MenuStateMachine();
    bool fired = false;
    m.OnAbortMission += () => fired = true;
    m.OpenAbortPrompt();
    m.HandleInput("Left", 0);             // toggle to NO
    m.HandleInput("Return", 0);
    Assert.False(fired);
    Assert.False(m.AbortPromptActive);
}

[Fact]
public void Abort_prompt_Escape_dismisses_without_firing()
{
    var m = new MenuStateMachine();
    bool fired = false;
    m.OnAbortMission += () => fired = true;
    m.OpenAbortPrompt();
    m.HandleInput("Escape", 0);
    Assert.False(fired);
    Assert.False(m.AbortPromptActive);
}
```

Add to `tests/WaveControllerTests.cs`:

```csharp
[Fact]
public void Game_tick_runs_only_when_wave_active_and_not_aborting()
{
    Assert.True(WaveController.ShouldRunGameTick(waveActive: true,  abortPromptActive: false));
    Assert.False(WaveController.ShouldRunGameTick(waveActive: false, abortPromptActive: false));
    Assert.False(WaveController.ShouldRunGameTick(waveActive: true,  abortPromptActive: true));
}

[Property]
public Property ShouldRunGameTick_halts_while_abort_prompt_active()
{
    // Property "Freeze halts the sim": while the abort prompt is up, the game tick
    // never runs. Domain: WaveController._PhysicsProcess gate.
    return Prop.ForAll<bool, bool>((waveActive, aborting) =>
        WaveController.ShouldRunGameTick(waveActive, aborting) == (waveActive && !aborting));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build raptor.csproj && dotnet test tests/RaptorTests.csproj --filter "FullyQualifiedName~MenuStateMachineTests|FullyQualifiedName~WaveControllerTests"`
Expected: FAIL — `OpenAbortPrompt`, `AbortPromptActive`, `ShouldRunGameTick` not defined.

- [ ] **Step 3: Implement the prompt machinery + freeze predicate**

In `src/Sim/MenuStateMachine.cs`, add a field beside `_inAskBool` (`:104`):
```csharp
    private bool _abortPrompt = false;
```

Add the public surface (near `AskBoolQuestion`, `:170`):
```csharp
    /// <summary>True while the mid-wave "Abort Mission ?" prompt is open. Read by
    /// WaveController to freeze the wave tick.</summary>
    public bool AbortPromptActive => _abortPrompt;

    /// <summary>Open the mid-wave abort prompt (reuses the AskBool machinery). YES
    /// fires <see cref="OnAbortMission"/>; NO/Escape resumes the wave.</summary>
    public void OpenAbortPrompt()
    {
        _askBoolQuestion = "Abort Mission ?";
        _askBoolYes = true;
        _abortPrompt = true;
        _askBoolOnYes = () => OnAbortMission?.Invoke();
        _inAskBool = true;
    }
```

In `HandleAskBoolInput` (`:659`), clear `_abortPrompt` on both resolve paths. Change the `Escape` case (`:670`):
```csharp
            case "Escape":
                _inAskBool = false;
                _abortPrompt = false;
                _askBoolOnYes = null;
                return true;
```
and the `Return`/`Space` case (`:674`):
```csharp
            case "Return":
            case "Space":
                bool yes = _askBoolYes;
                var cb = _askBoolOnYes;
                _inAskBool = false;
                _abortPrompt = false;
                _askBoolOnYes = null;
                if (yes) cb?.Invoke();
                return true;
```

In `EnterMenu` (`:366`, beside `_inAskBool = false;`):
```csharp
        _abortPrompt = false;
```

In `src/Sim/WaveController.cs`, add the helper (next to `ScoreOnAbort`):
```csharp
    /// <summary>The game tick runs only when a wave is active and no abort prompt is
    /// open. Combines the wave-active guard with the mid-wave abort freeze.</summary>
    internal static bool ShouldRunGameTick(bool waveActive, bool abortPromptActive)
        => waveActive && !abortPromptActive;
```

Replace the `if (!_waveActive) return;` guard in `_PhysicsProcess` (`:820`):
```csharp
        if (!ShouldRunGameTick(_waveActive, _menu?.AbortPromptActive == true)) return;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RaptorTests.csproj --filter "FullyQualifiedName~MenuStateMachineTests|FullyQualifiedName~WaveControllerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Sim/MenuStateMachine.cs src/Sim/WaveController.cs tests/MenuStateMachineTests.cs tests/WaveControllerTests.cs
git commit -m "feat(abort): Abort Mission prompt + wave-tick freeze while it is open"
```

---

## Task 5: In-game input routing (Esc → prompt; route nav while prompt up)

**Files:**
- Modify: `src/Sim/InteractiveInputController.cs:42-74` (`_UnhandledInput`) + add `InGameInputDecision` enum and `DecideInGameInput`
- Test: `tests/InteractiveInputDecisionTests.cs` (new)

- [ ] **Step 1: Write the failing tests**

Create `tests/InteractiveInputDecisionTests.cs`:

```csharp
using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

public class InteractiveInputDecisionTests
{
    [Fact]
    public void Not_in_game_passes_through()
    {
        Assert.Equal(InteractiveInputController.InGameInputDecision.PassThrough,
            InteractiveInputController.DecideInGameInput(inGame: false, abortPromptActive: false, action: "Escape"));
    }

    [Fact]
    public void In_game_escape_opens_abort_prompt()
    {
        Assert.Equal(InteractiveInputController.InGameInputDecision.OpenAbort,
            InteractiveInputController.DecideInGameInput(inGame: true, abortPromptActive: false, action: "Escape"));
    }

    [Fact]
    public void In_game_non_escape_is_swallowed()
    {
        Assert.Equal(InteractiveInputController.InGameInputDecision.Swallow,
            InteractiveInputController.DecideInGameInput(inGame: true, abortPromptActive: false, action: "Up"));
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("Return")]
    [InlineData("Escape")]
    public void While_prompt_active_nav_keys_route_to_askbool(string action)
    {
        Assert.Equal(InteractiveInputController.InGameInputDecision.RouteToAskBool,
            InteractiveInputController.DecideInGameInput(inGame: true, abortPromptActive: true, action: action));
    }

    [Fact]
    public void While_prompt_active_unmapped_keys_are_swallowed()
    {
        Assert.Equal(InteractiveInputController.InGameInputDecision.Swallow,
            InteractiveInputController.DecideInGameInput(inGame: true, abortPromptActive: true, action: null));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build raptor.csproj && dotnet test tests/RaptorTests.csproj --filter "FullyQualifiedName~InteractiveInputDecisionTests"`
Expected: FAIL — `InGameInputDecision`, `DecideInGameInput` not defined.

- [ ] **Step 3: Implement the decision helper + rewire `_UnhandledInput`**

In `src/Sim/InteractiveInputController.cs`, add the enum + pure helper (class scope):
```csharp
    public enum InGameInputDecision { PassThrough, OpenAbort, RouteToAskBool, Swallow }

    /// <summary>
    /// Routing for keys while a wave is in progress. Not in game → caller handles
    /// normally. Abort prompt open → AskBool nav keys route to it, others swallowed.
    /// No prompt → Esc opens the abort prompt; all other gameplay keys are swallowed
    /// (gameplay movement/fire is read separately in _PhysicsProcess).
    /// </summary>
    internal static InGameInputDecision DecideInGameInput(bool inGame, bool abortPromptActive, string? action)
    {
        if (!inGame) return InGameInputDecision.PassThrough;
        if (abortPromptActive)
            return action switch
            {
                "Left" or "Right" or "Tab" or "Up" or "Down" or "Return" or "Space" or "Escape"
                    => InGameInputDecision.RouteToAskBool,
                _ => InGameInputDecision.Swallow,
            };
        return action == "Escape" ? InGameInputDecision.OpenAbort : InGameInputDecision.Swallow;
    }
```

Replace the blanket in-game early-return in `_UnhandledInput` (`:45`). Replace:
```csharp
        if (!Active) return;
        if (_menuController?.Menu.InGame == true) return;

        if (@event is InputEventKey keyEvent)
        {
```
with:
```csharp
        if (!Active) return;

        var menu = _menuController?.Menu;
        if (menu?.InGame == true)
        {
            if (@event is not InputEventKey igKey || !igKey.Pressed || igKey.Echo) return;
            string? igAction = KeyEventToMenuAction(igKey);
            switch (DecideInGameInput(true, menu.AbortPromptActive, igAction))
            {
                case InGameInputDecision.OpenAbort:
                    menu.OpenAbortPrompt();
                    GetViewport().SetInputAsHandled();
                    return;
                case InGameInputDecision.RouteToAskBool:
                    menu.HandleInput(igAction!, SimClock.Frame);
                    GetViewport().SetInputAsHandled();
                    return;
                default:
                    return;   // Swallow / PassThrough-not-applicable in-game
            }
        }

        if (@event is InputEventKey keyEvent)
        {
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RaptorTests.csproj --filter "FullyQualifiedName~InteractiveInputDecisionTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Sim/InteractiveInputController.cs tests/InteractiveInputDecisionTests.cs
git commit -m "feat(abort): in-game Esc opens abort prompt; route nav keys while open"
```

---

## Task 6: View — render "Abort Mission ?" over the frozen playfield

**Files:**
- Modify: `src/View/MenuRenderer.cs:225` (expose `DrawAbortPrompt`)
- Modify: `src/View/DebugRenderer.cs:480-514` (draw overlay at end of playfield render)

No unit test (Godot drawing requires the runtime); verified via the manual interactive check in Task 7. This task is a thin reuse of the already-tested `DrawAskBoolOverlay`.

- [ ] **Step 1: Expose the overlay draw on MenuRenderer**

In `src/View/MenuRenderer.cs`, add a public method (the existing private `DrawAskBoolOverlay` uses `_host`):
```csharp
    /// <summary>Draw the "Abort Mission ?" prompt over an arbitrary host (the frozen
    /// in-game playfield). Reuses the menu AskBool overlay.</summary>
    public void DrawAbortPrompt(MenuStateMachine menu, IHost host)
    {
        _host = host;
        DrawAskBoolOverlay(menu);
    }
```

- [ ] **Step 2: Draw it from the gameplay render path**

In `src/View/DebugRenderer.cs`, locate the end of `_Draw` (after the playfield is drawn — i.e. the final `RecordDrawnState();` of the in-game branch). Immediately before that final `RecordDrawnState()`, add:
```csharp
        if (_menu?.AbortPromptActive == true)
            _menuRenderer.DrawAbortPrompt(_menu, _menuHost);
```
(Use the same `_menuHost` already passed to `_menuRenderer.DrawMenuOverlay` at `:502`.)

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build raptor.csproj`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/View/MenuRenderer.cs src/View/DebugRenderer.cs
git commit -m "feat(abort): render Abort Mission prompt over the frozen playfield"
```

---

## Task 7: Regression gates + manual verification

**Files:** none (verification only).

- [ ] **Step 1: Full unit suite + lint**

Run:
```bash
dotnet build raptor.csproj && dotnet test tests/RaptorTests.csproj && bash scripts/lint_sim.sh
```
Expected: all tests PASS (≥ 786 + the new ones); `[lint_sim] OK` (0). If the known `SimClock`/`Override` parallel flake trips, rerun the failed test in isolation to confirm it is the pre-existing flake, not a new failure.

- [ ] **Step 2: Menu-event sweep (must stay 9/9)**

Run: `tests/run_menu_event_sweep.sh`
Expected: 9/9 PASS. (CampaignActive defaults false and no captured golden starts a campaign, so `VisibleItemCount` is unchanged.)

- [ ] **Step 3: L2a CI parity (4 scenarios byte-identical)**

Run: `tests/run_l2a.sh` for the 4 CI scenarios (mission_start, mission_long, full_demo, menu_demo).
Expected: mission_start 100%, mission_long 99.1%, full_demo 100%, menu_demo 100% — unchanged. (Abort path is unreachable under the harness; freeze predicate equals the old `_waveActive` guard when no prompt is open.)

- [ ] **Step 4: wave1 exact-diff no-regression spot check**

Run the wave1 exact-diff (see `~/.agent-state/raptor-godot--main/state.md` "WAVE1 EXACT-DIFF" for the full command):
```bash
RAPTOR_PLAYTHROUGH=/tmp/demo_trigger_long.txt \
RAPTOR_DEMO_PATH=benchmarks/bench_20260606_141540/demos/wave01.json \
RAPTOR_PARITY_OUT=/tmp/out.ndjson RAPTOR_DETERMINISTIC_RNG=1 RAPTOR_TEST_FAST=1 \
timeout 600 godot --headless --path . --quit-after 33000
# then: tests/comparator/parity_diff.py --c-golden /tmp/c_wave1.ndjson --godot-out <MISSION_1 slice> --exact
```
Expected: 263/264 (unchanged baseline; sole miss = the known #262 player_y harness artifact). Confirms the `ShouldRunGameTick` refactor + `_waveStartScore` snapshot don't perturb the normal path.

- [ ] **Step 5: Manual interactive verification (USER-GATED — mouse-grab)**

Per the Known Trap, this grabs the mouse/focus; run when the user is available, one Godot instance only. Verify:
1. Start a new pilot → enter a wave → press `Esc` → "Abort Mission ?" appears and enemies/bullets freeze. NO → wave resumes. `Esc` again → YES → ship-landing → hangar; score is the wave-start value (earned points discarded); the **same** wave is selectable again.
2. From the hangar, MAIN MENU → main menu shows RETURN. RETURN → back to hangar. From the main menu, `Esc` → back to hangar.
3. Cold launch (no campaign): main menu has no RETURN; `Esc` from Credits/Help still returns to the menu.

- [ ] **Step 6: Final commit (if any verification-driven tweaks)**

Only if Steps 1-5 surfaced fixes. Otherwise nothing to commit.

---

## Self-Review

**Spec coverage:**
- Part A (`CampaignActive`) → Task 1. Part B (RETURN + Esc) → Task 2. Part C: score snapshot/restore → Task 3; prompt + freeze → Task 4; input routing → Task 5; render → Task 6. Regression/manual → Task 7. ✓ All spec sections mapped.

**Properties → tests:**
- *Score restore* → Task 3 `ScoreOnAbort_always_restores_wave_start_score` (`[Property]`). ✓
- *Abort replays wave* → Task 3 `AbortMission` calls `CompleteMission(finalWave:false)` (no wave-num advance); asserted behaviorally + manual Task 7 step 5.1. ✓
- *Resume is inert* → Task 4 `Abort_prompt_NO...` clears without firing `OnAbortMission`; freeze means no mutation occurred (Task 4 `ShouldRunGameTick`); manual 5.1. ✓
- *Freeze halts the sim* → Task 4 `ShouldRunGameTick_halts_while_abort_prompt_active` (`[Property]`). ✓
- *RETURN visibility ⟺ CampaignActive* → Task 1 `VisibleCount_includes_RETURN_iff_campaign_active` (`[Property]`). ✓
- *CampaignActive lifecycle* → Task 1 (cold-launch / create / load via confirm / death / quit). ✓
- *Parity-inert* → Task 7 steps 2-4 (menu sweep 9/9, 4 CI L2a byte-identical, wave1 263/264). ✓

**External-dependency gate:** spec declares **no external dependencies** (all internal C#/Godot, verified by reading). The analogous integration concern — the real Godot input path reaching the prompt — is covered by Task 5's `DecideInGameInput` (the controller's routing decision, the headless-testable seam) plus Task 7's manual interactive check of the full `InputEvent → SetInputAsHandled` path. No fictional-boundary risk. ✓

**Placeholder scan:** none. **Type consistency:** `CampaignActive`, `VisibleCount`, `OnAbortMission`, `OpenAbortPrompt`, `AbortPromptActive`, `ScoreOnAbort(uint,uint)`, `AbortMission`, `ShouldRunGameTick(bool,bool)`, `InGameInputDecision`, `DecideInGameInput(bool,bool,string?)`, `DrawAbortPrompt(MenuStateMachine,IHost)` — names match across all tasks. ✓
