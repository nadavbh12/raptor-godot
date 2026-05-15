using System.Linq;
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
}
