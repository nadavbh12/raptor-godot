using System.Collections.Generic;
using Raptor.Sim;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Raptor.Sim.Shots;
using Xunit;

namespace Raptor.Tests;

// Covers the enemy-bullet-vs-player AABB pass extracted into CollisionDetection
// (E10). The beam/hit-type passes are exercised by the full parity gate.
public class CollisionDetectionTests
{
    [Fact]
    public void CollectBeamEnemyHits_right_edge_uses_full_width_minus_one()
    {
        // Finding #27: C's beam-vs-enemy test is b.X < enemy->x2 where
        // x2 = sprite->x + width - 1 (ENEMY.C:877, used by SHOTS.C:1093). Godot
        // computed 2*HalfW-1 = Width-2 for ODD widths, dropping the rightmost
        // interior pixel. Inert on shipped assets (all even-dimensioned); a
        // Width=25 enemy exposes the off-by-one.
        var enemy = new EnemyLogic(new SpriteMeta
        {
            Hits = 3, NumFlight = 0, FlightType = 1, Width = 25, Height = 24,
        }, spawnX: 100, mapY: 10);
        // correct ex2 = 100+25-1 = 124; buggy = 100+2*12-1 = 123. Beam at X=123:
        // correct → 123<124 true (hit); buggy → 123<123 false (miss).
        var beam = BulletLogic.VerticalBeam(x: 123, y: 0, life: 5, damage: 3,
                                            startPlayerX: 123, startPlayerY: 200);
        var outHits = new List<(EnemyLogic enemy, int dmg)>();

        CollisionDetection.CollectBeamEnemyHits(
            new List<BulletLogic> { beam }, new List<EnemyLogic> { enemy },
            playerCy: 200, outHits);

        Assert.Single(outHits);
        Assert.Same(enemy, outHits[0].enemy);
    }

    private static EnemyLogic BeamEnemy(int hits, int x, int y, int flightType = 1)
        => new EnemyLogic(new SpriteMeta
        {
            Hits = hits, NumFlight = 0, FlightType = flightType, Width = 32, Height = 24,
        }, spawnX: x, mapY: y);

    private static BulletLogic Beam(int x, int damage, HitType ht = HitType.All)
    {
        var b = BulletLogic.VerticalBeam(x: x, y: 0, life: 5, damage: damage,
                                         startPlayerX: x, startPlayerY: 200);
        b.HitType = ht;
        return b;
    }

    [Fact]
    public void CollectBeamEnemyHits_has_no_hittype_gate_hits_ground_enemy()
    {
        // Finding #9: C's S_BEAM loop (SHOTS.C:1092-1106) is purely geometric — no
        // air/ground gate. FORWARD_LASER sets ht=S_AIR but meffect=TRUE skips the
        // ht-based switch (SHOTS.C:1155), so the beam hits air AND ground alike.
        // Godot's spurious HitTypeMatches gate blocked HitType.Air beams from ground.
        var ground = BeamEnemy(hits: 10, x: 100, y: 50, flightType: 3);  // IsGround
        var outHits = new List<(EnemyLogic enemy, int dmg)>();

        CollisionDetection.CollectBeamEnemyHits(
            new List<BulletLogic> { Beam(110, 3, HitType.Air) },
            new List<EnemyLogic> { ground }, playerCy: 200, outHits);

        Assert.Single(outHits);
        Assert.Same(ground, outHits[0].enemy);
    }

    [Fact]
    public void CollectBeamEnemyHits_continues_past_enemy_landing_on_minus_one()
    {
        // Finding #24: C breaks only when post-subtraction hits != -1
        // (SHOTS.C:1099-1101). An enemy reduced to exactly -1 is passed THROUGH and
        // the beam continues to the next enemy in the column. Hits=5, damage=6 → -1.
        var first  = BeamEnemy(hits: 5,  x: 100, y: 30);
        var second = BeamEnemy(hits: 10, x: 100, y: 60);
        var outHits = new List<(EnemyLogic enemy, int dmg)>();

        CollisionDetection.CollectBeamEnemyHits(
            new List<BulletLogic> { Beam(110, 6) },
            new List<EnemyLogic> { first, second }, playerCy: 200, outHits);

        Assert.Equal(2, outHits.Count);
        Assert.Same(first, outHits[0].enemy);
        Assert.Same(second, outHits[1].enemy);
    }

    [Fact]
    public void CollectBeamEnemyHits_breaks_when_post_damage_hits_not_minus_one()
    {
        // Control for #24: a normal hit (post-damage hits != -1, e.g. 20-6=14) still
        // breaks the beam after the first enemy — beams do NOT pierce in general.
        var first  = BeamEnemy(hits: 20, x: 100, y: 30);
        var second = BeamEnemy(hits: 10, x: 100, y: 60);
        var outHits = new List<(EnemyLogic enemy, int dmg)>();

        CollisionDetection.CollectBeamEnemyHits(
            new List<BulletLogic> { Beam(110, 6) },
            new List<EnemyLogic> { first, second }, playerCy: 200, outHits);

        Assert.Single(outHits);
        Assert.Same(first, outHits[0].enemy);
    }

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
