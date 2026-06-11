using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

public class InteractiveInputDecisionTests
{
    [Fact]
    public void Not_in_game_passes_through()
    {
        Assert.Equal(InteractiveInputController.InGameInputDecision.PassThrough,
            InteractiveInputController.DecideInGameInput(inGame: false, abortPromptActive: false, action: "Escape"));
    }

    [Fact]
    public void Not_in_game_passes_through_regardless_of_action()
    {
        Assert.Equal(InteractiveInputController.InGameInputDecision.PassThrough,
            InteractiveInputController.DecideInGameInput(inGame: false, abortPromptActive: false, action: "Up"));
    }

    [Fact]
    public void In_game_escape_opens_abort_prompt()
    {
        Assert.Equal(InteractiveInputController.InGameInputDecision.OpenAbort,
            InteractiveInputController.DecideInGameInput(inGame: true, abortPromptActive: false, action: "Escape"));
    }

    [Fact]
    public void In_game_non_escape_is_swallowed()
    {
        Assert.Equal(InteractiveInputController.InGameInputDecision.Swallow,
            InteractiveInputController.DecideInGameInput(inGame: true, abortPromptActive: false, action: "Up"));
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("Return")]
    [InlineData("Escape")]
    public void While_prompt_active_nav_keys_route_to_askbool(string action)
    {
        Assert.Equal(InteractiveInputController.InGameInputDecision.RouteToAskBool,
            InteractiveInputController.DecideInGameInput(inGame: true, abortPromptActive: true, action: action));
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("A")]
    public void While_prompt_active_non_nav_keys_are_swallowed(string action)
    {
        Assert.Equal(InteractiveInputController.InGameInputDecision.Swallow,
            InteractiveInputController.DecideInGameInput(inGame: true, abortPromptActive: true, action: action));
    }

    [Fact]
    public void While_prompt_active_unmapped_keys_are_swallowed()
    {
        Assert.Equal(InteractiveInputController.InGameInputDecision.Swallow,
            InteractiveInputController.DecideInGameInput(inGame: true, abortPromptActive: true, action: null));
    }
}
