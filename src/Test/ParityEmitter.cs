using System;
using System.IO;
using Godot;

namespace Raptor.Test;

/// <summary>
/// Pure C# checkpoint writer. Holds no Godot objects; safe to instantiate
/// in xUnit tests without the Godot native runtime.
///
/// When an output path is provided, emits one NDJSON line per simulated
/// second (every 70 frames). Schema lives at tests/parity/schema.json.
///
/// State properties are stub values for Stage 3. Later tasks (Player, Enemy,
/// Bullet) will wire real values into these properties.
/// </summary>
internal class ParityEmitWorker : IDisposable
{
    private StreamWriter? _out;

    // Stub state. Later tasks wire these to real entity values.
    public string WinState { get; set; } = "UNKNOWN";
    public int PlayerX { get; set; } = 160;
    public int PlayerY { get; set; } = 100;
    public uint Score { get; set; } = 0;
    public int Shield { get; set; } = 100;
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
    /// Called every physics tick. Emits a checkpoint line every 70 frames (1 simulated second).
    /// </summary>
    public void Tick()
    {
        if (_out == null) return;
        if (Sim.SimClock.Frame % 70 != 0) return;
        Emit();
    }

    private void Emit()
    {
        // Manual formatting to match C-side output byte-for-byte.
        // System.Text.Json.JsonSerializer is avoided to prevent any
        // default casing or escaping surprises.
        var line = $"{{\"fc\":{Sim.SimClock.Frame}," +
                   $"\"win\":\"{WinState}\"," +
                   $"\"player_x\":{PlayerX}," +
                   $"\"player_y\":{PlayerY}," +
                   $"\"score\":{Score}," +
                   $"\"shield\":{Shield}," +
                   $"\"enemies\":{Enemies}," +
                   $"\"pbullets\":{Pbullets}," +
                   $"\"ebullets\":{Ebullets}," +
                   $"\"obj_hash\":\"0000000000000000\"}}";
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

    // Forwarded properties for scene-level wiring by later tasks.
    public string WinState { get => _worker.WinState; set => _worker.WinState = value; }
    public int PlayerX { get => _worker.PlayerX; set => _worker.PlayerX = value; }
    public int PlayerY { get => _worker.PlayerY; set => _worker.PlayerY = value; }
    public uint Score { get => _worker.Score; set => _worker.Score = value; }
    public int Shield { get => _worker.Shield; set => _worker.Shield = value; }
    public int Enemies { get => _worker.Enemies; set => _worker.Enemies = value; }
    public int Pbullets { get => _worker.Pbullets; set => _worker.Pbullets = value; }
    public int Ebullets { get => _worker.Ebullets; set => _worker.Ebullets = value; }

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
