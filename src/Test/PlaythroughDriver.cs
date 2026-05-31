using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Raptor.Sim;
using Raptor.Sim.Shots;

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
    private Sim.WaveController? _wave;

    public bool Active => _pt != null;

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

    // Keys currently held by `down NAME` until matching `up NAME`. Game-side
    // input (WaveController.PhaseInput) reads this to drive in-game player
    // movement. mission_long uses `down Up` to hold the throttle.
    private readonly HashSet<string> _heldKeys = new();

    /// <summary>Returns -1 if Up is held, +1 if Down is held, else 0.</summary>
    public int PlayerInputY =>
        (_heldKeys.Contains("Down") ? 1 : 0) - (_heldKeys.Contains("Up") ? 1 : 0);

    /// <summary>Returns -1 if Left is held, +1 if Right is held, else 0.</summary>
    public int PlayerInputX =>
        (_heldKeys.Contains("Right") ? 1 : 0) - (_heldKeys.Contains("Left") ? 1 : 0);

    /// <summary>
    /// True if the fire button (BUT_1) is held. Mirrors INPUT.C's k_Fire scancode.
    /// The checked-in C SETUP.INI maps Fire to SC_A, while older scripts used
    /// named aliases like "Fire" or "Ctrl".
    /// </summary>
    public bool IsFireHeld => _heldKeys.Any(IsFireKey);

    public static bool IsFireKey(string key) => key == "A" || key == "Fire" || key == "Ctrl";

    /// <summary>BUT_2 (next-special-weapon cycle). Named "FireSp" / "Alt" in scripts.</summary>
    public bool IsFireSpHeld => _heldKeys.Contains("FireSp") || _heldKeys.Contains("Alt");

    /// <summary>BUT_3 (mega bomb). Named "Mega" / "Shift" in scripts.</summary>
    public bool IsMegaHeld => _heldKeys.Contains("Mega") || _heldKeys.Contains("Shift");

    // One-shot special-weapon selections queued by SC_1..SC_MINUS keystrokes.
    // RAP.C:955-996 maps each numeric scancode to OBJS_MakeSpecial(type);
    // WaveController.PhaseInput drains this queue each tick.
    private readonly Queue<ObjType> _specialSelects = new();

    /// <summary>
    /// Mirrors C RAP.C SC_1..SC_MINUS → OBJS_MakeSpecial mapping. Returns
    /// null for any key name not in the special-select set (e.g. "Up", "1"
    /// in menu state); the caller then treats the key as a menu key.
    /// </summary>
    public static ObjType? KeyToSpecial(string key) => key switch
    {
        "1"     => ObjType.DumbMissile,
        "2"     => ObjType.MiniGun,
        "3"     => ObjType.Turret,
        "4"     => ObjType.MissilePods,
        "5"     => ObjType.AirMissile,
        "6"     => ObjType.GrdMissile,
        "7"     => ObjType.Bomb,
        "8"     => ObjType.EnergyGrab,
        "9"     => ObjType.PulseCannon,
        "0"     => ObjType.DeathRay,
        "Minus" => ObjType.ForwardLaser,
        _       => null,
    };

    /// <summary>True if any special-select keystrokes are pending.</summary>
    public bool HasPendingSpecialSelect => _specialSelects.Count > 0;

    /// <summary>
    /// Dequeue the next pending special-select keystroke. Returns false when
    /// the queue is empty. WaveController.PhaseInput drains this each tick.
    /// </summary>
    public bool TryDequeueSpecialSelect(out ObjType w)
    {
        if (_specialSelects.Count == 0) { w = default; return false; }
        w = _specialSelects.Dequeue();
        return true;
    }

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
        _wave = GetNodeOrNull<Sim.WaveController>("../WaveController");

        _pt.OnKeyPress = key =>
        {
            if (key == "D" && _wave != null)
            {
                string demoPath = Path.Combine(ProjectSettings.GlobalizePath("res://assets"), "demos", "DEMO1G1_REC.json");
                _wave.StartDemoPlayback(DemoReplay.LoadFile(demoPath), Sim.SimClock.Frame);
                return;
            }

            if (_menu?.PilotCreateStep > 0)
            {
                _pendingKey = key;
                return;
            }

            // Special-weapon select keys (SC_1..SC_MINUS) bypass the menu
            // dispatch and go straight to the in-game shooter via the
            // _specialSelects queue. They never reach _pendingKey, so they
            // also don't trip the menu's same-tick deferral.
            var sw = KeyToSpecial(key);
            if (sw.HasValue)
            {
                _specialSelects.Enqueue(sw.Value);
                return;
            }
            // Queue for next-tick application; see _pendingKey docs.
            _pendingKey = key;
        };

        _pt.OnKeyDown = key =>
        {
            _heldKeys.Add(key);
        };

        _pt.OnKeyUp = key =>
        {
            _heldKeys.Remove(key);
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
