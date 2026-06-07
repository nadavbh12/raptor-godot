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

    [Fact]
    public void Sequencer_runs_40_flyoff_moves_and_completes_one_iter_after_value_zero()
    {
        // Findings #8/#16: C step B (countdown decrement, RAP.C:1046) runs BEFORE
        // step D (fly-off, RAP.C:599), so PlayerDelta is evaluated on the
        // POST-decrement startendwave (values 39..0 → 40 moves), and end_wave fires
        // on the PRE-decrement value 0 — one iter AFTER the value-0 move, with NO
        // move on the completion iter (startendwave is EMPTY by then).
        var seq = new EndWaveSequencer();
        int moves = 0, lastMoveTick = -1, completeAtTick = -1;
        for (int tick = 0; tick < 100; tick++)
        {
            var r = seq.Tick(waveActive: true, demoActive: false, spawnExhausted: true,
                             playerAlive: true, playerX: 160, enemiesRemaining: false);
            if (r.ForcedDy != 0) { moves++; lastMoveTick = tick; }
            if (r.MissionComplete) { completeAtTick = tick; break; }
        }
        Assert.Equal(40, moves);                        // post-decrement 39..0
        Assert.Equal(lastMoveTick + 1, completeAtTick); // completion one iter after the value-0 move
    }
}
