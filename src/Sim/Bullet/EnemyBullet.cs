using Godot;

namespace Raptor.Sim.Bullet;

public partial class EnemyBullet : Area2D
{
    public BulletLogic Logic { get; private set; } = null!;

    public void Init(int x, int y, int velX = 0, int velY = 4)
    {
        Logic = new BulletLogic(BulletKind.Enemy, x, y, velX, velY);
        Position = new Vector2I(x, y);  // LINT-OK: render position only
    }

    public void Tick()
    {
        Logic.Tick();
        Position = new Vector2I(Logic.X, Logic.Y);  // LINT-OK
        if (!Logic.Alive) QueueFree();
    }
}
