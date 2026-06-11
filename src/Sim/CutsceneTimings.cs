namespace Raptor.Sim;

/// <summary>
/// Frame-count totals for the AGX cinematics, the single source of truth shared by
/// the Sim auto-advance (<see cref="MenuStateMachine.CompleteCutsceneIfDone"/>) and the
/// View frame builder (<c>Raptor.View.CutsceneLibrary</c>). Pure integers — no engine
/// time, RNG, or <c>delta</c> (sim rule #1).
///
/// Each AGX scene shows <c>count</c> frames at a <c>rate</c> (frames-per-second), looped
/// <c>loops</c> times (the <c>MOVIE_Play(frm, loops, …)</c> argument), then an optional
/// trailing fade. C's <c>GFX_WaitUpdate(rate)</c> waits <c>70/rate</c> of the 70 Hz ticks
/// per frame (rate is fps, not the hold), so the per-frame hold in SimClock frames is
/// <see cref="Hold"/> = 70/rate. Numbers are read straight from dosraptor SOURCE/INTRO.C.
/// Fades (GFX_FadeOut) run with no per-tick wait in C — kept here only as a short visual
/// tail, not faithful timing.
/// </summary>
public static class CutsceneTimings
{
    /// <summary>C GFX_WaitUpdate: per-frame hold in 70 Hz ticks for an fps <paramref name="rate"/>.</summary>
    public static int Hold(int rate)
    {
        if (rate > 70) rate = 70; else if (rate < 1) rate = 1;
        return 70 / rate;
    }

    // INTRO_Death1 (air) + INTRO_Death2 (ground, MOVIE_Play loops=8) + GFX_FadeOut(100).
    public const int DeathAirCount    = 30;
    public const int DeathAirRate     = 11;   // fps → hold 70/11 = 6
    public const int DeathGroundCount = 6;
    public const int DeathGroundRate  = 3;    // fps → hold 70/3 = 23
    public const int DeathGroundLoops = 8;
    public const int DeathFade        = 100;
    public const int DeathTotal =
        DeathAirCount * (70 / DeathAirRate)
        + DeathGroundCount * (70 / DeathGroundRate) * DeathGroundLoops
        + DeathFade;   // 180 + 1104 + 100 = 1384

    // INTRO_Landing: 33 frames @ 10 fps, last frame M_FADEOUT 64.
    public const int LandingCount = 33;
    public const int LandingRate  = 10;   // hold 70/10 = 7
    public const int LandingFade  = 64;
    public const int LandingTotal = LandingCount * (70 / LandingRate) + LandingFade;   // 231+64 = 295

    // INTRO_PlayMain = City + Side1(×2) + Pilot + Side2(SHIPSD1+SHIPSD2) + Explosion.
    public const int CityCount  = 30; public const int CityRate  = 8;   // hold 8
    public const int Side1Count = 20; public const int Side1Rate = 18; public const int Side1Loops = 2;  // hold 3
    public const int PilotCount = 21; public const int PilotRate = 10;  // hold 7
    public const int Side2Count = 20; public const int Side2Rate = 18;  // SHIPSD1 then SHIPSD2; hold 3
    public const int ExploCount = 22; public const int ExploRate = 12; public const int ExploFade = 60;  // hold 5
    public const int PlayMainTotal =
        CityCount * (70 / CityRate)
        + Side1Count * (70 / Side1Rate) * Side1Loops
        + PilotCount * (70 / PilotRate)
        + Side2Count * (70 / Side2Rate)   // SHIPSD1 pass
        + Side2Count * (70 / Side2Rate)   // SHIPSD2 pass
        + ExploCount * (70 / ExploRate)
        + ExploFade;                      // 240+120+147+60+60+110+60 = 797

    // INTRO_Credits publisher logos: APOGEE_PIC held 30×4, then CYGNUS_PIC held 65×3.
    // These are explicit per-iteration tick loops in C (not GFX_WaitUpdate), so they are
    // already in ticks — no 70/rate conversion.
    public const int ApogeeHold = 30 * 4;   // 120
    public const int CygnusHold = 65 * 3;   // 195
    public const int LogosTotal = ApogeeHold + CygnusHold;   // 315

    // The full startup attract = INTRO_Credits (logos) then INTRO_PlayMain (RAP.C).
    public const int IntroTotal = LogosTotal + PlayMainTotal;   // 1112

    // INTRO_EndGame(0) cinematic = INTRO_Game1End (5 frames @ 4 fps, MOVIE_Play loops=8) then
    // INTRO_Landing. (INTRO_Base is skipped — !GAME2 in shareware.) The WIN_WinGame text +
    // WIN_Order screens that follow are menu UI, handled outside the cutscene player.
    public const int Game1EndCount = 5; public const int Game1EndRate = 4; public const int Game1EndLoops = 8;  // hold 17
    public const int Game1EndContent = Game1EndCount * (70 / Game1EndRate) * Game1EndLoops;   // 680
    public const int VictoryTotal = Game1EndContent + LandingCount * (70 / LandingRate) + LandingFade;   // 680+231+64 = 975

    // Episode 1 ships MAP1G1..MAP9G1; clearing wave 9 ends the episode (C game_wave==dwrap).
    public const int Episode1WaveCount = 9;

    // Idle time on the main menu before the attract loop replays (C WINDOWS.C:108
    // DEMO_DELAY = 800*5; d_count increments ~once per 70 Hz frame → ~57 s).
    public const int IdleAttractDelay = 800 * 5;   // 4000
}
