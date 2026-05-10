namespace Raptor.Sim.Bullet;

public enum BulletKind { Player, Enemy }

/// <summary>
/// Pure-C# bullet. Integer position, integer velocity-per-tick.
/// Bullets despawn when out of [0, 320] x [0, 200].
/// </summary>
public sealed class BulletLogic
{
    public BulletKind Kind { get; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int VelX { get; }
    public int VelY { get; }
    public bool Alive { get; private set; } = true;

    public BulletLogic(BulletKind kind, int x, int y, int velX, int velY)
    {
        Kind = kind;
        X = x; Y = y;
        VelX = velX; VelY = velY;
    }

    public void Tick()
    {
        if (!Alive) return;
        X += VelX;
        Y += VelY;
        if (X < 0 || X > 320 || Y < 0 || Y > 200) Alive = false;
    }

    public void Kill() { Alive = false; }
}
