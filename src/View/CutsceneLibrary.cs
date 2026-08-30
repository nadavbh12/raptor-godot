using System;
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
        // INTRO_City: FX_FLYBY at frames 4 and 9.
        Append(frames, agxRoot, "CHASE_AGX", CutsceneTimings.CityCount, CutsceneTimings.CityRate, loops: 1,
            sfxFor: i => i is 4 or 9 ? "sound.fx_flyby" : null);
        // INTRO_Side1: background jet (MOVIE_BPatch FX_JETSND), retriggered each loop.
        Append(frames, agxRoot, "SHIPSD1_AGX", CutsceneTimings.Side1Count, CutsceneTimings.Side1Rate, loops: CutsceneTimings.Side1Loops,
            sfxFor: i => i == 0 ? "sound.fx_jetsnd" : null);
        // INTRO_Pilot: background intro-jet (FX_IJETSND → JETSND_FX).
        Append(frames, agxRoot, "PILOT_AGX", CutsceneTimings.PilotCount, CutsceneTimings.PilotRate, loops: 1,
            sfxFor: i => i == 0 ? "sound.fx_jetsnd" : null);
        // INTRO_Side2 pass A (SHIPSD1): background jet.
        Append(frames, agxRoot, "SHIPSD1_AGX", CutsceneTimings.Side2Count, CutsceneTimings.Side2Rate, loops: 1,
            sfxFor: i => i == 0 ? "sound.fx_jetsnd" : null);
        // INTRO_Side2 pass B (SHIPSD2): FX_INTROGUN on frames > 1 (the strafing run).
        Append(frames, agxRoot, "SHIPSD2_AGX", CutsceneTimings.Side2Count, CutsceneTimings.Side2Rate, loops: 1,
            sfxFor: i => i > 1 ? "sound.fx_introgun" : null);
        // INTRO_Explosion: bg jet (FX_EJETSND), FX_INTROHIT frames 2-9, FX_AIREXPLO frames>=8 odd
        // (last condition wins, mirroring C), plus the final frame.
        int exploLast = CutsceneTimings.ExploCount - 1;
        Append(frames, agxRoot, "EXPLO_AGX", CutsceneTimings.ExploCount, CutsceneTimings.ExploRate, loops: 1,
            sfxFor: i =>
            {
                string? s = null;
                if (i == 0) s = "sound.fx_jetsnd";
                if (i >= 2 && i < 10) s = "sound.fx_introhit";
                if (i >= 8 && (i & 1) == 1) s = "sound3d.fx_airexplo";
                if (i == exploLast) s = "sound3d.fx_airexplo";
                return s;
            });
        return new AgxMovie(frames, CutsceneTimings.ExploFade);
    }

    /// INTRO_Credits publisher logos: APOGEE_PIC then CYGNUS_PIC (full-screen PICs under
    /// assets/sprites, each held a fixed number of ticks). Drawn like any movie frame.
    public static AgxMovie Logos(string spritesRoot)
    {
        var frames = new List<AgxMovieFrame>
        {
            new(FindSprite(spritesRoot, "APOGEE_PIC"), CutsceneTimings.ApogeeHold),
            new(FindSprite(spritesRoot, "CYGNUS_PIC"), CutsceneTimings.CygnusHold),
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

    /// INTRO_EndGame(0) cinematic: INTRO_Game1End (GAME1END_AGX 5@4 ×8) then INTRO_Landing.
    /// (INTRO_Base is skipped — !GAME2 in shareware.)
    public static AgxMovie Victory(string agxRoot)
    {
        var frames = new List<AgxMovieFrame>();
        Append(frames, agxRoot, "GAME1END_AGX",
            CutsceneTimings.Game1EndCount, CutsceneTimings.Game1EndRate,
            loops: CutsceneTimings.Game1EndLoops);
        Append(frames, agxRoot, "LANDING_AGX",
            CutsceneTimings.LandingCount, CutsceneTimings.LandingRate, loops: 1);
        return new AgxMovie(frames, CutsceneTimings.LandingFade);
    }

    /// <summary>The movie a cutscene <see cref="WinState"/> renders, or null for non-cutscene states.</summary>
    public static AgxMovie? ForState(string agxRoot, string spritesRoot, WinState state) => state switch
    {
        WinState.Death   => Death(agxRoot),
        WinState.Landing => Landing(agxRoot),
        WinState.Intro   => StartupIntro(agxRoot, spritesRoot),
        WinState.Victory => Victory(agxRoot),
        _                => null,
    };

    private static void Append(
        List<AgxMovieFrame> frames, string agxRoot, string family,
        int count, int rate, int loops, Func<int, string?>? sfxFor = null)
    {
        // rate is fps; the per-frame hold in 70 Hz SimClock ticks is 70/rate (GFX_WaitUpdate).
        // sfxFor maps the within-scene frame index (0..count-1) to a one-shot SoundEmitter label.
        int hold = CutsceneTimings.Hold(rate);
        for (int loop = 0; loop < loops; loop++)
            for (int i = 0; i < count; i++)
                frames.Add(new AgxMovieFrame(
                    Path.Combine(agxRoot, $"{family}_{i:D2}.png"), hold, sfxFor?.Invoke(i)));
    }

    /// <summary>
    /// Resolve a sprite by its GLB item name, ignoring the numeric prefix.
    /// Extracted files are NNNN_&lt;iname&gt;.png where NNNN is the item's position
    /// in the GLB table; that position shifts between game editions, so only
    /// the name is a stable identifier. Returns a path that may not exist,
    /// which the frame loader already treats as a missing texture.
    /// </summary>
    private static string FindSprite(string spritesRoot, string iname)
    {
        if (Directory.Exists(spritesRoot))
        {
            string[] hits = Directory.GetFiles(spritesRoot, $"*_{iname}.png");
            if (hits.Length > 0)
            {
                System.Array.Sort(hits, System.StringComparer.Ordinal);
                return hits[0];
            }
        }
        return Path.Combine(spritesRoot, $"{iname}.png");
    }
}
