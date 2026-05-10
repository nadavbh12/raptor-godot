using System;
using Raptor.Sim.Bullet;

namespace Raptor.Sim.Enemy;

/// <summary>
/// Pure-C# enemy state machine. Holds reference to its SpriteMeta.
///
/// FlightType handling for Stage 3:
///   0 (REPEAT)  — cycle through flightx[0..NumFlight-1], wrap forever.
///   1 (LINEAR)  — walk flightx[0..NumFlight-1] once, then mark Done.
/// Other flight types are stubbed to LINEAR for now (refine in later stages).
/// </summary>
public sealed class EnemyLogic
{
    public SpriteMeta Meta { get; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Hits { get; private set; }
    public bool Done { get; private set; }       // ran past end of LINEAR path
    public bool Alive => Hits > 0 && !Done;
    public int FlightIndex { get; private set; }

    private int _shootCounter;

    public EnemyLogic(SpriteMeta meta, int spawnX, int spawnY)
    {
        Meta   = meta;
        Hits   = meta.Hits > 0 ? meta.Hits : 1;
        X      = spawnX;
        Y      = spawnY;
    }

    /// <summary>
    /// One tick. Returns a fired BulletLogic if the enemy fired this tick,
    /// else null. Caller should spawn an EnemyBullet Node from it.
    /// </summary>
    public BulletLogic? Tick()
    {
        if (!Alive) return null;

        AdvancePath();
        return MaybeFire();
    }

    public void TakeDamage(int dmg)
    {
        if (!Alive) return;
        Hits -= dmg;
        if (Hits < 0) Hits = 0;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private void AdvancePath()
    {
        int n = Math.Min(Meta.NumFlight,
                Math.Min(Meta.FlightX.Length, Meta.FlightY.Length));
        if (n <= 0) return;

        int idx = FlightIndex;
        if (idx >= n)
        {
            if (Meta.FlightType == 0 /*REPEAT*/) idx = 0;
            else { Done = true; return; }
        }

        X           = Meta.FlightX[idx];
        Y           = Meta.FlightY[idx];
        FlightIndex = idx + 1;
    }

    private BulletLogic? MaybeFire()
    {
        if (Meta.NumGuns <= 0 || Meta.ShootFrame <= 0) return null;

        _shootCounter++;
        if (_shootCounter < Meta.ShootFrame) return null;
        _shootCounter = 0;

        // Single-gun firing for Stage 3. Multi-gun expansion in a later stage.
        const int gunIdx = 0;
        int sx = X + (gunIdx < Meta.ShootX.Length ? Meta.ShootX[gunIdx] : 0);
        int sy = Y + (gunIdx < Meta.ShootY.Length ? Meta.ShootY[gunIdx] : 0);
        return new BulletLogic(BulletKind.Enemy, sx, sy, 0, 4);
    }
}
