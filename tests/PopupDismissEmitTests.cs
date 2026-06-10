using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

public class PopupDismissEmitTests
{
    // Property: popup-dismiss sentinel. Credits and WIN_Msg have no cursor →
    // EffectiveSelectedItem() reports 0 while they are showing (matches C's
    // raptor_parity_menu_event(field=1) → selected_item = field-1 = 0).
    [Fact]
    public void WinMsg_reports_selected_item_zero()
    {
        using var dir = new PilotSaveStoreTests.TempDir();
        var m = new MenuStateMachine { PilotSaveDirectory = dir.Path };
        m.EnterMenu(0);
        m.HandleInput("Down", 0);            // highlight LOAD (item 1)
        m.HandleInput("Return", 0);          // → WIN_Msg "No Pilots to Load"
        Assert.True(m.InWinMsg);
        Assert.Equal(0, m.EffectiveSelectedItem());
        Assert.Equal(MenuStateMachine.Screen.WinMsg, m.EffectiveScreen());
    }

    [Fact]
    public void Credits_reports_selected_item_zero()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Down", 0); m.HandleInput("Down", 0);
        m.HandleInput("Down", 0); m.HandleInput("Down", 0);   // highlight CREDITS (item 4)
        m.HandleInput("Return", 0);                            // enter Credits
        Assert.Equal(WinState.Credits, m.State);
        Assert.Equal(0, m.EffectiveSelectedItem());
    }
}
