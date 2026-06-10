namespace Raptor.Sim;

/// <summary>
/// Frame-count totals for the AGX cinematics, the single source of truth shared by
/// the Sim auto-advance (<see cref="MenuStateMachine.CompleteCutsceneIfDone"/>) and the
/// View frame builder (<c>Raptor.View.CutsceneLibrary</c>). Pure integers — no engine
/// time, RNG, or <c>delta</c> (sim rule #1).
///
/// Each AGX scene holds <c>count</c> frames for <c>rate</c> ticks each, looped <c>loops</c>
/// times (the <c>MOVIE_Play(frm, loops, …)</c> argument), then an optional trailing
/// palette fade. Numbers are read straight from dosraptor SOURCE/INTRO.C.
/// </summary>
public static class CutsceneTimings
{
    // INTRO_Death1 (air) + INTRO_Death2 (ground, MOVIE_Play loops=8) + GFX_FadeOut(100).
    public const int DeathAirCount    = 30;
    public const int DeathAirRate     = 11;
    public const int DeathGroundCount = 6;
    public const int DeathGroundRate  = 3;
    public const int DeathGroundLoops = 8;
    public const int DeathFade        = 100;
    public const int DeathTotal =
        DeathAirCount * DeathAirRate
        + DeathGroundCount * DeathGroundRate * DeathGroundLoops
        + DeathFade;   // 330 + 144 + 100 = 574

    // INTRO_Landing: 33 frames @ rate 10, last frame M_FADEOUT 64.
    public const int LandingCount = 33;
    public const int LandingRate  = 10;
    public const int LandingFade  = 64;
    public const int LandingTotal = LandingCount * LandingRate + LandingFade;   // 394

    // INTRO_PlayMain = City + Side1(×2) + Pilot + Side2(SHIPSD1+SHIPSD2) + Explosion.
    public const int CityCount  = 30; public const int CityRate  = 8;
    public const int Side1Count = 20; public const int Side1Rate = 18; public const int Side1Loops = 2;
    public const int PilotCount = 21; public const int PilotRate = 10;
    public const int Side2Count = 20; public const int Side2Rate = 18;   // SHIPSD1 then SHIPSD2
    public const int ExploCount = 22; public const int ExploRate = 12; public const int ExploFade = 60;
    public const int PlayMainTotal =
        CityCount * CityRate
        + Side1Count * Side1Rate * Side1Loops
        + PilotCount * PilotRate
        + Side2Count * Side2Rate          // SHIPSD1 pass
        + Side2Count * Side2Rate          // SHIPSD2 pass
        + ExploCount * ExploRate
        + ExploFade;                      // 240+720+210+360+360+264+60 = 2214

    // INTRO_Credits publisher logos: APOGEE_PIC held 30×4, then CYGNUS_PIC held 65×3.
    public const int ApogeeHold = 30 * 4;   // 120
    public const int CygnusHold = 65 * 3;   // 195
    public const int LogosTotal = ApogeeHold + CygnusHold;   // 315

    // The full startup attract = INTRO_Credits (logos) then INTRO_PlayMain (RAP.C).
    public const int IntroTotal = LogosTotal + PlayMainTotal;   // 2529

    // INTRO_EndGame(0) cinematic = INTRO_Game1End (5 frames @4, MOVIE_Play loops=8) then
    // INTRO_Landing. (INTRO_Base is skipped — !GAME2 in shareware.) The WIN_WinGame text +
    // WIN_Order screens that follow are menu UI, handled outside the cutscene player.
    public const int Game1EndCount = 5; public const int Game1EndRate = 4; public const int Game1EndLoops = 8;
    public const int Game1EndContent = Game1EndCount * Game1EndRate * Game1EndLoops;   // 160
    public const int VictoryTotal = Game1EndContent + LandingCount * LandingRate + LandingFade;   // 160+330+64 = 554

    // Episode 1 ships MAP1G1..MAP9G1; clearing wave 9 ends the episode (C game_wave==dwrap).
    public const int Episode1WaveCount = 9;
}
