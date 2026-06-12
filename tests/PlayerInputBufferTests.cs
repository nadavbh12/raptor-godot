using System;
using System.IO;
using Raptor.Sim.Player;
using Raptor.Sim;
using Raptor.View;
using Xunit;

namespace Raptor.Tests;

public class InputStateTests
{
    [Fact]
    public void Idle_is_all_zero_or_false()
    {
        var s = InputState.Idle;
        Assert.Equal(0, s.Dx);
        Assert.Equal(0, s.Dy);
        Assert.False(s.B1);
        Assert.False(s.B2);
        Assert.False(s.B3);
        Assert.False(s.B4);
    }

    [Fact]
    public void From_round_trips_all_fields()
    {
        var s = InputState.From(-1, 1, true, false, true, false);
        Assert.Equal(-1, s.Dx);
        Assert.Equal(1, s.Dy);
        Assert.True(s.B1); Assert.False(s.B2);
        Assert.True(s.B3); Assert.False(s.B4);
    }

    [Fact]
    public void Interactive_compose_state_maps_direction_and_buttons()
    {
        var s = InteractiveInputController.ComposeState(
            right: true,
            left: false,
            down: false,
            up: true,
            fire: true,
            special: false,
            bomb: true,
            pause: false);

        Assert.Equal(1, s.Dx);
        Assert.Equal(-1, s.Dy);
        Assert.True(s.B1);
        Assert.False(s.B2);
        Assert.True(s.B3);
        Assert.False(s.B4);
    }

    [Fact]
    public void Interactive_menu_actions_map_to_c_key_names()
    {
        Assert.Equal("Down", InteractiveInputController.ActionToMenuKey("menu_down"));
        Assert.Equal("Up", InteractiveInputController.ActionToMenuKey("menu_up"));
        Assert.Equal("Left", InteractiveInputController.ActionToMenuKey("menu_left"));
        Assert.Equal("Right", InteractiveInputController.ActionToMenuKey("menu_right"));
        Assert.Equal("Return", InteractiveInputController.ActionToMenuKey("menu_accept"));
        Assert.Equal("Escape", InteractiveInputController.ActionToMenuKey("menu_back"));
        Assert.Equal("F1", InteractiveInputController.ActionToMenuKey("menu_help"));
        Assert.Null(InteractiveInputController.ActionToMenuKey("fire_main"));
    }

    [Fact]
    public void Interactive_text_keys_map_to_registration_input()
    {
        Assert.Equal("a", InteractiveInputController.KeyToTextAction(Godot.Key.A, 'a'));
        Assert.Equal("Z", InteractiveInputController.KeyToTextAction(Godot.Key.Z, 'Z'));
        Assert.Equal("7", InteractiveInputController.KeyToTextAction(Godot.Key.Key7, '7'));
        Assert.Equal("Backspace", InteractiveInputController.KeyToTextAction(Godot.Key.Backspace, 0));
        Assert.Null(InteractiveInputController.KeyToTextAction(Godot.Key.Tab, '\t'));
    }

    [Fact]
    public void Interactive_mouse_mapping_scales_window_pixel_coordinates()
    {
        // 1280x800 matches the 320:200 aspect → uniform 4x, no bars: window/4 = game.
        var p = InteractiveInputController.ToGameViewport(new Godot.Vector2(760, 596), new Godot.Vector2(1280, 800));

        Assert.Equal(new Godot.Vector2I(190, 149), p);
    }

    [Fact]
    public void Interactive_mouse_mapping_accounts_for_letterbox_at_non_game_aspect()
    {
        // A 1280x720 (16:9) window pillarboxes the 1.6 game: uniform scale 3.6, 64px
        // side bars. The game's top-left (window 64,0) → (0,0); its center → (160,100).
        // The old independent-scale math (no bar offset) mismapped these → clicks missed.
        Assert.Equal(new Godot.Vector2I(0, 0),
            InteractiveInputController.ToGameViewport(new Godot.Vector2(64, 0), new Godot.Vector2(1280, 720)));
        Assert.Equal(new Godot.Vector2I(160, 100),
            InteractiveInputController.ToGameViewport(new Godot.Vector2(640, 360), new Godot.Vector2(1280, 720)));
    }

    [Fact]
    public void Live_input_resolve_prefers_playthrough_when_present()
    {
        var interactive = InputState.From(-1, -1, false, false, false, false);

        var s = LiveInputLogic.Resolve(
            usePlaythrough: true,
            playthroughDx: 1,
            playthroughDy: 1,
            playthroughFire: true,
            playthroughSpecial: true,
            playthroughMega: false,
            interactive);

        Assert.Equal(1, s.Dx);
        Assert.Equal(1, s.Dy);
        Assert.True(s.B1);
        Assert.True(s.B2);
        Assert.False(s.B3);
    }

    [Fact]
    public void Live_input_special_cycle_is_edge_triggered()
    {
        var shooter = new Raptor.Sim.Shots.PlayerShooter();
        shooter.GrantWeapon((int)Raptor.Sim.ObjType.DumbMissile);
        shooter.GrantWeapon((int)Raptor.Sim.ObjType.MiniGun);
        shooter.SelectSpecial(Raptor.Sim.ObjType.DumbMissile);
        bool latch = false;

        LiveInputLogic.ApplySpecialCycle(shooter, held: true, ref latch);
        LiveInputLogic.ApplySpecialCycle(shooter, held: true, ref latch);

        Assert.Equal(Raptor.Sim.ObjType.MiniGun, shooter.SpecialWeapon);
    }

    [Fact]
    public void Menu_chrome_uses_extracted_c_menu_assets()
    {
        Assert.Equal("0030_BACKGRND_PIC.png", MenuChrome.Background.FileName);
        Assert.Equal("0015_RAPLOG_PIC.png", MenuChrome.RaptorLogo.FileName);
        Assert.Equal("0018_COPYRGHT_PIC.png", MenuChrome.Copyright.FileName);
        Assert.Equal("0042_HANGER_PIC.png", MenuChrome.Hangar.FileName);
        Assert.Equal("0045_SHIPCOMP_PIC.png", MenuChrome.ShipComputer.FileName);
        Assert.Equal("0048_REGISTER_PIC.png", MenuChrome.Register.FileName);
        Assert.Equal("0054_WMALEID_PIC.png", MenuChrome.RegisterPortrait.FileName);
        Assert.Equal(6, MenuChrome.MainVisibleItems.Count);
        Assert.Equal("0031_MENU1_PIC.png", MenuChrome.MainVisibleItems[0].FileName);
        Assert.Equal(95, MenuChrome.MainVisibleItems[0].X);
        Assert.Equal(89, MenuChrome.MainVisibleItems[0].Y);
        Assert.Equal("0034_MENU4_PIC.png", MenuChrome.MainVisibleItems[2].FileName);
        Assert.Equal("0036_MENU6_PIC.png", MenuChrome.MainVisibleItems[4].FileName);
        Assert.Equal("0038_MENU8_PIC.png", MenuChrome.MainVisibleItems[5].FileName);
    }

    [Fact]
    public void Menu_chrome_main_pointer_tracks_selected_button()
    {
        Assert.Equal(96, MenuChrome.MainPointerY(0));
        Assert.Equal(152, MenuChrome.MainPointerY(4));
    }

    [Fact]
    public void Menu_renderer_does_not_depend_on_static_screenshot_patches()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        Assert.False(Directory.Exists(Path.Combine(repoRoot, "assets", "menu")));

        string renderer = File.ReadAllText(Path.Combine(repoRoot, "src", "View", "DebugRenderer.cs"));
        Assert.DoesNotContain("assets/menu", renderer);
        Assert.DoesNotContain("DrawMenuPatch", renderer);
        Assert.DoesNotContain("register_form_name_test", renderer);
        Assert.DoesNotContain("register_form_callsign_t1", renderer);
        Assert.DoesNotContain("shipcomp_sector_monitor", renderer);
        Assert.DoesNotContain("\"TEST\"", renderer);
        Assert.DoesNotContain("\"T1\"", renderer);
    }

    [Fact]
    public void Menu_renderer_hides_menu_during_demo_gameplay_even_when_menu_state_is_not_ingame()
    {
        Assert.False(DebugRenderer.ShouldDrawMenuOverlayForState(
            interactiveUi: true,
            menuInGame: false,
            gameplayVisualActive: true));
    }
}
