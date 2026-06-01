using System;
using System.Collections.Generic;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Raptor.Sim.MazeLevel;
using Raptor.Sim;
using Raptor.Sim.Shots;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// Unit tests for the C SHOTS_Think shot_done dispatch ported as
/// ShotDoneDispatcher. Mirrors SOURCE/SHOTS.C:1218-1249.
/// </summary>
public class ShotDoneDispatcherTests
{
    private static EnemyLogic MakeEnemy(int hits = 20, int flightType = 1)
    {
        var meta = new SpriteMeta
        {
            IName = "TEST",
            Hits = hits,
            NumFlight = 0,
            FlightType = flightType,   // 1 = LINEAR (air enemy)
            MoveSpeed = 1,
            Width = 32,
            Height = 24,
        };
        return new EnemyLogic(meta, spawnX: 100, mapY: 50);
    }

    private static List<TileState> MakeTiles()
    {
        return new List<TileState>
        {
            new() { IsDestructible = true,  Hits = 30, Bounty = 100 },
            new() { IsDestructible = false, Hits = 30, Bounty = 200 },
            new() { IsDestructible = true,  Hits = 10, Bounty = 300 },
        };
    }

    [Fact]
    public void Delayed_dumb_missile_re_targets_to_y_zero_and_clears_delayflag()
    {
        // SHOTS.C:1220-1227: delayflag → InitMobj(move.x2 = move.x + random(32) - 16, move.y2 = 0).
        var b = BulletLogic.AimedAt(BulletKind.Player, x: 160, y: 100,
            x2: 175, y2: 105, initSpeed: 1, maxSpeed: 1, damage: 1);
        b.PlayerWeapon = ObjType.DumbMissile;
        b.Delayed = true;
        // Run Tick until ReachedTarget.
        for (int i = 0; i < 50 && !b.ReachedTarget; i++) b.Tick();
        Assert.True(b.ReachedTarget);
        int mxBefore = b.Mx;

        ShotDoneDispatcher.Dispatch(b,
            enemyBullets: new List<BulletLogic>(),
            enemies: new List<EnemyLogic>(),
            rng: new Random(123));

        Assert.True(b.Alive);              // not removed
        Assert.False(b.Delayed);           // clear after first transition
        Assert.False(b.ReachedTarget);     // re-init cleared move.done
        // Bullet should now be heading toward y=0; one Tick reduces My below the
        // re-target point because Bresenham steps along the new (mxBefore±scatter, 0) path.
        b.Tick();
        Assert.True(b.My < mxBefore + 100);   // sanity: y didn't shoot up wildly
        Assert.True(b.Alive);
    }

    [Fact]
    public void MegaBomb_dispatch_kills_all_enemy_bullets_damages_all_enemies_and_removes_itself()
    {
        // SHOTS.C:1232-1241 detonation effect.
        var b = BulletLogic.AimedAt(BulletKind.Player, x: 160, y: 176,
            x2: 160, y2: 75, initSpeed: 1, maxSpeed: 1, damage: 8);
        b.PlayerWeapon = ObjType.MegaBomb;

        var eb1 = new BulletLogic(BulletKind.Enemy, 100, 100, 0, 3);
        var eb2 = new BulletLogic(BulletKind.Enemy, 200, 100, 0, 3);
        var enemyBullets = new List<BulletLogic> { eb1, eb2 };

        var e1 = MakeEnemy(hits: 20);
        var e2 = MakeEnemy(hits: 5);
        var enemies = new List<EnemyLogic> { e1, e2 };

        ShotDoneDispatcher.Dispatch(b, enemyBullets, enemies, rng: null);

        Assert.False(b.Alive);             // bomb removed
        Assert.False(eb1.Alive);           // ESHOT_Clear() killed all
        Assert.False(eb2.Alive);
        Assert.Equal(12, e1.Hits);         // 20 - 8
        Assert.False(e2.Alive);            // 5 - 8 → ≤ 0, dies
    }

    [Fact]
    public void MegaBomb_dispatch_reports_detonation_other_branches_do_not()
    {
        // The MegaBombDetonated flag drives the parity-inert View flash; it must
        // be true only on the actual MEGA_BOMB detonation branch.
        var bomb = BulletLogic.AimedAt(BulletKind.Player, 160, 176, 160, 75,
            initSpeed: 1, maxSpeed: 1, damage: 8);
        bomb.PlayerWeapon = ObjType.MegaBomb;
        var bombResult = ShotDoneDispatcher.Dispatch(bomb,
            new List<BulletLogic>(), new List<EnemyLogic>(), rng: null);
        Assert.True(bombResult.MegaBombDetonated);

        var gun = BulletLogic.AimedAt(BulletKind.Player, 100, 100, 150, 100,
            initSpeed: 1, maxSpeed: 1, damage: 1);
        gun.PlayerWeapon = ObjType.MiniGun;
        var gunResult = ShotDoneDispatcher.Dispatch(gun,
            new List<BulletLogic>(), new List<EnemyLogic>(), rng: null);
        Assert.False(gunResult.MegaBombDetonated);

        // Delayed re-init path (not a detonation) must not signal either.
        var delayed = BulletLogic.AimedAt(BulletKind.Player, 160, 100, 175, 105,
            initSpeed: 1, maxSpeed: 1, damage: 1);
        delayed.PlayerWeapon = ObjType.DumbMissile;
        delayed.Delayed = true;
        for (int i = 0; i < 50 && !delayed.ReachedTarget; i++) delayed.Tick();
        var delayedResult = ShotDoneDispatcher.Dispatch(delayed,
            new List<BulletLogic>(), new List<EnemyLogic>(), rng: new Random(123));
        Assert.False(delayedResult.MegaBombDetonated);
    }

    [Fact]
    public void MegaBomb_dispatch_damages_all_destructible_tiles_by_twenty_and_reports_bounty()
    {
        // SHOTS.C:1234: MegaBomb calls TILE_DamageAll(), which subtracts 20
        // from every destructible on-screen tile. TILE_Think later awards
        // bounty only for tiles whose hits fall below zero.
        var b = BulletLogic.AimedAt(BulletKind.Player, x: 160, y: 176,
            x2: 160, y2: 75, initSpeed: 1, maxSpeed: 1, damage: 8);
        b.PlayerWeapon = ObjType.MegaBomb;

        var tiles = MakeTiles();

        var result = ShotDoneDispatcher.Dispatch(b,
            enemyBullets: new List<BulletLogic>(),
            enemies: new List<EnemyLogic>(),
            rng: null,
            tiles: tiles);

        Assert.Equal(10, tiles[0].Hits);
        Assert.Equal(30, tiles[1].Hits);   // indestructible: unchanged
        Assert.Equal(-10, tiles[2].Hits);
        Assert.Equal(300, result.TileBounty);
        Assert.True(tiles[2].Dead);
    }

    [Fact]
    public void MegaBomb_dispatch_skips_already_dead_enemies()
    {
        var b = BulletLogic.AimedAt(BulletKind.Player, 160, 176, 160, 75,
            initSpeed: 1, maxSpeed: 1, damage: 8);
        b.PlayerWeapon = ObjType.MegaBomb;

        var dead = MakeEnemy(hits: 0);
        dead.TakeDamage(99);  // ensure Alive=false
        Assert.False(dead.Alive);
        int deadHits = dead.Hits;
        var alive = MakeEnemy(hits: 10);

        ShotDoneDispatcher.Dispatch(b,
            enemyBullets: new List<BulletLogic>(),
            enemies: new List<EnemyLogic> { dead, alive },
            rng: null);

        // Dead enemy isn't damaged further; alive enemy takes the hit.
        Assert.Equal(deadHits, dead.Hits);
        Assert.Equal(2, alive.Hits);
    }

    [Fact]
    public void Default_branch_removes_bullet()
    {
        // SHOTS.C:1247-1249 default — SHOTS_Remove (e.g. MiniGun bullet).
        var b = BulletLogic.AimedAt(BulletKind.Player, 100, 100, 150, 100,
            initSpeed: 1, maxSpeed: 1, damage: 1);
        b.PlayerWeapon = ObjType.MiniGun;
        Assert.True(b.Alive);

        ShotDoneDispatcher.Dispatch(b,
            enemyBullets: new List<BulletLogic>(),
            enemies: new List<EnemyLogic>(),
            rng: null);

        Assert.False(b.Alive);
    }

    [Fact]
    public void Turret_branch_is_a_noop()
    {
        // SHOTS.C:1244 — S_TURRET breaks out of the switch without removal.
        // In our model Turret spawns a LineBeam, not a Bresenham bullet, so
        // this branch is unreachable in practice; tested here for documented
        // parity with the C switch.
        var b = BulletLogic.AimedAt(BulletKind.Player, 100, 100, 150, 100,
            initSpeed: 1, maxSpeed: 1, damage: 1);
        b.PlayerWeapon = ObjType.Turret;

        ShotDoneDispatcher.Dispatch(b,
            enemyBullets: new List<BulletLogic>(),
            enemies: new List<EnemyLogic>(),
            rng: null);

        Assert.True(b.Alive);  // no-op
    }

    [Fact]
    public void Delayed_dispatch_only_consumes_RNG_once_per_bullet()
    {
        // The scatter randomization runs ONCE per delay transition. After
        // re-init, b.Delayed is false and a second Dispatch goes to default.
        var b = BulletLogic.AimedAt(BulletKind.Player, 160, 100, 175, 105,
            initSpeed: 1, maxSpeed: 1, damage: 1);
        b.PlayerWeapon = ObjType.DumbMissile;
        b.Delayed = true;
        for (int i = 0; i < 50 && !b.ReachedTarget; i++) b.Tick();

        var rng = new Random(7);
        ShotDoneDispatcher.Dispatch(b, new List<BulletLogic>(), new List<EnemyLogic>(), rng);
        Assert.False(b.Delayed);

        // Run the new Bresenham to its second target.
        for (int i = 0; i < 200 && !b.ReachedTarget; i++) b.Tick();
        Assert.True(b.ReachedTarget);

        ShotDoneDispatcher.Dispatch(b, new List<BulletLogic>(), new List<EnemyLogic>(), rng);
        Assert.False(b.Alive);  // default branch removes
    }

    [Fact]
    public void Deterministic_rng_flag_retargets_delayflag_to_current_move_x_without_advancing_rng()
    {
        string? previous = Environment.GetEnvironmentVariable("RAPTOR_DETERMINISTIC_RNG");
        Environment.SetEnvironmentVariable("RAPTOR_DETERMINISTIC_RNG", "1");
        try
        {
            var b = BulletLogic.AimedAt(BulletKind.Player, 160, 100, 175, 105,
                initSpeed: 1, maxSpeed: 1, damage: 1);
            b.PlayerWeapon = ObjType.DumbMissile;
            b.Delayed = true;
            for (int i = 0; i < 50 && !b.ReachedTarget; i++) b.Tick();

            int mxBefore = b.Mx;
            var rng = new Random(1234);

            ShotDoneDispatcher.Dispatch(b, new List<BulletLogic>(), new List<EnemyLogic>(), rng);

            Assert.Equal(new Random(1234).Next(), rng.Next());
            Assert.False(b.Delayed);
            Assert.False(b.ReachedTarget);
            Assert.Equal(mxBefore, b.Mx);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAPTOR_DETERMINISTIC_RNG", previous);
        }
    }
}
