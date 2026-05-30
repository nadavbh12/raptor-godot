using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

// Pins the invariants of the single RNG chokepoint (DeterministicRandom.NextOrMidpoint)
// and guards that no sim code draws RNG outside it. See CLAUDE.md rule 1.
public class DeterministicRandomTests : IDisposable
{
    public DeterministicRandomTests()
    {
        // Hermetic: ignore the ambient RAPTOR_DETERMINISTIC_RNG env var.
        DeterministicRandom.Override = true;
    }

    public void Dispose()
    {
        DeterministicRandom.Override = null;
    }

    // When deterministic, the chokepoint's output for a given bound is independent of
    // how many draws preceded it — there is no hidden RNG state to advance.
    [Property(MaxTest = 50)]
    public Property NextOrMidpoint_is_order_independent()
    {
        var paramsGen = Gen.Zip(Gen.Choose(1, 4096), Gen.Choose(0, 50));
        return Prop.ForAll(paramsGen.ToArbitrary(), parms =>
        {
            var (n, preceding) = parms;
            var rng = new Random(12345);
            for (int i = 0; i < preceding; i++)
                DeterministicRandom.NextOrMidpoint(rng, 7, 3);
            return DeterministicRandom.NextOrMidpoint(rng, n, 0) == n / 2;
        });
    }

    // Deterministic midpoint uses integer floor division (n / 2), even with a null rng.
    [Property(MaxTest = 50)]
    public Property NextOrMidpoint_returns_floor_half()
    {
        return Prop.ForAll(Gen.Choose(1, 100000).ToArbitrary(), n =>
            DeterministicRandom.NextOrMidpoint(null, n, 0) == n / 2);
    }

    // When the chokepoint is disabled it defers to the real rng — production isolation.
    [Fact]
    public void Production_isolation_uses_real_rng_when_disabled()
    {
        DeterministicRandom.Override = false;
        var a = new Random(99);
        var expected = new Random(99).Next(1000);
        Assert.Equal(expected, DeterministicRandom.NextOrMidpoint(a, 1000, 0));
    }

    // Audit guard: every RNG draw in src/Sim must funnel through NextOrMidpoint.
    // Any direct .Next(...)/.Randf*(...)/.Randi*(...) call there is a bypass.
    [Fact]
    public void No_sim_rng_draw_bypasses_the_chokepoint()
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        Assert.NotNull(repoRoot);

        var simDir = Path.Combine(repoRoot!, "src", "Sim");
        Assert.True(Directory.Exists(simDir), $"src/Sim not found at {simDir}");

        var pattern = new Regex(@"\.(Next|Randf|RandfRange|RandiRange)\s*\(");
        var exempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DeterministicRandom.cs",
            "LegacyRandom.cs",
        };

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(simDir, "*.cs", SearchOption.AllDirectories))
        {
            if (exempt.Contains(Path.GetFileName(file))) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//")) continue;
                if (pattern.IsMatch(line))
                    offenders.Add($"{file}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Sim RNG draws must funnel through DeterministicRandom.NextOrMidpoint. Offenders:\n"
            + string.Join("\n", offenders));
    }

    private static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "raptor.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
