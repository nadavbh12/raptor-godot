using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

public class MenuEventEmitterTests
{
    [Fact]
    public void MainMenu_down_reports_highlight_1()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Down", 1);
        Assert.Equal(WinState.Menu, m.State);
        Assert.Equal(1, m.EffectiveSelectedItem());
    }

    [Fact]
    public void Hangar_reports_c_active_field_constant()
    {
        // The hangar uses a CUSTOM cursor (SUPPLIES/MISSION/…), not SWD field nav, so
        // C's active_field stays at a constant gadget (=2) regardless of cursor position.
        // Menu-event parity matches what C records, so EffectiveSelectedItem returns 2.
        // Verified against tests/parity/c_menu_goldens/hangar_nav_mission_supplies.menu.ndjson.
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.CompleteMission(10);                 // -> Landing (ship lands) ...
        m.CompleteCutsceneIfDone(10 + CutsceneTimings.LandingTotal);  // ... -> Hangar
        Assert.Equal(WinState.Hangar, m.State);
        Assert.Equal(2, m.EffectiveSelectedItem());
    }
}
