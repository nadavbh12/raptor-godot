using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Raptor.Sim.Player;
using Xunit;

namespace Raptor.Tests;

public class PlayerTests
{
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
