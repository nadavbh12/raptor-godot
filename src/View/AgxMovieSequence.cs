using System.Collections.Generic;
using System.IO;

namespace Raptor.View;

public readonly record struct AgxMovieFrame(string Path, int DurationFrames);

public static class AgxMovieSequence
{
    public const int DeathAirFrames = 30;
    public const int DeathAirDurationFrames = 11;
    public const int DeathGroundFrames = 6;
    public const int DeathGroundDurationFrames = 3;
    public const int DeathGroundRepeats = 8;
    public const int DeathFadeOutFrames = 100;
    public const int DeathContentFrames =
        DeathAirFrames * DeathAirDurationFrames
        + DeathGroundFrames * DeathGroundDurationFrames * DeathGroundRepeats;
    public const int DeathTotalFrames = DeathContentFrames + DeathFadeOutFrames;

    public static IEnumerable<AgxMovieFrame> BuildDeathSequence(string agxRoot)
    {
        for (int i = 0; i < DeathAirFrames; i++)
            yield return new AgxMovieFrame(
                Path.Combine(agxRoot, $"DOWN_AGX_{i:D2}.png"),
                DeathAirDurationFrames);

        for (int repeat = 0; repeat < DeathGroundRepeats; repeat++)
        {
            for (int i = 0; i < DeathGroundFrames; i++)
                yield return new AgxMovieFrame(
                    Path.Combine(agxRoot, $"SDEATH_AGX_{i:D2}.png"),
                    DeathGroundDurationFrames);
        }
    }

    public static bool TrySelectDeathFrame(string agxRoot, int elapsedFrames, out AgxMovieFrame frame)
    {
        if (elapsedFrames < 0) elapsedFrames = 0;

        foreach (var candidate in BuildDeathSequence(agxRoot))
        {
            if (elapsedFrames < candidate.DurationFrames)
            {
                frame = candidate;
                return true;
            }
            elapsedFrames -= candidate.DurationFrames;
        }

        frame = default;
        return false;
    }
}
