using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Raptor.Sim.Bullet;
using Xunit;

namespace Raptor.Tests;

public class BulletLogicTests
{
    [Fact]
    public void Player_bullet_moves_up_by_velocity_per_tick()
    {
        var b = new BulletLogic(BulletKind.Player, 100, 100, 0, -8);
        b.Tick();
        Assert.Equal(100, b.X);
        Assert.Equal(92, b.Y);
        Assert.True(b.Alive);
    }

    [Fact]
    public void Bullet_position_after_N_ticks_equals_start_plus_N_times_velocity()
    {
        var b = new BulletLogic(BulletKind.Player, 50, 100, 2, -3);
        for (int i = 0; i < 10; i++) b.Tick();
        Assert.Equal(50 + 10 * 2, b.X);
        Assert.Equal(100 + 10 * -3, b.Y);
    }

    [Fact]
    public void Bullet_dies_when_above_top_edge()
    {
        var b = new BulletLogic(BulletKind.Player, 100, 5, 0, -8);
        b.Tick();
        Assert.False(b.Alive);
    }

    [Fact]
    public void Bullet_dies_when_below_bottom_edge()
    {
        var b = new BulletLogic(BulletKind.Enemy, 100, 195, 0, 8);
        b.Tick();
        Assert.False(b.Alive);
    }

    [Fact]
    public void Killed_bullet_does_not_move_on_subsequent_ticks()
    {
        var b = new BulletLogic(BulletKind.Player, 100, 100, 0, -8);
        b.Kill();
        var (x, y) = (b.X, b.Y);
        b.Tick();
        Assert.Equal(x, b.X);
        Assert.Equal(y, b.Y);
    }

    // Parity with C ESHOT.C: shot->x is the DISPLAYED position and lags
    // shot->move.x by one MoveSobj step. ESHOT_Shoot also performs a
    // single MoveSobj(&move, 1) before adding the bullet to the list.
    // Net effect for a straight-down (ATDOWN) bullet with initSpeed=3,
    // maxSpeed=6: the displayed Y sequence over iters [0..5] is
    // sy+1, sy+4, sy+8, sy+13, sy+19, sy+25 (deltas 1,3,4,5,6,6).
    [Fact]
    public void Eshot_atdown_displayed_y_matches_C_lag_pattern()
    {
        // sy = 9 (chosen so values stay positive)
        var b = new BulletLogic(BulletKind.Enemy, 100, 9,
            dx: 0, dy: 1, initSpeed: 3, maxSpeed: 6, damage: 2);
        int[] expected = { 10, 13, 17, 22, 28, 34 };
        for (int i = 0; i < expected.Length; i++)
        {
            b.Tick();
            Assert.Equal(expected[i], b.Y);
        }
    }

    // Spec §11 State bounds: alive bullets are in [0, 320] x [0, 200].
    [Property(MaxTest = 50)]
    public Property Alive_bullet_position_in_bounds()
    {
        // Generate a 4-tuple: (startX, startY, velX, velY)
        var bulletParamsGen = Gen.Zip(
            Gen.Zip(Gen.Choose(0, 320), Gen.Choose(0, 200)),
            Gen.Zip(Gen.Choose(-15, 15), Gen.Choose(-15, 15)));
        return Prop.ForAll(bulletParamsGen.ToArbitrary(), parms =>
        {
            var ((x, y), (vx, vy)) = parms;
            var b = new BulletLogic(BulletKind.Player, x, y, vx, vy);
            for (int i = 0; i < 100; i++) {
                b.Tick();
                if (b.Alive && (b.X < 0 || b.X > 320 || b.Y < 0 || b.Y > 200))
                    return false;
            }
            return true;
        });
    }
}
