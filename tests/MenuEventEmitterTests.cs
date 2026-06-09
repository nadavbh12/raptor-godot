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
    public void Hangar_reports_hangar_position()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.CompleteMission(10);                 // -> Hangar, position 1 (SUPPLIES)
        Assert.Equal(WinState.Hangar, m.State);
        Assert.Equal(1, m.EffectiveSelectedItem());
    }
}
