using System.IO;
using Godot;
using Raptor.Sim.Bonus;

namespace Raptor.Test;

/// <summary>
/// Per-iter bonus lifecycle dump. Activate with RAPTOR_BONUS_DUMP=path.
/// Mirrors the C BONUS.C trace added for parity debugging.
/// </summary>
public partial class BonusDumper : Node
{
    private StreamWriter? _out;
    private Sim.WaveController? _wave;

    public override void _Ready()
    {
        string path = OS.GetEnvironment("RAPTOR_BONUS_DUMP");
        if (string.IsNullOrEmpty(path)) return;
        _out = new StreamWriter(path) { AutoFlush = true };

        _wave = GetNodeOrNull<Sim.WaveController>("../WaveController");
        if (_wave != null)
        {
            _wave.OnBonusTrace += OnBonusTrace;
            _wave.OnIterEnd += OnIterEnd;
        }
    }

    public override void _ExitTree()
    {
        if (_wave != null)
        {
            _wave.OnBonusTrace -= OnBonusTrace;
            _wave.OnIterEnd -= OnIterEnd;
        }
        _out?.Flush();
        _out?.Dispose();
        _out = null;
    }

    private void OnBonusTrace(string line)
    {
        _out?.WriteLine(line);
    }

    private void OnIterEnd()
    {
        if (_out == null || _wave == null) return;
        foreach (BonusLogic b in _wave.GetBonuses())
        {
            if (!b.Alive) continue;
            _out.WriteLine(_wave.BonusTraceLine("state", b));
        }
    }
}
