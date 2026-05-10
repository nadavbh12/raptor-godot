namespace Raptor.Sim.Player;

/// <summary>
/// Pure-C# player state machine. Position is integer pixels.
/// Constants from SOURCE/RAP.C and SOURCE/PUBLIC.H of the C version.
/// </summary>
public sealed class PlayerLogic
{
    public const int InitX = 160;
    public const int InitY = 160;
    public const int MinX = 16;
    public const int MaxX = 304;     // 320 - 16
    public const int MinY = 0;
    public const int MaxY = 199;
    public const int VelocityPerTick = 4;

    public int X { get; private set; } = InitX;
    public int Y { get; private set; } = InitY;
    public int Pic { get; private set; } = 4;   // PLAYERINITX uses pic 4 (centered banking frame)

    public void Reset()
    {
        X = InitX;
        Y = InitY;
        Pic = 4;
    }

    /// <summary>
    /// Apply input for one tick. dx/dy are -1, 0, or 1.
    /// Position is clamped to [MinX, MaxX] x [MinY, MaxY].
    /// Pic represents banking based on horizontal direction:
    ///   dx == 0  -> pic 4 (center)
    ///   dx > 0   -> pic 5 (slight right) … this can be refined later;
    ///              Stage 3 just keeps pic 4 for all and animates in View.
    /// </summary>
    public void Tick(int dx, int dy)
    {
        if (dx < -1) dx = -1; if (dx > 1) dx = 1;
        if (dy < -1) dy = -1; if (dy > 1) dy = 1;

        int newX = X + dx * VelocityPerTick;
        int newY = Y + dy * VelocityPerTick;
        if (newX < MinX) newX = MinX;
        if (newX > MaxX) newX = MaxX;
        if (newY < MinY) newY = MinY;
        if (newY > MaxY) newY = MaxY;
        X = newX;
        Y = newY;
    }
}
