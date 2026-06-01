using Raptor.View;
using Xunit;

namespace RaptorTests;

public class GroundDriftTests
{
    [Theory]
    [InlineData(0, true, 0)]
    [InlineData(3, true, 3)]
    [InlineData(3, false, 0)]
    public void Ground_drift_offset_is_age_when_scrolling(int age, bool scrolling, int expected)
        => Assert.Equal(expected, GroundExplosionDrift.YOffset(age, scrolling));
}
