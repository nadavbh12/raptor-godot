namespace Raptor.Sim.Bonus;

/// <summary>
/// A floating pickup that drifts down the screen. Mirrors C SOURCE/BONUS.C
/// BONUS struct + BONUS_Think. ObjType is the raw OBJ_TYPE value the bonus
/// grants on pickup; effect application lives in WaveController.
///
/// Wobble shifts only the rendered position; pickup still uses raw X/Y center.
/// </summary>
public sealed class BonusLogic
{
    /// <summary>OBJ_TYPE value (SOURCE/OBJECTS.H). 0-14 are weapons; 16=S_ENERGY; 19-24=S_ITEMBUY1-6.</summary>
    public int ObjType { get; }
    public int X       { get; private set; }
    public int Y       { get; private set; }
    public int Pos     { get; private set; }
    public int Frame   { get; private set; }
    public int GlowFrame { get; private set; }
    public bool DisplayAsPickedUpMoney { get; private set; }
    public int PickedUpMoneyCountdown { get; private set; }
    public bool Alive  { get; private set; } = true;

    /// <summary>
    /// BONUS_Think pickup-AABB half-width. BONUS_WIDTH=16, BONUS_HEIGHT=16
    /// (the SOURCE constants); C compares `cur->x > playerx && cur->x &lt; x2`
    /// — the bonus CENTER is inside the player rect (no half-dim involved on
    /// the bonus side). We expose this for tests that need to compute overlap.
    /// </summary>
    public const int Width  = 16;
    public const int Height = 16;

    public BonusLogic(int objType, int x, int y, int initialPos = 0)
    {
        ObjType = objType;
        X = x;
        Y = y;
        Pos = ((initialPos % 16) + 16) % 16;
    }

    /// <summary>
    /// Why a <see cref="Tick"/> removed a bonus, so callers can mirror C's distinct
    /// removal traces: countdown expiry is silent (BONUS.C:271) while the off-bottom
    /// cull emits a "remove_bottom" trace before removal (BONUS.C:280).
    /// </summary>
    public enum TickOutcome { None, RemovedExpired, RemovedOffBottom }

    /// <summary>
    /// One game-loop iteration (one BONUS_Think pass). BONUS.C:187 advances y by
    /// 1 each tick. BONUS.C:241-242 despawns when off the bottom of the screen.
    /// Returns which removal (if any) happened this pass.
    /// </summary>
    /// <param name="advance">
    /// True on the (gcnt &amp; 1) phase. In C, gcnt is a GLOBAL static incremented
    /// once per BONUS_Think (BONUS.C:205/288), so the wobble/sprite-frame advance
    /// happens for every bonus in lockstep on alternating game iterations —
    /// independent of when each bonus spawned. WaveController owns the counter.
    /// </param>
    public TickOutcome Tick(bool advance)
    {
        if (!Alive) return TickOutcome.None;

        // BONUS.C:220 — the glow center gy is computed from the CURRENT y/pos,
        // BEFORE the y++ (222) and the gcnt&1 pos++ (226). glow_ly = ICNGLW_BLK
        // height = 32, so glow_ly>>1 = 16.
        int gy = Y - GlowHalfHeight + Ypos[Pos];

        Y++;
        if (advance)
        {
            Pos = (Pos + 1) % 16;
            Frame = (Frame + 1) % FrameCountFor(ObjType);
        }
        GlowFrame = (GlowFrame + 1) % 4;

        if (DisplayAsPickedUpMoney)
        {
            PickedUpMoneyCountdown--;
            if (PickedUpMoneyCountdown <= 0)
            {
                Alive = false;
                return TickOutcome.RemovedExpired;  // BONUS.C:271 — silent removal
            }
            return TickOutcome.None;
        }

        // BONUS.C:278 — off-bottom cull uses the glow center gy, not raw y.
        if (gy > 200)
        {
            Alive = false;
            return TickOutcome.RemovedOffBottom;  // BONUS.C:280 — emits remove_bottom
        }
        return TickOutcome.None;
    }

    /// <summary>glow_ly (ICNGLW_BLK height = 32) >> 1, from BONUS.C:220.</summary>
    private const int GlowHalfHeight = 16;

    /// <summary>Wobble offset table, BONUS.C:19 (indexed by Pos 0..15).</summary>
    private static readonly int[] Ypos =
        { -3, -3, -3, -2, -1, 0, 1, 2, 3, 3, 3, 2, 1, 0, -1, -2 };

    /// <summary>Force-kill (used on player pickup).</summary>
    public void Kill() => Alive = false;

    public void MarkPickedUpMoney()
    {
        DisplayAsPickedUpMoney = true;
        PickedUpMoneyCountdown = 50;
    }

    /// <summary>
    /// Applies the C BONUS_Think dflag countdown block (BONUS.C:268-271) that runs
    /// in the SAME pass a money bonus is picked up. Godot ticks bonuses in
    /// PhaseMovement (before pickup detection in PhaseCollisionResolve), so that
    /// pass's decrement is otherwise skipped on the pickup iter — apply it here so
    /// the post-pickup state reads cnt=49, matching C. Decrements only; the y/pos
    /// drift already ran this iter in <see cref="Tick"/>.
    /// </summary>
    public void DecrementPickupCountdownSamePass()
    {
        PickedUpMoneyCountdown--;
        if (PickedUpMoneyCountdown <= 0) Alive = false;
    }

    public bool CanBePickedUpBy(int playerX, int playerY, int playerWidth = 32, int playerHeight = 32)
    {
        return Alive
            && !DisplayAsPickedUpMoney
            && X > playerX
            && X < playerX + playerWidth
            && Y > playerY
            && Y < playerY + playerHeight;
    }

    private static int FrameCountFor(int objType) => objType switch
    {
        1 or 2 or 12 => 2,
        4 or 5 or 10 or 13 or 14 or 16 or 23 or 24 => 4,
        _ => 1,
    };
}
