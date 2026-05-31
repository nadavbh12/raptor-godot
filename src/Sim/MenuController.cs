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

    public override void _Ready()
    {
        var emitter = GetNodeOrNull<ParityEmitter>("../ParityEmitter");
        if (emitter != null)
        {
            emitter.Menu = Menu;
            Menu.OnStateChanged += emitter.OnStateChanged;
        }

        // Wire MAIN_QUIT (EXIT TO DOS confirmation) to Godot's tree quit.
        Menu.OnQuit += () => GetTree().Quit();

        // Apply gameplay state from a loaded pilot: score + inventory.
        // CurGame and diff are reserved for when those fields drive game start.
        var wave = GetNodeOrNull<WaveController>("../WaveController");
        if (wave != null)
        {
            Menu.OnPilotLoaded += pilot =>
            {
                wave.SetScore(pilot.Score);
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

    public override void _Process(double delta)
    {
        Menu.CompleteDeathMovieIfDone(SimClock.Frame, MenuStateMachine.DeathMovieFrames);
    }
}
