using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

public class MenuStateMachineTests
{
    [Fact]
    public void Default_state_is_Unknown_before_EnterMenu()
    {
        var m = new MenuStateMachine();
        Assert.Equal(WinState.Unknown, m.State);
        Assert.Equal(0, m.CurrentItem);
    }

    [Fact]
    public void EnterMenu_sets_Menu_state_and_anchors_frame()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(42);
        Assert.Equal(WinState.Menu, m.State);
        Assert.Equal(0, m.CurrentItem);
        Assert.Equal(42, m.StateEnteredFrame);
    }

    [Fact]
    public void Down_advances_item_with_wrap()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < MenuStateMachine.ItemCount; i++)
            m.HandleInput("Down", 0);
        Assert.Equal(0, m.CurrentItem);  // wrapped back to 0
    }

    [Fact]
    public void Up_decrements_item_with_wrap()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Up", 0);
        Assert.Equal(MenuStateMachine.ItemCount - 1, m.CurrentItem);
    }

    [Fact]
    public void Four_Downs_from_zero_land_on_credits_item()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < 4; i++) m.HandleInput("Down", 0);
        Assert.Equal(MenuStateMachine.CreditsItemIndex, m.CurrentItem);
    }

    [Fact]
    public void Return_on_credits_item_enters_Credits_state()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < 4; i++) m.HandleInput("Down", 0);
        Assert.Equal(WinState.Menu, m.State);

        bool transitioned = m.HandleInput("Return", 100);

        Assert.True(transitioned);
        Assert.Equal(WinState.Credits, m.State);
        // Anchor is offset by CreditsFadeFrames to simulate animation delay.
        Assert.Equal(100 + MenuStateMachine.CreditsFadeFrames, m.StateEnteredFrame);
    }

    [Fact]
    public void Return_on_non_credits_item_stays_in_Menu()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        // item 0 = NEW — Return should not transition.
        bool transitioned = m.HandleInput("Return", 50);
        Assert.False(transitioned);
        Assert.Equal(WinState.Menu, m.State);
    }

    [Fact]
    public void Return_in_Credits_enters_Unknown_keeps_anchor()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < 4; i++) m.HandleInput("Down", 0);
        m.HandleInput("Return", 100);   // enter Credits, anchor = 100 + CreditsFadeFrames
        int expectedAnchor = 100 + MenuStateMachine.CreditsFadeFrames;
        Assert.Equal(expectedAnchor, m.StateEnteredFrame);

        bool transitioned = m.HandleInput("Return", 200);   // exit Credits

        Assert.True(transitioned);
        Assert.Equal(WinState.Unknown, m.State);
        // Anchor must NOT change on transition to Unknown (mirrors parity.c).
        Assert.Equal(expectedAnchor, m.StateEnteredFrame);
    }

    [Fact]
    public void OnStateChanged_fires_on_EnterMenu()
    {
        var m = new MenuStateMachine();
        int fired = 0;
        m.OnStateChanged += () => fired++;
        m.EnterMenu(0);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void OnStateChanged_fires_on_Credits_entry_and_exit()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        int fired = 0;
        m.OnStateChanged += () => fired++;

        for (int i = 0; i < 4; i++) m.HandleInput("Down", 0);
        m.HandleInput("Return", 100);   // enter Credits
        Assert.Equal(1, fired);

        m.HandleInput("Return", 200);   // exit Credits
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Down_in_Credits_has_no_effect()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < 4; i++) m.HandleInput("Down", 0);
        m.HandleInput("Return", 100);
        Assert.Equal(WinState.Credits, m.State);

        bool transitioned = m.HandleInput("Down", 110);
        Assert.False(transitioned);
        Assert.Equal(WinState.Credits, m.State);
    }

    [Fact]
    public void WinState_to_parity_string_matches_schema()
    {
        Assert.Equal("UNKNOWN",   WinState.Unknown.ToParityString());
        Assert.Equal("MENU",      WinState.Menu.ToParityString());
        Assert.Equal("CREDITS",   WinState.Credits.ToParityString());
        Assert.Equal("HELP",      WinState.Help.ToParityString());
        Assert.Equal("ORDER",     WinState.Order.ToParityString());
        Assert.Equal("HANGAR",    WinState.Hangar.ToParityString());
        Assert.Equal("STORE",     WinState.Store.ToParityString());
        Assert.Equal("BRIEFING",  WinState.Briefing.ToParityString());
        Assert.Equal("MISSION_1", WinState.Mission_1.ToParityString());
        Assert.Equal("MISSION_2", WinState.Mission_2.ToParityString());
        Assert.Equal("MISSION_3", WinState.Mission_3.ToParityString());
    }
}
