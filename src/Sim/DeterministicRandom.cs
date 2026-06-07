using System;

namespace Raptor.Sim;

internal static class DeterministicRandom
{
    // Test seam; null => read the RAPTOR_DETERMINISTIC_RNG env var. [ThreadStatic]
    // so xUnit's parallel collections don't race on it: a test that forces real
    // RNG (Override=false) on one collection's thread must not leak that value to
    // a deterministic test reading it on another thread. Production never assigns
    // Override (it stays null on every thread → env is read), so per-thread
    // isolation is behaviour-preserving there. The sim reads Override synchronously
    // on the same thread a test sets it, so [ThreadStatic] is transparent to tests.
    [ThreadStatic] internal static bool? Override;

    public static bool Enabled =>
        Override ?? (Environment.GetEnvironmentVariable("RAPTOR_DETERMINISTIC_RNG") == "1");

    public static int NextOrMidpoint(Random? rng, int maxValue, int fallback)
    {
        if (maxValue <= 0) throw new ArgumentOutOfRangeException(nameof(maxValue));
        if (Enabled) return maxValue / 2;
        return rng?.Next(maxValue) ?? fallback;
    }
}
