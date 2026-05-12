using System.IO;
using Godot;

namespace Raptor.Test;

/// <summary>
/// Godot Node wrapper around <see cref="Playthrough"/>. Reads the
/// RAPTOR_PLAYTHROUGH environment variable and, if set, loads the named
/// script and drives it tick-by-tick via _PhysicsProcess.
///
/// Input events from the script are forwarded to the MenuStateMachine via
/// MenuController, which mirrors the C version's keydown injection path.
///
/// When the script issues "quit", calls GetTree().Quit() to exit cleanly.
/// </summary>
public partial class PlaythroughDriver : Node
{
    private Playthrough? _pt;
    private Sim.MenuStateMachine? _menu;
    private ParityEmitter? _emitter;

    // Key dispatched by the script on the previous tick, applied to
    // MenuStateMachine on the current tick. The 1-frame delay mirrors the
    // wall-clock lag C has between SDL keypress injection (in the timer
    // thread) and the menu_exit code on the main thread actually calling
    // set_win_state. Without it, atomic same-tick state changes can wipe
    // the OLD anchor before its final bucket boundary gets emitted —
    // mission_long's MENU fc=630 boundary falls exactly on Return #4's
    // frame, and was getting dropped that way.
    //
    // For the deferred apply to run AFTER ParityEmitter's emit on the
    // transition tick, this node must sit BELOW ParityEmitter in the
    // scene tree (Main.tscn order).
    private string? _pendingKey;

    public override void _Ready()
    {
        var scriptPath = OS.GetEnvironment("RAPTOR_PLAYTHROUGH");
        if (string.IsNullOrEmpty(scriptPath)) return;

        if (!File.Exists(scriptPath))
        {
            GD.PrintErr($"PlaythroughDriver: script not found: {scriptPath}");
            return;
        }

        _pt = Playthrough.LoadFile(scriptPath);
        _pt.OnLog = msg => GD.Print(msg);

        var menuController = GetNodeOrNull<Sim.MenuController>("../MenuController");
        if (menuController != null)
        {
            _menu = menuController.Menu;
        }
        else
        {
            GD.PrintErr("PlaythroughDriver: MenuController not found; input will be ignored");
        }

        _emitter = GetNodeOrNull<ParityEmitter>("../ParityEmitter");

        _pt.OnKeyPress = key =>
        {
            // Queue for next-tick application; see _pendingKey docs.
            _pendingKey = key;
        };

        _pt.OnKeyDown = key =>
        {
            // Stage 4: stub. Later stages will inject into Godot's InputEvent pipeline.
            GD.Print($"PlaythroughDriver: down {key} (stub)");
        };

        _pt.OnKeyUp = key =>
        {
            GD.Print($"PlaythroughDriver: up {key} (stub)");
        };

        _pt.OnDump = label =>
        {
            GD.Print($"PlaythroughDriver: dump {label}");
            var renderer = GetNodeOrNull<View.DebugRenderer>("../DebugRenderer");
            renderer?.RequestScriptDump(label);
        };

        _pt.OnQuit = () =>
        {
            // Signal the emitter to close immediately, matching the C version's
            // exit(0) which terminates without emitting any further checkpoints.
            _emitter?.SignalQuit();
            GetTree().Quit();
        };

        // Notify the playthrough that the menu is ready immediately.
        // In the C version this fires when WIN_MainMenu calls raptor_playthrough_menu_ready()
        // after SWD_ShowAllWindows + GFX_DisplayUpdate. Here MenuController._Ready
        // has already called EnterMenu, so we can arm the script right away.
        _pt.NotifyMenuReady(Sim.SimClock.Frame);
    }

    public override void _PhysicsProcess(double _)
    {
        // Apply any key queued by last tick's Tick(). ParityEmitter has
        // already emitted for the current frame (we sit below it in the
        // scene tree), so the OLD state's bucket — if its boundary lies
        // on this frame — gets emitted before the state changes here.
        if (_pendingKey != null && _menu != null)
        {
            _menu.HandleInput(_pendingKey, Sim.SimClock.Frame);
            _pendingKey = null;
        }
        _pt?.Tick(Sim.SimClock.Frame);
    }
}
