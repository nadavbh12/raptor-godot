using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Raptor.Sim.Player;
using Xunit;

namespace Raptor.Tests;

public class PlayerTests
{
    [Fact]
    public void TickDelta_applies_exact_per_iter_delta()
    {
        // Replay fallback for non-keyboard recordings: apply the recorded
        // g_addx/g_addy directly (device-independent), bypassing the input ramp.
        var p = new PlayerLogic();
        p.TickDelta(5, -3);
        Assert.Equal(PlayerLogic.InitX + 5, p.X);
        Assert.Equal(PlayerLogic.InitY - 3, p.Y);
    }

    [Fact]
    public void Reset_returns_to_init_position()
    {
        var p = new PlayerLogic();
        p.Tick(1, 1); p.Tick(1, 1);
        p.Reset();
        Assert.Equal(PlayerLogic.InitX, p.X);
        Assert.Equal(PlayerLogic.InitY, p.Y);
    }

    [Fact]
    public void Holding_up_for_70_ticks_moves_player_up_by_70_x_velocity()
    {
        var p = new PlayerLogic();
        int startY = p.Y;
        for (int i = 0; i < 70; i++) p.Tick(0, -1);
        Assert.Equal(System.Math.Max(PlayerLogic.MinY, startY - 70 * PlayerLogic.VelocityPerTick), p.Y);
    }

    [Fact]
    public void Position_clamped_to_X_bounds()
    {
        var p = new PlayerLogic();
        for (int i = 0; i < 1000; i++) p.Tick(-1, 0);
        Assert.Equal(PlayerLogic.MinX, p.X);
        for (int i = 0; i < 1000; i++) p.Tick(1, 0);
        Assert.Equal(PlayerLogic.MaxX, p.X);
    }

    [Fact]
    public void Position_clamps_to_c_player_bounds()
    {
        // C PUBLIC.H / INPUT.C:844-866 — the authoritative player-position clamps:
        //   PLAYERMINX=5, PLAYERMAXX=314 (right edge), so playerx ∈ [5, 314-32=282];
        //   MINPLAYERY=0, MAXPLAYERY=160 (top-left y, NOT screen-height minus sprite).
        // Godot previously used screen-gutter values (16 / 304-32 / 200-32) which
        // diverged from the C goldens once the player rode an edge (player_x@180).
        var p = new PlayerLogic();
        for (int i = 0; i < 1000; i++) p.Tick(-1, 0);
        Assert.Equal(5, p.X);                                  // PLAYERMINX
        for (int i = 0; i < 1000; i++) p.Tick(1, 0);
        Assert.Equal(314 - PlayerLogic.SpriteWidth, p.X);      // PLAYERMAXX - PLAYERWIDTH = 282
        for (int i = 0; i < 1000; i++) p.Tick(0, 1);
        Assert.Equal(160, p.Y);                                // MAXPLAYERY
        for (int i = 0; i < 1000; i++) p.Tick(0, -1);
        Assert.Equal(0, p.Y);                                  // MINPLAYERY
    }

    // Shield / Alive tests (PlayerLogic already had Shield and TakeDamage from earlier stage)

    [Fact]
    public void Shield_Starts_At_Zero_Before_Reset()
    {
        // A new PlayerLogic has no pilot; shield is 0 until Reset() is called.
        var p = new PlayerLogic();
        Assert.Equal(0, p.Shield);
        Assert.False(p.Alive);
    }

    [Fact]
    public void Reset_Sets_Shield_To_InitShield()
    {
        var p = new PlayerLogic();
        p.Reset();
        Assert.Equal(PlayerLogic.InitShield, p.Shield);
        Assert.True(p.Alive);
    }

    [Fact]
    public void TakeDamage_Reduces_Shield_And_Floors_At_Zero()
    {
        var p = new PlayerLogic();
        p.Reset();
        bool dead = p.TakeDamage(30);
        Assert.Equal(PlayerLogic.InitShield - 30, p.Shield);
        Assert.False(dead);
        Assert.True(p.Alive);
        dead = p.TakeDamage(200);
        Assert.Equal(0, p.Shield);
        Assert.True(dead);
        Assert.False(p.Alive);
    }

    [Fact]
    public void TakeDamage_Zero_Or_Negative_Is_NoOp()
    {
        var p = new PlayerLogic();
        p.Reset();
        int shield = p.Shield;
        Assert.False(p.TakeDamage(0));
        Assert.False(p.TakeDamage(-5));
        Assert.Equal(shield, p.Shield);
    }

    [Fact]
    public void Reset_Restores_Shield_After_Damage()
    {
        var p = new PlayerLogic();
        p.Reset();
        p.TakeDamage(50);
        p.Reset();
        Assert.Equal(PlayerLogic.InitShield, p.Shield);
        Assert.True(p.Alive);
    }

    [Fact]
    public void ApplyDemoFrame_overrides_position_and_pic()
    {
        var p = new PlayerLogic();
        p.Reset();
        p.Tick(1, -1);
        p.ApplyDemoFrame(-14, 177, 5);

        Assert.Equal(-14, p.X);
        Assert.Equal(177, p.Y);
        Assert.Equal(5, p.Pic);
    }

    [Fact]
    public void SetShield_clamps_to_valid_range()
    {
        var p = new PlayerLogic();
        p.SetShield(500);
        Assert.Equal(PlayerLogic.MaxShield, p.Shield);
        p.SetShield(-1);
        Assert.Equal(0, p.Shield);
    }

    [Fact]
    public void Shield_Is_Live_View_Over_Shared_Inventory_Energy_Slot()
    {
        // Task 4.2: PlayerLogic.Shield is a view over the unified Inventory Energy slot.
        var inv = new Raptor.Sim.Inventory();
        var p = new PlayerLogic(inv);
        p.Reset();

        Assert.Equal(75, p.Shield);
        Assert.Equal(75, inv.GetAmt(Raptor.Sim.ObjType.Energy));

        // Draining the shared inventory directly is reflected in Shield (live view).
        inv.SubEnergy(10);
        Assert.Equal(65, p.Shield);
    }

    // Spec §11 State bounds: Player position is always in [MinX, MaxX] x [MinY, MaxY].
    [Property(MaxTest = 50)]
    public Property Player_position_stays_in_bounds_under_arbitrary_input()
    {
        // Generate a pair of int[] (dx inputs and dy inputs), each 50 elements from {-1,0,1}
        var inputPairsGen = Gen.Zip(
            Gen.ArrayOf(Gen.Choose(-1, 1), 50),
            Gen.ArrayOf(Gen.Choose(-1, 1), 50));
        return Prop.ForAll(inputPairsGen.ToArbitrary(), pair =>
        {
            var (xs, ys) = pair;
            var p = new PlayerLogic();
            for (int i = 0; i < xs.Length; i++) p.Tick(xs[i], ys[i]);
            return p.X >= PlayerLogic.MinX && p.X <= PlayerLogic.MaxX
                && p.Y >= PlayerLogic.MinY && p.Y <= PlayerLogic.MaxY;
        });
    }
}
