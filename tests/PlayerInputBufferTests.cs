using Raptor.Sim.Player;
using Xunit;

namespace Raptor.Tests;

public class InputStateTests
{
    [Fact]
    public void Idle_is_all_zero_or_false()
    {
        var s = InputState.Idle;
        Assert.Equal(0, s.Dx);
        Assert.Equal(0, s.Dy);
        Assert.False(s.B1);
        Assert.False(s.B2);
        Assert.False(s.B3);
        Assert.False(s.B4);
    }

    [Fact]
    public void From_round_trips_all_fields()
    {
        var s = InputState.From(-1, 1, true, false, true, false);
        Assert.Equal(-1, s.Dx);
        Assert.Equal(1, s.Dy);
        Assert.True(s.B1); Assert.False(s.B2);
        Assert.True(s.B3); Assert.False(s.B4);
    }
}
