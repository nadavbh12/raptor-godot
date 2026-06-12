using System.Linq;
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
    public void Boss_is_nerfed_at_diff_1_half_hp_and_fewer_bursts_like_C()
    {
        // Finding #2: ENEMY.C:457-463 — at curplr_diff <= DIFF_1 a boss is nerfed at
        // spawn: hits -= hits>>1 (half HP) and shootcount -= shootcount>>2 (3/4 of the
        // bursts). The health-bar max stays lib->hits (ENEMY.C:1306 hits*100/lib->hits),
        // so MaxHits is UNCHANGED — the boss just starts at 50%. Without this, the wave1
        // (DIFF_1) boss SHIP10G1 had 300 HP instead of 150 and fired 12 bursts instead
        // of 9, so it survived the player's fire and the wave never cleared.
        var bossMeta = new SpriteMeta
        {
            Hits = 300, ShootCnt = 12, BossFlag = 1,
            NumFlight = 1, FlightType = 1, FlightX = new[] { 0 }, FlightY = new[] { 0 },
        };

        var rookie = new EnemyLogic(bossMeta, 100, 0, curPlayerDiff: 1);
        Assert.Equal(150, rookie.Hits);        // 300 - (300>>1)
        Assert.Equal(300, rookie.MaxHits);     // health bar still relative to lib->hits
        Assert.Equal(9, rookie.ShootCount);    // 12 - (12>>2)

        var veteran = new EnemyLogic(bossMeta, 100, 0, curPlayerDiff: 2);
        Assert.Equal(300, veteran.Hits);       // no nerf at DIFF_2+
        Assert.Equal(12, veteran.ShootCount);

        // Non-boss at DIFF_1: untouched.
        var grunt = new EnemyLogic(
            new SpriteMeta { Hits = 10, ShootCnt = 4, BossFlag = 0,
                             NumFlight = 1, FlightX = new[] { 0 }, FlightY = new[] { 0 } },
            100, 0, curPlayerDiff: 1);
        Assert.Equal(10, grunt.Hits);
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
    public void GanimShoot_enemy_delays_first_shot_by_the_shoot_animation_like_C()
    {
        // ENEMY.C:788-859 — a GANIM_SHOOT enemy (animtype=1) does NOT fire when its
        // countdown expires; it sets anim_on=TRUE (plays the shoot animation) and
        // shoot_on flips only after num_frames advance at the frame_rate cadence,
        // delaying the first shot by the animation length. A GANIM_NORM enemy fires
        // the instant the countdown expires. Verified against the C binary replaying
        // bench_20260612_150134: SHIP33G1 (animtype=1, num_frames=4, frame_rate=1)
        // fires ~8 iters after its countdown; the animtype-less port fired
        // immediately — the wave-5 ebullets@738 enemy-firing divergence.
        SpriteMeta Meta(int animType, int numFrames) => new SpriteMeta
        {
            Hits = 5, NumFlight = 1, FlightType = 1, FlightX = new[] { 0 }, FlightY = new[] { 0 },
            AnimType = animType, NumFrames = numFrames, FrameRate = 1, Rewind = 1,
            NumGuns = 1, ShootFrame = 10, ShootCnt = 1, ShootStart = 0, ShotSpace = 4,
            Countdown = 0, MoveSpeed = 2, ShootX = new[] { 0 }, ShootY = new[] { 0 }, ShootType = new[] { 4 },
        };
        int FirstFireTick(SpriteMeta m)
        {
            var e = new EnemyLogic(m, 100, 0);
            for (int t = 1; t <= 50; t++)
                if (e.Tick(144, 160) != null) return t;
            return -1;
        }

        int norm  = FirstFireTick(Meta(animType: 0, numFrames: 1));   // GANIM_NORM
        int shoot = FirstFireTick(Meta(animType: 1, numFrames: 4));   // GANIM_SHOOT

        Assert.True(norm >= 1, $"GANIM_NORM should fire (got {norm})");
        Assert.True(shoot > norm + 3,
            $"GANIM_SHOOT first shot ({shoot}) must lag GANIM_NORM ({norm}) by the anim length");
    }

    // Drives a single-gun enemy until it fires one bullet of the given
    // ES shoot_type, returning that bullet for inspection.
    private static BulletLogic FireOneBullet(int shootType, int enemyX, int enemyY,
                                             int playerX, int playerY)
    {
        var meta = SyntheticPath((0, 0));
        meta.Hits = 5;
        meta.NumGuns = 1;
        meta.ShootFrame = 20;
        meta.ShootCnt = 1;
        meta.ShootStart = -1;
        meta.ShotSpace = 0;
        meta.ShootX = new[] { 0 };
        meta.ShootY = new[] { 0 };
        meta.ShootType = new[] { shootType };
        var e = new EnemyLogic(meta, enemyX, enemyY);
        for (int i = 0; i < 200; i++)
        {
            var b = e.Tick(playerX, playerY);
            if (b != null) return b;
        }
        throw new Xunit.Sdk.XunitException("enemy never fired");
    }

    [Fact]
    public void Plasma_fires_straight_down_with_full_damage()
    {
        // ESHOT.C:395-403 — ES_PLASMA: cur->move.x -= xoff (x-only, no yoff),
        // move.x2 = move.x (vertical), move.y2 = 200, speed 8 -> lib 10, hits=15.
        // It must descend vertically, NOT home on the player like ES_ATPLAYER.
        var b = FireOneBullet(7, enemyX: 100, enemyY: 40, playerX: 200, playerY: 180);

        Assert.Equal(EnemyShotType.Plasma, b.ShotType);
        Assert.Equal(15, b.Damage);

        int startX = b.X;
        for (int i = 0; i < 6; i++) b.Tick();
        Assert.Equal(startX, b.X);   // vertical — no drift toward playerX=200
        Assert.True(b.Y > 40);        // descends
    }

    [Fact]
    public void Coconut_deals_one_damage_not_two()
    {
        // ESHOT.C:405-414 — ES_COCONUTS aims at player center (homing) like
        // ATPLAYER, but lib->hits = 1 (LIB_COCO). Trajectory is already correct
        // via the default branch; only the per-hit damage was wrong (was 2).
        var b = FireOneBullet(8, enemyX: 100, enemyY: 40, playerX: 200, playerY: 180);

        Assert.Equal(EnemyShotType.Coconuts, b.ShotType);
        Assert.Equal(1, b.Damage);
    }

    // Regression (waves 4 & 6 enemy-bullet retention, no-input death_wave sweep
    // 2026-05-30): ES_ANGLELEFT/ANGLERIGHT must travel a 45° diagonal at a speed
    // that RAMPS 3->6, not a constant (±3,+3). C (ESHOT.C:341-360) targets
    // (move.x ± 32, move.y + 32) — a 45° Bresenham direction — with
    // cur->speed = LIB_NORMAL.speed>>1 = 3, then speed++ each tick up to
    // LIB_NORMAL.speed = 6 (ESHOT_Think default branch, ESHOT.C:476-484).
    // MoveSobj (RAP.C:416) walks past maxloop==0, so the 32px target only sets
    // direction and the shot crosses the whole screen at the ramping speed. A
    // constant velocity made these shots ~2× too slow, so they lingered and
    // Godot retained more enemy bullets than C in MAP4G1/MAP6G1. Displayed-
    // position deltas mirror the ATDOWN lag pattern: 1,3,4,5,6,6.
    [Fact]
    public void Angle_shots_ramp_speed_3_to_6_on_45_diagonal_like_C()
    {
        int[] cum = { 1, 4, 8, 13, 19, 25 };   // cumulative of deltas 1,3,4,5,6,6

        var left = FireOneBullet(2, enemyX: 160, enemyY: 40, playerX: 144, playerY: 160);
        Assert.Equal(EnemyShotType.AngleLeft, left.ShotType);
        int lsx = left.X, lsy = left.Y;
        for (int i = 0; i < cum.Length; i++)
        {
            left.Tick();
            Assert.Equal(lsx - cum[i], left.X);   // moves LEFT
            Assert.Equal(lsy + cum[i], left.Y);   // and DOWN at 45°
        }

        var right = FireOneBullet(3, enemyX: 160, enemyY: 40, playerX: 144, playerY: 160);
        Assert.Equal(EnemyShotType.AngleRight, right.ShotType);
        int rsx = right.X, rsy = right.Y;
        for (int i = 0; i < cum.Length; i++)
        {
            right.Tick();
            Assert.Equal(rsx + cum[i], right.X);   // moves RIGHT
            Assert.Equal(rsy + cum[i], right.Y);   // and DOWN at 45°
        }
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
        // Regression: the JSON encodes "bossflag" as an int (0/1), not a bool.
        // Deserializing it as System.Boolean throws; SpriteMeta.BossFlag must be int.
        Assert.Equal(0, m.BossFlag);
        Assert.False(new EnemyLogic(m, 0, 0).IsBoss);
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

    // F_REPEAT ping-pong: forward through waypoints, then BACKWARD to `repos`,
    // then forward again. C ENEMY.C:883-900. Naive wrap (idx=0 on overflow)
    // breaks the back-and-forth animation that boss-like REPEAT enemies use.
    [Fact]
    public void F_repeat_enemy_ping_pongs_between_repos_and_last_waypoint()
    {
        // 4 waypoints all walking DOWN: 102 → 112 → 122 → 132 (sy=92, +y[i]).
        // Ping-pong reversal: after reaching y=132, the next target is
        // waypoint[2]=122 (a smooth 10-pixel reversal, not a jump back to 102).
        // Wrap behavior incorrectly targets waypoint[0]=102 → a 30-pixel jump.
        var meta = new SpriteMeta
        {
            Hits = 10,
            NumFlight = 4,
            FlightType = 0,    // F_REPEAT
            FlightX = new[] { 0, 0, 0, 0 },
            FlightY = new[] { 10, 20, 30, 40 },
            MoveSpeed = 4,
            Repos = 0,
            Width = 16, Height = 16,
        };
        var e = new EnemyLogic(meta, spawnX: 160, mapY: 80);

        // Run long enough to traverse all 4 waypoints forward (≈ 13 ticks at
        // speed 4 covers 52 px), then a few more ticks to enter the reverse leg.
        int maxY = 0;
        int firstY = e.Y;
        int yAtMaxPlus6 = -1;
        bool sawMax = false;
        for (int i = 0; i < 200; i++)
        {
            e.Tick(playerX: 160, playerY: 160);
            if (e.Y > maxY) { maxY = e.Y; sawMax = false; }
            if (!sawMax && i > 13 && e.Y == maxY) sawMax = true;
            if (sawMax && yAtMaxPlus6 == -1 && i >= 14 + 6) yAtMaxPlus6 = e.Y;
        }

        Assert.True(maxY >= 130, $"Forward walk should reach last waypoint (~132). Max={maxY}");

        // Build a full Y trace to detect smooth reversal.
        var ys2 = new System.Collections.Generic.List<int>();
        var e2 = new EnemyLogic(meta, spawnX: 160, mapY: 80);
        for (int i = 0; i < 50; i++)
        {
            e2.Tick(playerX: 160, playerY: 160);
            ys2.Add(e2.Y);
        }
        // After hitting 132 (max), the next 6 Y values should be DECREASING
        // (smooth reversal). With wrap behavior they'd jump back to ~102.
        int maxIdx = ys2.IndexOf(ys2.Max());
        Assert.True(maxIdx + 6 < ys2.Count,
            $"Trace too short. maxIdx={maxIdx} traceLen={ys2.Count}");
        int yAfter = ys2[maxIdx + 6];
        Assert.InRange(yAfter, 100, 130);
        // Smooth reversal means yAfter < max, and substantially so.
        Assert.True(yAfter < ys2[maxIdx],
            $"After max(idx={maxIdx},Y={ys2[maxIdx]}), Y should decrease. Got Y={yAfter}. Trace[maxIdx-2..maxIdx+10] = [{string.Join(',', ys2.GetRange(System.Math.Max(0, maxIdx - 2), System.Math.Min(13, ys2.Count - System.Math.Max(0, maxIdx - 2))))}]");
    }

    [Fact]
    public void F_repeat_enemy_never_terminates_under_normal_conditions()
    {
        var meta = new SpriteMeta
        {
            Hits = 10,
            NumFlight = 2,
            FlightType = 0,
            FlightX = new[] { 0, 0 },
            FlightY = new[] { 5, 10 },
            MoveSpeed = 2,
            Repos = 0,
            Width = 16, Height = 16,
        };
        var e = new EnemyLogic(meta, spawnX: 160, mapY: 80);

        for (int i = 0; i < 500; i++) e.Tick();

        Assert.False(e.Done, "F_REPEAT enemy should ping-pong forever, not terminate");
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

        // 30 ticks: waypoints (~tick 10) + early chase. LINEAR would Done at
        // ~tick 10 when out of waypoints; KAMI must keep going. Capped well
        // before the off-screen termination check fires (~tick 60 for this
        // setup, once the chase bresenham walks past the player).
        for (int i = 0; i < 30 && !e.Done; i++)
            e.Tick(playerX: 160, playerY: 150);

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

    // Regression: F_KAMI orbs got stuck at the player's position once the
    // KAMI_CHASE→KAMI_END bresenham completed in the visible playfield.
    // C ENEMY.C:928 only does the move.done snap-and-advance block when
    // `kami != KAMI_END`; our port ran it unconditionally, so the snap kept
    // reverting the post-bresenham step and AdvanceFlightSegment short-
    // circuited before the off-screen check.
    //
    // With player ON-screen (typical), the orb's chase target is inside the
    // playfield. After the bresenham reaches that target the orb must keep
    // moving in the original direction and terminate when it leaves the
    // tight off-screen bounds at ENEMY.C:913-925.
    [Fact]
    public void Kami_enemy_terminates_with_player_on_screen()
    {
        var meta = new SpriteMeta
        {
            Hits = 10,
            NumFlight = 1,
            FlightType = 2,        // F_KAMI
            FlightX = new[] { 0 },
            FlightY = new[] { 5 },  // tiny waypoint so chase fires fast
            MoveSpeed = 2,
            Width = 16, Height = 16,
        };
        var e = new EnemyLogic(meta, spawnX: 100, mapY: 50);

        // Player at a typical on-screen position near the bottom of the
        // playfield. The chase target lands inside the visible area.
        for (int i = 0; i < 1000 && !e.Done; i++)
            e.Tick(playerX: 144, playerY: 160);

        Assert.True(e.Done,
            $"KAMI orb should terminate even with on-screen player, " +
            $"got X={e.X} Y={e.Y} after 1000 ticks");
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

    // Regression (F_REPEAT flight parity — latent flight finding split out from #2):
    // at a ping-pong reversal C re-targets the SAME waypoint the boss already sits
    // on — a ZERO-LENGTH segment. C's InitMobj leaves addx=1, maxloop=delx+1=1, so
    // MoveMobj/MoveEobj DRIFT the boss one extra pixel before reversing (an authentic
    // C quirk). Godot used to special-case zero-length in InitBresenhamForTarget
    // (maxloop=0, moveDone, no drift) → a "clean" reversal that diverged from C at
    // the first turn (the wave-1 boss SHIP10G1). NOTE the `if maxloop<1 done` post-
    // loop check in MoveEobjSteps is FAITHFUL (C ENEMY.C:149-150) and must stay; only
    // the special case is the bug.
    //
    // Golden = the C enemy-flight Bresenham (RAP.C InitMobj/MoveMobj + ENEMY.C
    // MoveEobj) ported + verified in tools/flight_parity_check.py: with the special
    // case present Godot diverges from C at tick 158; with it removed they match for
    // 2000 ticks. EnemyLogic.X/Y is the PRE-move snapshot (C sets sprite->x from
    // move.x before MoveEobj runs), so actual[t] == golden[t-1] (one-tick offset).
    [Fact]
    public void F_repeat_flight_matches_c_bresenham_through_reversals()
    {
        var meta = new SpriteMeta
        {
            Hits       = 100000,   // stay alive for the whole window
            MoveSpeed  = 2,
            NumFlight  = 8,
            FlightType = 0,        // F_REPEAT (ping-pong)
            FlightX    = new[] {   0,  64,  64,   8,  -8, -64, -64,  -4 },  // SHIP10G1
            FlightY    = new[] { -72, -32,  -8,  32,  32,  -8, -32, -70 },
            Repos      = 0,
            NumGuns    = 0,
            // Height defaults to 24 → HalfY 12 → flight home _sy = 100 - 12 = 88.
        };
        // C (x,y) for ticks 1..180 (post-move), from flight_parity_check.py.
        int[] golden = {
            160,3,160,5,160,7,160,9,160,11,160,13,160,15,161,17,163,18,165,19,
            167,21,169,22,171,23,173,24,175,26,177,27,179,28,181,29,183,31,185,32,
            187,33,189,34,191,36,193,37,195,38,197,39,199,41,201,42,203,43,205,44,
            207,46,209,47,211,48,213,49,215,51,217,52,219,53,221,54,223,56,224,57,
            224,59,224,61,224,63,224,65,224,67,224,69,224,71,224,73,224,75,224,77,
            224,79,223,81,221,82,219,84,217,85,215,87,213,88,211,89,209,91,207,92,
            205,94,203,95,201,97,199,98,197,99,195,101,193,102,191,104,189,105,187,107,
            185,108,183,109,181,111,179,112,177,114,175,115,173,117,171,118,169,119,167,120,
            165,120,163,120,161,120,159,120,157,120,155,120,153,120,151,119,149,118,147,116,
            145,115,143,113,141,112,139,111,137,109,135,108,133,106,131,105,129,103,127,102,
            125,101,123,99,121,98,119,96,117,95,115,93,113,92,111,91,109,89,107,88,
            105,86,103,85,101,83,99,82,97,81,96,79,96,77,96,75,96,73,96,71,96,69,
            96,67,96,65,96,63,96,61,96,59,96,57,97,55,99,54,101,53,103,51,105,50,
            107,49,109,48,111,46,113,45,115,44,117,43,119,41,121,40,123,39,125,37,
            127,36,129,35,131,34,133,32,135,31,137,30,139,29,141,27,143,26,145,25,
            147,24,149,22,151,21,153,20,155,18,157,18,155,19,153,20,151,21,149,23,
            147,24,145,25,143,26,141,28,139,29,137,30,135,31,133,33,131,34,129,35,
            127,37,125,38,123,39,121,40,119,42,117,43,115,44,113,45,
        };

        var e = new EnemyLogic(meta, spawnX: 160, mapY: 0);
        var actual = new (int x, int y)[181];
        for (int t = 0; t < 181; t++)
        {
            e.Tick();
            actual[t] = (e.X, e.Y);
        }

        for (int t = 1; t <= 180; t++)
            Assert.Equal((golden[2 * (t - 1)], golden[2 * (t - 1) + 1]), actual[t]);
    }
}
