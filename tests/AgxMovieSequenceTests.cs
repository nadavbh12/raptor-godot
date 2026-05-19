using System;
using System.IO;
using System.Linq;
using Raptor.View;
using Xunit;

namespace Raptor.Tests;

public class AgxMovieSequenceTests
{
    private static string AssetRoot()
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

    [Fact]
    public void Death_movie_uses_extracted_air_then_ground_agx_frame_sequence()
    {
        string agxRoot = AssetRoot();

        var sequence = AgxMovieSequence.BuildDeathSequence(agxRoot).ToList();

        Assert.Equal(78, sequence.Count);
        Assert.Equal(Path.Combine(agxRoot, "DOWN_AGX_00.png"), sequence[0].Path);
        Assert.Equal(11, sequence[0].DurationFrames);
        Assert.Equal(Path.Combine(agxRoot, "DOWN_AGX_29.png"), sequence[29].Path);
        Assert.Equal(Path.Combine(agxRoot, "SDEATH_AGX_00.png"), sequence[30].Path);
        Assert.Equal(3, sequence[30].DurationFrames);
        Assert.Equal(Path.Combine(agxRoot, "SDEATH_AGX_05.png"), sequence[^1].Path);
        Assert.All(sequence.Select(f => f.Path).Distinct(), path => Assert.True(File.Exists(path), path));
        Assert.DoesNotContain(sequence, f => f.Path.EndsWith("DOWN_AGX.png"));
        Assert.DoesNotContain(sequence, f => f.Path.EndsWith("SDEATH_AGX.png"));
    }

    [Fact]
    public void Death_movie_selects_frames_by_c_framerate_hold_counts()
    {
        string agxRoot = AssetRoot();

        Assert.True(AgxMovieSequence.TrySelectDeathFrame(agxRoot, 0, out var first));
        Assert.True(AgxMovieSequence.TrySelectDeathFrame(agxRoot, 10, out var stillFirst));
        Assert.True(AgxMovieSequence.TrySelectDeathFrame(agxRoot, 11, out var second));
        Assert.True(AgxMovieSequence.TrySelectDeathFrame(agxRoot, 330, out var firstGround));

        Assert.EndsWith("DOWN_AGX_00.png", first.Path);
        Assert.Equal(first.Path, stillFirst.Path);
        Assert.EndsWith("DOWN_AGX_01.png", second.Path);
        Assert.EndsWith("SDEATH_AGX_00.png", firstGround.Path);
    }
}
