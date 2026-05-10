using Godot;
using Raptor.Sim.Bullet;

namespace Raptor.Sim.Enemy;

public partial class Enemy : Node2D
{
    public EnemyLogic Logic { get; private set; } = null!;

    public void Init(SpriteMeta meta, int spawnX, int spawnY)
    {
        Logic    = new EnemyLogic(meta, spawnX, spawnY);
        Position = new Vector2I(spawnX, spawnY);  // LINT-OK: render-side mirror
    }

    /// <summary>
    /// Tick the enemy. Returns a BulletLogic if a bullet was fired (caller
    /// should spawn an EnemyBullet Node from it), or null.
    /// </summary>
    public BulletLogic? Tick()
    {
        var fired = Logic.Tick();
        Position = new Vector2I(Logic.X, Logic.Y);  // LINT-OK: render-side mirror
        if (!Logic.Alive) QueueFree();
        return fired;
    }
}
