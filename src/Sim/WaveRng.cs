namespace Raptor.Sim;

/// <summary>
/// Per-wave RNG seeding. Centralises (1) the deterministic wave seed and (2) the
/// derivation of the PlayerShooter RNG stream from it, so the seeding rule lives
/// in one place instead of being split between WavePhaseScheduler (where the seed
/// formula did not belong) and inline LegacyRandom constructions in
/// WaveController. Pure / behaviour-preserving: the seed values are unchanged, so
/// the RNG streams — and thus parity — are bit-identical.
/// </summary>
internal static class WaveRng
{
    /// <summary>
    /// Computes the deterministic per-wave seed.
    /// Returns the parsed seedOverride if non-null/non-empty and parseable as int;
    /// otherwise returns 1024 * waveNum.
    /// </summary>
    public static ulong ComputeSeed(int waveNum, string? seedOverride = null)
    {
        if (!string.IsNullOrEmpty(seedOverride) && int.TryParse(seedOverride, out int seed))
            return (ulong)seed;
        return (ulong)(1024 * waveNum);
    }

    /// <summary>
    /// Derive the deterministic PlayerShooter RNG from the per-wave Godot seed, so
    /// DUMB_MISSLE scatter and MINI_GUN picks are replay-stable.
    /// </summary>
    public static LegacyRandom NewShooterRng(ulong godotSeed)
        => new((int)(godotSeed & 0x7FFFFFFFu));
}
