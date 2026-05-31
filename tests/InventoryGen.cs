using System;
using System.Collections.Generic;
using System.Linq;
using Raptor.Sim;

namespace Raptor.Tests;

/// <summary>
/// Deterministic random inventory generator for property-based round-trip tests.
/// Uses a fixed seed so every run produces identical cases.
/// </summary>
public static class InventoryGen
{
    // ObjType values 0..23 (LastObject=24 is the sentinel with no ObjLib entry).
    private static readonly ObjType[] ValidTypes =
        Enumerable.Range(0, 24).Select(i => (ObjType)i).ToArray();

    /// <summary>
    /// Yields <paramref name="count"/> test cases, each a tuple of
    /// (deduped OBJ records, sweapon int) for use as xUnit MemberData.
    /// Fixed seed 12345 → fully deterministic across runs.
    /// </summary>
    public static IEnumerable<object[]> RandomInventories(int count)
    {
        var rng = new Random(12345);

        for (int i = 0; i < count; i++)
        {
            // Shuffle types and take a random subset to avoid duplicate-type collisions.
            var types = ValidTypes.OrderBy(_ => rng.Next()).Take(rng.Next(1, 8)).ToArray();

            var objs = types.Select(t =>
            {
                // Num: 1..10 (safe below any MaxCnt — Energy max=100, MegaBomb max=5 cap check
                // handled by Load which bypasses Add logic entirely and writes Num directly).
                int num = rng.Next(1, 11);
                bool inuse = rng.Next(2) == 0;
                return (t, num, inuse);
            }).ToArray();

            // sweapon: any int 0..23 (LoadInventory will validate it against owned specials).
            int sweapon = rng.Next(0, 24);

            yield return new object[] { objs, sweapon };
        }
    }
}
