using System;
using System.Collections.Generic;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Raptor.Sim.Shots;

namespace Raptor.Sim;

/// <summary>
/// Pure collision-detection passes extracted from WaveController's collect phase
/// (E10). These find which entities collide; WaveController applies the effects
/// (kill, impact flash, damage accumulation, scoring) so the side-effect order and
/// the shared RNG draws stay in the phase owner. Mirrors the established static
/// dispatcher pattern (<see cref="Shots.PlayerBulletCollisionDispatcher"/>).
/// </summary>
internal static class CollisionDetection
{
    /// <summary>An enemy bullet that overlapped the player this tick.</summary>
    internal readonly record struct EnemyBulletHit(BulletLogic Bullet, int ImpactX, int ImpactY);

    /// <summary>
    /// Enemy bullets vs player AABB (ESHOT collision). C uses strict less-than on
    /// the half-extents around the player centre (player_cx/cy). ES_LASER is
    /// excluded — it applies alignment damage in its tick phase, not as an AABB hit.
    /// Appends each hit to <paramref name="outHits"/> in bullet-list order.
    /// </summary>
    internal static void CollectEnemyBulletHits(
        IReadOnlyList<BulletLogic> enemyBullets, int playerCx, int playerCy,
        int halfW, int halfH, List<EnemyBulletHit> outHits)
    {
        foreach (var b in enemyBullets)
        {
            if (!b.Alive) continue;
            if (b.IsEnemyLaser) continue;
            int dx = Math.Abs(b.X - playerCx);
            int dy = Math.Abs(b.Y - playerCy);
            if (dx < halfW && dy < halfH)
                outHits.Add(new EnemyBulletHit(b, b.X, b.Y));
        }
    }

    /// <summary>
    /// Vertical/line beams vs enemy column (SHOTS.C:1068-1088): the first enemy
    /// whose x-range contains the beam X, above the player centre and y &gt; -30.
    /// The beam does not despawn on hit. Appends (enemy, damage) to
    /// <paramref name="outHits"/>. LineBeam (TURRET) bullets have BeamDamages=false
    /// (damage already applied at spawn), so they are skipped.
    /// </summary>
    internal static void CollectBeamEnemyHits(
        IReadOnlyList<BulletLogic> playerBullets, IReadOnlyList<EnemyLogic> enemies,
        int playerCy, List<(EnemyLogic enemy, int dmg)> outHits)
    {
        foreach (var b in playerBullets)
        {
            if (!b.Alive || !b.IsBeam || !b.BeamDamages) continue;
            foreach (var e in enemies)
            {
                if (!e.Alive) continue;
                if (!HitTypeMatches(b.HitType, e)) continue;
                int ex  = e.X;
                int ex2 = e.X + e.Meta.Width - 1;   // sprite->x2 = sprite->x + width - 1 (ENEMY.C:877)
                if (b.X > ex && b.X < ex2 && e.Y < playerCy && e.Y > -30)
                {
                    outHits.Add((e, b.Damage));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// SHOTS.C SHOTS_Think hit-type matching:
    ///   S_ALL / S_GRALL — anything; S_AIR — air only; S_GROUND/S_GTILE — ground
    ///   only; S_SUCK — energy-grab path (matches anything).
    /// </summary>
    internal static bool HitTypeMatches(HitType ht, EnemyLogic e) => ht switch
    {
        HitType.All    => true,
        HitType.GrAll  => true,
        HitType.Air    => !e.IsGround,
        HitType.Ground => e.IsGround,
        HitType.GTile  => e.IsGround,
        HitType.Suck   => true,
        _              => true,
    };
}
