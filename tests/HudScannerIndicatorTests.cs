using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class HudScannerIndicatorTests
{
    [Theory]
    [InlineData(0, 110, 209)]
    [InlineData(49, 159, 160)]
    public void Idle_scanner_lines_match_c_positions(int dpos, int expectedLeftX, int expectedRightX)
    {
        var lines = HudScannerIndicator.BuildIdle(dpos).ToArray();

        Assert.Equal(new HudScannerIndicator.Line(expectedLeftX, 190, 3, 68), lines[0]);
        Assert.Equal(new HudScannerIndicator.Line(expectedRightX, 190, 3, 68), lines[1]);
    }

    [Fact]
    public void Idle_scanner_advances_only_on_sim_ticks()
    {
        var state = new HudScannerIndicator.State();

        Assert.Equal(0, state.CurrentDpos);
        state.AfterRenderFrame();
        state.AfterRenderFrame();
        Assert.Equal(0, state.CurrentDpos);

        state.AfterSimTick();
        Assert.Equal(1, state.CurrentDpos);

        for (int i = 0; i < 48; i++)
            state.AfterSimTick();
        Assert.Equal(49, state.CurrentDpos);

        state.AfterSimTick();
        Assert.Equal(0, state.CurrentDpos);
    }

    [Fact]
    public void Damage_bar_draws_frame_then_fill_with_c_geometry()
    {
        var boxes = HudScannerIndicator.BuildDamage(damage: 60).ToArray();
        Assert.Equal(2, boxes.Length);
        Assert.Equal(new HudScannerIndicator.Box(109, 191, 102, 8, 74), boxes[0]);
        Assert.Equal(new HudScannerIndicator.Box(110, 192, 60, 6, 68), boxes[1]);
    }

    [Fact]
    public void Zero_damage_builds_no_boxes() => Assert.Empty(HudScannerIndicator.BuildDamage(0));

    // Property "Damage-bar fidelity": fill width == raw damage (no clamp); frame fixed;
    // damage branch non-empty iff damage > 0.
    [Property(MaxTest = 50)]
    public Property Damage_bar_fill_width_equals_raw_damage()
        => Prop.ForAll(Gen.Choose(0, 200).ToArbitrary(), dmg =>
        {
            var boxes = HudScannerIndicator.BuildDamage(dmg).ToArray();
            if (dmg <= 0) return boxes.Length == 0;
            return boxes.Length == 2
                && boxes[0] == new HudScannerIndicator.Box(109, 191, 102, 8, 74)
                && boxes[1] == new HudScannerIndicator.Box(110, 192, dmg, 6, 68);
        });
}
