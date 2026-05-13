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

    // Regression: ATPLAYER bullets must use integer Bresenham (mirrors C
    // ESHOT_Shoot + InitMobj + MoveSobj in ESHOT.C:319-328 + RAP.C:339-411).
    // A prior float-vector aim truncated 0.93-px/tick steps to 0, causing
    // bullets to lag 1 px behind C and miss the player. mission_long
    // late-game shield drifted Godot=12 vs C=8 because of this.
    //
    // From (68, 51) to (160, 16): delx=92, dely=35. delx>=dely so X is the
    // dominant axis. InitMobj err = -(35>>1) = -17. ESHOT_Shoot pre-advances
    // one step: x=69, err += 35 → 18 > 0 ⇒ y=50, err -= 92 → -74. Then
    // ESHOT_Think tick #1 with speed=1: snapshot (69,50), step ⇒ x=70,
    // err += 35 → -39 (≤0, y unchanged). speed++ → 2. Matches the C
    // mission_long dump: "i=1373 eb x=69 y=50 mx=70 my=50 speed=2 cnt=1".
    [Fact]
    public void Atplayer_bullet_uses_integer_bresenham()
    {
        var b = BulletLogic.AimedAt(BulletKind.Enemy, 68, 51, 160, 16,
            initSpeed: 1, maxSpeed: 6, damage: 1);
        Assert.Equal(68, b.X);
        Assert.Equal(51, b.Y);
        Assert.Equal(69, b.Mx);
        Assert.Equal(50, b.My);

        b.Tick();
        Assert.Equal(69, b.X);
        Assert.Equal(50, b.Y);
        Assert.Equal(70, b.Mx);
        Assert.Equal(50, b.My);
    }

    // Regression: an aimed bullet on the 45° diagonal must step diagonally
    // each Bresenham tick. Catches off-by-one in the delx==dely branch.
    [Fact]
    public void Aimed_bullet_diagonal_steps_diagonally()
    {
        var b = BulletLogic.AimedAt(BulletKind.Enemy, 0, 0, 10, 10,
            initSpeed: 1, maxSpeed: 1, damage: 1);
        // delx=10, dely=10. err = -(10>>1) = -5. After pre-advance step:
        // x=1, err=-5+10=5>0 ⇒ y=1, err-=10 → -5.
        Assert.Equal(1, b.Mx);
        Assert.Equal(1, b.My);
        b.Tick();
        // Snapshot (1,1), step: x=2, err=5>0 ⇒ y=2, err=-5.
        Assert.Equal(2, b.Mx);
        Assert.Equal(2, b.My);
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
