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
    public void Enemy_walks_path_one_waypoint_per_tick_then_marks_done()
    {
        var meta = SyntheticPath((100, 0), (110, 10), (120, 20));
        var e = new EnemyLogic(meta, 0, 0);
        e.Tick(); Assert.Equal((100, 0),   (e.X, e.Y));
        e.Tick(); Assert.Equal((110, 10),  (e.X, e.Y));
        e.Tick(); Assert.Equal((120, 20),  (e.X, e.Y));
        e.Tick(); Assert.True(e.Done);
        Assert.False(e.Alive);
    }

    [Fact]
    public void Repeat_flight_cycles_path_indefinitely()
    {
        var meta = SyntheticPath((100, 0), (110, 10));
        meta.FlightType = 0;  // REPEAT
        var e = new EnemyLogic(meta, 0, 0);
        for (int i = 0; i < 20; i++) e.Tick();
        // After 20 ticks of REPEAT, still alive, position is one of the two waypoints
        Assert.True(e.Alive);
        Assert.Contains((e.X, e.Y), new[] { (100, 0), (110, 10) });
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
    public void Enemy_with_guns_fires_every_ShootFrame_ticks()
    {
        var meta = SyntheticPath((50, 50), (60, 60), (70, 70), (80, 80), (90, 90));
        meta.NumGuns   = 1;
        meta.ShootFrame = 2;
        meta.ShootX    = new[] { 0 };
        meta.ShootY    = new[] { 0 };
        var e = new EnemyLogic(meta, 0, 0);
        // Tick 1: counter reaches 1 — no fire yet.
        // Tick 2: counter reaches 2 == ShootFrame — fires and resets.
        Assert.Null(e.Tick());
        var fired = e.Tick();
        Assert.NotNull(fired);
        Assert.Equal(BulletKind.Enemy, fired!.Kind);
    }

    // Spec §11 State bounds: enemy Hits never goes negative.
    [Property(MaxTest = 50)]
    public Property Hits_never_below_zero_under_arbitrary_damage()
    {
        return Prop.ForAll(
            Gen.NonEmptyListOf(Gen.Choose(0, 100)).ToArbitrary(),
            damages =>
            {
                var meta = SyntheticPath((0, 0));
                meta.Hits = 5;
                var e = new EnemyLogic(meta, 0, 0);
                foreach (var d in damages) e.TakeDamage(d);
                return e.Hits >= 0;
            });
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
}
