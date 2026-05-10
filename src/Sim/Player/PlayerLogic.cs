namespace Raptor.Sim.Player;

/// <summary>
/// Pure-C# player state machine. Position is integer pixels.
/// Constants from SOURCE/RAP.C and SOURCE/PUBLIC.H of the C version.
/// </summary>
public sealed class PlayerLogic
{
    // PLAYERINITX = 160 - (PLAYERWIDTH/2) = 160 - 16 = 144 (from SOURCE/PUBLIC.H).
    public const int InitX = 144;
    public const int InitY = 160;
    public const int MinX = 16;
    public const int MaxX = 304;     // 320 - 16
    public const int MinY = 0;
    public const int MaxY = 199;
    public const int VelocityPerTick = 4;

    // Starting shield for a new pilot (OBJS_Add(S_ENERGY) + initial fill).
    // From the C golden: first HANGAR checkpoint shows shield=75.
    public const int InitShield = 75;
    public const int MaxShield = 100;

    public int X { get; private set; } = InitX;
    public int Y { get; private set; } = InitY;
    // Shield starts at 0 (no pilot); Reset() sets it to InitShield when pilot is created.
    public int Shield { get; private set; } = 0;
    public bool Alive => Shield > 0;
    public int Pic { get; private set; } = 4;   // pic 4 = centered banking frame

    public void Reset()
    {
        X = InitX;
        Y = InitY;
        Shield = InitShield;
        Pic = 4;
    }

    /// <summary>
    /// Apply shield damage. Shield is clamped to [0, MaxShield].
    /// Returns true if the player died (shield reached 0).
    /// </summary>
    public bool TakeDamage(int dmg)
    {
        if (dmg <= 0) return false;
        Shield -= dmg;
        if (Shield < 0) Shield = 0;
        return Shield == 0;
    }

    /// <summary>
    /// Heal shield. Clamped to MaxShield.
    /// </summary>
    public void Heal(int amount)
    {
        Shield += amount;
        if (Shield > MaxShield) Shield = MaxShield;
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
