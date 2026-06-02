namespace Raptor.Sim;

/// <summary>
/// Owns the HANGAR cursor position and the pure cursor-navigation arithmetic
/// (Down/Right → pos--, Up/Left/Tab → pos++, both wrapping over 4 slots) plus
/// the pointer hit-test. Extracted from <see cref="MenuStateMachine"/> (Phase 4
/// tranche A).
///
/// Hangar has 4 positions: 0=MISSION, 1=SUPPLIES, 2=MAINMENU, 3=QSAVE.
/// C default hangto=HANGTOSTORE(0) → case HANGTOSTORE → pos=1 (SUPPLIES).
/// From golden: 02_hangar_supplies = initial dump, then Down to 03_hangar_mission.
/// Down in C: pos-- (so 1→0=MISSION). Then Return on MISSION → hangar_exit → UNKNOWN.
///
/// The Return/Escape/F1/F2 branches that trigger win-state transitions (enter
/// sector-select / store, return to menu, open help, open the save dialog) stay
/// in MenuStateMachine.HandleHangarInput: they are entangled with EnterState/
/// EnterMenu/EnterHelp/OpenAskBoolSave/Store creation and the parity frame-
/// anchor, which MenuStateMachine owns. <see cref="MenuStateMachine.HangarPosition"/>
/// forwards to <see cref="Position"/>.
/// </summary>
internal sealed class HangarController
{
    private int _hangarPos = 1;  // default: SUPPLIES (hangto=HANGTOSTORE)

    /// <summary>Cursor slot: 0=MISSION, 1=SUPPLIES, 2=MAINMENU, 3=QSAVE.</summary>
    public int Position
    {
        get => _hangarPos;
        set => _hangarPos = value;
    }

    /// <summary>
    /// Apply a cursor-only navigation key. Returns true if <paramref name="action"/>
    /// was a navigation key (in which case the caller should not fall through to
    /// the transition branches). Mirrors the SC_DOWN / SC_UP cases in WINDOWS.C.
    /// </summary>
    public bool TryNavigate(string action)
    {
        if (action == "Down" || action == "Right")
        {
            // In C: Down/Right → pos-- (SC_DOWN case in WINDOWS.C); wraps from 0 to 3.
            _hangarPos = (_hangarPos - 1 + 4) % 4;
            return true;
        }
        if (action == "Up" || action == "Left")
        {
            // In C: Up/Left/Tab → pos++.
            _hangarPos = (_hangarPos + 1) % 4;
            return true;
        }
        return false;
    }

    /// <summary>Hit-test a pointer click against the four hangar hotspots. Returns -1 on miss.</summary>
    public static int PositionAt(int x, int y)
    {
        if (InRect(x, y, 120, 140, 60, 45)) return 0; // MISSION
        if (InRect(x, y, 215, 60, 55, 50)) return 1;  // SUPPLIES
        if (InRect(x, y, 5, 112, 65, 50)) return 2;   // MAIN MENU
        if (InRect(x, y, 225, 150, 75, 45)) return 3; // QUICK SAVE
        return -1;
    }

    private static bool InRect(int x, int y, int rx, int ry, int w, int h)
        => x >= rx && x < rx + w && y >= ry && y < ry + h;
}
