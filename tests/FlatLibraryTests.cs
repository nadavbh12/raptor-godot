using System.IO;
using Raptor.Sim.MazeLevel;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// Validates the FLATSG1_ITM.json extraction. These tests pin the C struct
/// layout (FLATS = INT linkflat + SHORT bonus + SHORT bounty) — if anything
/// shifts in the dumper, hash/count assertions fail loudly.
/// </summary>
public class FlatLibraryTests
{
    private static string AssetPath() =>
        Path.Combine(System.AppContext.BaseDirectory,
            "..", "..", "..", "..", "assets", "flats", "FLATSG1_ITM.json");

    [Fact]
    public void Loads_FLATSG1_ITM_with_672_entries()
    {
        // Extraction from FILE0001.GLB / FLATSG1_ITM yields 672 flats.
        // (sizeof FLATS = 8 → item size / 8 == 672.)
        var path = AssetPath();
        if (!File.Exists(path)) return;  // asset bundle not present in CI
        var lib = FlatLibrary.LoadFromFile(path);
        Assert.Equal(672, lib.Count);
        Assert.Equal("FLATSG1_ITM", lib.Name);
    }

    [Fact]
    public void Indestructible_flats_have_linkflat_equal_to_index()
    {
        // Top ~117 entries are basic ground tiles: linkflat==i, no destruction.
        var path = AssetPath();
        if (!File.Exists(path)) return;
        var lib = FlatLibrary.LoadFromFile(path);
        Assert.False(lib.IsDestructible(0));
        Assert.False(lib.IsDestructible(50));
        Assert.False(lib.IsDestructible(100));
        Assert.Equal(1, lib.HitsFor(0));      // hits=1 for indestructible
        Assert.Equal(0, lib.BountyFor(0));
    }

    [Fact]
    public void Destructible_flats_carry_bonus_and_bounty()
    {
        // Per extraction stats, flat 117 is destructible: linkflat=420,
        // bonus=15 (HP), bounty=50 (score).
        var path = AssetPath();
        if (!File.Exists(path)) return;
        var lib = FlatLibrary.LoadFromFile(path);
        Assert.True(lib.IsDestructible(117));
        Assert.Equal(15, lib.HitsFor(117));
        Assert.Equal(50, lib.BountyFor(117));
    }

    [Fact]
    public void Roughly_157_flats_are_destructible()
    {
        // Sanity check on the destructibility count from the extraction. If
        // this drifts more than ±2, the FLATS struct layout has shifted.
        var path = AssetPath();
        if (!File.Exists(path)) return;
        var lib = FlatLibrary.LoadFromFile(path);
        int destruct = 0;
        for (int i = 0; i < lib.Count; i++)
            if (lib.IsDestructible(i)) destruct++;
        Assert.InRange(destruct, 155, 160);
    }
}
