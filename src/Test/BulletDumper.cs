using System.IO;
using System.Text;
using Godot;

namespace Raptor.Test;

/// <summary>
/// Per-iter enemy-bullet position dump. Mirrors C's raptor_bullet_dump_iter
/// (port/platform/parity.c) so we can run mission_start.txt on both sides
/// and compare bullets at the same logical game-loop iteration index.
///
/// One line per live ESHOT per game-loop iteration; emits on OnIterEnd
/// (after movement + collision + cleanup phases). Line format matches the
/// C version's ESHOT_DumpForParity exactly:
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

    private void OnIterEnd()
    {
        if (_out == null || _wave == null) return;
        var sb = new StringBuilder();
        int idx = 0;
        foreach (var b in _wave.GetEnemyBullets())
        {
            if (!b.Alive) continue;
            sb.Append("i=").Append(_iter)
              .Append(" eb idx=").Append(idx)
              .Append(" type=").Append((int)b.ShotType)
              .Append(" x=").Append(b.X)
              .Append(" y=").Append(b.Y)
              .Append(" mx=").Append(b.X)
              .Append(" my=").Append(b.Y)
              .Append(" speed=").Append(b.CurSpeed)
              .Append(" curframe=").Append(b.FrameCounter % b.NumFrames)
              .Append(" cnt=").Append(b.FrameCounter)
              .Append('\n');
            idx++;
        }
        _out.Write(sb.ToString());
        _iter++;
    }
}
