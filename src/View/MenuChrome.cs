using System.Collections.Generic;
using Godot;
using Raptor.Sim;

namespace Raptor.View;

internal readonly record struct MenuSpriteSpec(string FileName, int X, int Y);

internal static class MenuChrome
{
    public static readonly Color MenuOrange = new(0.95f, 0.36f, 0.06f);
    public static readonly Color MenuDark = new(0.02f, 0.02f, 0.02f, 0.86f);
    public static readonly Color MenuMid = new(0.28f, 0.28f, 0.26f, 0.95f);

    public static readonly MenuSpriteSpec Background = new("0030_BACKGRND_PIC.png", 0, 0);
    public static readonly MenuSpriteSpec RaptorLogo = new("0015_RAPLOG_PIC.png", 21, 1);
    public static readonly MenuSpriteSpec Copyright = new("0018_COPYRGHT_PIC.png", 118, 196);
    public static readonly MenuSpriteSpec Hangar = new("0042_HANGER_PIC.png", 0, 0);
    // Position matches HANG_PIC field in HANGAR_SWD (extracted JSON).
    public static readonly MenuSpriteSpec HangarPilot = new("0043_HANGP_PIC.png", 84, 117);
    public static readonly MenuSpriteSpec ShipComputer = new("0045_SHIPCOMP_PIC.png", 0, 0);
    public static readonly MenuSpriteSpec Register = new("0048_REGISTER_PIC.png", 0, 0);
    public static readonly MenuSpriteSpec HelpComputer = new("0079_HELPCOMP_PIC.png", 104, 58);
    public static readonly MenuSpriteSpec Pointer = new("0072_POINT_PIC.png", 63, 0);
    public static readonly MenuSpriteSpec Cursor = new("0014_CURSOR_PIC.png", 0, 0);
    public static readonly MenuSpriteSpec LightOn = new("0074_LIGHTON_PIC.png", 0, 0);
    public static readonly MenuSpriteSpec LightOff = new("0075_LIGHTOFF_PIC.png", 0, 0);
    public static readonly MenuSpriteSpec RegisterPortrait = new("0054_WMALEID_PIC.png", 5, 109);
    public static readonly IReadOnlyList<MenuSpriteSpec> DifficultyPortraits =
    [
        new("0022_CDIF1_PIC.png", 0, 0),
        new("0023_CDIF2_PIC.png", 0, 0),
        new("0024_CDIF3_PIC.png", 0, 0),
        new("0025_CDIF4_PIC.png", 0, 0),
    ];

    public static readonly IReadOnlyList<MenuSpriteSpec> MainVisibleItems =
    [
        new("0031_MENU1_PIC.png", 95, 89),
        new("0032_MENU2_PIC.png", 95, 103),
        new("0034_MENU4_PIC.png", 95, 117),
        new("0035_MENU5_PIC.png", 95, 131),
        new("0036_MENU6_PIC.png", 95, 145),
        new("0038_MENU8_PIC.png", 95, 159),
    ];

    public static readonly IReadOnlyList<int> MainSelectableY =
    [
        89,   // New Mission
        103,  // Load Mission
        117,  // Game Options
        131,  // Order Info
        145,  // Credits
        159,  // Quit
        200,  // Return to Game, hidden when not in game
    ];

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
