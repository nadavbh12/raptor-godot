using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

public class SimClockTests
{
    [Fact]
    public void Frame_starts_at_zero_after_reset()
    {
        SimClock.ResetForTest();
        Assert.Equal(0, SimClock.Frame);
    }

    [Fact]
    public void Tick_increments_Frame_by_one()
    {
        SimClock.ResetForTest();
        SimClock.Tick();
        Assert.Equal(1, SimClock.Frame);
    }

    // Spec §11: Frame monotonicity — Frame is strictly increasing within a session.
    [Property(MaxTest = 50)]
    public Property Frame_after_N_ticks_equals_N_when_started_from_zero()
    {
        return Prop.ForAll(
            Gen.Choose(0, 10000).ToArbitrary(),
            n =>
            {
                SimClock.ResetForTest();
                for (int i = 0; i < n; i++) SimClock.Tick();
                return SimClock.Frame == n;
            });
    }

    // Spec §11: Frame monotonicity — sequence is strictly increasing.
    [Property(MaxTest = 50)]
    public Property Frame_is_strictly_increasing_over_a_run()
    {
        return Prop.ForAll(
            Gen.Choose(1, 1000).ToArbitrary(),
            n =>
            {
                SimClock.ResetForTest();
                int prev = SimClock.Frame;
                for (int i = 0; i < n; i++) {
                    SimClock.Tick();
                    if (SimClock.Frame <= prev) return false;
                    prev = SimClock.Frame;
                }
                return true;
            });
    }
}
