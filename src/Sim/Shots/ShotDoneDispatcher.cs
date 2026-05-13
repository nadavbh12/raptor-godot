using System;
using System.Collections.Generic;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;

namespace Raptor.Sim.Shots;

/// <summary>
/// Pure-C# helper for the C SHOTS_Think shot_done branch (SHOTS.C:1218-1249).
/// Lives outside <see cref="WaveController"/> so it can be unit-tested without
/// constructing a Godot Node. WaveController.PhaseMovement calls
/// <see cref="Dispatch"/> for each player Bresenham bullet whose Bresenham
/// loop completed in the prior Tick.
///
/// Mirrored behaviour:
///   delayflag (DUMB_MISSLE) → InitMobj toward (move.x + random(32)-16, 0)
///   MEGA_BOMB              → drop all enemy bullets, damage all enemies, kill
///   TURRET                 → no-op (case S_TURRET: break; in C — but TURRET
///                            spawns a LineBeam, not a Bresenham bullet, so
///                            this branch is unreachable in practice)
///   default                → SHOTS_Remove (kill the bullet)
/// </summary>
internal static class ShotDoneDispatcher
{
    public static void Dispatch(BulletLogic b,
                                IList<BulletLogic> enemyBullets,
                                IList<EnemyLogic> enemies,
                                Random? rng)
    {
        if (b.Delayed)
        {
            int scatter = (rng?.Next(32) ?? 16) - 16;
            b.ReInitBresenhamTarget(b.Mx + scatter, 0);
            b.Delayed = false;
            return;
        }
        switch (b.PlayerWeapon)
        {
            case WeaponType.MegaBomb:
                foreach (var eb in enemyBullets) eb.Kill();
                foreach (var e in enemies) if (e.Alive) e.TakeDamage(b.Damage);
                b.Kill();
                return;
            case WeaponType.Turret:
                return;
            default:
                b.Kill();
                return;
        }
    }
}
