using System.Collections.Generic;
using System.IO;
using Raptor.Sim;

namespace Raptor.View;

/// <summary>
/// Builds the AGX cinematic movies from the per-frame PNGs under <c>assets/agx</c>,
/// using the frame counts/rates in <see cref="CutsceneTimings"/> (single source of
/// truth). Mirrors dosraptor SOURCE/INTRO.C. View-only, parity-inert.
/// </summary>
public static class CutsceneLibrary
{
    /// INTRO_Death = INTRO_Death1 (DOWN air) + INTRO_Death2 (SDEATH ground ×8), fade 100.
    public static AgxMovie Death(string agxRoot)
    {
        var frames = new List<AgxMovieFrame>();
        Append(frames, agxRoot, "DOWN_AGX",
            CutsceneTimings.DeathAirCount, CutsceneTimings.DeathAirRate, loops: 1);
        Append(frames, agxRoot, "SDEATH_AGX",
            CutsceneTimings.DeathGroundCount, CutsceneTimings.DeathGroundRate,
            loops: CutsceneTimings.DeathGroundLoops);
        return new AgxMovie(frames, CutsceneTimings.DeathFade);
    }

    /// INTRO_Landing: ship lands on base, 33 frames @ rate 10, trailing fade 64.
    public static AgxMovie Landing(string agxRoot)
    {
        var frames = new List<AgxMovieFrame>();
        Append(frames, agxRoot, "LANDING_AGX",
            CutsceneTimings.LandingCount, CutsceneTimings.LandingRate, loops: 1);
        return new AgxMovie(frames, CutsceneTimings.LandingFade);
    }

    /// <summary>The movie a cutscene <see cref="WinState"/> renders, or null for non-cutscene states.</summary>
    public static AgxMovie? ForState(string agxRoot, WinState state) => state switch
    {
        WinState.Death   => Death(agxRoot),
        WinState.Landing => Landing(agxRoot),
        _                => null,
    };

    private static void Append(
        List<AgxMovieFrame> frames, string agxRoot, string family,
        int count, int rate, int loops)
    {
        for (int loop = 0; loop < loops; loop++)
            for (int i = 0; i < count; i++)
                frames.Add(new AgxMovieFrame(
                    Path.Combine(agxRoot, $"{family}_{i:D2}.png"), rate));
    }
}
