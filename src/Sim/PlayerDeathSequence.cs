using System;
using System.Collections.Generic;
using Raptor.Sim.Player;
using Raptor.Sim.Shots;

namespace Raptor.Sim;

/// <summary>
/// The player-death explosion sequence (RAP.C end-of-life). On death a countdown
/// runs for <see cref="EndDuration"/> ticks: each tick scatters two small wing
/// explosions, and at <see cref="EndExplode"/> a big centre blast plus a debris
/// burst fire and the ship hides. At zero it parks at a -2 sentinel that
/// WaveController consumes to end the wave. Owns the countdown; WaveController
/// drives it in phase order and applies the returned explosions / hide-player flag
/// (the established "return a result, caller mutates" pattern). The shared shooter
/// RNG is passed in so the single stream — and its draw order — stays intact.
/// </summary>
internal sealed class PlayerDeathSequence
{
    internal const int EndDuration = 20 * 3;
    internal const int EndExplode = 24;

    // C exptype constants (SOURCE/MAP.H); same values as WaveController's shared
    // copies — duplicated (not referenced) to avoid a back-dependency on WaveController.
    private const int ExpAirSmall1 = 0;   // EXP_AIRSMALL1 → EXPLO2_BLK
    private const int ExpAirLarge  = 2;   // EXP_AIRLARGE → LGFLAK_BLK
    private const int ExpAirSmall2 = 10;  // EXP_AIRSMALL2 → SMFLAK_BLK
    private const int ExpAirMed2   = 10;  // A_MED_AIR_EXPLO2 → SMFLAK_BLK

    internal readonly record struct DeathExplosion(int ExpType, int X, int Y);
    internal readonly record struct TickResult(IReadOnlyList<DeathExplosion> Explosions, bool HidePlayer);

    private int _countdown = -1;

    /// <summary>True while the death countdown is running (gates shield recharge, etc.).</summary>
    public bool Active => _countdown >= 0;

    /// <summary>Begin the death sequence (mirrors C setting the countdown on death).</summary>
    public void Trigger() => _countdown = EndDuration;

    /// <summary>Reset per wave.</summary>
    public void Reset() => _countdown = -1;

    /// <summary>
    /// One death-sequence tick: returns the explosions to spawn this tick and
    /// whether the ship should now be hidden. The countdown advances; at 0 it parks
    /// at the -2 sentinel (see <see cref="ConsumeSentinel"/>). No-op when inactive.
    /// </summary>
    public TickResult Tick(int playerX, int playerY, Random rng)
    {
        if (_countdown < 0)
            return new TickResult(Array.Empty<DeathExplosion>(), false);

        var explosions = BuildExplosions(playerX, playerY, _countdown, rng);
        bool hidePlayer = _countdown == EndExplode;

        if (_countdown == 0)
            _countdown = -2;
        else
            _countdown--;

        return new TickResult(explosions, hidePlayer);
    }

    /// <summary>
    /// After the countdown parks at -2 the next frame consumes it: returns true
    /// once (resetting to -1) so WaveController can end the wave and notify the menu.
    /// </summary>
    public bool ConsumeSentinel()
    {
        if (_countdown != -2) return false;
        _countdown = -1;
        return true;
    }

    internal static List<DeathExplosion> BuildExplosions(
        int playerX, int playerY, int countdown, Random rng)
    {
        var explosions = new List<DeathExplosion>
        {
            new(ExpAirSmall1, playerX + PlayerShooter.NextRandom(rng, 32, "death.med.x"),
                playerY + PlayerShooter.NextRandom(rng, 32, "death.med.y")),
            new(ExpAirSmall2, playerX + PlayerShooter.NextRandom(rng, 32, "death.small.x"),
                playerY + PlayerShooter.NextRandom(rng, 32, "death.small.y")),
        };

        if (countdown == EndExplode)
        {
            explosions.Add(new DeathExplosion(ExpAirLarge, playerX + 16, playerY + 16));
            for (int i = 0; i < (PlayerLogic.SpriteWidth * PlayerLogic.SpriteHeight) / 2; i++)
            {
                int x = playerX - PlayerLogic.SpriteWidth / 2
                        + PlayerShooter.NextRandom(rng, PlayerLogic.SpriteWidth * 2, "death.burst.x");
                int y = playerY - PlayerLogic.SpriteHeight / 2
                        + PlayerShooter.NextRandom(rng, PlayerLogic.SpriteHeight * 2, "death.burst.y");
                explosions.Add(new DeathExplosion((i & 1) != 0 ? ExpAirLarge : ExpAirMed2, x, y));
            }
        }

        return explosions;
    }
}
