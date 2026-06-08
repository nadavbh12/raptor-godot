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

            Menu.OnPilotLoaded += pilot =>
            {
                wave.SetScore(pilot.Score);
                // Apply the pilot's saved per-episode difficulty (was unwired, so a
                // loaded pilot also defaulted to DIFF_2 regardless of how it was made).
                int g = pilot.CurGame;
                if (g < 0) g = 0; else if (g >= pilot.Diff.Length) g = pilot.Diff.Length - 1;
                wave.SetPlayerDiff(pilot.Diff[g]);
                // Load from the exact file the summary was read from (pilot.FilePath),
                // not a re-derived path — LoadAll's directory resolution (RAPTOR_SAVE_DIR,
                // sibling probe, etc.) may differ from PilotSaveDirectory.
                wave.Inventory.CopyFrom(PilotSaveStore.LoadInventory(pilot.FilePath));
            };
        }

        // Enter MENU state immediately — mirrors raptor_parity_set_win_state(1)
        // called right after WIN_MainMenu shows its window.
        Menu.EnterMenu(SimClock.Frame);
    }

    // Menu/UI driver: polls SimClock.Frame at render rate for the death-movie/quit
    // check; never reads `delta`, so it's parity-inert (not in-wave sim).
    public override void _Process(double delta)  // LINT-OK: see above — intentional UI _Process.
    {
        bool deathMovieJustCompleted =
            Menu.CompleteDeathMovieIfDone(SimClock.Frame, MenuStateMachine.DeathMovieFrames);

        // Death-wave parity runs: the scenario is over once the death movie returns to
        // MENU — quit rather than idle through the script's trailing `wait`.
        if (deathMovieJustCompleted && _quitAfterDeath)
            GetTree().Quit();
    }
}
