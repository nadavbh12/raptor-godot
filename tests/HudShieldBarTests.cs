using System.Linq;
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class HudShieldBarTests
{
    [Fact]
    public void Filled_shield_bar_matches_c_geometry_and_color_ramp()
    {
        var segments = HudShieldBar.Build(x: 308, level: 100).ToArray();

        Assert.Equal(100, segments.Length);
        Assert.Equal(new HudShieldBar.Segment(308, 199, 4, 1, 74), segments[0]);
        Assert.Equal(new HudShieldBar.Segment(308, 1, 4, 1, 66), segments[99]);
    }

    [Fact]
    public void Partial_shield_bar_clears_unfilled_rows_to_black()
    {
        var segments = HudShieldBar.Build(x: 308, level: 75).ToArray();

        Assert.Equal(74, segments[0].PaletteIndex);
        Assert.Equal(68, segments[74].PaletteIndex);
        Assert.Equal(51, segments[74].Y);
        Assert.Equal(0, segments[75].PaletteIndex);
        Assert.Equal(49, segments[75].Y);
        Assert.Equal(0, segments[99].PaletteIndex);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(125, 100)]
    public void Shield_level_is_clamped_to_valid_range(int input, int expectedFilledRows)
    {
        var segments = HudShieldBar.Build(x: 308, level: input).ToArray();

        Assert.Equal(expectedFilledRows, segments.Count(s => s.PaletteIndex != 0));
    }

    [Fact]
    public void Draws_both_c_side_bars_left_super_right_shield()
    {
        // C draws TWO bars every frame (RAP.C:549/557): the regular shield on the
        // RIGHT (MAP_RIGHT+4 = 308) and the super-shield amount on the LEFT
        // (MAP_LEFT-8 = 8). The port previously drew only the right bar, leaving the
        // left rail empty (user-reported asymmetry).
        var bars = HudShieldBar.Bars(shield: 60, superShield: 30).ToArray();

        Assert.Equal(2, bars.Length);
        Assert.Equal((308, 60), bars[0]);   // RIGHT = regular shield (C RAP.C:557)
        Assert.Equal((8, 30),  bars[1]);    // LEFT  = super-shield   (C RAP.C:549)
    }
}
