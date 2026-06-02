using System.Collections.Generic;
using Raptor.Sim;
using Raptor.Sim.Bullet;
using Xunit;

namespace Raptor.Tests;

// Covers the enemy-bullet-vs-player AABB pass extracted into CollisionDetection
// (E10). The beam/hit-type passes are exercised by the full parity gate.
public class CollisionDetectionTests
{
    [Fact]
    public void CollectEnemyBulletHits_returns_alive_bullets_inside_player_aabb()
    {
        var hit  = new BulletLogic(BulletKind.Enemy, 160, 100, 0, 0);  // dx=dy=0 → inside
        var miss = new BulletLogic(BulletKind.Enemy, 200, 100, 0, 0);  // dx=40 → outside
        var dead = new BulletLogic(BulletKind.Enemy, 160, 100, 0, 0);
        dead.Kill();

        var bullets = new List<BulletLogic> { hit, miss, dead };
        var outHits = new List<CollisionDetection.EnemyBulletHit>();

        CollisionDetection.CollectEnemyBulletHits(bullets, playerCx: 160, playerCy: 100, halfW: 16, halfH: 16, outHits);

        Assert.Single(outHits);
        Assert.Same(hit, outHits[0].Bullet);
        Assert.Equal(160, outHits[0].ImpactX);
        Assert.Equal(100, outHits[0].ImpactY);
    }

    [Fact]
    public void CollectEnemyBulletHits_uses_strict_half_extent_bounds()
    {
        // C uses strict less-than: dx == halfW is NOT a hit.
        var onBoundary = new BulletLogic(BulletKind.Enemy, 176, 100, 0, 0); // dx=16 == halfW
        var bullets = new List<BulletLogic> { onBoundary };
        var outHits = new List<CollisionDetection.EnemyBulletHit>();

        CollisionDetection.CollectEnemyBulletHits(bullets, 160, 100, 16, 16, outHits);

        Assert.Empty(outHits);
    }
}
