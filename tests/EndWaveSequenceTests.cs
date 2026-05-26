using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

public class EndWaveSequenceTests
{
    [Fact]
    public void Duration_and_flyoff_match_C_constants()
    {
        Assert.Equal(60, EndWaveSequence.Duration);
        Assert.Equal(40, EndWaveSequence.FlyOff);
    }

    [Fact]
    public void No_motion_above_flyoff_threshold()
    {
        Assert.Equal((0, 0), EndWaveSequence.PlayerDelta(countdown: 60, playerX: 100, shieldAlive: true));
        Assert.Equal((0, 0), EndWaveSequence.PlayerDelta(countdown: 40, playerX: 100, shieldAlive: true));
    }

    [Fact]
    public void Below_flyoff_threshold_glides_up_by_4()
    {
        var (_, dy) = EndWaveSequence.PlayerDelta(countdown: 39, playerX: 160, shieldAlive: true);
        Assert.Equal(-4, dy);
    }

    [Fact]
    public void Slides_right_by_8_when_left_of_center_band()
    {
        var (dx, _) = EndWaveSequence.PlayerDelta(countdown: 30, playerX: 100, shieldAlive: true);
        Assert.Equal(8, dx);
    }

    [Fact]
    public void Slides_left_by_8_when_right_of_center_band()
    {
        var (dx, _) = EndWaveSequence.PlayerDelta(countdown: 30, playerX: 200, shieldAlive: true);
        Assert.Equal(-8, dx);
    }

    [Theory]
    [InlineData(152)]  // edge of center band (160 - 8)
    [InlineData(160)]  // center
    [InlineData(168)]  // edge of center band (160 + 8)
    public void No_horizontal_drift_inside_center_band(int playerX)
    {
        var (dx, _) = EndWaveSequence.PlayerDelta(countdown: 30, playerX: playerX, shieldAlive: true);
        Assert.Equal(0, dx);
    }

    [Fact]
    public void Dead_player_does_not_fly_off()
    {
        Assert.Equal((0, 0), EndWaveSequence.PlayerDelta(countdown: 30, playerX: 100, shieldAlive: false));
    }

    [Fact]
    public void Input_locks_at_or_below_flyoff_threshold()
    {
        Assert.False(EndWaveSequence.InputLocked(countdown: 60));
        Assert.False(EndWaveSequence.InputLocked(countdown: 41));
        Assert.True(EndWaveSequence.InputLocked(countdown: 40));
        Assert.True(EndWaveSequence.InputLocked(countdown: 0));
    }

    [Fact]
    public void Input_unlocked_when_countdown_inactive()
    {
        Assert.False(EndWaveSequence.InputLocked(countdown: -1));
    }
}
