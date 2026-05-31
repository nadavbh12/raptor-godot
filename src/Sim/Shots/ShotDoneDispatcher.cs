using System;
using System.Collections.Generic;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Raptor.Sim.MazeLevel;
using Raptor.Sim;

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
    public readonly struct DispatchResult
    {
        public DispatchResult(int tileBounty)
        {
            TileBounty = tileBounty;
        }

        public int TileBounty { get; }
    }

    public static DispatchResult Dispatch(BulletLogic b,
                                          IList<BulletLogic> enemyBullets,
                                          IList<EnemyLogic> enemies,
                                          Random? rng,
                                          IList<TileState>? tiles = null)
    {
        if (b.Delayed)
        {
            int scatter = DeterministicRandom.NextOrMidpoint(rng, 32, 16) - 16;
            b.ReInitBresenhamTarget(b.Mx + scatter, 0);
            b.Delayed = false;
            return new DispatchResult(0);
        }
        switch (b.PlayerWeapon)
        {
            case ObjType.MegaBomb:
                foreach (var eb in enemyBullets) eb.Kill();
                foreach (var e in enemies) if (e.Alive) e.TakeDamage(b.Damage, deferRemovalForDump: true);
                int bounty = tiles == null ? 0 : DamageAllTiles(tiles, damage: 20);
                b.Kill();
                return new DispatchResult(bounty);
            case ObjType.Turret:
                return new DispatchResult(0);
            default:
                b.Kill();
                return new DispatchResult(0);
        }
    }

    private static int DamageAllTiles(IList<TileState> tiles, int damage)
    {
        int bounty = 0;
        foreach (var tile in tiles)
        {
            if (!tile.IsDestructible) continue;
            int before = tile.Hits;
            tile.Hits -= damage;
            if (before >= 0 && tile.Hits < 0 && !tile.Dead)
            {
                bounty += tile.Bounty;
                tile.Dead = true;
            }
        }
        return bounty;
    }
}
