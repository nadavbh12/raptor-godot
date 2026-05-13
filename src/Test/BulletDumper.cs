using System.Globalization;
using System.IO;
using System.Text;
using Godot;

namespace Raptor.Test;

/// <summary>
/// Per-iter enemy + enemy-bullet position dump. Mirrors C's
/// raptor_bullet_dump_iter (port/platform/parity.c) so we can run
/// mission_start.txt on both sides and compare entities at the same
/// logical game-loop iteration.
///
/// Emits two kinds of lines per iter (one per entity):
///   i=&lt;iter&gt; en idx=&lt;idx&gt; iname=&lt;N&gt; x=&lt;X&gt; y=&lt;Y&gt; hits=&lt;H&gt;
///   i=&lt;iter&gt; eb idx=&lt;idx&gt; type=&lt;T&gt; x=&lt;X&gt; y=&lt;Y&gt; mx=&lt;MX&gt; my=&lt;MY&gt; speed=&lt;S&gt; curframe=&lt;CF&gt; cnt=&lt;CNT&gt;
///
/// Activate: RAPTOR_BULLET_DUMP=/path/to/dump.txt
/// </summary>
public partial class BulletDumper : Node
{
    private StreamWriter? _out;
    private Sim.WaveController? _wave;
    private int _iter = 0;

    public override void _Ready()
    {
        string path = OS.GetEnvironment("RAPTOR_BULLET_DUMP");
        if (string.IsNullOrEmpty(path)) return;
        _out = new StreamWriter(path) { AutoFlush = true };

        _wave = GetNodeOrNull<Sim.WaveController>("../WaveController");
        if (_wave != null) _wave.OnIterEnd += OnIterEnd;
    }

    public override void _ExitTree()
    {
        if (_wave != null) _wave.OnIterEnd -= OnIterEnd;
        _out?.Flush();
        _out?.Dispose();
        _out = null;
    }

    // Use InvariantCulture for integer formatting so negative numbers do not
    // get a LRM (U+200E) inserted on locales that use directional marks.
    // Without this the dump emits "y=‎-148" which downstream regex parsers
    // fail to match against the C output "y=-148".
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private void OnIterEnd()
    {
        if (_out == null || _wave == null) return;
        var sb = new StringBuilder();
        int idx = 0;
        foreach (var e in _wave.GetEnemies())
        {
            if (!e.Alive) continue;
            sb.AppendFormat(Inv, "i={0} en idx={1} iname={2} x={3} y={4} hits={5}\n",
                _iter, idx, e.Meta.IName, e.X, e.Y, e.Hits);
            idx++;
        }
        idx = 0;
        foreach (var b in _wave.GetEnemyBullets())
        {
            if (!b.Alive) continue;
            sb.AppendFormat(Inv,
                "i={0} eb idx={1} type={2} x={3} y={4} mx={5} my={6} speed={7} curframe={8} cnt={9}\n",
                _iter, idx, (int)b.ShotType, b.X, b.Y, b.Mx, b.My, b.CurSpeed,
                b.FrameCounter % b.NumFrames, b.FrameCounter);
            idx++;
        }
        _out.Write(sb.ToString());
        _iter++;
    }
}
