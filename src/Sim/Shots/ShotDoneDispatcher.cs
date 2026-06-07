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
        public DispatchResult(bool megaBombDetonated = false)
        {
            MegaBombDetonated = megaBombDetonated;
        }

        /// True only on the tick a MEGA_BOMB shot_done actually detonates.
        /// Parity-inert: drives a transient View flash, never sim state.
        public bool MegaBombDetonated { get; }
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
            return new DispatchResult();
        }
        switch (b.PlayerWeapon)
        {
            case ObjType.MegaBomb:
                foreach (var eb in enemyBullets) eb.Kill();
                foreach (var e in enemies) if (e.Alive) e.TakeDamage(b.Damage, deferRemovalForDump: true);
                if (tiles != null) DamageAllTiles(tiles, damage: 20);
                b.Kill();
                return new DispatchResult(megaBombDetonated: true);
            case ObjType.Turret:
                return new DispatchResult();
            default:
                b.Kill();
                return new DispatchResult();
        }
    }

    /// <summary>
    /// C TILE_DamageAll (TILE.C:336-351): decrement-only, hits -= 20 for every
    /// destructible on-screen tile. The award/explode is deferred to the next-iter
    /// TileThinkAwardScan (finding #1/#23) — never done inline here.
    /// </summary>
    private static void DamageAllTiles(IList<TileState> tiles, int damage)
    {
        foreach (var tile in tiles)
        {
            if (!tile.IsDestructible) continue;
            tile.Hits -= damage;
        }
    }
}
