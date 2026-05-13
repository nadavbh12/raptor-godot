namespace Raptor.Sim.Bonus;

/// <summary>
/// A floating pickup that drifts down the screen. Mirrors C SOURCE/BONUS.C
/// BONUS struct + BONUS_Think. ObjType is the raw OBJ_TYPE value the bonus
/// grants on pickup; effect application lives in WaveController.
///
/// Wobble (BONUS.C:181-182's xpos[pos]/ypos[pos] rotation) is intentionally
/// omitted — the wobble shifts the rendered position but the pickup AABB
/// (BONUS.C:207) uses the raw cur->x/cur->y center, so wobble has no parity
/// effect on inventory. A view layer can add it later for cosmetic motion.
/// </summary>
public sealed class BonusLogic
{
    /// <summary>OBJ_TYPE value (SOURCE/OBJECTS.H). 0-14 are weapons; 16=S_ENERGY; 19-24=S_ITEMBUY1-6.</summary>
    public int ObjType { get; }
    public int X       { get; private set; }
    public int Y       { get; private set; }
    public bool Alive  { get; private set; } = true;

    /// <summary>
    /// BONUS_Think pickup-AABB half-width. BONUS_WIDTH=16, BONUS_HEIGHT=16
    /// (the SOURCE constants); C compares `cur->x > playerx && cur->x &lt; x2`
    /// — the bonus CENTER is inside the player rect (no half-dim involved on
    /// the bonus side). We expose this for tests that need to compute overlap.
    /// </summary>
    public const int Width  = 16;
    public const int Height = 16;

    public BonusLogic(int objType, int x, int y)
    {
        ObjType = objType;
        X = x;
        Y = y;
    }

    /// <summary>
    /// One game-loop iteration. BONUS.C:187 advances y by 1 each tick.
    /// BONUS.C:241-242 despawns when off the bottom of the screen.
    /// </summary>
    public void Tick()
    {
        if (!Alive) return;
        Y++;
        if (Y > 200) Alive = false;
    }

    /// <summary>Force-kill (used on player pickup).</summary>
    public void Kill() => Alive = false;
}
