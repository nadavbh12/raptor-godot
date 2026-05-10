namespace Raptor.Sim.Player;

/// <summary>
/// One tick of player input. Mirror of the C version's BUT_*/dx/dy.
/// Pure data; no Godot dependencies.
/// </summary>
public readonly struct InputState
{
    public int Dx { get; init; }   // -1, 0, 1
    public int Dy { get; init; }   // -1, 0, 1
    public bool B1 { get; init; }  // fire
    public bool B2 { get; init; }  // special
    public bool B3 { get; init; }  // bomb
    public bool B4 { get; init; }  // pause/menu

    public static readonly InputState Idle = new();

    public static InputState From(int dx, int dy, bool b1, bool b2, bool b3, bool b4)
        => new() { Dx = dx, Dy = dy, B1 = b1, B2 = b2, B3 = b3, B4 = b4 };
}
