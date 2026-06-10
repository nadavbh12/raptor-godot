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
        for (int i = 0; i < MenuStateMachine.CreditsItemIndex; i++)
            m.HandleInput("Down", 0);                          // highlight CREDITS
        m.HandleInput("Return", 0);                            // enter Credits
        Assert.Equal(WinState.Credits, m.State);
        Assert.Equal(MenuStateMachine.Screen.Credits, m.EffectiveScreen());
        Assert.Equal(0, m.EffectiveSelectedItem());
    }

    // Fix 1: LOAD window EffectiveSelectedItem() must report the LOAD_LOAD button
    // field constant (4), NOT the pilot-list cursor index.
    // C golden: selected_item stays 4 throughout LOAD window (Down/Up cycle the
    // shown pilot but do NOT move the button cursor).
    [Fact]
    public void LoadWindow_reports_LOAD_LOAD_field_constant_not_pilot_index()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "raptor_load_field_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            PilotSaveStore.Save(dir, "ALPHA",   "AL", 0, 1000u);
            PilotSaveStore.Save(dir, "BRAVO",   "BR", 1, 25000u);
            PilotSaveStore.Save(dir, "CHARLIE", "CH", 3, 148500u);

            var m = new MenuStateMachine { PilotSaveDirectory = dir };
            m.EnterMenu(0);
            m.HandleInput("Down", 0);    // highlight LOAD (item 1)
            m.HandleInput("Return", 0);  // open LOAD window

            Assert.True(m.InLoadMission, "expected LOAD window to be open");

            // Initially the button field must be the constant, not the pilot index.
            Assert.Equal(MenuStateMachine.LoadWindowSelectedItem, m.EffectiveSelectedItem());

            // Down cycles the shown pilot (LoadMissionSelectedIndex changes) but the
            // button field must not change.
            int pilotBefore = m.LoadMissionSelectedIndex;
            m.HandleInput("Down", 0);
            Assert.NotEqual(pilotBefore, m.LoadMissionSelectedIndex); // pilot cycled
            Assert.Equal(MenuStateMachine.LoadWindowSelectedItem, m.EffectiveSelectedItem());

            // Up also keeps the constant.
            m.HandleInput("Up", 0);
            Assert.Equal(MenuStateMachine.LoadWindowSelectedItem, m.EffectiveSelectedItem());
        }
        finally
        {
            if (System.IO.Directory.Exists(dir))
                System.IO.Directory.Delete(dir, true);
        }
    }

    // Fix 2: after dismissing a WIN_Msg the main-menu cursor must snap back to
    // NEW (index 0), matching C's SWD re-init to the first field on return.
    [Fact]
    public void WinMsg_dismiss_resets_cursor_to_NEW()
    {
        using var dir = new PilotSaveStoreTests.TempDir();
        var m = new MenuStateMachine { PilotSaveDirectory = dir.Path };
        m.EnterMenu(0);
        m.HandleInput("Down", 0);    // highlight LOAD (CurrentItem = 1)
        m.HandleInput("Return", 0);  // → WIN_Msg "No Pilots to Load"
        Assert.True(m.InWinMsg);
        Assert.Equal(MenuStateMachine.LoadItemIndex, m.CurrentItem); // sanity: was on LOAD
        m.HandleInput("Return", 0);  // dismiss WIN_Msg
        Assert.False(m.InWinMsg, "WIN_Msg should be dismissed");
        Assert.Equal(MenuStateMachine.NewItemIndex, m.CurrentItem);  // must snap to NEW
    }
}
