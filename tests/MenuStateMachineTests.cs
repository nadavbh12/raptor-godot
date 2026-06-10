using Raptor.Sim;
using Raptor.View;
using System;
using System.IO;
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
    public void Down_advances_item_with_wrap_skipping_RETURN_when_not_in_game()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < m.VisibleItemCount; i++)
            m.HandleInput("Down", 0);
        Assert.Equal(0, m.CurrentItem);  // wrapped back to NEW
    }

    [Fact]
    public void Up_wraps_to_QUIT_skipping_RETURN_when_not_in_game()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Up", 0);
        // C only shows MAIN_RETURN when ingameflag. From cold launch we wrap
        // to QUIT (item 5), not RETURN (item 6).
        Assert.Equal(MenuStateMachine.QuitItemIndex, m.CurrentItem);
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

    [Fact]
    public void Mission_complete_plays_landing_then_returns_to_hangar()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Return", 10);
        m.HandleInput("A", 12);        // C requires a non-empty name to advance
        m.HandleInput("Return", 20);
        m.HandleInput("Return", 30);
        m.HandleInput("Return", 40);
        m.HandleInput("Down", 200);
        m.HandleInput("Return", 210);
        m.HandleInput("Return", 220);
        Assert.True(m.InGame);

        // C plays INTRO_Landing (ship lands) after each cleared wave (WINDOWS.C:1863)
        // before the next hangar. InGame ends immediately; the hangar is deferred until
        // the landing movie finishes.
        m.CompleteMission(500);
        Assert.False(m.InGame);
        Assert.Equal(WinState.Landing, m.State);

        Assert.False(m.CompleteCutsceneIfDone(500 + CutsceneTimings.LandingTotal - 1));
        Assert.Equal(WinState.Landing, m.State);

        Assert.True(m.CompleteCutsceneIfDone(500 + CutsceneTimings.LandingTotal));
        Assert.Equal(WinState.Hangar, m.State);
        Assert.Equal(1, m.HangarPosition);
    }

    [Fact]
    public void Player_death_exits_ingame_to_death_state()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Return", 10);
        m.HandleInput("A", 12);        // C requires a non-empty name to advance
        m.HandleInput("Return", 20);
        m.HandleInput("Return", 30);
        m.HandleInput("Return", 40);
        m.HandleInput("Down", 200);
        m.HandleInput("Return", 210);
        m.HandleInput("Return", 220);
        Assert.True(m.InGame);

        m.PlayerDied(500);

        Assert.False(m.InGame);
        Assert.Equal(WinState.Death, m.State);
    }

    [Fact]
    public void Final_wave_completion_plays_victory_then_returns_to_menu()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);

        // Clearing the episode's final wave plays INTRO_EndGame's cinematic (Game1End +
        // Landing), then returns to the main menu (vs a normal wave → Landing → Hangar).
        m.CompleteMission(500, finalWave: true);
        Assert.Equal(WinState.Victory, m.State);

        Assert.False(m.CompleteCutsceneIfDone(500 + CutsceneTimings.VictoryTotal - 1));
        Assert.Equal(WinState.Victory, m.State);

        Assert.True(m.CompleteCutsceneIfDone(500 + CutsceneTimings.VictoryTotal));
        Assert.Equal(WinState.Menu, m.State);
    }

    [Fact]
    public void Episode1_final_wave_is_the_ninth()
    {
        // Episode 1 ships MAP1G1..MAP9G1; victory triggers on the last (C: game_wave==dwrap).
        Assert.False(WaveController.IsEpisodeFinalWave(1, 0));
        Assert.False(WaveController.IsEpisodeFinalWave(8, 0));
        Assert.True(WaveController.IsEpisodeFinalWave(9, 0));
    }

    [Fact]
    public void Startup_intro_plays_then_returns_to_main_menu()
    {
        var m = new MenuStateMachine();
        m.StartIntro(0);
        Assert.Equal(WinState.Intro, m.State);

        Assert.False(m.CompleteCutsceneIfDone(CutsceneTimings.IntroTotal - 1));
        Assert.Equal(WinState.Intro, m.State);

        Assert.True(m.CompleteCutsceneIfDone(CutsceneTimings.IntroTotal));
        Assert.Equal(WinState.Menu, m.State);
    }

    [Fact]
    public void Skip_cutscene_jumps_intro_straight_to_menu()
    {
        var m = new MenuStateMachine();
        m.StartIntro(0);
        Assert.True(m.SkipCutscene(5));        // any key during the attract intro
        Assert.Equal(WinState.Menu, m.State);

        // Outside a cutscene it is a no-op.
        Assert.False(m.SkipCutscene(10));
        Assert.Equal(WinState.Menu, m.State);
    }

    [Fact]
    public void Startup_intro_plays_only_in_interactive_runs()
    {
        // Interactive launch (no playthrough, not skipped) → play the attract intro.
        Assert.True(MenuController.ShouldPlayStartupIntro(playthroughActive: false, skipIntroEnv: false));
        // Any parity/test harness drives a playthrough → must go straight to the menu.
        Assert.False(MenuController.ShouldPlayStartupIntro(playthroughActive: true, skipIntroEnv: false));
        // Explicit RAPTOR_SKIPINTRO (faithful to C) → skip.
        Assert.False(MenuController.ShouldPlayStartupIntro(playthroughActive: false, skipIntroEnv: true));
    }

    [Fact]
    public void Death_movie_completion_returns_to_main_menu()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Return", 10);
        m.HandleInput("A", 12);        // C requires a non-empty name to advance
        m.HandleInput("Return", 20);
        m.HandleInput("Return", 30);
        m.HandleInput("Return", 40);
        m.HandleInput("Down", 200);
        m.HandleInput("Return", 210);
        m.HandleInput("Return", 220);
        m.PlayerDied(500);

        bool beforeEnd = m.CompleteCutsceneIfDone(500 + CutsceneTimings.DeathTotal - 1);
        bool atEnd = m.CompleteCutsceneIfDone(500 + CutsceneTimings.DeathTotal);

        Assert.False(beforeEnd);
        Assert.True(atEnd);
        Assert.Equal(WinState.Menu, m.State);
        Assert.False(m.InGame);
    }

    // -------------------------------------------------------------------------
    // Stage 5a: F1 → HELP transition (help_f1 script)
    // -------------------------------------------------------------------------

    [Fact]
    public void F1_in_Menu_enters_Help_state()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        bool transitioned = m.HandleInput("F1", 50);
        Assert.True(transitioned);
        Assert.Equal(WinState.Help, m.State);
        Assert.Equal("HELP1_TXT", m.HelpTextName);
    }

    [Fact]
    public void F1_in_Menu_anchors_with_HelpFadeFrames_offset()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("F1", 50);
        Assert.Equal(50 + MenuStateMachine.HelpFadeFrames, m.StateEnteredFrame);
    }

    [Fact]
    public void Pilot_creation_tracks_name_and_callsign_text()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);

        m.HandleInput("Return", 10);
        m.HandleInput("t", 11);
        m.HandleInput("E", 12);
        m.HandleInput("s", 13);
        m.HandleInput("T", 14);
        Assert.Equal("TEST", m.PilotName);
        Assert.Equal("", m.Callsign);

        m.HandleInput("Return", 20);
        m.HandleInput("T", 21);
        m.HandleInput("1", 22);
        Assert.Equal("TEST", m.PilotName);
        Assert.Equal("T1", m.Callsign);
    }

    [Fact]
    public void Pointer_click_on_main_menu_new_enters_pilot_creation()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);

        bool handled = m.HandlePointerClick(120, 94, 10);

        Assert.True(handled);
        Assert.Equal(1, m.PilotCreateStep);
    }

    [Fact]
    public void Pointer_click_can_focus_registration_fields()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Return", 10);

        Assert.True(m.HandlePointerClick(190, 149, 11));
        Assert.Equal(2, m.PilotCreateStep);

        Assert.True(m.HandlePointerClick(190, 133, 12));
        Assert.Equal(1, m.PilotCreateStep);
    }

    [Fact]
    public void Difficulty_arrows_move_selected_option()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Return", 10);
        m.HandleInput("A", 12);        // C requires a non-empty name to advance
        m.HandleInput("Return", 20);
        m.HandleInput("Return", 30);
        Assert.Equal(3, m.DifficultyFieldId);

        m.HandleInput("Down", 31);
        Assert.Equal(4, m.DifficultyFieldId);

        m.HandleInput("Up", 32);
        Assert.Equal(3, m.DifficultyFieldId);
    }

    [Fact]
    public void Pointer_click_on_difficulty_accepts_selected_option()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Return", 10);
        m.HandleInput("A", 12);        // C requires a non-empty name to advance
        m.HandleInput("Return", 20);
        m.HandleInput("Return", 30);

        bool handled = m.HandlePointerClick(170, 130, 40);

        Assert.True(handled);
        Assert.Equal(WinState.Hangar, m.State);
        Assert.Equal(0, m.PilotCreateStep);
    }

    [Fact]
    public void Escape_in_pilot_creation_steps_back_one_dialog()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Return", 10);
        m.HandleInput("N", 11);
        m.HandleInput("Return", 20);
        m.HandleInput("C", 21);
        m.HandleInput("Return", 30);
        Assert.Equal(3, m.PilotCreateStep);

        m.HandleInput("Escape", 31);
        Assert.Equal(2, m.PilotCreateStep);
        Assert.Equal("N", m.PilotName);
        Assert.Equal("C", m.Callsign);

        m.HandleInput("Escape", 32);
        Assert.Equal(1, m.PilotCreateStep);

        m.HandleInput("Escape", 33);
        Assert.Equal(0, m.PilotCreateStep);
        Assert.Equal("", m.PilotName);
        Assert.Equal("", m.Callsign);
        Assert.Equal(WinState.Menu, m.State);
    }

    [Fact]
    public void Escape_in_sector_select_returns_to_hangar()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Return", 10);
        m.HandleInput("A", 12);        // C requires a non-empty name to advance
        m.HandleInput("Return", 20);
        m.HandleInput("Return", 30);
        m.HandleInput("Return", 40);
        m.HandleInput("Down", 150);
        m.HandleInput("Return", 160);
        Assert.Equal(WinState.Unknown, m.State);
        Assert.True(m.InSectorSelect);

        bool transitioned = m.HandleInput("Escape", 170);

        Assert.True(transitioned);
        Assert.Equal(WinState.Hangar, m.State);
        Assert.False(m.InSectorSelect);
    }

    [Fact]
    public void Escape_in_hangar_returns_to_main_menu()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Return", 10);
        m.HandleInput("A", 12);        // C requires a non-empty name to advance
        m.HandleInput("Return", 20);
        m.HandleInput("Return", 30);
        m.HandleInput("Return", 40);
        Assert.Equal(WinState.Hangar, m.State);

        bool transitioned = m.HandleInput("Escape", 150);

        Assert.True(transitioned);
        Assert.Equal(WinState.Menu, m.State);
        Assert.Equal(0, m.CurrentItem);
    }

    [Fact]
    public void Return_in_Help_exits_to_Unknown()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("F1", 50);
        bool transitioned = m.HandleInput("Return", 120);
        Assert.True(transitioned);
        Assert.Equal(WinState.Unknown, m.State);
    }

    [Fact]
    public void Escape_in_Help_exits_to_Unknown()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("F1", 50);
        bool transitioned = m.HandleInput("Escape", 120);
        Assert.True(transitioned);
        Assert.Equal(WinState.Unknown, m.State);
    }

    [Fact]
    public void Help_anchor_unchanged_when_exiting_to_Unknown()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("F1", 50);
        int expectedAnchor = 50 + MenuStateMachine.HelpFadeFrames;
        m.HandleInput("Return", 120);
        // Anchor must not change on transition to Unknown.
        Assert.Equal(expectedAnchor, m.StateEnteredFrame);
    }

    // Help pagination — mirrors HELP.C/HELP_Win modular page cycle through
    // 39 items (HELP1_TXT through VEND00_TXT, GLB indices 0x12..0x38).
    // Keys mapped from HELP.C:91-121: SC_HOME→0, SC_F1→1 (note: not 0!),
    // SC_END→maxpages-1, SC_DOWN/RIGHT/PAGEDN→++, SC_UP/LEFT/PAGEUP→--.
    [Fact]
    public void Help_page_order_starts_with_HELP1_TXT_and_has_39_entries()
    {
        Assert.Equal(39, MenuStateMachine.HelpPageOrder.Count);
        Assert.Equal("HELP1_TXT", MenuStateMachine.HelpPageOrder[0]);
        Assert.Equal("STORY1_TXT", MenuStateMachine.HelpPageOrder[1]);
        Assert.Equal("RAP1_TXT", MenuStateMachine.HelpPageOrder[30]);
        Assert.Equal("VEND00_TXT", MenuStateMachine.HelpPageOrder[38]);
    }

    [Fact]
    public void Down_in_Help_advances_page_and_updates_help_text_name()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("F1", 50);                // enter HELP at page 0 = HELP1_TXT
        Assert.Equal(WinState.Help, m.State);
        Assert.Equal(0, m.HelpPageIndex);
        Assert.Equal("HELP1_TXT", m.HelpTextName);

        m.HandleInput("Down", 100);
        Assert.Equal(1, m.HelpPageIndex);
        Assert.Equal("STORY1_TXT", m.HelpTextName);
    }

    [Fact]
    public void Up_in_Help_wraps_to_last_page_from_page_zero()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("F1", 50);                // page 0
        m.HandleInput("Up", 100);
        Assert.Equal(38, m.HelpPageIndex);
        Assert.Equal("VEND00_TXT", m.HelpTextName);
    }

    [Fact]
    public void PageDown_in_Help_advances_page()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("F1", 50);
        m.HandleInput("PageDown", 100);
        Assert.Equal(1, m.HelpPageIndex);
    }

    [Fact]
    public void Right_in_Help_advances_page()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("F1", 50);
        m.HandleInput("Right", 100);
        Assert.Equal(1, m.HelpPageIndex);
    }

    [Fact]
    public void Home_in_Help_jumps_to_page_zero()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("F1", 50);
        m.HandleInput("Down", 60);
        m.HandleInput("Down", 70);
        Assert.Equal(2, m.HelpPageIndex);

        m.HandleInput("Home", 80);
        Assert.Equal(0, m.HelpPageIndex);
        Assert.Equal("HELP1_TXT", m.HelpTextName);
    }

    [Fact]
    public void F1_in_Help_jumps_to_page_one_matching_HELP_C_behavior()
    {
        // C HELP.C:98-101: SC_F1 sets curpage = 1, not 0. So pressing F1 while
        // already inside the Help window jumps to page index 1 = STORY1_TXT.
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("F1", 50);                // enter HELP at page 0
        Assert.Equal(0, m.HelpPageIndex);

        m.HandleInput("F1", 100);
        Assert.Equal(1, m.HelpPageIndex);
        Assert.Equal("STORY1_TXT", m.HelpTextName);
    }

    [Fact]
    public void End_in_Help_jumps_to_last_page()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("F1", 50);
        m.HandleInput("End", 100);
        Assert.Equal(38, m.HelpPageIndex);
        Assert.Equal("VEND00_TXT", m.HelpTextName);
    }

    // F1 entry points from C — each sub-screen has its own context-specific
    // help topic, all routing through HELP_Win.
    [Fact]
    public void F1_in_Hangar_enters_Help_at_HANGHLP1_TXT()
    {
        var m = ReachHangar();
        Assert.Equal(WinState.Hangar, m.State);

        bool transitioned = m.HandleInput("F1", 200);

        Assert.True(transitioned);
        Assert.Equal(WinState.Help, m.State);
        Assert.Equal("HANGHLP1_TXT", m.HelpTextName);
        Assert.Equal(24, m.HelpPageIndex);   // HANGHLP1_TXT is page 24 in HelpPageOrder
    }

    [Fact]
    public void F1_in_Store_enters_Help_at_STORHLP1_TXT()
    {
        var m = ReachHangar();
        m.HandleInput("Return", 200);        // hangarPos=1 SUPPLIES → STORE
        Assert.Equal(WinState.Store, m.State);

        bool transitioned = m.HandleInput("F1", 210);

        Assert.True(transitioned);
        Assert.Equal(WinState.Help, m.State);
        Assert.Equal("STORHLP1_TXT", m.HelpTextName);
        Assert.Equal(28, m.HelpPageIndex);
    }

    [Fact]
    public void F1_in_SectorSelect_enters_Help_at_COMPHLP1_TXT()
    {
        var m = ReachHangar();
        // Navigate hangar to MISSION (pos=0), then Return → sector select.
        m.HandleInput("Down", 200);          // pos 1→0=MISSION
        m.HandleInput("Return", 210);
        Assert.Equal(WinState.Unknown, m.State);
        Assert.True(m.InSectorSelect);

        bool transitioned = m.HandleInput("F1", 220);

        Assert.True(transitioned);
        Assert.Equal(WinState.Help, m.State);
        Assert.Equal("COMPHLP1_TXT", m.HelpTextName);
        Assert.Equal(26, m.HelpPageIndex);
    }

    [Fact]
    public void F1_in_pilot_creation_enters_Help_at_NEWPLAY1_TXT()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Return", 50);         // NEW → step 1
        Assert.Equal(1, m.PilotCreateStep);

        bool transitioned = m.HandleInput("F1", 60);

        Assert.True(transitioned);
        Assert.Equal(WinState.Help, m.State);
        Assert.Equal("NEWPLAY1_TXT", m.HelpTextName);
        Assert.Equal(20, m.HelpPageIndex);
    }

    // IReadOnlyList<ObjType> has no IndexOf; small local helper for store tests.
    private static int IndexOf(System.Collections.Generic.IReadOnlyList<ObjType> list, ObjType t)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == t) return i;
        return -1;
    }

    // Helper: walk a fresh state machine through the pilot-creation flow
    // until it lands in WinState.Hangar. Used by the F1 entry-point tests.
    private static MenuStateMachine ReachHangar()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Return", 10);   // NEW → step 1
        m.HandleInput("A", 12);        // type a name (C requires it to advance)
        m.HandleInput("Return", 20);   // confirm name → step 2
        m.HandleInput("Return", 30);   // confirm callsign → step 3
        m.HandleInput("Return", 40);   // accept difficulty → Hangar
        return m;
    }

    // Task 5.2: Return inside the store triggers Buy (Buy mode) / Sell (Sell mode)
    // against the live score wired via GetScore/SetScore. Drive the full state
    // machine into the store and verify the Return-key buy path deducts the score.
    [Fact]
    public void Return_in_store_buy_mode_buys_current_item_and_deducts_live_score()
    {
        var m = ReachHangar();          // hangarPos == 1 (SUPPLIES)
        uint score = 1_000_000;
        m.GetScore = () => score;
        m.SetScore = v => score = v;

        m.HandleInput("Return", 50);    // SUPPLIES → STORE
        Assert.Equal(WinState.Store, m.State);
        Assert.NotNull(m.Store);

        // Land the cursor on a concrete, affordable buyable.
        var store = m.Store!;
        int idx = IndexOf(store.BuyItems, ObjType.PlasmaGuns);
        Assert.True(idx >= 0);
        m.HandleInput("Right", 51);     // dismiss greeting (CurItem stays 0)
        for (int i = 0; i < idx; i++) m.HandleInput("Right", 52 + i);
        Assert.Equal(ObjType.PlasmaGuns, store.CurrentObject);

        bool stayed = m.HandleInput("Return", 100);   // BUY
        Assert.False(stayed);                          // stays in store, re-renders
        Assert.Equal(WinState.Store, m.State);
        Assert.Equal(1_000_000u - 78_800u, score);     // PlasmaGuns cost deducted
        Assert.True(m.Inventory.IsEquip(ObjType.PlasmaGuns));
    }

    [Fact]
    public void Return_in_store_uses_fallback_score_when_no_accessors_wired()
    {
        // Headless / no-WaveController: GetScore/SetScore null → fallback (10000).
        var m = ReachHangar();
        m.HandleInput("Return", 50);    // → STORE
        var store = m.Store!;
        // Cheapest catalog buyable affordable from 10000 is Energy (10000 exactly).
        int idx = IndexOf(store.BuyItems, ObjType.Energy);
        Assert.True(idx >= 0);
        m.HandleInput("Right", 51);
        for (int i = 0; i < idx; i++) m.HandleInput("Right", 52 + i);
        Assert.Equal(ObjType.Energy, store.CurrentObject);

        m.HandleInput("Return", 100);   // buy Energy for 10000 → fallback score 0
        Assert.Equal(0, store.Money);   // Money reads the fallback via the accessor
        Assert.True(m.Inventory.IsEquip(ObjType.Energy));
    }

    [Fact]
    public void Order_entry_sets_curpage_to_RAP1_TXT_index()
    {
        // ORDER routes to HELP_Win("RAP1_TXT") (WINDOWS.C:2126). That's
        // page index 30 in the table — Down from there must advance to
        // RAP2_TXT (index 31), not wrap to HELP2_TXT or similar.
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < 3; i++) m.HandleInput("Down", 0);  // ORDER
        m.HandleInput("Return", 50);
        Assert.Equal(WinState.Help, m.State);
        Assert.Equal("RAP1_TXT", m.HelpTextName);
        Assert.Equal(30, m.HelpPageIndex);

        m.HandleInput("Down", 100);
        Assert.Equal(31, m.HelpPageIndex);
        Assert.Equal("RAP2_TXT", m.HelpTextName);
    }

    // Regression: in C, WIN_Credits/HELP_Win are blocking sub-calls — control
    // returns into the WIN_MainMenu input loop and main-menu keys still work.
    // Godot models them as a transition out to WinState.Unknown (to match C
    // parity.c which emits win=UNKNOWN at fc 140/210 in credits.parity.txt).
    // The renderer draws the main menu visual when Unknown && !InSectorSelect,
    // so the screen looks normal — but HandleInput must also keep routing keys
    // to the main-menu handler or the menu appears stuck after Esc from
    // Credits/Order/Help (interactive-only regression — scripted parity ended
    // its run at Esc and never noticed).
    [Fact]
    public void Down_after_Credits_exit_moves_main_menu_selection()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < 4; i++) m.HandleInput("Down", 0);
        Assert.Equal(MenuStateMachine.CreditsItemIndex, m.CurrentItem);
        m.HandleInput("Return", 100);           // enter CREDITS
        m.HandleInput("Return", 200);           // exit CREDITS → UNKNOWN
        Assert.Equal(WinState.Unknown, m.State);

        m.HandleInput("Down", 210);
        Assert.Equal(MenuStateMachine.QuitItemIndex, m.CurrentItem);
    }

    [Fact]
    public void Down_after_Help_exit_moves_main_menu_selection()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("F1", 50);                // enter HELP via F1
        m.HandleInput("Escape", 120);           // exit HELP → UNKNOWN
        Assert.Equal(WinState.Unknown, m.State);
        int before = m.CurrentItem;

        m.HandleInput("Down", 130);
        int n = m.VisibleItemCount;
        Assert.Equal((before + 1) % n, m.CurrentItem);
    }

    [Fact]
    public void Return_after_Order_exit_reopens_help_via_main_menu()
    {
        // Order item routes to WinState.Help with RAP1_TXT. After Esc the
        // user lands in Unknown but should still be able to Return on the
        // Order item again (no Down needed — selection is preserved).
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < 3; i++) m.HandleInput("Down", 0);
        Assert.Equal(MenuStateMachine.OrderItemIndex, m.CurrentItem);
        m.HandleInput("Return", 50);            // enter HELP (RAP1_TXT)
        Assert.Equal(WinState.Help, m.State);
        m.HandleInput("Escape", 100);           // exit HELP → UNKNOWN
        Assert.Equal(WinState.Unknown, m.State);
        Assert.Equal(MenuStateMachine.OrderItemIndex, m.CurrentItem);

        bool transitioned = m.HandleInput("Return", 110);
        Assert.True(transitioned);
        Assert.Equal(WinState.Help, m.State);
        Assert.Equal("RAP1_TXT", m.HelpTextName);
    }

    [Fact]
    public void Escape_after_Credits_exit_returns_to_Menu_state()
    {
        // Pressing Escape from the main menu visual rebuilds WIN_MainMenu in
        // C; in Godot we route through EnterMenu, which transitions us back
        // to the proper WinState.Menu (so subsequent parity emission matches
        // a normal main-menu session, not a stale Unknown).
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < 4; i++) m.HandleInput("Down", 0);
        m.HandleInput("Return", 100);           // enter CREDITS
        m.HandleInput("Return", 200);           // exit CREDITS → UNKNOWN
        Assert.Equal(WinState.Unknown, m.State);

        m.HandleInput("Escape", 210);
        Assert.Equal(WinState.Menu, m.State);
    }

    // -------------------------------------------------------------------------
    // Stage 5a: ORDER item → HELP (order script)
    // -------------------------------------------------------------------------

    [Fact]
    public void Three_Downs_from_zero_land_on_order_item()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Down", 0);
        m.HandleInput("Down", 0);
        m.HandleInput("Down", 0);
        Assert.Equal(MenuStateMachine.OrderItemIndex, m.CurrentItem);
    }

    [Fact]
    public void Two_Downs_from_zero_land_on_options_item()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);

        m.HandleInput("Down", 0);
        m.HandleInput("Down", 0);

        Assert.Equal(MenuStateMachine.OptionsItemIndex, m.CurrentItem);
    }

    [Fact]
    public void Return_on_options_item_opens_options_without_reanchoring_menu()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(42);
        for (int i = 0; i < MenuStateMachine.OptionsItemIndex; i++)
            m.HandleInput("Down", 50 + i);

        bool handled = m.HandleInput("Return", 80);

        Assert.True(handled);
        Assert.True(m.InOptions);
        Assert.Equal(WinState.Menu, m.State);
        Assert.Equal(42, m.StateEnteredFrame);
        Assert.Equal(0, m.OptionsField);
    }

    [Fact]
    public void Escape_in_options_returns_to_main_menu_selection()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < MenuStateMachine.OptionsItemIndex; i++)
            m.HandleInput("Down", i);
        m.HandleInput("Return", 10);
        Assert.True(m.InOptions);

        bool handled = m.HandleInput("Escape", 20);

        Assert.True(handled);
        Assert.False(m.InOptions);
        Assert.Equal(WinState.Menu, m.State);
        Assert.Equal(MenuStateMachine.OptionsItemIndex, m.CurrentItem);
    }

    [Fact]
    public void Options_keyboard_controls_detail_and_volume_fields()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < MenuStateMachine.OptionsItemIndex; i++)
            m.HandleInput("Down", i);
        m.HandleInput("Return", 10);
        Assert.True(m.OptionDetailHigh);

        m.HandleInput("Return", 11);
        Assert.False(m.OptionDetailHigh);

        m.HandleInput("Down", 12);      // C kbactive: first arrow primes (stays 0)
        Assert.Equal(0, m.OptionsField);
        m.HandleInput("Down", 13);
        Assert.Equal(1, m.OptionsField);
        int music = m.OptionMusicVolume;
        // Round-trip so the assertion holds regardless of the default (which is
        // the C first-run max of 127, where Right would clamp).
        m.HandleInput("Left", 14);
        Assert.Equal(music - 8, m.OptionMusicVolume);
        m.HandleInput("Right", 15);
        Assert.Equal(music, m.OptionMusicVolume);

        m.HandleInput("Down", 16);
        Assert.Equal(2, m.OptionsField);
        int fx = m.OptionFxVolume;
        m.HandleInput("Left", 16);
        Assert.Equal(fx - 8, m.OptionFxVolume);
    }

    [Fact]
    public void Options_volume_defaults_to_full()
    {
        // WINDOWS.C:39 opt_vol initializes to {127,127}; FX.C:906/973 default
        // both globals to 127. A fresh install shows both knobs fully right.
        var m = new MenuStateMachine();
        Assert.Equal(127, m.OptionMusicVolume);
        Assert.Equal(127, m.OptionFxVolume);
    }

    [Fact]
    public void Return_on_order_item_enters_Help_state_with_order_text()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < MenuStateMachine.OrderItemIndex; i++)
            m.HandleInput("Down", 0);
        bool transitioned = m.HandleInput("Return", 80);
        Assert.True(transitioned);
        // C version calls HELP_Win(RAP1_TXT) for ORDER — win-state is HELP, not ORDER.
        Assert.Equal(WinState.Help, m.State);
        Assert.Equal("RAP1_TXT", m.HelpTextName);
    }

    [Fact]
    public void Return_on_order_item_anchors_with_HelpFadeFrames()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < MenuStateMachine.OrderItemIndex; i++)
            m.HandleInput("Down", 0);
        m.HandleInput("Return", 80);
        Assert.Equal(80 + MenuStateMachine.HelpFadeFrames, m.StateEnteredFrame);
    }

    // -------------------------------------------------------------------------
    // Stage 5a: LOAD item → stays in MENU (load_mission script)
    // -------------------------------------------------------------------------

    [Fact]
    public void One_Down_from_zero_lands_on_load_item()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        m.HandleInput("Down", 0);
        Assert.Equal(MenuStateMachine.LoadItemIndex, m.CurrentItem);
    }

    [Fact]
    public void Return_on_load_item_stays_in_Menu()
    {
        using var dir = new PilotSaveStoreTests.TempDir();
        PilotSaveStoreTests.WriteFakePilot(dir.Path, slot: 0, name: "ALICE", callsign: "ACE", idPic: 0, score: 1000);
        var m = new MenuStateMachine { PilotSaveDirectory = dir.Path };
        m.EnterMenu(0);
        m.HandleInput("Down", 0);
        bool transitioned = m.HandleInput("Return", 93);
        Assert.True(transitioned);
        Assert.Equal(WinState.Menu, m.State);
        Assert.True(m.InLoadMission);
    }

    [Fact]
    public void Return_on_load_item_keeps_Menu_anchor_unchanged()
    {
        using var dir = new PilotSaveStoreTests.TempDir();
        PilotSaveStoreTests.WriteFakePilot(dir.Path, slot: 0, name: "ALICE", callsign: "ACE", idPic: 0, score: 1000);
        var m = new MenuStateMachine { PilotSaveDirectory = dir.Path };
        m.EnterMenu(42);
        m.HandleInput("Down", 0);
        m.HandleInput("Return", 93);
        // Anchor must remain at the EnterMenu anchor value.
        Assert.Equal(42, m.StateEnteredFrame);
    }

    [Fact]
    public void Return_on_load_item_with_no_pilots_shows_no_pilots_popup()
    {
        using var dir = new PilotSaveStoreTests.TempDir();
        var m = new MenuStateMachine { PilotSaveDirectory = dir.Path };
        m.EnterMenu(42);
        m.HandleInput("Down", 0);

        bool transitioned = m.HandleInput("Return", 93);

        // C WINDOWS.C:2104-2106: RAP_LoadWin returns -1 → WIN_Msg("No Pilots to Load")
        Assert.True(transitioned);
        Assert.False(m.InLoadMission);
        Assert.True(m.InWinMsg);
        Assert.Equal("No Pilots to Load", m.WinMsgText);
        Assert.Empty(m.LoadMissionPilots);
    }

    [Fact]
    public void Any_key_dismisses_no_pilots_popup()
    {
        using var dir = new PilotSaveStoreTests.TempDir();
        var m = new MenuStateMachine { PilotSaveDirectory = dir.Path };
        m.EnterMenu(42);
        m.HandleInput("Down", 0);
        m.HandleInput("Return", 93);
        Assert.True(m.InWinMsg);

        m.HandleInput("Return", 100);

        Assert.False(m.InWinMsg);
        Assert.Equal(WinState.Menu, m.State);
        Assert.Equal(42, m.StateEnteredFrame);
    }

    [Fact]
    public void Escape_in_load_mission_closes_dialog_and_keeps_Menu_anchor()
    {
        using var dir = new PilotSaveStoreTests.TempDir();
        PilotSaveStoreTests.WriteFakePilot(dir.Path, slot: 0, name: "ALICE", callsign: "ACE", idPic: 0, score: 1000);
        var m = new MenuStateMachine { PilotSaveDirectory = dir.Path };
        m.EnterMenu(42);
        m.HandleInput("Down", 0);
        m.HandleInput("Return", 93);

        bool transitioned = m.HandleInput("Escape", 100);

        Assert.True(transitioned);
        Assert.False(m.InLoadMission);
        Assert.Equal(WinState.Menu, m.State);
        Assert.Equal(42, m.StateEnteredFrame);
    }

    [Fact]
    public void Return_on_load_item_loads_first_available_pilot_summary()
    {
        using var dir = new PilotSaveStoreTests.TempDir();
        PilotSaveStoreTests.WriteFakePilot(dir.Path, slot: 0, name: "ALICE", callsign: "ACE", idPic: 1, score: 29425);
        var m = new MenuStateMachine { PilotSaveDirectory = dir.Path };
        m.EnterMenu(0);
        m.HandleInput("Down", 0);

        m.HandleInput("Return", 93);

        Assert.True(m.InLoadMission);
        Assert.NotNull(m.LoadMissionPilot);
        Assert.Equal("ALICE", m.LoadMissionPilot.Name);
        Assert.Equal("ACE", m.LoadMissionPilot.Callsign);
        Assert.Equal("0029425", m.LoadMissionPilot.CreditsText);
        Assert.Equal(1, m.LoadMissionPilot.IdPic);
    }

    [Fact]
    public void Down_in_load_mission_advances_to_next_pilot()
    {
        var m = EnterLoadMissionWithThreePilots(out var dir);
        using (dir)
        {
            Assert.Equal("ALICE", m.LoadMissionPilot!.Name);

            m.HandleInput("Down", 100);
            Assert.Equal("BOB", m.LoadMissionPilot!.Name);

            m.HandleInput("Down", 110);
            Assert.Equal("CAROL", m.LoadMissionPilot!.Name);
        }
    }

    [Fact]
    public void Down_from_last_pilot_wraps_to_first()
    {
        var m = EnterLoadMissionWithThreePilots(out var dir);
        using (dir)
        {
            m.HandleInput("Down", 100);
            m.HandleInput("Down", 110);
            m.HandleInput("Down", 120);
            Assert.Equal("ALICE", m.LoadMissionPilot!.Name);
        }
    }

    [Fact]
    public void Up_from_first_pilot_wraps_to_last()
    {
        var m = EnterLoadMissionWithThreePilots(out var dir);
        using (dir)
        {
            m.HandleInput("Up", 100);
            Assert.Equal("CAROL", m.LoadMissionPilot!.Name);
        }
    }

    [Fact]
    public void PageDown_and_Left_navigate_like_Down_in_load_mission()
    {
        var m = EnterLoadMissionWithThreePilots(out var dir);
        using (dir)
        {
            m.HandleInput("PageDown", 100);
            Assert.Equal("BOB", m.LoadMissionPilot!.Name);

            m.HandleInput("Left", 110);
            Assert.Equal("CAROL", m.LoadMissionPilot!.Name);
        }
    }

    [Fact]
    public void PageUp_and_Right_navigate_like_Up_in_load_mission()
    {
        var m = EnterLoadMissionWithThreePilots(out var dir);
        using (dir)
        {
            m.HandleInput("PageUp", 100);
            Assert.Equal("CAROL", m.LoadMissionPilot!.Name);

            m.HandleInput("Right", 110);
            Assert.Equal("BOB", m.LoadMissionPilot!.Name);
        }
    }

    [Fact]
    public void F2_in_hangar_opens_AskBool_save_prompt()
    {
        var m = HangarReadyMachineWithSaveDir(out var dir);
        using (dir)
        {
            m.HandleInput("F2", 100);

            Assert.True(m.InAskBool);
            Assert.Equal("Save TEST - T1 ?", m.AskBoolQuestion);
            Assert.True(m.AskBoolYesSelected);
            Assert.Equal(WinState.Hangar, m.State);
        }
    }

    // Hangar playtest bugs (2026-06-10): MAINMENU + QSAVE buttons were stubbed
    // (// MAINMENU, QSAVE: stub. return false;), so the user could not exit or
    // save via the on-screen buttons; and the save path hardcoded score:0.

    [Fact]
    public void Return_on_hangar_MAINMENU_button_exits_to_main_menu()
    {
        var m = ReachHangar();                 // hangarPos == 1 (SUPPLIES)
        m.HandleInput("Up", 50);               // SUPPLIES(1) → MAINMENU(2)
        Assert.Equal(2, m.HangarPosition);
        bool handled = m.HandleInput("Return", 60);
        Assert.True(handled);
        Assert.Equal(WinState.Menu, m.State);  // C HANG_MAIN_MENU → return to menu
    }

    [Fact]
    public void Return_on_hangar_QSAVE_button_opens_save_prompt()
    {
        var m = HangarReadyMachineWithSaveDir(out var dir);
        using (dir)
        {
            m.HandleInput("Up", 100);          // SUPPLIES(1) → MAINMENU(2)
            m.HandleInput("Up", 101);          // MAINMENU(2) → QSAVE(3)
            Assert.Equal(3, m.HangarPosition);
            bool handled = m.HandleInput("Return", 102);
            Assert.True(handled);
            Assert.True(m.InAskBool);
            Assert.Equal("Save TEST - T1 ?", m.AskBoolQuestion);
        }
    }

    [Fact]
    public void Hangar_save_persists_live_score_not_zero()
    {
        var m = HangarReadyMachineWithSaveDir(out var dir);
        using (dir)
        {
            m.GetScore = () => 33333u;
            m.HandleInput("F2", 100);          // open save prompt
            Assert.True(m.InAskBool);
            m.HandleInput("Return", 101);      // confirm YES → write the file
            var saved = PilotSaveStore.LoadAll(dir.Path);
            Assert.Single(saved);
            Assert.Equal("TEST", saved[0].Name);
            Assert.Equal(33333u, saved[0].Score);   // live score, not hardcoded 0
        }
    }

    [Fact]
    public void Lowercase_s_in_hangar_opens_save_prompt()
    {
        var m = HangarReadyMachineWithSaveDir(out var dir);
        using (dir)
        {
            m.HandleInput("s", 100);           // interactive keys arrive lowercase
            Assert.True(m.InAskBool);
            Assert.Equal("Save TEST - T1 ?", m.AskBoolQuestion);
        }
    }

    [Fact]
    public void Return_on_LOAD_dialog_applies_pilot_and_transitions_to_Hangar()
    {
        using var dir = new PilotSaveStoreTests.TempDir();
        PilotSaveStoreTests.WriteFakePilot(dir.Path, slot: 0,
            name: "VETERAN", callsign: "VET", idPic: 3, score: 42000,
            sweapon: 4, curGame: 1, gameWave: new[] { 5, 0, 0 },
            diff: new[] { 2, 2, 2, 0 }, trainFlag: false, finTrain: false);
        var m = new MenuStateMachine { PilotSaveDirectory = dir.Path };
        m.EnterMenu(0);
        m.HandleInput("Down", 0);   // → LOAD MISSION
        m.HandleInput("Return", 1); // open LOAD dialog

        PilotSaveSummary? loadedSummary = null;
        m.OnPilotLoaded += s => loadedSummary = s;

        m.HandleInput("Return", 10); // confirm selection

        Assert.False(m.InLoadMission);
        Assert.Equal(WinState.Hangar, m.State);
        Assert.Equal("VETERAN", m.PilotName);
        Assert.Equal("VET", m.Callsign);
        Assert.Equal(3, m.IdPic);
        Assert.NotNull(loadedSummary);
        Assert.Equal(42000u, loadedSummary!.Score);
    }

    [Fact]
    public void Escape_in_LOAD_dialog_does_not_apply_pilot()
    {
        using var dir = new PilotSaveStoreTests.TempDir();
        PilotSaveStoreTests.WriteFakePilot(dir.Path, slot: 0,
            name: "VETERAN", callsign: "VET", idPic: 3, score: 42000);
        var m = new MenuStateMachine { PilotSaveDirectory = dir.Path };
        m.EnterMenu(0);
        m.HandleInput("Down", 0);
        m.HandleInput("Return", 1); // open LOAD dialog

        bool fired = false;
        m.OnPilotLoaded += _ => fired = true;
        m.HandleInput("Escape", 10);

        Assert.False(m.InLoadMission);
        Assert.False(fired);
        Assert.Equal(WinState.Menu, m.State);
        Assert.Equal("", m.PilotName);
    }

    [Fact]
    public void Return_with_YES_in_AskBool_save_writes_pilot_and_closes()
    {
        var m = HangarReadyMachineWithSaveDir(out var dir);
        using (dir)
        {
            m.HandleInput("F2", 100);
            m.HandleInput("Return", 110);

            Assert.False(m.InAskBool);
            Assert.Equal(WinState.Hangar, m.State);
            var pilots = PilotSaveStore.LoadAll(dir.Path);
            Assert.Single(pilots);
            Assert.Equal("TEST", pilots[0].Name);
            Assert.Equal("T1", pilots[0].Callsign);
        }
    }

    [Fact]
    public void Left_Right_Tab_toggle_AskBool_selection()
    {
        var m = HangarReadyMachineWithSaveDir(out var dir);
        using (dir)
        {
            m.HandleInput("F2", 100);
            Assert.True(m.AskBoolYesSelected);

            m.HandleInput("Right", 110);
            Assert.False(m.AskBoolYesSelected);

            m.HandleInput("Left", 120);
            Assert.True(m.AskBoolYesSelected);

            m.HandleInput("Tab", 130);
            Assert.False(m.AskBoolYesSelected);
        }
    }

    [Fact]
    public void Return_with_NO_in_AskBool_save_closes_without_writing()
    {
        var m = HangarReadyMachineWithSaveDir(out var dir);
        using (dir)
        {
            m.HandleInput("F2", 100);
            m.HandleInput("Right", 110); // → NO
            m.HandleInput("Return", 120);

            Assert.False(m.InAskBool);
            Assert.Empty(PilotSaveStore.LoadAll(dir.Path));
        }
    }

    [Fact]
    public void Escape_in_AskBool_closes_without_writing()
    {
        var m = HangarReadyMachineWithSaveDir(out var dir);
        using (dir)
        {
            m.HandleInput("F2", 100);
            m.HandleInput("Escape", 110);

            Assert.False(m.InAskBool);
            Assert.Empty(PilotSaveStore.LoadAll(dir.Path));
        }
    }

    [Fact]
    public void Up_from_NEW_wraps_to_QUIT_when_not_in_game()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        Assert.Equal(MenuStateMachine.NewItemIndex, m.CurrentItem);

        m.HandleInput("Up", 1);

        // Wrap excludes RETURN (item 6) at cold launch — lands on QUIT (5).
        Assert.Equal(MenuStateMachine.QuitItemIndex, m.CurrentItem);
    }

    [Fact]
    public void Down_from_QUIT_wraps_to_NEW_when_not_in_game()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < MenuStateMachine.QuitItemIndex; i++)
            m.HandleInput("Down", 0);
        Assert.Equal(MenuStateMachine.QuitItemIndex, m.CurrentItem);

        m.HandleInput("Down", 1);

        Assert.Equal(MenuStateMachine.NewItemIndex, m.CurrentItem);
    }

    [Fact]
    public void Return_on_QUIT_opens_exit_to_dos_confirmation()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < MenuStateMachine.QuitItemIndex; i++)
            m.HandleInput("Down", 0);

        bool transitioned = m.HandleInput("Return", 100);

        Assert.True(transitioned);
        Assert.True(m.InAskBool);
        // WINDOWS.C:609 — WIN_AskExit calls WIN_AskBool("EXIT TO DOS") verbatim
        // (no trailing "?"; the dragbar shows the literal string).
        Assert.Equal("EXIT TO DOS", m.AskBoolQuestion);
        Assert.True(m.AskBoolYesSelected);
        Assert.False(m.QuitRequested);
    }

    [Fact]
    public void Return_YES_on_exit_to_dos_sets_QuitRequested()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < MenuStateMachine.QuitItemIndex; i++)
            m.HandleInput("Down", 0);
        m.HandleInput("Return", 100);  // open dialog

        bool quitFired = false;
        m.OnQuit += () => quitFired = true;
        m.HandleInput("Return", 110);  // YES selected by default

        Assert.True(m.QuitRequested);
        Assert.True(quitFired);
        Assert.False(m.InAskBool);
    }

    [Fact]
    public void Return_NO_on_exit_to_dos_does_not_quit()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        for (int i = 0; i < MenuStateMachine.QuitItemIndex; i++)
            m.HandleInput("Down", 0);
        m.HandleInput("Return", 100);  // open dialog
        m.HandleInput("Right", 110);   // toggle to NO

        m.HandleInput("Return", 120);

        Assert.False(m.QuitRequested);
        Assert.False(m.InAskBool);
    }

    private static MenuStateMachine HangarReadyMachineWithSaveDir(out PilotSaveStoreTests.TempDir dir)
    {
        dir = new PilotSaveStoreTests.TempDir();
        var m = new MenuStateMachine { PilotSaveDirectory = dir.Path };
        m.EnterMenu(0);
        // Create pilot TEST / T1 / default difficulty → lands in hangar.
        m.HandleInput("Return", 1); // Return on NEW MISSION (item 0)
        foreach (var ch in "TEST") m.HandleInput(ch.ToString(), 2);
        m.HandleInput("Return", 3);
        foreach (var ch in "T1") m.HandleInput(ch.ToString(), 4);
        m.HandleInput("Return", 5);
        m.HandleInput("Return", 6); // accept default difficulty
        return m;
    }

    private static MenuStateMachine EnterLoadMissionWithThreePilots(out PilotSaveStoreTests.TempDir dir)
    {
        dir = new PilotSaveStoreTests.TempDir();
        PilotSaveStoreTests.WriteFakePilot(dir.Path, slot: 0, name: "ALICE", callsign: "ACE", idPic: 0, score: 1000);
        PilotSaveStoreTests.WriteFakePilot(dir.Path, slot: 2, name: "BOB", callsign: "BEAR", idPic: 1, score: 25000);
        PilotSaveStoreTests.WriteFakePilot(dir.Path, slot: 5, name: "CAROL", callsign: "CAT", idPic: 3, score: 99999);

        var m = new MenuStateMachine { PilotSaveDirectory = dir.Path };
        m.EnterMenu(0);
        m.HandleInput("Down", 0);
        m.HandleInput("Return", 93);
        return m;
    }

    [Fact]
    public void OnStateChanged_fires_on_F1_transition()
    {
        var m = new MenuStateMachine();
        m.EnterMenu(0);
        int fired = 0;
        m.OnStateChanged += () => fired++;
        m.HandleInput("F1", 50);
        Assert.Equal(1, fired);
    }
}
