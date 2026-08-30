using System.Collections.Generic;
using Godot;
using Raptor.Sim;

namespace Raptor.View;

/// A menu sprite identified by its GLB item name (e.g. "BACKGRND_PIC").
/// Deliberately NOT the extracted filename: those carry a numeric prefix
/// giving the item's position in the GLB table, and that position differs
/// between editions -- the shareware archive has fewer items than the
/// registered one, so every prefix after the gap shifts. The name is stable.
internal readonly record struct MenuSpriteSpec(string IName, int X, int Y);

internal static class MenuChrome
{
    public static readonly Color MenuOrange = new(0.95f, 0.36f, 0.06f);
    public static readonly Color MenuDark = new(0.02f, 0.02f, 0.02f, 0.86f);
    public static readonly Color MenuMid = new(0.28f, 0.28f, 0.26f, 0.95f);

    public static readonly MenuSpriteSpec Background = new("BACKGRND_PIC", 0, 0);
    public static readonly MenuSpriteSpec RaptorLogo = new("RAPLOG_PIC", 21, 1);
    public static readonly MenuSpriteSpec Copyright = new("COPYRGHT_PIC", 118, 196);
    public static readonly MenuSpriteSpec Hangar = new("HANGER_PIC", 0, 0);
    // Position matches HANG_PIC field in HANGAR_SWD (extracted JSON).
    public static readonly MenuSpriteSpec HangarPilot = new("HANGP_PIC", 84, 117);
    public static readonly MenuSpriteSpec ShipComputer = new("SHIPCOMP_PIC", 0, 0);
    public static readonly MenuSpriteSpec Register = new("REGISTER_PIC", 0, 0);
    public static readonly MenuSpriteSpec HelpComputer = new("HELPCOMP_PIC", 104, 58);
    public static readonly MenuSpriteSpec Pointer = new("POINT_PIC", 63, 0);
    public static readonly MenuSpriteSpec Slider = new("SLIDE_PIC", 0, 0);
    public static readonly MenuSpriteSpec Cursor = new("CURSOR_PIC", 0, 0);
    public static readonly MenuSpriteSpec LightOn = new("LIGHTON_PIC", 0, 0);
    public static readonly MenuSpriteSpec LightOff = new("LIGHTOFF_PIC", 0, 0);
    public static readonly MenuSpriteSpec RegisterPortrait = new("WMALEID_PIC", 5, 109);
    // ID-portrait variants (C sid_pics order): 0=WMALE 1=BMALE 2=WFEMALE 3=BFEMALE.
    public static readonly IReadOnlyList<MenuSpriteSpec> RegisterPortraits =
    [
        new("WMALEID_PIC", 5, 109),
        new("BMALEID_PIC", 5, 109),
        new("WFMALEID_PIC", 5, 109),
        new("BFMALEID_PIC", 5, 109),
    ];
    public static readonly IReadOnlyList<MenuSpriteSpec> DifficultyPortraits =
    [
        new("CDIF1_PIC", 0, 0),
        new("CDIF2_PIC", 0, 0),
        new("CDIF3_PIC", 0, 0),
        new("CDIF4_PIC", 0, 0),
    ];

    public static readonly IReadOnlyList<MenuSpriteSpec> MainVisibleItems =
    [
        new("MENU1_PIC", 95, 89),
        new("MENU2_PIC", 95, 103),
        new("MENU4_PIC", 95, 117),
        new("MENU5_PIC", 95, 131),
        new("MENU6_PIC", 95, 145),
        new("MENU8_PIC", 95, 159),
    ];

    public static readonly IReadOnlyList<int> MainSelectableY =
    [
        89,   // New Mission
        103,  // Load Mission
        117,  // Game Options
        131,  // Order Info
        145,  // Credits
        159,  // Quit
        173,  // Return to Game (MAIN_SWD field 7), shown only while a campaign is active
    ];

    /// <summary>RETURN-to-game item (MENU7_PIC). Rendered only while a campaign is in
    /// progress (C ingameflag, WINDOWS.C:2026). MAIN_SWD field idx 7 = (95, 173),
    /// directly below QUIT.</summary>
    public static readonly MenuSpriteSpec ReturnItem = new("MENU7_PIC", 95, 173);

    public static readonly IReadOnlyList<(string Label, int X, int Y)> HangarTargets =
    [
        ("MISSION", 139, 160),
        ("SUPPLIES", 238, 82),
        ("MAIN MENU", 31, 136),
        ("QUICK SAVE", 252, 174),
    ];

    public static readonly IReadOnlyList<(string Label, int X, int Y)> ShipComputerButtons =
    [
        ("AUTO", 214, 169),
        ("BRAVO", 48, 34),
        ("TANGO", 48, 52),
        ("OUTER", 48, 70),
    ];

    public static int MainPointerY(int itemIndex)
    {
        if (itemIndex < 0 || itemIndex >= MainSelectableY.Count)
            return MainSelectableY[0] + 4;
        return MainSelectableY[itemIndex] + 7;
    }

    public static string RegisterTitle(int pilotCreateStep) => pilotCreateStep switch
    {
        1 => "PILOT NAME",
        2 => "CALLSIGN",
        _ => "DIFFICULTY",
    };

    public static string RegisterValue(int pilotCreateStep) => pilotCreateStep switch
    {
        1 => "PLAYER",
        2 => "RAPTOR",
        _ => "NORMAL",
    };

    public static bool UsesFullScreenArt(WinState state) =>
        state is WinState.Menu or WinState.Hangar or WinState.Help or WinState.Credits or WinState.Unknown;
}
