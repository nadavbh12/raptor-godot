using System;
using System.IO;
using System.Linq;
using Raptor.Sim;
using Raptor.View;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// Foundation for the AGX cinematics (spec 2026-06-10-cutscenes-design).
/// Generalizes the death-scene movie pattern: AgxMovie (frames + trailing fade +
/// total), CutsceneLibrary (per-scene builders), CutsceneTimings (Sim-side totals).
/// The death assertions are migrated from the old AgxMovieSequenceTests verbatim —
/// they are the "death invariance" regression guard.
/// </summary>
public class CutsceneTests
{
    private static string AgxRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != Path.GetPathRoot(dir))
        {
            string candidate = Path.Combine(dir, "assets", "agx");
            if (Directory.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)!.FullName;
        }
        return Path.GetFullPath(Path.Combine("assets", "agx"));
    }

    private static string SpritesRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != Path.GetPathRoot(dir))
        {
            string candidate = Path.Combine(dir, "assets", "sprites");
            if (Directory.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)!.FullName;
        }
        return Path.GetFullPath(Path.Combine("assets", "sprites"));
    }

    // ---- CutsceneTimings (Sim-side totals) ----

    // Per-frame hold = 70/framerate ticks (GFX_WaitUpdate: count = 70/count at 70 Hz);
    // framerate is fps, NOT the hold count.

    [Fact]
    public void Death_total_uses_70_over_framerate_holds()
    {
        // air 30 @ (70/11=6) + ground 6 @ (70/3=23) ×8 loops + 100 fade.
        Assert.Equal(30 * (70 / 11) + 6 * (70 / 3) * 8 + 100, CutsceneTimings.DeathTotal);
        Assert.Equal(1384, CutsceneTimings.DeathTotal);
    }

    [Fact]
    public void Landing_total_is_content_plus_fade()
    {
        // 33 LANDING frames held 70/10=7 ticks each + 64 trailing fade (INTRO_Landing @10 fps).
        Assert.Equal(33 * (70 / 10) + 64, CutsceneTimings.LandingTotal);
        Assert.Equal(295, CutsceneTimings.LandingTotal);
    }

    // ---- AgxMovie totality + total accounting ----

    [Fact]
    public void Movie_total_frames_equals_content_plus_fade()
    {
        var m = CutsceneLibrary.Landing(AgxRoot());
        int content = m.Frames.Sum(f => f.DurationFrames);
        Assert.Equal(content, m.ContentFrames);
        Assert.Equal(content + m.FadeOutFrames, m.TotalFrames);
    }

    [Fact]
    public void Movie_selects_frame_iff_elapsed_in_total_range()
    {
        var m = CutsceneLibrary.Landing(AgxRoot());

        Assert.True(m.TrySelectFrame(0, out var f0, out var a0));
        Assert.EndsWith("LANDING_AGX_00.png", f0.Path);
        Assert.Equal(1f, a0);

        Assert.True(m.TrySelectFrame(70 / 10, out var f1, out _));   // second frame after a 7-tick hold
        Assert.EndsWith("LANDING_AGX_01.png", f1.Path);

        // Inside the trailing fade window: last frame, alpha < 1.
        Assert.True(m.TrySelectFrame(m.TotalFrames - 1, out var fLast, out var aFade));
        Assert.EndsWith("LANDING_AGX_32.png", fLast.Path);
        Assert.True(aFade is < 1f and >= 0f);

        // Past the end: nothing.
        Assert.False(m.TrySelectFrame(m.TotalFrames, out _, out _));
        Assert.False(m.TrySelectFrame(m.TotalFrames + 100, out _, out _));
    }

    // ---- CutsceneLibrary.Landing builds the faithful sequence ----

    [Fact]
    public void Landing_movie_builds_33_frames_at_rate_10_with_fade()
    {
        var m = CutsceneLibrary.Landing(AgxRoot());

        Assert.Equal(33, m.Frames.Count);
        Assert.EndsWith("LANDING_AGX_00.png", m.Frames[0].Path);
        Assert.Equal(70 / 10, m.Frames[0].DurationFrames);   // 7-tick hold @ 10 fps
        Assert.EndsWith("LANDING_AGX_32.png", m.Frames[^1].Path);
        Assert.Equal(64, m.FadeOutFrames);
        Assert.Equal(CutsceneTimings.LandingTotal, m.TotalFrames);
        Assert.All(m.Frames.Select(f => f.Path), p => Assert.True(File.Exists(p), p));
    }

    // ---- CutsceneLibrary.Death — migrated invariance guard ----

    [Fact]
    public void Death_movie_preserves_air_then_ground_frame_sequence()
    {
        var m = CutsceneLibrary.Death(AgxRoot());

        Assert.Equal(78, m.Frames.Count);  // 30 air + 6 ground × 8 loops
        Assert.EndsWith("DOWN_AGX_00.png", m.Frames[0].Path);
        Assert.Equal(70 / 11, m.Frames[0].DurationFrames);    // 6-tick hold @ 11 fps
        Assert.EndsWith("DOWN_AGX_29.png", m.Frames[29].Path);
        Assert.EndsWith("SDEATH_AGX_00.png", m.Frames[30].Path);
        Assert.Equal(70 / 3, m.Frames[30].DurationFrames);    // 23-tick hold @ 3 fps
        Assert.EndsWith("SDEATH_AGX_05.png", m.Frames[^1].Path);
        Assert.Equal(CutsceneTimings.DeathTotal, m.TotalFrames);
        Assert.All(m.Frames.Select(f => f.Path).Distinct(), p => Assert.True(File.Exists(p), p));
        Assert.DoesNotContain(m.Frames, f => f.Path.EndsWith("DOWN_AGX.png"));
        Assert.DoesNotContain(m.Frames, f => f.Path.EndsWith("SDEATH_AGX.png"));
    }

    [Fact]
    public void Death_movie_selects_frames_by_c_framerate_hold_counts()
    {
        var m = CutsceneLibrary.Death(AgxRoot());

        Assert.True(m.TrySelectFrame(0, out var first, out _));
        Assert.True(m.TrySelectFrame(5, out var stillFirst, out _));         // within the 6-tick hold
        Assert.True(m.TrySelectFrame(70 / 11, out var second, out _));       // next frame at 6
        Assert.True(m.TrySelectFrame(30 * (70 / 11), out var firstGround, out _));  // after 30 air frames

        Assert.EndsWith("DOWN_AGX_00.png", first.Path);
        Assert.Equal(first.Path, stillFirst.Path);
        Assert.EndsWith("DOWN_AGX_01.png", second.Path);
        Assert.EndsWith("SDEATH_AGX_00.png", firstGround.Path);
    }

    // ---- Intro sound-effect schedule (INTRO.C per-frame soundfx) ----

    [Fact]
    public void Attract_intro_schedules_per_frame_sound_effects()
    {
        var m = CutsceneLibrary.AttractIntro(AgxRoot());

        // INTRO_City: FX_FLYBY at frames 4 and 9.
        Assert.Equal("sound.fx_flyby", m.Frames[4].Sfx);
        Assert.Equal("sound.fx_flyby", m.Frames[9].Sfx);
        Assert.Null(m.Frames[0].Sfx);
        // INTRO_Explosion ends on an air explosion (last frame soundfx = FX_AIREXPLO).
        Assert.Equal("sound3d.fx_airexplo", m.Frames[^1].Sfx);
        // Side2's SHIPSD2 pass fires the intro gun (frames within-scene index > 1).
        Assert.Contains(m.Frames, f => f.Sfx == "sound.fx_introgun");
    }

    [Fact]
    public void Attract_intro_emits_sfx_sequence_over_time()
    {
        // Mirror the renderer's dedup: step elapsed across the whole movie, fire a frame's
        // Sfx once when the index first advances to it.
        var m = CutsceneLibrary.AttractIntro(AgxRoot());
        var emitted = new System.Collections.Generic.List<string>();
        int last = -1;
        for (int e = 0; e < m.ContentFrames; e++)
        {
            int idx = m.FrameIndexAt(e);
            if (idx >= 0 && idx != last)
            {
                last = idx;
                if (m.Frames[idx].Sfx is { } s) emitted.Add(s);
            }
        }

        Assert.Equal(2, emitted.FindAll(s => s == "sound.fx_flyby").Count);   // two city flybys
        Assert.Contains("sound.fx_jetsnd", emitted);                          // jet during side passes
        Assert.Contains("sound.fx_introgun", emitted);                        // strafing run
        Assert.Contains("sound3d.fx_airexplo", emitted);                      // explosion
        Assert.Equal("sound3d.fx_airexplo", emitted[^1]);                     // ends on the explosion
    }

    [Fact]
    public void Frame_index_advances_with_elapsed()
    {
        var m = CutsceneLibrary.Landing(AgxRoot());
        Assert.Equal(0, m.FrameIndexAt(0));
        Assert.Equal(0, m.FrameIndexAt(70 / 10 - 1));   // still within the first hold
        Assert.Equal(1, m.FrameIndexAt(70 / 10));        // advanced to frame 1
        Assert.Equal(-1, m.FrameIndexAt(m.ContentFrames));   // past the content (fade/end)
    }

    // ---- ForState routing (what the renderer asks for) ----

    [Fact]
    public void ForState_maps_cutscene_states_to_movies()
    {
        string agx = AgxRoot(), spr = SpritesRoot();
        Assert.NotNull(CutsceneLibrary.ForState(agx, spr, WinState.Death));
        Assert.NotNull(CutsceneLibrary.ForState(agx, spr, WinState.Landing));
        Assert.NotNull(CutsceneLibrary.ForState(agx, spr, WinState.Intro));
        Assert.NotNull(CutsceneLibrary.ForState(agx, spr, WinState.Victory));
        Assert.Null(CutsceneLibrary.ForState(agx, spr, WinState.Hangar));
        Assert.Null(CutsceneLibrary.ForState(agx, spr, WinState.Menu));
    }

    // ---- Attract intro (INTRO_PlayMain = City + Side1×2 + Pilot + Side2 + Explosion) ----

    [Fact]
    public void PlayMain_total_sums_attract_segments_plus_explosion_fade()
    {
        // Holds = 70/fps: City 30@(70/8) + Side1 20@(70/18)×2 + Pilot 21@(70/10)
        // + Side2(SHIPSD1+SHIPSD2 20@(70/18)) + Explo 22@(70/12) + 60 fade.
        int expected = 30 * (70 / 8) + 20 * (70 / 18) * 2 + 21 * (70 / 10)
                     + 20 * (70 / 18) + 20 * (70 / 18) + 22 * (70 / 12) + 60;
        Assert.Equal(797, expected);
        Assert.Equal(expected, CutsceneTimings.PlayMainTotal);
    }

    [Fact]
    public void Logos_and_playmain_compose_the_full_startup_intro_total()
    {
        // INTRO_Credits (APOGEE 30×4 + CYGNUS 65×3 = 315, explicit tick loops) then INTRO_PlayMain.
        Assert.Equal(30 * 4, CutsceneTimings.ApogeeHold);
        Assert.Equal(65 * 3, CutsceneTimings.CygnusHold);
        Assert.Equal(315, CutsceneTimings.LogosTotal);
        Assert.Equal(CutsceneTimings.LogosTotal + CutsceneTimings.PlayMainTotal, CutsceneTimings.IntroTotal);
        Assert.Equal(1112, CutsceneTimings.IntroTotal);
    }

    [Fact]
    public void AttractIntro_concatenates_city_side1x2_pilot_side2_explosion()
    {
        var m = CutsceneLibrary.AttractIntro(AgxRoot());

        // 30 city + 40 side1(×2) + 21 pilot + 20 side2a + 20 side2b + 22 explosion = 153 frames.
        Assert.Equal(153, m.Frames.Count);
        Assert.EndsWith("CHASE_AGX_00.png", m.Frames[0].Path);
        Assert.Equal(70 / 8, m.Frames[0].DurationFrames);            // 8-tick hold @ 8 fps
        Assert.EndsWith("SHIPSD1_AGX_00.png", m.Frames[30].Path);   // Side1 loop 1
        Assert.Equal(70 / 18, m.Frames[30].DurationFrames);          // 3-tick hold @ 18 fps
        Assert.EndsWith("SHIPSD1_AGX_00.png", m.Frames[50].Path);   // Side1 loop 2
        Assert.EndsWith("PILOT_AGX_00.png", m.Frames[70].Path);
        Assert.EndsWith("SHIPSD1_AGX_00.png", m.Frames[91].Path);   // Side2a
        Assert.EndsWith("SHIPSD2_AGX_00.png", m.Frames[111].Path);  // Side2b
        Assert.EndsWith("EXPLO_AGX_00.png", m.Frames[131].Path);
        Assert.EndsWith("EXPLO_AGX_21.png", m.Frames[^1].Path);
        Assert.Equal(60, m.FadeOutFrames);
        Assert.Equal(CutsceneTimings.PlayMainTotal, m.TotalFrames);
        Assert.All(m.Frames.Select(f => f.Path).Distinct(), p => Assert.True(File.Exists(p), p));
    }

    // ---- Episode-1 victory (INTRO_EndGame(0) cinematic = Game1End + Landing) ----

    [Fact]
    public void Victory_total_is_game1end_content_plus_landing()
    {
        // INTRO_Game1End: 5 frames @ (70/4=17), MOVIE_Play loops=8 → 680 content; then
        // INTRO_Landing (33 @ (70/10=7) = 231 + 64 fade). Game1End fadeout folded into the cut.
        Assert.Equal(5 * (70 / 4) * 8, CutsceneTimings.Game1EndContent);
        Assert.Equal(5 * (70 / 4) * 8 + 33 * (70 / 10) + 64, CutsceneTimings.VictoryTotal);
        Assert.Equal(975, CutsceneTimings.VictoryTotal);
    }

    [Fact]
    public void Victory_movie_concatenates_game1end_then_landing()
    {
        var m = CutsceneLibrary.Victory(AgxRoot());

        // 5 game1end × 8 loops + 33 landing = 73 frames.
        Assert.Equal(40 + 33, m.Frames.Count);
        Assert.EndsWith("GAME1END_AGX_00.png", m.Frames[0].Path);
        Assert.Equal(70 / 4, m.Frames[0].DurationFrames);   // 17-tick hold @ 4 fps
        Assert.EndsWith("GAME1END_AGX_00.png", m.Frames[5].Path);   // loop 2 of game1end
        Assert.EndsWith("LANDING_AGX_00.png", m.Frames[40].Path);   // landing starts after 8 loops
        Assert.EndsWith("LANDING_AGX_32.png", m.Frames[^1].Path);
        Assert.Equal(64, m.FadeOutFrames);
        Assert.Equal(CutsceneTimings.VictoryTotal, m.TotalFrames);
        Assert.All(m.Frames.Select(f => f.Path).Distinct(), p => Assert.True(File.Exists(p), p));
    }

    [Fact]
    public void StartupIntro_prepends_publisher_logos_to_the_attract()
    {
        var m = CutsceneLibrary.StartupIntro(AgxRoot(), SpritesRoot());

        // 2 logos + 153 attract = 155 frames.
        Assert.Equal(155, m.Frames.Count);
        Assert.EndsWith("APOGEE_PIC.png", m.Frames[0].Path);
        Assert.Equal(CutsceneTimings.ApogeeHold, m.Frames[0].DurationFrames);
        Assert.EndsWith("CYGNUS_PIC.png", m.Frames[1].Path);
        Assert.Equal(CutsceneTimings.CygnusHold, m.Frames[1].DurationFrames);
        Assert.EndsWith("CHASE_AGX_00.png", m.Frames[2].Path);   // attract starts after logos
        Assert.EndsWith("EXPLO_AGX_21.png", m.Frames[^1].Path);
        Assert.Equal(CutsceneTimings.IntroTotal, m.TotalFrames);
        Assert.All(m.Frames.Select(f => f.Path).Distinct(), p => Assert.True(File.Exists(p), p));
    }
}
