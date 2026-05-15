using Raptor.View;
using Xunit;

namespace RaptorTests;

public class HudPaletteTests
{
    [Theory]
    [InlineData(74, 101, 16, 8)]
    [InlineData(68, 190, 85, 44)]
    [InlineData(66, 223, 113, 60)]
    public void Hud_bar_colors_match_c_palette_samples(int index, byte r, byte g, byte b)
    {
        Assert.Equal(new HudPalette.Rgb(r, g, b), HudPalette.Color(index));
    }
}
