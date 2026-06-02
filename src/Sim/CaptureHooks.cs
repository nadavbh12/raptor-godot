using System;
using System.Collections.Generic;
using System.Linq;

namespace Raptor.Sim;

/// <summary>
/// The env-gated cosmetic-capture hooks (RAPTOR_GODMODE / RAPTOR_BOSS_LOWHP /
/// RAPTOR_FORCE_SECRET / RAPTOR_GRANT). All OFF by default → parity-inert: none of
/// these envs is set by the 12-scenario L2a gate, so the default code path is
/// unchanged. Centralised here so the env→config mapping is testable (pass a fake
/// getEnv) instead of being inlined against OS.GetEnvironment in WaveController.
/// </summary>
internal static class CaptureHooks
{
    internal readonly record struct Config(
        bool Godmode, int BossLowHp, bool ForceSecret, IReadOnlyList<ObjType> Grants);

    /// <summary>Read the capture config from an env getter (e.g. OS.GetEnvironment).</summary>
    public static Config Read(Func<string, string> getEnv)
    {
        int.TryParse(getEnv("RAPTOR_BOSS_LOWHP"), out int bossLowHp);
        return new Config(
            Godmode:     getEnv("RAPTOR_GODMODE") == "1",
            BossLowHp:   bossLowHp,
            ForceSecret: getEnv("RAPTOR_FORCE_SECRET") == "1",
            Grants:      ParseGrant(getEnv("RAPTOR_GRANT")).ToList());
    }

    /// <summary>
    /// Parse the RAPTOR_GRANT capture env (comma/space/semicolon list of item
    /// names) into the ObjTypes to grant. Unknown names are ignored. Pure and
    /// testable; returns empty for null/blank (the parity-inert default).
    /// </summary>
    public static IEnumerable<ObjType> ParseGrant(string? env)
    {
        if (string.IsNullOrWhiteSpace(env)) yield break;
        foreach (var raw in env.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "detect":      yield return ObjType.Detect;      break;
                case "supershield": yield return ObjType.SuperShield; break;
                case "megabomb":    yield return ObjType.MegaBomb;    break;
            }
        }
    }
}
