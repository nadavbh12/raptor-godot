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
            if (_menu == null) return;
            bool transitioned = _menu.HandleInput(key, Sim.SimClock.Frame);
            // OnStateChanged is wired via event in MenuController._Ready;
            // no manual notify needed here. Transition is handled by the event.
            _ = transitioned;
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
            // Stage 4: no-op. Stage 8 will capture framebuffers.
            GD.Print($"PlaythroughDriver: dump {label} (stub)");
        };

        _pt.OnQuit = () => GetTree().Quit();

        // Notify the playthrough that the menu is ready immediately.
        // In the C version this fires when WIN_MainMenu calls raptor_playthrough_menu_ready()
        // after SWD_ShowAllWindows + GFX_DisplayUpdate. Here MenuController._Ready
        // has already called EnterMenu, so we can arm the script right away.
        _pt.NotifyMenuReady(Sim.SimClock.Frame);
    }

    public override void _PhysicsProcess(double _)
    {
        _pt?.Tick(Sim.SimClock.Frame);
    }
}
