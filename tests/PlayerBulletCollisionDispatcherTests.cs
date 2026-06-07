using System.Collections.Generic;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Raptor.Sim.MazeLevel;
using Raptor.Sim.Shots;
using Xunit;

namespace Raptor.Tests;

public class PlayerBulletCollisionDispatcherTests
{
    private static EnemyLogic EnemyAt(int x, int y, int hits = 10, int flightType = 1)
    {
        return new EnemyLogic(new SpriteMeta
        {
            Hits = hits,
            NumFlight = 0,
            FlightType = flightType,
            Width = 32,
            Height = 24,
        }, x, y);
    }

    /// <summary>
    /// The on-screen slice is always MAP_ONSCREEN*MAP_COLS=72 tiles, and C's
    /// TILE_IsHit/TILE_Bomb (TILE.C:500,543 `while ( ts != lastspot )`) never
    /// hit-test the final slot (slot 71). These unit fixtures place the tile
    /// under test at slot 0 and append a never-hit sentinel that stands in for
    /// the excluded last slot, so the bound (finding #14) doesn't swallow the
    /// real tile. The sentinel is positioned off any bullet box.
    /// </summary>
    private static List<TileState> SliceWithTargetThenSkippedLast(TileState target)
        => new() { target, new TileState { ScreenX = 10000, ScreenY = 10000 } };

    [Fact]
    public void Enemy_hit_suppresses_same_pass_tile_hit_for_all_bullets()
    {
        // SHOTS.C handles S_ALL as:
        //   if (ENEMY_DamageAll(...)) doneflag = TRUE;
        //   else if (TILE_IsHit(...)) move.done = TRUE;
        // A bullet cannot damage an enemy and a tile in the same pass.
        var bullet = new BulletLogic(BulletKind.Player, x: 110, y: 60, velX: 0, velY: 0, damage: 1)
        {
            HitType = HitType.All,
        };
        var enemy = EnemyAt(100, 50);
        var tile = new TileState
        {
            ScreenX = 96,
            ScreenY = 48,
            IsDestructible = true,
            Hits = 5,
            Bounty = 0,
        };

        var result = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { bullet },
            new List<EnemyLogic> { enemy },
            new List<TileState> { tile },
            mapCols: 9);

        Assert.Single(result.HitEnemies);
        Assert.Same(enemy, result.HitEnemies[0].Enemy);
        Assert.True(bullet.DeferredDoneFlag);
        Assert.True(bullet.Alive);
        Assert.Equal(9, enemy.Hits);
        Assert.Equal(5, tile.Hits);
        Assert.Equal(1, result.RandomSparkColorCount);
        Assert.Single(result.RandomSparkPositions);
        Assert.Equal((110, 60), result.RandomSparkPositions[0]);
    }

    [Fact]
    public void Enemy_hits_apply_immediately_and_pending_dead_enemy_can_still_absorb_same_pass_hits()
    {
        // ENEMY_DamageAll mutates cur->hits immediately and does not remove
        // the sprite from onscreen[] until the next ENEMY_Think pass. Later
        // bullets in the same SHOTS_Think pass can therefore keep hitting the
        // same dead/pending sprite and drive hits negative.
        var first = new BulletLogic(BulletKind.Player, x: 110, y: 60, velX: 0, velY: 0, damage: 1)
        {
            HitType = HitType.All,
        };
        var second = new BulletLogic(BulletKind.Player, x: 111, y: 61, velX: 0, velY: 0, damage: 1)
        {
            HitType = HitType.All,
        };
        var enemy = EnemyAt(100, 50, hits: 1);

        var result = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { first, second },
            new List<EnemyLogic> { enemy },
            new List<TileState>(),
            mapCols: 9);

        Assert.Equal(2, result.HitEnemies.Count);
        Assert.Equal(-1, enemy.Hits);
        Assert.True(enemy.PendingRemovalDump);
    }

    [Fact]
    public void Pending_dead_enemy_from_prior_pass_is_not_damageable_again()
    {
        // C keeps a just-dead enemy around long enough for the next
        // ENEMY_Think/display dump, but the next SHOTS_Think builds its
        // damageable onscreen[] list from live sprites. Overkill is only for
        // bullets later in the same SHOTS_Think pass, not later passes.
        var bullet = new BulletLogic(BulletKind.Player, x: 110, y: 60, velX: 0, velY: 0, damage: 1)
        {
            HitType = HitType.All,
        };
        var enemy = EnemyAt(100, 50, hits: 1);
        enemy.TakeDamage(1, deferRemovalForDump: true);

        var result = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { bullet },
            new List<EnemyLogic> { enemy },
            new List<TileState>(),
            mapCols: 9);

        Assert.Empty(result.HitEnemies);
        Assert.False(bullet.DeferredDoneFlag);
        Assert.Equal(0, enemy.Hits);
        Assert.True(enemy.PendingRemovalDump);
    }

    [Fact]
    public void Same_collision_pass_bullets_both_absorb_and_decrement_without_killing()
    {
        // C's TILE_IsHit does not consult tdead and only decrements. If two shots
        // hit the same destructible tile in one SHOTS_Think pass, both are absorbed
        // and both decrement hits; the tile is not marked dead and no bounty is
        // awarded here — the next-iter TileThinkAwardScan does that (finding #1).
        var first = new BulletLogic(BulletKind.Player, x: 110, y: 60, velX: 0, velY: 0, damage: 20)
        {
            HitType = HitType.Ground,
        };
        var second = new BulletLogic(BulletKind.Player, x: 111, y: 61, velX: 0, velY: 0, damage: 20)
        {
            HitType = HitType.Ground,
        };
        var tile = new TileState
        {
            ScreenX = 96,
            ScreenY = 48,
            IsDestructible = true,
            Hits = 1,
            Bounty = 250,
        };

        var result = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { first, second },
            new List<EnemyLogic>(),
            SliceWithTargetThenSkippedLast(tile),
            mapCols: 9);

        Assert.False(first.Alive);
        Assert.False(second.Alive);
        Assert.False(tile.Dead);          // not killed at hit time
        Assert.Equal(-39, tile.Hits);     // both shots decremented (1 - 20 - 20)
    }

    [Fact]
    public void Ground_tile_hit_records_random_spark_position()
    {
        var bullet = new BulletLogic(BulletKind.Player, x: 110, y: 60, velX: 0, velY: 0, damage: 2)
        {
            HitType = HitType.Ground,
        };
        var tile = new TileState
        {
            ScreenX = 96,
            ScreenY = 48,
            IsDestructible = true,
            Hits = 10,
        };

        var result = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { bullet },
            new List<EnemyLogic>(),
            SliceWithTargetThenSkippedLast(tile),
            mapCols: 9);

        Assert.True(result.TileHit);
        Assert.Single(result.RandomSparkPositions);
        Assert.Equal((110, 60), result.RandomSparkPositions[0]);
    }


    [Theory]
    [InlineData(HitType.All, 1)]
    [InlineData(HitType.Air, 1)]
    [InlineData(HitType.GrAll, 1)]
    [InlineData(HitType.Ground, 0)]
    [InlineData(HitType.Suck, 0)]
    public void Enemy_hit_counts_c_random_spark_color_only_for_matching_hit_types(HitType hitType, int expectedCount)
    {
        var bullet = new BulletLogic(BulletKind.Player, x: 110, y: 60, velX: 0, velY: 0, damage: 1)
        {
            HitType = hitType,
        };
        var enemy = EnemyAt(100, 50, flightType: hitType == HitType.Ground ? 3 : 1);

        var result = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { bullet },
            new List<EnemyLogic> { enemy },
            new List<TileState>(),
            mapCols: 9);

        Assert.Single(result.HitEnemies);
        Assert.Equal(expectedCount, result.RandomSparkColorCount);
    }

    [Fact]
    public void Ground_enemy_hit_records_orange_spark_without_random_color()
    {
        var bullet = new BulletLogic(BulletKind.Player, x: 110, y: 60, velX: 0, velY: 0, damage: 1)
        {
            HitType = HitType.Ground,
        };
        var enemy = EnemyAt(100, 50, flightType: 3);

        var result = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { bullet },
            new List<EnemyLogic> { enemy },
            new List<TileState>(),
            mapCols: 9);

        Assert.Single(result.HitEnemies);
        Assert.Empty(result.RandomSparkPositions);
        Assert.Single(result.OrangeSparkPositions);
        Assert.Equal((110, 60), result.OrangeSparkPositions[0]);
    }

    [Fact]
    public void Offscreen_enemy_is_not_hit_by_player_bullet()
    {
        // C ENEMY_DamageAll/Air/Ground iterate `cur_visable`; offscreen
        // enemies are not damage candidates even if the point overlaps.
        var bullet = new BulletLogic(BulletKind.Player, x: 110, y: -15, velX: 0, velY: 0, damage: 1)
        {
            HitType = HitType.GrAll,
        };
        var enemy = EnemyAt(100, -40);

        var result = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { bullet },
            new List<EnemyLogic> { enemy },
            new List<TileState>(),
            mapCols: 9);

        Assert.Empty(result.HitEnemies);
        Assert.False(bullet.DeferredDoneFlag);
    }

    [Fact]
    public void Top_edge_flush_enemy_is_not_damage_visible_like_c_cur_visable()
    {
        // ENEMY.C only adds an enemy to onscreen[] when y + height > 0.
        // A sprite exactly flush with the top edge is not a damage candidate.
        var bullet = new BulletLogic(BulletKind.Player, x: 110, y: -12, velX: 0, velY: 0, damage: 1)
        {
            HitType = HitType.GrAll,
        };
        var enemy = EnemyAt(100, -24);

        var result = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { bullet },
            new List<EnemyLogic> { enemy },
            new List<TileState>(),
            mapCols: 9);

        Assert.Empty(result.HitEnemies);
        Assert.False(bullet.DeferredDoneFlag);
    }

    [Fact]
    public void Reached_player_aimed_bullet_can_snapshot_final_c_collision_position()
    {
        var bullet = BulletLogic.PlayerAimedAt(
            x: 180, y: 52,
            x2: 194, y2: 50,
            initSpeed: 8, maxSpeed: 10,
            hlx: 4, hly: 4,
            damage: 1);
        bullet.HitType = HitType.GrAll;
        while (!bullet.ReachedTarget)
            bullet.Tick();

        Assert.True(bullet.IsPlayerAimedBresenham);
        bullet.SnapshotForPendingShotDonePass();

        var enemy = EnemyAt(185, 39, hits: 1);
        var result = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { bullet },
            new List<EnemyLogic> { enemy },
            new List<TileState>(),
            mapCols: 9);

        Assert.Single(result.HitEnemies);
        Assert.Equal(0, enemy.Hits);
        Assert.True(bullet.DeferredDoneFlag);
    }

    private static BulletLogic WeaponBullet(HitType ht, int damage)
        => new BulletLogic(BulletKind.Player, x: 110, y: 60, velX: 0, velY: 0, damage: damage) { HitType = ht };

    [Fact]
    public void Gtile_bomb_damages_ground_enemy_by_hardcoded_five()
    {
        // Finding #21: SHOTS.C:1241 ENEMY_DamageGround(x, y, 5) — a GTILE bomb deals
        // a HARDCODED 5 to ground enemies (lib->hits=50 goes only to TILE_Bomb).
        var ground = EnemyAt(100, 50, hits: 20, flightType: 3);
        var r = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { WeaponBullet(HitType.GTile, 50) },
            new List<EnemyLogic> { ground }, new List<TileState>(), mapCols: 9,
            curPlayerDiff: 2);
        Assert.Equal(15, ground.Hits);   // 20 - 5 (NOT 20 - 50)
    }

    [Fact]
    public void Gtile_bomb_ground_damage_doubles_at_diff0()
    {
        // SHOTS.C:1241 routes through ENEMY_DamageGround, so the hardcoded 5 is
        // ALSO doubled at DIFF_0 (5 → 10).
        var ground = EnemyAt(100, 50, hits: 20, flightType: 3);
        var r = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { WeaponBullet(HitType.GTile, 50) },
            new List<EnemyLogic> { ground }, new List<TileState>(), mapCols: 9,
            curPlayerDiff: 0);
        Assert.Equal(10, ground.Hits);   // 20 - 5 - 5
    }

    [Fact]
    public void Suck_skips_ground_enemies()
    {
        // Finding #21: ENEMY_DamageEnergy `if (cur->groundflag) continue;` skips
        // ground enemies entirely.
        var ground = EnemyAt(100, 50, hits: 10, flightType: 3);
        var r = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { WeaponBullet(HitType.Suck, 3) },
            new List<EnemyLogic> { ground }, new List<TileState>(), mapCols: 9,
            curPlayerDiff: 2);
        Assert.Empty(r.HitEnemies);
        Assert.Equal(10, ground.Hits);
    }

    [Fact]
    public void Suck_decrements_air_enemy_by_exactly_one()
    {
        // Finding #21: ENEMY_DamageEnergy decrements `cur->hits--` (exactly 1), NOT
        // by lib->hits (=3, which only feeds the unported suckagain effect).
        var air = EnemyAt(100, 50, hits: 10, flightType: 1);
        var r = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { WeaponBullet(HitType.Suck, 3) },
            new List<EnemyLogic> { air }, new List<TileState>(), mapCols: 9,
            curPlayerDiff: 2);
        Assert.Single(r.HitEnemies);
        Assert.Equal(9, air.Hits);   // 10 - 1 (NOT 10 - 3)
    }

    [Theory]
    [InlineData(HitType.Air, 6)]      // 10 - 2 - 2 = 6 (doubled, ENEMY_DamageAir)
    [InlineData(HitType.Ground, 6)]   // 10 - 2 - 2 = 6 (doubled, ENEMY_DamageGround)
    [InlineData(HitType.All, 8)]      // 10 - 2 = 8 (NOT doubled, ENEMY_DamageAll)
    [InlineData(HitType.GrAll, 8)]    // 10 - 2 = 8 (NOT doubled)
    public void Diff0_doubles_air_and_ground_not_all(HitType ht, int expectedHits)
    {
        // Finding #22: at DIFF_0 (training) C ENEMY_DamageAir/Ground subtract damage
        // TWICE (ENEMY.C); ENEMY_DamageAll does NOT double.
        int flightType = ht == HitType.Ground ? 3 : 1;   // Ground needs a ground enemy
        var enemy = EnemyAt(100, 50, hits: 10, flightType: flightType);
        var r = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { WeaponBullet(ht, 2) },
            new List<EnemyLogic> { enemy }, new List<TileState>(), mapCols: 9,
            curPlayerDiff: 0);
        Assert.Equal(expectedHits, enemy.Hits);
    }

    [Fact]
    public void Diff2_does_not_double_air_or_ground()
    {
        // Guard: at the normal difficulty (DIFF_2) there is no doubling.
        var air = EnemyAt(100, 50, hits: 10, flightType: 1);
        var r = PlayerBulletCollisionDispatcher.Collect(
            new List<BulletLogic> { WeaponBullet(HitType.Air, 2) },
            new List<EnemyLogic> { air }, new List<TileState>(), mapCols: 9,
            curPlayerDiff: 2);
        Assert.Equal(8, air.Hits);   // 10 - 2 (single)
    }
}
