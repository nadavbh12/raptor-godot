namespace Raptor.Sim;

/// <summary>
/// End-of-mission "fly-off" sequence ported from C RAP.C:599-617.
///
/// When the wave ends (last enemy cleared, no explosions remaining), C sets
/// <c>startendwave = END_DURATION</c> (60 game-loop iterations). The counter
/// decrements once per loop. While the counter is below <c>END_FLYOFF</c> (40)
/// and the player still has shield, C calls <c>IPT_FMovePlayer(xAdj, -4)</c>
/// to slide the ship up by 4 px and horizontally toward center 160 by ±8 px.
/// At <c>startendwave == END_FLYOFF</c> input locks. At 0, <c>end_wave = TRUE</c>
/// exits the gameplay loop and control returns to the hangar.
/// </summary>
public static class EndWaveSequence
{
    /// <summary>Total countdown length (C PUBLIC.H: END_DURATION = 20 * 3).</summary>
    public const int Duration = 60;

    /// <summary>Countdown value at which input locks and fly-off motion begins
    /// (C PUBLIC.H: END_FLYOFF = 20 * 2).</summary>
    public const int FlyOff = 40;

    /// <summary>
    /// Compute the per-tick player displacement during the end-wave countdown.
    /// Mirrors RAP.C:607-616.
    /// </summary>
    /// <param name="countdown">Current countdown value (60 → 0).</param>
    /// <param name="playerX">Player top-left X. C uses <c>playerx</c> directly.</param>
    /// <param name="shieldAlive">True iff shield &gt; 0.</param>
    public static (int dx, int dy) PlayerDelta(int countdown, int playerX, bool shieldAlive)
    {
        // No motion while shield is dead (C: `if (startendwave != EMPTY && shield > 0)`).
        if (!shieldAlive) return (0, 0);
        // Motion only kicks in once countdown crosses below END_FLYOFF.
        if (countdown >= FlyOff) return (0, 0);
        int dx = 0;
        if (playerX < 160 - 8) dx = 8;
        else if (playerX > 160 + 8) dx = -8;
        return (dx, -4);
    }

    /// <summary>
    /// True iff the player's directional input should be ignored. C calls
    /// <c>IPT_PauseControl(TRUE)</c> at <c>startendwave == END_FLYOFF</c> (RAP.C:601-605).
    /// We model that as "input locked once countdown ≤ FlyOff", which matches
    /// the visible behavior — the ship glides on its own past that point.
    /// </summary>
    public static bool InputLocked(int countdown) =>
        countdown >= 0 && countdown <= FlyOff;
}
