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
}
