using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Xunit;

namespace Raptor.Tests;

public class EnemyLogicTests
{
    private static SpriteMeta SyntheticPath(params (int x, int y)[] waypoints)
    {
        var fx = new int[waypoints.Length];
        var fy = new int[waypoints.Length];
        for (int i = 0; i < waypoints.Length; i++) { fx[i] = waypoints[i].x; fy[i] = waypoints[i].y; }
        return new SpriteMeta
        {
            Hits      = 3,
            NumFlight = waypoints.Length,
            FlightType = 1,   // LINEAR
            FlightX   = fx,
            FlightY   = fy,
            NumGuns   = 0,
        };
    }

    [Fact]
    public void Take_damage_kills_after_enough_hits()
    {
        var meta = SyntheticPath((50, 50));
        meta.Hits = 3;
        var e = new EnemyLogic(meta, 0, 0);
        e.TakeDamage(1); Assert.True(e.Alive);
        e.TakeDamage(1); Assert.True(e.Alive);
        e.TakeDamage(1); Assert.False(e.Alive);
        Assert.False(e.PendingRemovalDump);
    }

    [Fact]
    public void Damage_preserves_negative_hits_like_C()
    {
        var meta = SyntheticPath((50, 50));
        meta.Hits = 1;
        var e = new EnemyLogic(meta, 0, 0);

        e.TakeDamage(2);

        Assert.Equal(-1, e.Hits);
        Assert.False(e.Alive);
    }

    [Fact]
    public void Multiple_same_pass_hits_can_overkill_enemy_like_C()
    {
        var meta = SyntheticPath((50, 50));
        meta.Hits = 1;
        var e = new EnemyLogic(meta, 0, 0);

        e.TakeDamage(1, deferRemovalForDump: true);
        e.TakeDamage(1, deferRemovalForDump: true);

        Assert.Equal(-1, e.Hits);
        Assert.False(e.Alive);
        Assert.True(e.PendingRemovalDump);
    }

    [Fact]
    public void Pending_removal_enemy_still_thinks_and_can_fire_once_like_C()
    {
        var meta = SyntheticPath((0, 0));
        meta.Hits = 1;
        meta.NumGuns = 2;
        meta.ShootFrame = 20;
        meta.ShootCnt = 1;
        meta.ShootStart = -1;
        meta.ShotSpace = 0;
        meta.ShootX = new[] { 13, 19 };
        meta.ShootY = new[] { 20, 20 };
        meta.ShootType = new[] { 1, 1 };
        var e = new EnemyLogic(meta, 176, 100);
        e.TakeDamage(1, deferRemovalForDump: true);

        var first = e.Tick();

        Assert.NotNull(first);
        Assert.NotNull(e.ExtraBulletsThisTick);
        Assert.Equal(2, 1 + e.ExtraBulletsThisTick!.Count);
    }

    [Fact]
    public void Deferred_shot_damage_keeps_dead_enemy_for_one_dump()
    {
        var meta = SyntheticPath((50, 50));
        meta.Hits = 1;
        var e = new EnemyLogic(meta, 0, 0);

        e.TakeDamage(1, deferRemovalForDump: true);

        Assert.False(e.Alive);
        Assert.True(e.PendingRemovalDump);
        e.ClearPendingRemovalDump();
        Assert.False(e.PendingRemovalDump);
    }

    [Fact]
    public void Shoot_countdown_matches_C_ENEMY_Add_formula()
    {
        // ENEMY.C:408: new->countdown = lib->countdown + (-new->move.y)
        // where new->move.y is the post-correction screen Y (mapY in our pipeline).
        // For a sprite with lib->countdown=50 spawned at mapY=-148 the countdown
        // initial value is 50 - (-148) = 198.
        Assert.Equal(198, EnemyLogic.InitialShootCountdown(50, -148));
        // mapY = 0 (on the top edge) → countdown = lib->countdown.
        Assert.Equal(10, EnemyLogic.InitialShootCountdown(10, 0));
        // mapY > 0 (already on-screen) → countdown shorter than lib->countdown.
        Assert.Equal(40, EnemyLogic.InitialShootCountdown(100, 60));
    }

    [Fact]
    public void Enemy_with_zero_guns_never_fires()
    {
        var meta = SyntheticPath((50, 50), (60, 60), (70, 70));
        meta.NumGuns = 0;
        var e = new EnemyLogic(meta, 0, 0);
        for (int i = 0; i < 10; i++) Assert.Null(e.Tick());
    }

    [Fact]
    public void ContainsPointStrict_uses_C_top_left_bbox_not_center_distance()
    {
        // ENEMY.C damage predicates use strict top-left bounds:
        //   x > sprite->x && x < sprite->x2 && y > sprite->y && y < sprite->y2
        // A point left of sprite->x must not hit even if it is within half-width
        // distance of the top-left corner. This is the mission_fight iter-160
        // false positive that prematurely removed the x=162 player bullet.
        var e = new EnemyLogic(new SpriteMeta
        {
            Hits = 7,
            NumFlight = 0,
            FlightType = 1,
            Width = 32,
            Height = 24,
        }, spawnX: 176, mapY: 140);

        Assert.False(e.ContainsPointStrict(162, 142));
        Assert.False(e.ContainsPointStrict(176, 142)); // strict left edge
        Assert.True(e.ContainsPointStrict(177, 142));
        Assert.True(e.ContainsPointStrict(206, 162));
        Assert.False(e.ContainsPointStrict(207, 162)); // strict right edge (x + width - 1)
    }

    [Fact]
    public void Library_loads_real_sprite_meta_file()
    {
        // Validates that the actual extracted SPRITE1_ITM.json parses with the
        // JsonPropertyName mapping. If field names differ, this test surfaces it.
        // The path walks from tests/bin/Debug/net8.0/ up four levels to the repo root.
        var path = System.IO.Path.Combine(
            System.AppContext.BaseDirectory,
            "..", "..", "..", "..", "assets", "sprites_meta", "SPRITE1_ITM.json");
        if (!System.IO.File.Exists(path))
        {
            // Allow the test to skip on machines without the asset bundle.
            return;
        }
        var lib = SpriteMetaLibrary.LoadFromFile(path);
        Assert.True(lib.Count > 0);
        var m = lib.Get(0);
        Assert.True(m.Hits >= 0);
        Assert.True(m.FlightX.Length >= 0);
    }

    private static SpriteMeta Ground(int flightType, int width = 32, int height = 24, int movespeed = 1, int hits = 20) =>
        new SpriteMeta
        {
            IName = "BONUS1G1_PIC",
            Hits = hits,
            NumFlight = 0,
            FlightType = flightType,
            MoveSpeed = movespeed,
            Width = width,
            Height = height,
        };

    // Regression: F_GROUND (FlightType=3) enemies must walk down 1 px/tick.
    // ENEMY.C:458-462 sets move.y2=211, and ENEMY.C:940-947 advances y when
    // scroll_flag is true and sets doneflag when y > 211. Prior to this fix
    // F_GROUND enemies (including BONUS pickups and SHIP20G1 helicopters)
    // were getting Done=true on the first tick and culled by PhaseCleanup,
    // so they never appeared in Godot's parity stream.
    [Fact]
    public void F_ground_enemy_walks_down_one_pixel_per_tick()
    {
        var e = new EnemyLogic(Ground(flightType: 3), spawnX: 144, mapY: -128);
        Assert.True(e.Alive);
        Assert.Equal((144, -128), (e.X, e.Y));
        e.Tick();
        Assert.Equal((144, -127), (e.X, e.Y));
        for (int i = 0; i < 10; i++) e.Tick();
        Assert.Equal((144, -117), (e.X, e.Y));
        Assert.True(e.Alive);
    }

    // Regression: F_GROUND enemy must Done out once y > 211 (move.y2).
    [Fact]
    public void F_ground_enemy_dies_when_y_passes_211()
    {
        var e = new EnemyLogic(Ground(flightType: 3), spawnX: 100, mapY: 200);
        for (int i = 0; i < 12; i++) e.Tick();
        Assert.True(e.Done);
        Assert.False(e.Alive);
    }

    // Regression: F_GROUNDRIGHT (FlightType=5) must shift the initial x by
    // -width at spawn (ENEMY.C:464-470: new->x -= new->width). Without this
    // SHIP20G1 spawned at x=-8 instead of x=-88, off-by-80px from C.
    [Fact]
    public void F_groundright_enemy_spawns_shifted_left_by_width()
    {
        var e = new EnemyLogic(Ground(flightType: 5, width: 80), spawnX: -8, mapY: -131);
        // Initial position reflects the C `new->x -= new->width` shift: -8 - 80 = -88.
        Assert.Equal(-88, e.X);
        Assert.Equal(-131, e.Y);
    }

    // IsGround classifies F_GROUND family (FlightType 3/4/5). Used by player-
    // bullet collision to honor SHOTS.C ht filters (S_AIR / S_GROUND).
    [Fact]
    public void IsGround_is_true_for_F_GROUND_family()
    {
        Assert.True (new EnemyLogic(Ground(flightType: 3), 0, 0).IsGround);
        Assert.True (new EnemyLogic(Ground(flightType: 4), 0, 0).IsGround);
        Assert.True (new EnemyLogic(Ground(flightType: 5), 0, 0).IsGround);
    }

    [Fact]
    public void IsGround_is_false_for_air_flight_types()
    {
        var meta = SyntheticPath((100, 0));
        meta.FlightType = 1;  // LINEAR (air)
        Assert.False(new EnemyLogic(meta, 0, 0).IsGround);
        meta.FlightType = 0;  // REPEAT
        Assert.False(new EnemyLogic(meta, 0, 0).IsGround);
        meta.FlightType = 2;  // SINGLE? whichever
        Assert.False(new EnemyLogic(meta, 0, 0).IsGround);
    }

    // F_KAMI tests — SHIP34G1_PIC orbs in level 1 are F_KAMI enemies that
    // walk above-screen waypoints, then chase the player, then fly past.
    // C ENEMY.C:904-957. Bug regression: stubbing F_KAMI to LINEAR caused
    // them to terminate after the off-screen waypoints, so they never
    // entered the visible play area (only their projected shadows showed).
    [Fact]
    public void Kami_enemy_is_not_done_after_completing_waypoints()
    {
        var meta = new SpriteMeta
        {
            Hits = 10,
            NumFlight = 2,
            FlightType = 2,    // F_KAMI
            FlightX = new[] { 0, 0 },
            FlightY = new[] { 10, 20 },
            MoveSpeed = 2,
            Width = 16, Height = 16,
        };
        var e = new EnemyLogic(meta, spawnX: 160, mapY: 80);

        for (int i = 0; i < 200 && !e.Done; i++)
            e.Tick(playerX: 160, playerY: 150);

        // LINEAR would Done. KAMI must transition to chase and keep going.
        Assert.False(e.Done);
    }

    [Fact]
    public void Kami_enemy_chases_toward_player_after_waypoints()
    {
        var meta = new SpriteMeta
        {
            Hits = 10,
            NumFlight = 1,
            FlightType = 2,
            FlightX = new[] { 0 },
            FlightY = new[] { 5 },     // tiny waypoint — chase fires fast
            MoveSpeed = 2,
            Width = 16, Height = 16,
        };
        var e = new EnemyLogic(meta, spawnX: 100, mapY: 50);

        // Run until clearly past waypoints (~30 ticks moves only 60px).
        for (int i = 0; i < 60; i++)
            e.Tick(playerX: 200, playerY: 180);

        // Without chase, the enemy stops at the waypoint (~100, sy+5) and
        // does nothing. With chase, X must trend toward player_x=200.
        Assert.True(e.X > 110,
            $"Expected enemy to chase toward player (x>110), got X={e.X} Y={e.Y}");
    }

    [Fact]
    public void Kami_enemy_terminates_only_when_off_screen()
    {
        var meta = new SpriteMeta
        {
            Hits = 10,
            NumFlight = 1,
            FlightType = 2,
            FlightX = new[] { 0 },
            FlightY = new[] { 5 },
            MoveSpeed = 2,
            Width = 16, Height = 16,
        };
        var e = new EnemyLogic(meta, spawnX: 100, mapY: 50);

        for (int i = 0; i < 1000 && !e.Done; i++)
            e.Tick(playerX: 400, playerY: 400);   // player off-screen lower-right

        Assert.True(e.Done, "KAMI should eventually terminate by going off-screen");
        // X/Y reflect the PRE-move position from the last tick (mirrors C's
        // sprite->x/y vs move.x/y semantics — the off-screen test uses POST-move
        // move.y). So display position is "near" the boundary at termination.
        bool nearOffScreen = e.X > 200 || e.X + meta.Width < 5
                           || e.Y > 195 || e.Y + meta.Width < 5;
        Assert.True(nearOffScreen,
            $"KAMI should be heading off-screen at termination, X={e.X} Y={e.Y}");
    }

    // Regression: F_GROUNDRIGHT enemy only slides right after y reaches 0
    // (ENEMY.C:952: `if (sprite->y >= 0)`). Above the screen it just falls.
    [Fact]
    public void F_groundright_enemy_slides_right_only_after_y_reaches_zero()
    {
        var e = new EnemyLogic(Ground(flightType: 5, width: 80, movespeed: 3),
            spawnX: 0, mapY: -3);
        // Initial: x = 0 - 80 = -80, y = -3.
        Assert.Equal((-80, -3), (e.X, e.Y));
        e.Tick();  // y becomes -2 (still <0, no x slide)
        Assert.Equal((-80, -2), (e.X, e.Y));
        e.Tick();  // y becomes -1 (still <0, no x slide)
        Assert.Equal((-80, -1), (e.X, e.Y));
        e.Tick();  // y becomes 0, AND y >= 0 so x slides by movespeed=3
        Assert.Equal((-77, 0), (e.X, e.Y));
        e.Tick();  // y becomes 1, x slides by 3 → -74
        Assert.Equal((-74, 1), (e.X, e.Y));
    }
}
