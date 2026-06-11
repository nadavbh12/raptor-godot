using Godot;
using Raptor.Test;

namespace Raptor.Sim;

/// <summary>
/// Godot Node wrapper that owns a <see cref="MenuStateMachine"/> and wires it
/// to the <see cref="ParityEmitter"/>. Notifies the emitter whenever the menu
/// state machine transitions so the emitter can reset its per-context counter.
///
/// _Ready is called after all nodes are in the tree; GetNode calls are safe.
/// </summary>
public partial class MenuController : Node
{
    public MenuStateMachine Menu { get; } = new MenuStateMachine();

    // Parity death-wave runs: quit (and stop emitting) the moment the death movie
    // completes and the scenario returns to MENU, instead of idling through the
    // script's trailing `wait`. Keeps the captured output gameplay-only (matching a
    // trimmed C golden) and cuts run time. Off for normal play and other scenarios.
    private bool _quitAfterDeath;

    public override void _Ready()
    {
        _quitAfterDeath = OS.GetEnvironment("RAPTOR_QUIT_AFTER_DEATH") == "1";

        var emitter = GetNodeOrNull<ParityEmitter>("../ParityEmitter");
        if (emitter != null)
        {
            emitter.Menu = Menu;
            emitter.QuitAfterDeath = _quitAfterDeath;
            Menu.OnStateChanged += emitter.OnStateChanged;
        }

        // Wire MAIN_QUIT (EXIT TO DOS confirmation) to Godot's tree quit.
        Menu.OnQuit += () => GetTree().Quit();

        // Apply gameplay state from a loaded pilot: score + inventory.
        // CurGame and diff are reserved for when those fields drive game start.
        var wave = GetNodeOrNull<WaveController>("../WaveController");
        if (wave != null)
        {
            // Point the store at the same canonical inventory gameplay and
            // pilot load mutate (wave.Inventory.CopyFrom on load), so a loaded
            // pilot's inventory shows correctly in the supply room (Task 3.5).
            Menu.Inventory = wave.Inventory;

            // Store transactions (Buy/Sell) read and write the LIVE player score.
            // These accessors only EXPOSE the existing WaveController.Score /
            // SetScore — no change to any non-store score handling.
            Menu.GetScore = () => wave.Score;
            Menu.SetScore = wave.SetScore;
            // Save persists the campaign wave; load restores it (see OnPilotLoaded).
            Menu.GetCampaignWaveZeroBased = () => wave.CampaignWaveZeroBased;

            Menu.OnPilotLoaded += pilot =>
            {
                wave.SetScore(pilot.Score);
                // Apply the pilot's saved per-episode difficulty (was unwired, so a
                // loaded pilot also defaulted to DIFF_2 regardless of how it was made).
                int g = pilot.CurGame;
                if (g < 0) g = 0; else if (g >= pilot.Diff.Length) g = pilot.Diff.Length - 1;
                wave.SetPlayerDiff(pilot.Diff[g]);
                // Restore campaign progress so the loaded pilot resumes at the wave it
                // reached (C RAP_LoadPlayer game_wave[cur_game]). Godot plays episode 1
                // (cur_game 0); read the matching game_wave slot, guarding bounds.
                int gw = pilot.CurGame;
                if (gw < 0 || gw >= pilot.GameWave.Length) gw = 0;
                wave.SetCampaignWaveFromSaved(pilot.GameWave[gw]);
                // Load from the exact file the summary was read from (pilot.FilePath),
                // not a re-derived path — LoadAll's directory resolution (RAPTOR_SAVE_DIR,
                // sibling probe, etc.) may differ from PilotSaveDirectory.
                wave.Inventory.CopyFrom(PilotSaveStore.LoadInventory(pilot.FilePath));
            };
        }

        // Interactive launch shows the startup attract intro (INTRO_Credits +
        // INTRO_PlayMain, RAP.C); any parity/test run drives a playthrough and must reach
        // the menu immediately, so it's skipped there (faithful RAPTOR_SKIPINTRO too).
        // Read RAPTOR_PLAYTHROUGH directly (not PlaythroughDriver.Active) to avoid a
        // node _Ready ordering race — the driver may not have loaded its script yet.
        bool playthroughActive = OS.GetEnvironment("RAPTOR_PLAYTHROUGH") != "";
        bool skipIntroEnv = OS.GetEnvironment("RAPTOR_SKIPINTRO") == "1";
        _attractEnabled = ShouldPlayStartupIntro(playthroughActive, skipIntroEnv);
        if (_attractEnabled)
            Menu.StartIntro(SimClock.Frame);
        else
            // Enter MENU state immediately — mirrors raptor_parity_set_win_state(1)
            // called right after WIN_MainMenu shows its window.
            Menu.EnterMenu(SimClock.Frame);
    }

    // Interactive run → the startup + idle attract play; playthrough/parity runs disable both.
    private bool _attractEnabled;

    /// <summary>
    /// The startup attract intro plays only on an interactive launch: never when a
    /// playthrough script is driving the run (parity/tests assume the menu is up at
    /// frame 0) and never when RAPTOR_SKIPINTRO is set (faithful to C).
    /// </summary>
    public static bool ShouldPlayStartupIntro(bool playthroughActive, bool skipIntroEnv)
        => !playthroughActive && !skipIntroEnv;

    /// <summary>
    /// The idle attract loop replays the intro once the main menu has been idle for
    /// <see cref="CutsceneTimings.IdleAttractDelay"/> (C WIN_MainAuto / DEMO_DELAY).
    /// </summary>
    public static bool ShouldStartIdleAttract(bool attractEnabled, WinState state, int framesIdle)
        => attractEnabled && state == WinState.Menu && framesIdle >= CutsceneTimings.IdleAttractDelay;

    // Menu/UI driver: polls SimClock.Frame at render rate to advance finished cutscenes
    // (death → menu, landing → hangar) and to fire the idle attract loop; never reads
    // `delta`, so it's parity-inert.
    public override void _Process(double delta)  // LINT-OK: see above — intentional UI _Process.
    {
        bool wasDeath = Menu.State == WinState.Death;
        bool cutsceneJustCompleted = Menu.CompleteCutsceneIfDone(SimClock.Frame);

        // Cut any lingering cutscene SFX (e.g. the intro's final explosion) so its tail
        // doesn't bleed into the menu/hangar the cutscene transitions to.
        if (cutsceneJustCompleted)
            SoundEmitter.StopAll();

        // Death-wave parity runs: the scenario is over once the death movie returns to
        // MENU — quit rather than idle through the script's trailing `wait`.
        if (cutsceneJustCompleted && wasDeath && _quitAfterDeath)
            GetTree().Quit();

        // Idle main menu → replay the attract (WIN_MainAuto). Interactive only.
        if (ShouldStartIdleAttract(_attractEnabled, Menu.State, SimClock.Frame - Menu.LastActivityFrame))
            Menu.StartIntro(SimClock.Frame);
    }
}
