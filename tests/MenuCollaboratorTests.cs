using System.Collections.Generic;
using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

// Focused unit tests for the menu collaborators extracted from
// MenuStateMachine in Phase 4 tranches A (Help/Options/Hangar) and B
// (LoadMission/PilotCreation). The full behavior (transitions, anchors, event
// order) is pinned by MenuStateMachineTests through HandleInput; these cover the
// collaborators' own logic in isolation.

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

public class LoadMissionPanelTests
{
    private static List<PilotSaveSummary> ThreePilots() => new()
    {
        Pilot("ALICE"), Pilot("BOB"), Pilot("CAROL"),
    };

    private static PilotSaveSummary Pilot(string name) => new(
        Slot: 0, Name: name, Callsign: "X", IdPic: 0, Score: 0u, SWeapon: 0,
        CurGame: 0, GameWave: new[] { 0, 0, 0 }, Diff: new[] { 0, 0, 0, 0 },
        TrainFlag: false, FinTrain: false);

    [Fact]
    public void Defaults_are_closed_empty()
    {
        var p = new LoadMissionPanel();
        Assert.False(p.Active);
        Assert.Empty(p.Pilots);
        Assert.Null(p.SelectedPilot);
    }

    [Fact]
    public void Open_activates_with_first_pilot_selected()
    {
        var p = new LoadMissionPanel();
        p.Open(ThreePilots());
        Assert.True(p.Active);
        Assert.Equal(0, p.SelectedIndex);
        Assert.Equal("ALICE", p.SelectedPilot!.Name);
    }

    [Fact]
    public void Down_PageDown_Left_advance_to_next_with_wrap()
    {
        var p = new LoadMissionPanel();
        p.Open(ThreePilots());
        Assert.Equal(LoadMissionPanel.Result.Handled, p.HandleInput("Down"));
        Assert.Equal("BOB", p.SelectedPilot!.Name);
        p.HandleInput("PageDown");
        Assert.Equal("CAROL", p.SelectedPilot!.Name);
        p.HandleInput("Left");                 // wraps 2 → 0
        Assert.Equal("ALICE", p.SelectedPilot!.Name);
    }

    [Fact]
    public void Up_PageUp_Right_step_to_prev_with_wrap()
    {
        var p = new LoadMissionPanel();
        p.Open(ThreePilots());
        p.HandleInput("Up");                   // wraps 0 → 2
        Assert.Equal("CAROL", p.SelectedPilot!.Name);
        p.HandleInput("Right");
        Assert.Equal("BOB", p.SelectedPilot!.Name);
        p.HandleInput("PageUp");
        Assert.Equal("ALICE", p.SelectedPilot!.Name);
    }

    [Fact]
    public void Escape_closes_and_signals_Closed()
    {
        var p = new LoadMissionPanel();
        p.Open(ThreePilots());
        Assert.Equal(LoadMissionPanel.Result.Closed, p.HandleInput("Escape"));
        Assert.False(p.Active);
    }

    [Fact]
    public void Return_closes_and_signals_Confirm_keeping_selection_readable()
    {
        var p = new LoadMissionPanel();
        p.Open(ThreePilots());
        p.HandleInput("Down");                 // select BOB
        Assert.Equal(LoadMissionPanel.Result.Confirm, p.HandleInput("Return"));
        Assert.False(p.Active);
        Assert.Equal("BOB", p.SelectedPilot!.Name);  // still readable post-confirm
    }

    [Fact]
    public void Reset_clears_list_and_selection()
    {
        var p = new LoadMissionPanel();
        p.Open(ThreePilots());
        p.HandleInput("Down");
        p.Reset();
        Assert.False(p.Active);
        Assert.Empty(p.Pilots);
        Assert.Equal(0, p.SelectedIndex);
        Assert.Null(p.SelectedPilot);
    }
}

public class PilotCreationFlowTests
{
    [Fact]
    public void Defaults_are_idle_veteran_empty()
    {
        var f = new PilotCreationFlow();
        Assert.False(f.Active);
        Assert.Equal(0, f.Step);
        Assert.Equal(3, f.DifficultyFieldId);
        Assert.Equal("", f.PilotName);
        Assert.Equal("", f.Callsign);
    }

    [Fact]
    public void Begin_enters_name_step()
    {
        var f = new PilotCreationFlow();
        f.Begin();
        Assert.True(f.Active);
        Assert.Equal(1, f.Step);
    }

    [Fact]
    public void Text_entry_uppercases_and_caps_at_12_per_step()
    {
        var f = new PilotCreationFlow();
        f.Begin();                                // step 1 = name
        foreach (char c in "abcdefghijklmno")     // 15 chars
            f.HandleInput(c.ToString());
        Assert.Equal("ABCDEFGHIJKL", f.PilotName);  // capped at 12, upper-cased
        f.HandleInput("Backspace");
        Assert.Equal("ABCDEFGHIJK", f.PilotName);

        f.HandleInput("Return");                  // step 2 = callsign
        Assert.Equal(2, f.Step);
        f.HandleInput("1"); f.HandleInput("2"); f.HandleInput("z");
        Assert.Equal("12Z", f.Callsign);
        Assert.Equal("ABCDEFGHIJK", f.PilotName); // name unaffected by callsign entry
    }

    [Fact]
    public void Non_alphanumeric_keys_are_absorbed_without_mutating_text()
    {
        var f = new PilotCreationFlow();
        f.Begin();
        f.HandleInput("A");
        Assert.Equal(PilotCreationFlow.Result.Handled, f.HandleInput("Space"));
        Assert.Equal(PilotCreationFlow.Result.Handled, f.HandleInput("Tab"));
        Assert.Equal("A", f.PilotName);
    }

    [Fact]
    public void F1_signals_OpenHelp_without_changing_step()
    {
        var f = new PilotCreationFlow();
        f.Begin();
        Assert.Equal(PilotCreationFlow.Result.OpenHelp, f.HandleInput("F1"));
        Assert.Equal(1, f.Step);
    }

    [Fact]
    public void Escape_step1_aborts_and_clears_text()
    {
        var f = new PilotCreationFlow();
        f.Begin();
        f.HandleInput("A");
        f.HandleInput("Escape");
        Assert.Equal(0, f.Step);
        Assert.Equal("", f.PilotName);
        Assert.Equal("", f.Callsign);
    }

    [Fact]
    public void Escape_below_step3_resets_difficulty_default()
    {
        var f = new PilotCreationFlow();
        f.Begin();
        f.HandleInput("Return");   // step 2
        f.HandleInput("Return");   // step 3 (difficulty)
        f.HandleInput("Down");     // difficulty 3 → 4
        Assert.Equal(4, f.DifficultyFieldId);
        f.HandleInput("Escape");   // step 3 → 2 (< 3) → difficulty reset to 3
        Assert.Equal(2, f.Step);
        Assert.Equal(3, f.DifficultyFieldId);
    }

    [Fact]
    public void Difficulty_nav_wraps_1_to_5()
    {
        var f = new PilotCreationFlow();
        f.Begin();
        f.HandleInput("Return");   // step 2
        f.HandleInput("Return");   // step 3
        f.HandleInput("Up");       // 3 → 2
        f.HandleInput("Up");       // 2 → 1
        f.HandleInput("Up");       // 1 → 5 (wrap)
        Assert.Equal(5, f.DifficultyFieldId);
        f.HandleInput("Down");     // 5 → 1 (wrap)
        Assert.Equal(1, f.DifficultyFieldId);
    }

    [Fact]
    public void Confirm_at_step4_signals_Confirm_and_resets()
    {
        var f = new PilotCreationFlow();
        f.Begin();
        f.HandleInput("Return");   // step 2
        f.HandleInput("Return");   // step 3
        Assert.Equal(PilotCreationFlow.Result.Confirm, f.HandleInput("Return")); // step 4 accept
        Assert.Equal(0, f.Step);
        Assert.Equal(3, f.DifficultyFieldId);
    }

    [Fact]
    public void Step4_abort_mission_field_resets_without_confirm()
    {
        var f = new PilotCreationFlow();
        f.Begin();
        f.HandleInput("Return");   // step 2
        f.HandleInput("Return");   // step 3
        f.SetDifficultyField(5);   // ABORT MISSION
        Assert.Equal(PilotCreationFlow.Result.Handled, f.HandleInput("Return"));
        Assert.Equal(0, f.Step);
        Assert.Equal(3, f.DifficultyFieldId);
    }

    [Fact]
    public void SetIdentity_sets_name_and_callsign()
    {
        var f = new PilotCreationFlow();
        f.SetIdentity("VETERAN", "VET");
        Assert.Equal("VETERAN", f.PilotName);
        Assert.Equal("VET", f.Callsign);
    }
}
