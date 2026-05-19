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
    public const int SpriteWidth = 32;
    public const int SpriteHeight = 32;
    public const int MinX = 16;
    public const int MaxX = 304 - SpriteWidth;
    public const int MinY = 0;
    public const int MaxY = 200 - SpriteHeight;
    public const int VelocityPerTick = 4;

    // C INPUT.C:17-18 — acceleration ramp caps. When a direction key is held,
    // C's IPT_GetKeyBoard ramps the per-iter delta toward ±MAX_ADD{X,Y} by 1
    // per call, then applies playerx += g_addx ; playery += g_addy.
    // Releasing the key halves the delta each iter (toward 0).
    public const int MaxAddX = 10;
    public const int MaxAddY = 8;

    // Starting shield for a new pilot (OBJS_Add(S_ENERGY) + initial fill).
    // From the C golden: first HANGAR checkpoint shows shield=75.
    public const int InitShield = 75;
    public const int MaxShield = 100;

    public int X { get; private set; } = InitX;
    public int Y { get; private set; } = InitY;
    // Shield starts at 0 (no pilot); Reset() sets it to InitShield when pilot is created.
    public int Shield { get; private set; } = 0;
    public bool Alive => Shield > 0;
    // Banking frame index — mirrors C SOURCE/RAP.C playerpic / playerbasepic.
    // 0..6 spans 7 LPLAYER_PIC frames (0058..0064); 3 is neutral (playerbasepic).
    // Initial value 4 matches C's RAP.C:81 — it recenters to 3 on tick 1 with no input.
    public const int BasePic = 3;
    public int Pic { get; private set; } = 4;
    private int _oldX = InitX;
    // Per-iter accumulated velocity, ramped by Tick from key state.
    // Sign: +x = right, -x = left ; +y = down, -y = up. Bounds: |g_add*| ≤ MaxAdd*.
    private int _gAddX = 0;
    private int _gAddY = 0;

    public void Reset()
    {
        X = InitX;
        Y = InitY;
        Shield = InitShield;
        Pic = 4;
        _oldX = InitX;
        _gAddX = 0;
        _gAddY = 0;
    }

    public void ApplyDemoFrame(int x, int y, int pic)
    {
        X = x;
        Y = y;
        Pic = pic;
        _oldX = x;
        _gAddX = 0;
        _gAddY = 0;
    }

    public void SetShield(int shield)
    {
        Shield = System.Math.Clamp(shield, 0, MaxShield);
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
    /// Apply input for one tick. dx/dy are -1, 0, or 1 (key-held state, NOT a
    /// velocity). Mirrors C SOURCE/INPUT.C:472-520 (IPT_GetKeyBoard):
    /// while a direction is held, the per-iter delta ramps from ±1 toward
    /// ±MaxAdd{X,Y} by 1 per tick; with no input the delta halves toward 0.
    /// Then playerx += g_addx ; playery += g_addy, clamped to bounds.
    ///
    /// This gradual acceleration is what produces the "iter 7 ⇒ y=124" curve
    /// the C goldens record under `down Up`. A flat ±VelocityPerTick model
    /// over-shoots the y bounds inside the first second.
    ///
    /// Pic banking still mirrors C INPUT.C:806-828: bankRange = |X - oldX| >> 2,
    /// clamped to [0, 3]; moving left increments Pic toward base+bankRange,
    /// right decrements, idle recenters by ±1 toward base.
    /// </summary>
    public void Tick(int dx, int dy)
    {
        if (dx < -1) dx = -1; if (dx > 1) dx = 1;
        if (dy < -1) dy = -1; if (dy > 1) dy = 1;

        // X-axis ramp (IPT_GetKeyBoard: KBD_Key(k_Left) || KBD_Key(k_Right)).
        if (dx < 0)
        {
            if (_gAddX >= 0) _gAddX = -1;
            _gAddX--;
            if (-_gAddX > MaxAddX) _gAddX = -MaxAddX;
        }
        else if (dx > 0)
        {
            if (_gAddX <= 0) _gAddX = 1;
            _gAddX++;
            if (_gAddX > MaxAddX) _gAddX = MaxAddX;
        }
        else
        {
            _gAddX /= 2;  // C: signed truncation toward 0 — C# / matches.
        }

        // Y-axis ramp.
        if (dy < 0)
        {
            if (_gAddY >= 0) _gAddY = -1;
            _gAddY--;
            if (-_gAddY > MaxAddY) _gAddY = -MaxAddY;
        }
        else if (dy > 0)
        {
            if (_gAddY <= 0) _gAddY = 1;
            _gAddY++;
            if (_gAddY > MaxAddY) _gAddY = MaxAddY;
        }
        else
        {
            _gAddY /= 2;
        }

        int newX = X + _gAddX;
        int newY = Y + _gAddY;
        if (newX < MinX) { newX = MinX; _gAddX = 0; }
        if (newX > MaxX) { newX = MaxX; _gAddX = 0; }
        if (newY < MinY) { newY = MinY; _gAddY = 0; }
        if (newY > MaxY) { newY = MaxY; _gAddY = 0; }
        X = newX;
        Y = newY;

        int bankRange = System.Math.Abs(X - _oldX) >> 2;
        if (bankRange > 3) bankRange = 3;
        if (X < _oldX)
        {
            if (Pic < BasePic + bankRange) Pic++;
        }
        else if (X > _oldX)
        {
            if (Pic > BasePic - bankRange) Pic--;
        }
        else
        {
            if (Pic > BasePic) Pic--;
            else if (Pic < BasePic) Pic++;
        }
        _oldX = X;
    }
}
