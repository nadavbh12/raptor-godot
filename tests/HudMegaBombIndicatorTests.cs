using System.Linq;
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class HudMegaBombIndicatorTests
{
    [Fact]
    public void Indicators_match_c_bottom_left_positions()
    {
        var positions = HudMegaBombIndicator.Build(count: 3).ToArray();

        Assert.Equal(new HudMegaBombIndicator.Position(18, 186), positions[0]);
        Assert.Equal(new HudMegaBombIndicator.Position(31, 186), positions[1]);
        Assert.Equal(new HudMegaBombIndicator.Position(44, 186), positions[2]);
    }

    [Fact]
    public void No_inventory_draws_no_indicators()
    {
        Assert.Empty(HudMegaBombIndicator.Build(count: 0));
    }
}
