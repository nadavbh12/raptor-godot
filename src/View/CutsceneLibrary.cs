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

    /// INTRO_PlayMain: the startup/attract intro — City, Side1 (×2), Pilot, Side2
    /// (SHIPSD1 then SHIPSD2), Explosion, with the explosion's trailing fade. All AGX
    /// frames render identically, so the five sub-scenes concatenate into one movie.
    public static AgxMovie AttractIntro(string agxRoot)
    {
        var frames = new List<AgxMovieFrame>();
        Append(frames, agxRoot, "CHASE_AGX",   CutsceneTimings.CityCount,  CutsceneTimings.CityRate,  loops: 1);
        Append(frames, agxRoot, "SHIPSD1_AGX", CutsceneTimings.Side1Count, CutsceneTimings.Side1Rate, loops: CutsceneTimings.Side1Loops);
        Append(frames, agxRoot, "PILOT_AGX",   CutsceneTimings.PilotCount, CutsceneTimings.PilotRate, loops: 1);
        Append(frames, agxRoot, "SHIPSD1_AGX", CutsceneTimings.Side2Count, CutsceneTimings.Side2Rate, loops: 1);  // Side2 pass A
        Append(frames, agxRoot, "SHIPSD2_AGX", CutsceneTimings.Side2Count, CutsceneTimings.Side2Rate, loops: 1);  // Side2 pass B
        Append(frames, agxRoot, "EXPLO_AGX",   CutsceneTimings.ExploCount, CutsceneTimings.ExploRate, loops: 1);
        return new AgxMovie(frames, CutsceneTimings.ExploFade);
    }

    /// INTRO_Credits publisher logos: APOGEE_PIC then CYGNUS_PIC (full-screen PICs under
    /// assets/sprites, each held a fixed number of ticks). Drawn like any movie frame.
    public static AgxMovie Logos(string spritesRoot)
    {
        var frames = new List<AgxMovieFrame>
        {
            new(Path.Combine(spritesRoot, "0039_APOGEE_PIC.png"), CutsceneTimings.ApogeeHold),
            new(Path.Combine(spritesRoot, "0040_CYGNUS_PIC.png"), CutsceneTimings.CygnusHold),
        };
        return new AgxMovie(frames, 0);
    }

    /// The full startup attract shown once at launch: INTRO_Credits (logos) then
    /// INTRO_PlayMain (the five AGX scenes). One movie — every frame is a full-screen image.
    public static AgxMovie StartupIntro(string agxRoot, string spritesRoot)
    {
        var frames = new List<AgxMovieFrame>();
        frames.AddRange(Logos(spritesRoot).Frames);
        frames.AddRange(AttractIntro(agxRoot).Frames);
        return new AgxMovie(frames, CutsceneTimings.ExploFade);
    }

    /// <summary>The movie a cutscene <see cref="WinState"/> renders, or null for non-cutscene states.</summary>
    public static AgxMovie? ForState(string agxRoot, string spritesRoot, WinState state) => state switch
    {
        WinState.Death   => Death(agxRoot),
        WinState.Landing => Landing(agxRoot),
        WinState.Intro   => StartupIntro(agxRoot, spritesRoot),
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
