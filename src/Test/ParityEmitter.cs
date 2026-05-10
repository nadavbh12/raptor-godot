using System;
using System.IO;
using Godot;
using Raptor.Sim;

namespace Raptor.Test;

/// <summary>
/// Pure C# checkpoint writer. Holds no Godot objects; safe to instantiate
/// in xUnit tests without the Godot native runtime.
///
/// When an output path is provided, emits one NDJSON line per simulated
/// second (every 70 frames). Schema lives at tests/parity/schema.json.
///
/// Emission uses boundary-crossing detection (same algorithm as parity.c)
/// rather than strict modulo equality. Each win-state context has its own
/// anchor frame (StateEnteredFrame) and its own "last emitted second"
/// counter (_lastEmitSec), reset to -1 on context entry.
/// </summary>
internal class ParityEmitWorker : IDisposable
{
    private StreamWriter? _out;
    private int _lastEmitSec = -1;
    private int _lastAnchor = -1;  // tracks anchor changes to auto-reset _lastEmitSec

    // Win-state context, set externally by MenuController / PlaythroughDriver.
    // When Menu is set, emit uses Menu.StateEnteredFrame as the anchor and
    // Menu.State.ToParityString() as the win field. When null, falls back to
    // legacy absolute-frame mode (for tests that don't wire Menu).
    public Sim.MenuStateMachine? Menu { get; set; }

    // Stub fields — wired to real entities in later stages.
    // Default player position mirrors C's PLAYERINITX/PLAYERINITY (144, 160).
    public string WinState { get; set; } = "UNKNOWN";
    public int PlayerX { get; set; } = 144;
    public int PlayerY { get; set; } = 160;
    public uint Score { get; set; } = 0;
    public int Shield { get; set; } = 0;
    public int Enemies { get; set; } = 0;
    public int Pbullets { get; set; } = 0;
    public int Ebullets { get; set; } = 0;

    /// <summary>Opens the output file at the given path. No-op if path is null or empty.</summary>
    public void Open(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try {
            _out = new StreamWriter(path, append: false) { AutoFlush = true };
        } catch (Exception e) {
            GD.PrintErr($"ParityEmitter: failed to open {path}: {e.Message}");
        }
    }

    /// <summary>
    /// Resets the per-context emit counter. Retained for compatibility — callers
    /// may invoke this, but the canonical reset mechanism is anchor-change detection
    /// in Tick(), which mirrors parity.c's raptor_parity_set_win_state(win!=0) branch.
    /// </summary>
    public void OnStateChanged()
    {
        // No-op: anchor-change detection in Tick() handles the reset.
        // We intentionally do NOT reset _lastEmitSec here because the C version only
        // resets g_last_emit_sec when entering a non-Unknown state (win != 0), which
        // corresponds to a change in StateEnteredFrame — detected by _lastAnchor tracking.
    }

    /// <summary>
    /// Called every physics tick. Emits a checkpoint line whenever we cross
    /// into a new 70-frame "second" bucket relative to the current context anchor.
    /// Mirrors the boundary-crossing detection in parity.c:raptor_parity_tick.
    ///
    /// Auto-detects anchor changes (StateEnteredFrame changing) and resets the
    /// per-context emit counter, matching parity.c's g_last_emit_sec=-1 reset that
    /// fires only when raptor_parity_set_win_state is called with win != 0.
    /// </summary>
    public void Tick()
    {
        if (_out == null) return;

        int anchor;
        string win;

        if (Menu != null)
        {
            anchor = Menu.StateEnteredFrame;
            win = Menu.State.ToParityString();

            // Reset the emit counter whenever the anchor changes — this is
            // the equivalent of parity.c's g_last_emit_sec = -1 reset inside
            // raptor_parity_set_win_state(win != 0). The anchor only changes on
            // entry to a non-Unknown state (reAnchor=true in MenuStateMachine),
            // so transitions to Unknown don't trigger a reset, matching C behaviour.
            if (anchor != _lastAnchor)
            {
                _lastEmitSec = -1;
                _lastAnchor = anchor;
            }
        }
        else
        {
            // Legacy mode: emit at absolute frame multiples of 70 (for old tests).
            if (Sim.SimClock.Frame % 70 != 0) return;
            Emit(Sim.SimClock.Frame, WinState);
            return;
        }

        int relFc = Sim.SimClock.Frame - anchor;
        int curSec = relFc / 70;
        if (curSec <= _lastEmitSec) return;  // still in same or past second bucket
        _lastEmitSec = curSec;
        // Snap reported fc to the exact second boundary for determinism.
        Emit(curSec * 70, win);
    }

    private void Emit(int fc, string win)
    {
        // Manual formatting to match C-side output byte-for-byte.
        // System.Text.Json.JsonSerializer is avoided to prevent any
        // default casing or escaping surprises.
        // obj_hash is the FNV-1a 64-bit offset basis (empty object list).
        var line = $"{{\"fc\":{fc}," +
                   $"\"win\":\"{win}\"," +
                   $"\"player_x\":{PlayerX}," +
                   $"\"player_y\":{PlayerY}," +
                   $"\"score\":{Score}," +
                   $"\"shield\":{Shield}," +
                   $"\"enemies\":{Enemies}," +
                   $"\"pbullets\":{Pbullets}," +
                   $"\"ebullets\":{Ebullets}," +
                   $"\"obj_hash\":\"cbf29ce484222325\"}}";
        _out!.WriteLine(line);
    }

    public void Dispose()
    {
        _out?.Dispose();
        _out = null;
    }
}

/// <summary>
/// Godot Node wrapper around ParityEmitWorker. Bridges the Godot scene-tree
/// lifecycle (_Ready, _PhysicsProcess, _ExitTree) to the pure-C# worker.
///
/// Activate by setting the RAPTOR_PARITY_OUT environment variable to an output
/// file path before the scene loads.
/// </summary>
public partial class ParityEmitter : Node
{
    private readonly ParityEmitWorker _worker = new();

    // Forwarded properties for scene-level wiring.
    public Sim.MenuStateMachine? Menu { get => _worker.Menu; set => _worker.Menu = value; }
    public string WinState { get => _worker.WinState; set => _worker.WinState = value; }
    public int PlayerX { get => _worker.PlayerX; set => _worker.PlayerX = value; }
    public int PlayerY { get => _worker.PlayerY; set => _worker.PlayerY = value; }
    public uint Score { get => _worker.Score; set => _worker.Score = value; }
    public int Shield { get => _worker.Shield; set => _worker.Shield = value; }
    public int Enemies { get => _worker.Enemies; set => _worker.Enemies = value; }
    public int Pbullets { get => _worker.Pbullets; set => _worker.Pbullets = value; }
    public int Ebullets { get => _worker.Ebullets; set => _worker.Ebullets = value; }

    /// <summary>Notify the emitter that the menu context has changed.</summary>
    public void OnStateChanged() => _worker.OnStateChanged();

    public override void _Ready()
    {
        _worker.Open(OS.GetEnvironment("RAPTOR_PARITY_OUT"));
    }

    public override void _PhysicsProcess(double _) { _worker.Tick(); }

    public override void _ExitTree()
    {
        _worker.Dispose();
    }
}
