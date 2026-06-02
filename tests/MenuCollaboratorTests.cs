using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

// Focused unit tests for the three menu collaborators extracted from
// MenuStateMachine in Phase 4 tranche A. The full behavior (transitions,
// anchors, event order) is pinned by MenuStateMachineTests through HandleInput;
// these cover the collaborators' own logic in isolation.

public class HelpSystemControllerTests
{
    [Fact]
    public void PageOrder_has_39_entries_starting_with_HELP1_TXT()
    {
        Assert.Equal(39, HelpSystemController.PageOrder.Count);
        Assert.Equal("HELP1_TXT", HelpSystemController.PageOrder[0]);
        Assert.Equal("VEND00_TXT", HelpSystemController.PageOrder[38]);
    }

    [Fact]
    public void SelectPageByName_resolves_index_and_name()
    {
        var h = new HelpSystemController();
        h.SelectPageByName("HANGHLP1_TXT");
        Assert.Equal(24, h.PageIndex);
        Assert.Equal("HANGHLP1_TXT", h.TextName);
    }

    [Fact]
    public void SelectPageByName_unknown_falls_back_to_page_zero()
    {
        var h = new HelpSystemController();
        h.SelectPageByName("DOES_NOT_EXIST");
        Assert.Equal(0, h.PageIndex);
        Assert.Equal("HELP1_TXT", h.TextName);
    }

    [Fact]
    public void SetPage_wraps_modularly_at_both_ends()
    {
        var h = new HelpSystemController();
        h.SetPage(-1);
        Assert.Equal(38, h.PageIndex);
        Assert.Equal("VEND00_TXT", h.TextName);

        h.SetPage(39);   // == count → wraps to 0
        Assert.Equal(0, h.PageIndex);
        Assert.Equal("HELP1_TXT", h.TextName);
    }

    [Fact]
    public void ResetTextName_clears_name_only()
    {
        var h = new HelpSystemController();
        h.SelectPageByName("RAP1_TXT");   // index 30
        h.ResetTextName();
        Assert.Equal("HELP1_TXT", h.TextName);
        Assert.Equal(30, h.PageIndex);    // index deliberately untouched
    }
}

public class OptionsPanelTests
{
    [Fact]
    public void Defaults_are_closed_full_volume_high_detail()
    {
        var o = new OptionsPanel();
        Assert.False(o.Active);
        Assert.Equal(0, o.Field);
        Assert.True(o.DetailHigh);
        Assert.Equal(127, o.MusicVolume);
        Assert.Equal(127, o.FxVolume);
    }

    [Fact]
    public void Open_activates_with_detail_field_focused()
    {
        var o = new OptionsPanel();
        o.HandleInput("Down");          // move focus first
        o.Open();
        Assert.True(o.Active);
        Assert.Equal(0, o.Field);
    }

    [Fact]
    public void Down_Up_move_field_within_bounds()
    {
        var o = new OptionsPanel();
        o.Open();
        o.HandleInput("Down");
        Assert.Equal(1, o.Field);
        o.HandleInput("Down");
        Assert.Equal(2, o.Field);
        o.HandleInput("Down");          // clamps at 2
        Assert.Equal(2, o.Field);
        o.HandleInput("Up");
        Assert.Equal(1, o.Field);
    }

    [Fact]
    public void Left_Right_adjust_volume_in_steps_of_8_clamped()
    {
        var o = new OptionsPanel();
        o.Open();
        o.HandleInput("Down");          // field 1 = music
        o.HandleInput("Left");
        Assert.Equal(119, o.MusicVolume);
        o.HandleInput("Right");
        Assert.Equal(127, o.MusicVolume); // clamps at 127
    }

    [Fact]
    public void Return_on_detail_field_toggles_detail()
    {
        var o = new OptionsPanel();
        o.Open();
        o.HandleInput("Return");
        Assert.False(o.DetailHigh);
    }

    [Fact]
    public void Escape_closes_and_resets_field()
    {
        var o = new OptionsPanel();
        o.Open();
        o.HandleInput("Down");
        Assert.True(o.HandleInput("Escape"));
        Assert.False(o.Active);
        Assert.Equal(0, o.Field);
    }

    [Fact]
    public void Close_keeps_field_untouched()
    {
        var o = new OptionsPanel();
        o.Open();
        o.HandleInput("Down");
        o.Close();
        Assert.False(o.Active);
        Assert.Equal(1, o.Field);
    }
}

public class HangarControllerTests
{
    [Fact]
    public void Default_position_is_supplies()
    {
        Assert.Equal(1, new HangarController().Position);
    }

    [Fact]
    public void Down_decrements_position_with_wrap()
    {
        var h = new HangarController();   // pos 1
        Assert.True(h.TryNavigate("Down"));
        Assert.Equal(0, h.Position);
        Assert.True(h.TryNavigate("Down"));
        Assert.Equal(3, h.Position);      // wraps 0 → 3
    }

    [Fact]
    public void Up_increments_position_with_wrap()
    {
        var h = new HangarController { Position = 3 };
        Assert.True(h.TryNavigate("Up"));
        Assert.Equal(0, h.Position);      // wraps 3 → 0
    }

    [Fact]
    public void TryNavigate_returns_false_for_non_nav_keys()
    {
        var h = new HangarController();
        Assert.False(h.TryNavigate("Return"));
        Assert.False(h.TryNavigate("Escape"));
        Assert.Equal(1, h.Position);      // untouched
    }

    [Fact]
    public void PositionAt_hit_tests_the_four_hotspots()
    {
        Assert.Equal(0, HangarController.PositionAt(150, 160)); // MISSION
        Assert.Equal(1, HangarController.PositionAt(240, 80));  // SUPPLIES
        Assert.Equal(2, HangarController.PositionAt(30, 130));  // MAIN MENU
        Assert.Equal(3, HangarController.PositionAt(260, 170)); // QUICK SAVE
        Assert.Equal(-1, HangarController.PositionAt(0, 0));    // miss
    }
}
