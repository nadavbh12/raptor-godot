using System.IO;
using System.Text;
using Godot;
using Raptor.Sim;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;

namespace Raptor.Test;

/// <summary>
/// Optional dump of every alive entity's (x, y, slib/kind) at each parity
/// tick. One block of text per tick. Designed to be diff'd against a C
/// dump in the same format for spatial-divergence debugging.
///
/// Activate: RAPTOR_POS_DUMP=path/to/file.txt
///
/// Output format (one block per parity tick, blank line between):
///   fc=<MISSION_1 fc> abs=<SimClock.Frame> win=<state>
///   player x=<x> y=<y> shield=<s> score=<u>
///   enemy slib=<i> x=<x> y=<y> alive=<bool>
///   enemy ...
///   ebullet x=<x> y=<y> dx=<f> dy=<f> alive=<bool>
///   pbullet ...
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
    }

    public override void _PhysicsProcess(double _)
    {
        if (_out == null || _wave == null || _menu == null) return;

        int anchor = _menu.InGame ? _menu.GameEnteredFrame : _menu.StateEnteredFrame;
        if (anchor != _lastAnchor) { _lastEmitSec = -1; _lastAnchor = anchor; }

        int relFc  = Sim.SimClock.Frame - anchor;
        int curSec = relFc / 70;
        if (curSec <= _lastEmitSec) return;
        _lastEmitSec = curSec;

        string win = _menu.InGame ? "MISSION_1" : _menu.State.ToParityString();
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

    public override void _ExitTree()
    {
        _out?.Flush();
        _out?.Dispose();
        _out = null;
    }
}
