using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Raptor.Sim.MazeLevel;
using Raptor.Test;

namespace Raptor.Sim.Shots;

internal static class PlayerBulletCollisionDispatcher
{
    public readonly record struct EnemyHit(EnemyLogic Enemy, int Damage);
    private static readonly Lazy<StreamWriter?> HitTrace = new(OpenHitTrace);
    public static int TraceIter { get; set; } = -1;

    public sealed class Result
    {
        public List<EnemyHit> HitEnemies { get; } = new();
        public List<(int X, int Y)> RandomSparkPositions { get; } = new();
        public List<(int X, int Y)> OrangeSparkPositions { get; } = new();
        public List<(int X, int Y)> BlueSparkPositions { get; } = new();
        public int RandomSparkColorCount { get; set; }
        public bool TileHit { get; set; }
    }

    public static Result Collect(IList<BulletLogic> bullets,
                                 IList<EnemyLogic> enemies,
                                 IList<TileState> tiles,
                                 int mapCols)
    {
        var result = new Result();
        var damageableAtPassStart = new HashSet<EnemyLogic>();
        foreach (var e in enemies)
        {
            if (e.Alive)
                damageableAtPassStart.Add(e);
        }
        foreach (var b in bullets)
        {
            if (!b.Alive || b.IsBeam || b.DeferredDoneFlag) continue;
            bool hitEnemy = b.HitType switch
            {
                HitType.All    => TryHitEnemy(b, enemies, damageableAtPassStart, e => true, result),
                HitType.Air    => TryHitEnemy(b, enemies, damageableAtPassStart, e => !e.IsGround, result),
                HitType.Ground => TryHitEnemy(b, enemies, damageableAtPassStart, e => e.IsGround, result),
                HitType.GrAll  => TryHitEnemy(b, enemies, damageableAtPassStart, e => true, result),
                HitType.GTile  => false,
                HitType.Suck   => TryHitEnemy(b, enemies, damageableAtPassStart, e => true, result),
                _              => false,
            };
            if (hitEnemy && UsesRandomSparkColor(b.HitType))
            {
                result.RandomSparkColorCount++;
                result.RandomSparkPositions.Add((b.X, b.Y));
            }
            else if (hitEnemy && b.HitType == HitType.Ground)
            {
                result.OrangeSparkPositions.Add((b.X, b.Y));
            }
            else if (hitEnemy && b.HitType == HitType.Suck)
            {
                result.BlueSparkPositions.Add((b.X, b.Y));
            }

            TileDamageDispatcher.DamageResult tileHit = default;
            bool checkTile = false;
            if (b.HitType == HitType.All && !hitEnemy)
            {
                tileHit = TileDamageDispatcher.TileIsHit(tiles, b.X, b.Y, b.Damage);
                checkTile = true;
            }
            else if (b.HitType == HitType.Ground && !hitEnemy)
            {
                tileHit = TileDamageDispatcher.TileIsHit(tiles, b.X, b.Y, b.Damage);
                checkTile = true;
            }
            else if (b.HitType == HitType.GTile)
            {
                tileHit = TileDamageDispatcher.TileBomb(tiles, b.X, b.Y, b.Damage, mapCols);
                checkTile = true;
                TryHitEnemy(b, enemies, damageableAtPassStart, e => e.IsGround, result);
            }

            if (!checkTile || !tileHit.Hit) continue;
            result.TileHit = true;
            if (b.HitType != HitType.GTile)
            {
                result.RandomSparkColorCount++;
                result.RandomSparkPositions.Add((b.X, b.Y));
            }
            string reason = b.HitType switch
            {
                HitType.All => "tile_all",
                HitType.Ground => "tile_ground",
                HitType.GTile => "tile_bomb",
                _ => "tile",
            };
            int tileHits = tileHit.HitIndex >= 0 && tileHit.HitIndex < tiles.Count
                ? tiles[tileHit.HitIndex].Hits
                : 0;
            BulletDumper.RecordPlayerRemove(reason, b, tileHit.HitIndex, tileHits);
            if (b.HitType != HitType.GTile) b.Kill();
            // No award here — TILE_Think (TileThinkAwardScan) is the sole award site.
        }
        return result;
    }

    private static bool TryHitEnemy(BulletLogic b,
                                    IList<EnemyLogic> enemies,
                                    HashSet<EnemyLogic> damageableAtPassStart,
                                    System.Func<EnemyLogic, bool> hitTypeMatches,
                                    Result result)
    {
        foreach (var e in enemies)
        {
            if (!damageableAtPassStart.Contains(e)) continue;
            if (!IsVisibleForDamage(e)) continue;
            if (!hitTypeMatches(e)) continue;
            if (!e.ContainsPointStrict(b.X, b.Y)) continue;

            b.MarkDoneFlagForNextPass();
            TraceHit(b, e);
            e.TakeDamage(b.Damage, deferRemovalForDump: true);
            result.HitEnemies.Add(new EnemyHit(e, b.Damage));
            return true;
        }
        return false;
    }

    private static bool IsVisibleForDamage(EnemyLogic e)
    {
        return e.Y + e.Meta.Height > 0 && e.Y < 200
            && e.X + e.Meta.Width > 0 && e.X < 320;
    }

    private static bool UsesRandomSparkColor(HitType hitType) =>
        hitType == HitType.All || hitType == HitType.Air || hitType == HitType.GrAll;

    private static void TraceHit(BulletLogic b, EnemyLogic e)
    {
        var trace = HitTrace.Value;
        if (trace == null) return;
        trace.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "i={0} {1} x={2} y={3} damage={4} enemy_x={5} enemy_y={6} enemy_x2={7} enemy_y2={8} before={9} after={10}",
            TraceIter, b.HitType.ToString().ToLowerInvariant(), b.X, b.Y, b.Damage,
            e.X, e.Y, e.X + e.Meta.Width - 1, e.Y + e.Meta.Height - 1,
            e.Hits, e.Hits - b.Damage));
        trace.Flush();
    }

    private static StreamWriter? OpenHitTrace()
    {
        string? path = Environment.GetEnvironmentVariable("RAPTOR_HIT_TRACE");
        if (string.IsNullOrWhiteSpace(path)) return null;
        return new StreamWriter(path) { AutoFlush = true };
    }
}
