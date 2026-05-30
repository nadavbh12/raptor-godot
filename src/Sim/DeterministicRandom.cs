using System;

namespace Raptor.Sim;

internal static class DeterministicRandom
{
    internal static bool? Override;   // test seam; null => read env

    public static bool Enabled =>
        Override ?? (Environment.GetEnvironmentVariable("RAPTOR_DETERMINISTIC_RNG") == "1");

    public static int NextOrMidpoint(Random? rng, int maxValue, int fallback)
    {
        if (maxValue <= 0) throw new ArgumentOutOfRangeException(nameof(maxValue));
        if (Enabled) return maxValue / 2;
        return rng?.Next(maxValue) ?? fallback;
    }
}
