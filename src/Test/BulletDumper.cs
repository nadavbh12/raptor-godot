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
/// Emits three kinds of lines per iter (one per entity):
///   i=&lt;iter&gt; en idx=&lt;idx&gt; iname=&lt;N&gt; x=&lt;X&gt; y=&lt;Y&gt; hits=&lt;H&gt;
///   i=&lt;iter&gt; eb idx=&lt;idx&gt; type=&lt;T&gt; x=&lt;X&gt; y=&lt;Y&gt; mx=&lt;MX&gt; my=&lt;MY&gt; speed=&lt;S&gt; curframe=&lt;CF&gt; cnt=&lt;CNT&gt;
///   i=&lt;iter&gt; pb idx=&lt;idx&gt; wpn=&lt;W&gt; x=&lt;X&gt; y=&lt;Y&gt; mx=&lt;MX&gt; my=&lt;MY&gt; speed=&lt;S&gt; cnt=&lt;CNT&gt; beam=&lt;B&gt; reached=&lt;R&gt; doneflag=&lt;D&gt;
///
/// `wpn` is the WeaponType integer (S_FORWARD_GUNS=0..S_DEATH_RAY=14, or
/// -1 for ad-hoc test bullets without a weapon tag). `beam` is 1 if
/// the bullet is a stationary line/vertical beam, else 0. `reached` is 1
/// after the Bresenham loop completes (mirrors C `shot->move.done`).
///
/// Activate: RAPTOR_BULLET_DUMP=/path/to/dump.txt
/// </summary>
public partial class BulletDumper : Node
{
    private StreamWriter? _out;
    private Sim.WaveController? _wave;
    private int _iter = 0;
    private static BulletDumper? _active;

    public override void _Ready()
    {
        string path = OS.GetEnvironment("RAPTOR_BULLET_DUMP");
        if (string.IsNullOrEmpty(path)) return;
        _out = new StreamWriter(path) { AutoFlush = true };
        _active = this;

        _wave = GetNodeOrNull<Sim.WaveController>("../WaveController");
        if (_wave != null) _wave.OnIterEnd += OnIterEnd;
    }

    public override void _ExitTree()
    {
        if (_wave != null) _wave.OnIterEnd -= OnIterEnd;
        _out?.Flush();
        _out?.Dispose();
        _out = null;
        if (_active == this) _active = null;
    }

    // Use InvariantCulture for integer formatting so negative numbers do not
    // get a LRM (U+200E) inserted on locales that use directional marks.
    // Without this the dump emits "y=‎-148" which downstream regex parsers
    // fail to match against the C output "y=-148".
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void RecordPlayerRemove(string reason, Sim.Bullet.BulletLogic b,
                                          int tileIndex = -1, int tileHits = 0)
    {
        var active = _active;
        if (active?._out == null) return;
        int wpn = b.CWeaponTypeForDump is { } dumpW ? (int)dumpW
            : b.PlayerWeapon is { } w ? (int)w
            : -1;
        active._out.WriteLine(string.Format(Inv,
            "i={0} premove reason={1} wpn={2} x={3} y={4} mx={5} my={6} speed={7} reached={8} doneflag={9} tileidx={10} tilehits={11}",
            active._iter, reason, wpn, b.X, b.Y, b.Mx, b.My, b.CurSpeed,
            b.ReachedTarget ? 1 : 0, b.DoneFlagForDump ? 1 : 0, tileIndex, tileHits));
    }

    private void OnIterEnd()
    {
        if (_out == null || _wave == null) return;
        var sb = new StringBuilder();
        int idx = 0;
        foreach (var e in _wave.GetEnemies())
        {
            if (!e.Alive && !e.PendingRemovalDump) continue;
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
        idx = 0;
        foreach (var b in _wave.GetPlayerBullets())
        {
            if (!b.Alive) continue;
            int wpn   = b.CWeaponTypeForDump is { } dumpW ? (int)dumpW
                : b.PlayerWeapon is { } w ? (int)w
                : -1;
            int beam  = b.IsBeam ? 1 : 0;
            int reach = b.ReachedTarget ? 1 : 0;
            int doneflag = b.DoneFlagForDump ? 1 : 0;
            sb.AppendFormat(Inv,
                "i={0} pb idx={1} wpn={2} x={3} y={4} mx={5} my={6} speed={7} cnt={8} beam={9} reached={10} doneflag={11}\n",
                _iter, idx, wpn, b.X, b.Y, b.Mx, b.My, b.CurSpeed,
                b.CCounterForDump, beam, reach, doneflag);
            idx++;
        }
        _out.Write(sb.ToString());
        _iter++;
    }
}
