using Raptor.View;
using Xunit;

namespace RaptorTests;

public class HudWarningTests
{
    [Fact]
    public void Shield_low_warning_uses_c_persistent_blink_flag()
    {
        var state = new HudWarning.State();

        state.Tick(shield: 10, gameLoopIter: 1, systemDamaged: false);
        Assert.True(state.ShieldLowVisible);

        state.Tick(shield: 10, gameLoopIter: 8, systemDamaged: false);
        Assert.False(state.ShieldLowVisible);

        state.Tick(shield: 10, gameLoopIter: 16, systemDamaged: false);
        Assert.True(state.ShieldLowVisible);
    }

    [Fact]
    public void System_damage_warning_lasts_two_visible_c_blinks()
    {
        var state = new HudWarning.State();

        state.Tick(shield: 10, gameLoopIter: 1, systemDamaged: true);
        Assert.True(state.SystemDamageVisible);

        state.Tick(shield: 10, gameLoopIter: 8, systemDamaged: false);
        Assert.False(state.SystemDamageVisible);

        state.Tick(shield: 10, gameLoopIter: 16, systemDamaged: false);
        Assert.True(state.SystemDamageVisible);

        state.Tick(shield: 10, gameLoopIter: 24, systemDamaged: false);
        Assert.False(state.SystemDamageVisible);

        state.Tick(shield: 10, gameLoopIter: 32, systemDamaged: false);
        Assert.False(state.SystemDamageVisible);
    }

    [Fact]
    public void Reset_clears_a_lingering_low_shield_warning()
    {
        // Regression: after dying at low shield, the warning stayed visible and
        // lingered through the next wave's fade-in hold (PhaseHud/Tick is held
        // then). ResetForWave now calls Reset(), which must clear it at once.
        var state = new HudWarning.State();
        state.Tick(shield: 5, gameLoopIter: 1, systemDamaged: true);
        Assert.True(state.ShieldLowVisible);

        state.Reset();

        Assert.False(state.ShieldLowVisible);
        Assert.False(state.SystemDamageVisible);
    }

    [Fact]
    public void Warning_is_centered_at_c_map_bottom()
    {
        Assert.Equal(182, HudWarning.MapBottom);
        Assert.Equal(173, HudWarning.SystemDamageY);
        Assert.Equal(140, HudWarning.CenterX(spriteWidth: 40));
    }
}
