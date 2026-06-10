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

    // ---- CutsceneTimings (Sim-side totals) ----

    [Fact]
    public void Death_total_matches_legacy_574()
    {
        Assert.Equal(574, CutsceneTimings.DeathTotal);
    }

    [Fact]
    public void Landing_total_is_content_plus_fade()
    {
        // 33 LANDING frames held 10 ticks each + 64-step trailing fade (INTRO_Landing).
        Assert.Equal(33 * 10 + 64, CutsceneTimings.LandingTotal);
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

        Assert.True(m.TrySelectFrame(10, out var f1, out _));     // second frame at rate 10
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
        Assert.Equal(10, m.Frames[0].DurationFrames);
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
        Assert.Equal(11, m.Frames[0].DurationFrames);
        Assert.EndsWith("DOWN_AGX_29.png", m.Frames[29].Path);
        Assert.EndsWith("SDEATH_AGX_00.png", m.Frames[30].Path);
        Assert.Equal(3, m.Frames[30].DurationFrames);
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
        Assert.True(m.TrySelectFrame(10, out var stillFirst, out _));
        Assert.True(m.TrySelectFrame(11, out var second, out _));
        Assert.True(m.TrySelectFrame(330, out var firstGround, out _));

        Assert.EndsWith("DOWN_AGX_00.png", first.Path);
        Assert.Equal(first.Path, stillFirst.Path);
        Assert.EndsWith("DOWN_AGX_01.png", second.Path);
        Assert.EndsWith("SDEATH_AGX_00.png", firstGround.Path);
    }

    // ---- ForState routing (what the renderer asks for) ----

    [Fact]
    public void ForState_maps_cutscene_states_to_movies()
    {
        string root = AgxRoot();
        Assert.NotNull(CutsceneLibrary.ForState(root, WinState.Death));
        Assert.NotNull(CutsceneLibrary.ForState(root, WinState.Landing));
        Assert.NotNull(CutsceneLibrary.ForState(root, WinState.Intro));
        Assert.Null(CutsceneLibrary.ForState(root, WinState.Hangar));
        Assert.Null(CutsceneLibrary.ForState(root, WinState.Menu));
    }

    // ---- Attract intro (INTRO_PlayMain = City + Side1×2 + Pilot + Side2 + Explosion) ----

    [Fact]
    public void Intro_total_sums_attract_segments_plus_explosion_fade()
    {
        // City 30@8 + Side1 20@18×2 + Pilot 21@10 + Side2(SHIPSD1+SHIPSD2 20@18) + Explo 22@12 + fade 60.
        int expected = 30 * 8 + 20 * 18 * 2 + 21 * 10 + 20 * 18 + 20 * 18 + 22 * 12 + 60;
        Assert.Equal(2214, expected);
        Assert.Equal(expected, CutsceneTimings.IntroTotal);
    }

    [Fact]
    public void AttractIntro_concatenates_city_side1x2_pilot_side2_explosion()
    {
        var m = CutsceneLibrary.AttractIntro(AgxRoot());

        // 30 city + 40 side1(×2) + 21 pilot + 20 side2a + 20 side2b + 22 explosion = 153 frames.
        Assert.Equal(153, m.Frames.Count);
        Assert.EndsWith("CHASE_AGX_00.png", m.Frames[0].Path);
        Assert.Equal(8, m.Frames[0].DurationFrames);
        Assert.EndsWith("SHIPSD1_AGX_00.png", m.Frames[30].Path);   // Side1 loop 1
        Assert.Equal(18, m.Frames[30].DurationFrames);
        Assert.EndsWith("SHIPSD1_AGX_00.png", m.Frames[50].Path);   // Side1 loop 2
        Assert.EndsWith("PILOT_AGX_00.png", m.Frames[70].Path);
        Assert.EndsWith("SHIPSD1_AGX_00.png", m.Frames[91].Path);   // Side2a
        Assert.EndsWith("SHIPSD2_AGX_00.png", m.Frames[111].Path);  // Side2b
        Assert.EndsWith("EXPLO_AGX_00.png", m.Frames[131].Path);
        Assert.EndsWith("EXPLO_AGX_21.png", m.Frames[^1].Path);
        Assert.Equal(60, m.FadeOutFrames);
        Assert.Equal(CutsceneTimings.IntroTotal, m.TotalFrames);
        Assert.All(m.Frames.Select(f => f.Path).Distinct(), p => Assert.True(File.Exists(p), p));
    }
}
