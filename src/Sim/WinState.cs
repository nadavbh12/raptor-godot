namespace Raptor.Sim;

/// <summary>
/// Mirrors the parity-output <c>win</c> enum from tests/parity/schema.json.
/// Integer values match the C parity.c switch: 1=MENU, 2=CREDITS, 3=HELP,
/// 4=ORDER, 5=HANGAR, 6=STORE, 7=BRIEFING. 0=UNKNOWN (not in game).
/// String values returned by ToParityString() must match the schema exactly.
/// </summary>
public enum WinState
{
    Unknown   = 0,
    Menu      = 1,
    Credits   = 2,
    Help      = 3,
    Order     = 4,
    Hangar    = 5,
    Store     = 6,
    Briefing  = 7,
    Mission_1 = 10,
    Mission_2 = 11,
    Mission_3 = 12,
    Death     = 20,
    Landing   = 21,
    Intro     = 22,
}

public static class WinStateExtensions
{
    public static string ToParityString(this WinState s) => s switch
    {
        WinState.Menu      => "MENU",
        WinState.Credits   => "CREDITS",
        WinState.Help      => "HELP",
        WinState.Order     => "ORDER",
        WinState.Hangar    => "HANGAR",
        WinState.Store     => "STORE",
        WinState.Briefing  => "BRIEFING",
        WinState.Mission_1 => "MISSION_1",
        WinState.Mission_2 => "MISSION_2",
        WinState.Mission_3 => "MISSION_3",
        WinState.Death     => "DEATH",
        WinState.Landing   => "LANDING",
        WinState.Intro     => "INTRO",
        _                  => "UNKNOWN",
    };
}
