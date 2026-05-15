using Raptor.View;
using Xunit;

namespace RaptorTests;

public class HudWarningTests
{
    [Theory]
    [InlineData(10, 0, true)]
    [InlineData(11, 0, false)]
    [InlineData(10, 8, false)]
    [InlineData(10, 16, true)]
    public void Shield_low_warning_uses_c_threshold_and_blink_cadence(int shield, int frame, bool visible)
    {
        Assert.Equal(visible, HudWarning.ShieldLowVisible(shield, frame));
    }

    [Theory]
    [InlineData(10, 48, 0, true)]
    [InlineData(10, 48, 8, false)]
    [InlineData(10, 48, 49, false)]
    [InlineData(11, 48, 0, false)]
    public void System_damage_warning_follows_shield_low_blink_during_damage_window(
        int shield,
        int untilFrame,
        int frame,
        bool visible)
    {
        Assert.Equal(visible, HudWarning.SystemDamageVisible(shield, untilFrame, frame));
    }

    [Fact]
    public void Warning_is_centered_at_c_map_bottom()
    {
        Assert.Equal(182, HudWarning.MapBottom);
        Assert.Equal(173, HudWarning.SystemDamageY);
        Assert.Equal(140, HudWarning.CenterX(spriteWidth: 40));
    }
}
