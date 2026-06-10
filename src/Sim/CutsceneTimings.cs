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
}
