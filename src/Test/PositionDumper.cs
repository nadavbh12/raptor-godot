using System.IO;
using System.Text;
using Godot;
using Raptor.Sim;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;

namespace Raptor.Test;

/// <summary>
/// Optional dump of every alive entity's (x, y, slib/kind) at each parity
/// tick. Mirrors C's parity.c dump_positions: emit at iter ends, with bucket-
/// crossing detection (one emit per 70-frame window, snapped to bucket start).
///
/// In-game: subscribes to WaveController.OnIterEnd. Each iter end triggers a
/// possible emit if a new 70-frame bucket was crossed since the last emit.
/// Menu/dialog windows: falls back to per-frame _PhysicsProcess polling since
/// they don't have iter ends.
///
/// Activate: RAPTOR_POS_DUMP=path/to/file.txt
/// </summary>
public partial class PositionDumper : Node
{
    private StreamWriter? _out;
    private Sim.WaveController? _wave;
    private Sim.MenuStateMachine? _menu;
    private int _lastEmitSec = -1;
    private int _lastAnchor  = int.MinValue;

    public override void _Ready()
    {
        string path = OS.GetEnvironment("RAPTOR_POS_DUMP");
        if (string.IsNullOrEmpty(path)) return;
        _out = new StreamWriter(path) { AutoFlush = true };

        _wave = GetNodeOrNull<Sim.WaveController>("../WaveController");
        var menuCtrl = GetNodeOrNull<Sim.MenuController>("../MenuController");
        if (menuCtrl != null) _menu = menuCtrl.Menu;

        if (_wave != null) _wave.OnIterEnd += OnIterEnd;
    }

    public override void _ExitTree()
    {
        if (_wave != null) _wave.OnIterEnd -= OnIterEnd;
        _out?.Flush();
        _out?.Dispose();
        _out = null;
    }

    private void OnIterEnd()
    {
        // In-game iter-end emit (mirrors C's parity_tick at end of each
        // legacy_pump loop iteration).
        if (_out == null || _wave == null || _menu == null || !_menu.InGame) return;
        TryEmit(_menu.GameEnteredFrame, "MISSION_1");
    }

    public override void _PhysicsProcess(double _)
    {
        // Menu/dialog windows have no game loop iters; fall back to per-frame
        // polling so we still get fc=0/70/... emits in those contexts.
        if (_out == null || _menu == null || _menu.InGame) return;
        TryEmit(_menu.StateEnteredFrame, _menu.State.ToParityString());
    }

    private void TryEmit(int anchor, string win)
    {
        if (anchor != _lastAnchor) { _lastEmitSec = -1; _lastAnchor = anchor; }
        int relFc  = Sim.SimClock.Frame - anchor;
        int curSec = relFc / 70;
        if (curSec <= _lastEmitSec) return;
        _lastEmitSec = curSec;
        Dump(curSec * 70, win);
    }

    private void Dump(int fc, string win)
    {
        if (_out == null || _wave == null) return;
        var sb = new StringBuilder();
        sb.Append($"fc={fc} abs={Sim.SimClock.Frame} win={win} iter={_wave.GameLoopIter}\n");
        sb.Append($"player x={_wave.PlayerLogic.X} y={_wave.PlayerLogic.Y} ")
          .Append($"shield={_wave.PlayerLogic.Shield} score={_wave.Score}\n");

        foreach (var e in _wave.GetEnemies())
        {
            if (!e.Alive) continue;
            sb.Append($"enemy slib={e.Meta.IName} x={e.X} y={e.Y} hits={e.Hits}\n");
        }
        foreach (var b in _wave.GetEnemyBullets())
        {
            if (!b.Alive) continue;
            sb.Append($"ebullet x={b.X} y={b.Y}\n");
        }
        foreach (var b in _wave.GetPlayerBullets())
        {
            if (!b.Alive) continue;
            sb.Append($"pbullet x={b.X} y={b.Y}\n");
        }
        sb.Append('\n');
        _out.Write(sb.ToString());
    }
}
