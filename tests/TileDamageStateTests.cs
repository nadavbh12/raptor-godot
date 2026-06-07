using System.Collections.Generic;
using System.IO;
using Raptor.Sim.MazeLevel;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// Tile destruction parity vs C TILE.C, after the TILE_Think post-pass refactor
/// (structural-parity-review finding #1). The faithful model:
///   - TileIsHit ONLY decrements hits (the award/explode/tdead are deferred).
///   - TileThinkAwardScan is the SOLE award site (C TILE.C:386-398), run once per
///     iter before the bullet phase: for each tile hits&lt;0 &amp;&amp; !dead it awards
///     bounty, splashes 5 NON-recursively to 3 neighbors, schedules a 10-frame
///     fuse, and marks the tile dead.
///   - The fuse later splashes 20 NON-recursively (TILE.C:410-437).
/// So a connected structure (the wave-1 bridge) dies ring-by-ring over many iters,
/// never all at once — which is what fixes the Bug E score-timing lead.
/// </summary>
public class TileDamageStateTests
{
    private const int MapCols = 9;

    // Builds a TileDamageState whose backing arrays cover `count` map spots, all
    // referencing flat 0 in a synthesized FLATS library. Flat 0 is destructible
    // (linkflat != index) with the given hits/bounty. Returns the state already
    // initialized + slice rebuilt at tilepos 0 (so mapspot == slice slot).
    private static (TileDamageState tiles, List<MapTileEntry> mapTiles, List<(int exp, int x, int y)> explosions)
        BuildState(int count, short hits, short bounty)
    {
        var lib = new FlatLibrary
        {
            Name = "TEST",
            NumFlats = 1,
            // linkflat=1 (!=0) makes flat 0 destructible.
            Flats = new List<FlatEntry> { new FlatEntry { LinkFlat = 1, Bonus = hits, Bounty = bounty } },
        };
        string dir = Path.Combine(Path.GetTempPath(), "raptor_tile_test_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "FLATS.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(lib));

        var mapTiles = new List<MapTileEntry>();
        for (int i = 0; i < count; i++) mapTiles.Add(new MapTileEntry { Flats = 0 });

        var tiles = new TileDamageState();
        tiles.LoadFlats(path);
        tiles.InitializeTileBacking(mapTiles);
        tiles.RebuildTileSlice(mapTiles, tilepos: 0, tileyoff: -56);

        var explosions = new List<(int, int, int)>();
        return (tiles, mapTiles, explosions);
    }

    [Fact]
    public void Tile_hit_only_decrements_does_not_award_or_kill()
    {
        // C TILE_IsHit (TILE.C:505-521) ONLY decrements hits (+random(2) spark).
        // The award / explode / tdead are deferred to the next-iter TILE_Think.
        // Awarding or marking dead at hit time was Bug E.
        var (tiles, _, _) = BuildState(count: 27, hits: 15, bounty: 50);

        var r = TileDamageDispatcher.TileIsHit(
            tiles.Slice, tiles.Slice[10].ScreenX, tiles.Slice[10].ScreenY, damage: 20);

        Assert.True(r.Hit);
        Assert.Equal(-5, tiles.Slice[10].Hits);  // 15 - 20
        Assert.False(tiles.Slice[10].Dead);       // NOT killed/awarded at hit time
    }

    [Fact]
    public void Think_scan_is_sole_award_site_for_negative_tiles()
    {
        // C TILE_Think (TILE.C:386-398): for each tile hits<0 && !tdead, award
        // money, splash, schedule the fuse, mark tdead. The directly-shot tile is
        // awarded HERE (the iter after the hit), not at hit time.
        var (tiles, _, exps) = BuildState(count: 27, hits: 15, bounty: 50);
        tiles.Slice[10].Hits = -5;                 // as the prior iter's bullet left it
        tiles.SyncTileSliceToBacking();

        int destroyed = 0;
        int bounty = tiles.TileThinkAwardScan((ms, x, y) => { destroyed++; exps.Add((5, x + 16, y + 16)); });

        Assert.Equal(50, bounty);                  // exactly one tile awarded
        Assert.Equal(1, destroyed);
        Assert.True(tiles.Slice[10].Dead);
        Assert.Contains((5, tiles.Slice[10].ScreenX + 16, tiles.Slice[10].ScreenY + 16), exps);
    }

    [Fact]
    public void Think_scan_splash_is_non_recursive_one_ring_per_pass()
    {
        // THE finding-#1 test. A contiguous block of hits=15 tiles, one knocked
        // lethal. After ONE think-pass: only that tile is awarded+dead; its
        // immediate neighbors are pushed down by 5 (15→10) but NOT destroyed; tiles
        // two away are untouched. C never destroys the whole structure in a single
        // pass — it propagates one ring per iter via the delayed 20-splash.
        var (tiles, _, exps) = BuildState(count: 27, hits: 15, bounty: 50);
        tiles.Slice[10].Hits = -1;                 // center lethal
        tiles.SyncTileSliceToBacking();

        int bounty = tiles.TileThinkAwardScan((ms, x, y) => exps.Add((5, x + 16, y + 16)));

        Assert.Equal(50, bounty);                  // ONLY the center tile, not the block
        Assert.True(tiles.Slice[10].Dead);
        // left(9), up(1), right(11) decremented 15→10 and still ALIVE.
        Assert.Equal(10, tiles.Slice[9].Hits);  Assert.False(tiles.Slice[9].Dead);
        Assert.Equal(10, tiles.Slice[1].Hits);  Assert.False(tiles.Slice[1].Dead);
        Assert.Equal(10, tiles.Slice[11].Hits); Assert.False(tiles.Slice[11].Dead);
        // two tiles away (12) untouched.
        Assert.Equal(15, tiles.Slice[12].Hits);
    }

    [Fact]
    public void Delay_fuse_splashes_20_after_ten_frames_non_recursively()
    {
        // C TILE_Think delay branch (TILE.C:410-437): a fuse fires after its
        // countdown, doing a NON-recursive TILE_DoDamage(20) splash. It does NOT
        // award (that is the scan's job) and does NOT mark neighbors dead.
        var (tiles, _, _) = BuildState(count: 27, hits: 15, bounty: 50);
        tiles.ScheduleTileDelayExplosion(10);

        int fired = 0;
        for (int i = 0; i < 11; i++) tiles.ProcessTileDelayExplosions(() => fired++);
        Assert.Equal(0, fired);                    // frames 10..0, not yet < 0

        tiles.ProcessTileDelayExplosions(() => fired++);   // 12th call: frames -1 → fire
        Assert.Equal(1, fired);
        // neighbors of spot 10 splashed by 20: 15 → -5 (lethal, awaited by next scan).
        Assert.Equal(-5, tiles.Slice[9].Hits);
        Assert.Equal(-5, tiles.Slice[1].Hits);
        Assert.Equal(-5, tiles.Slice[11].Hits);
        Assert.False(tiles.Slice[9].Dead);         // destruction is the scan's job
    }

    [Fact]
    public void Splash_guards_on_destructible_only_not_dead()
    {
        // Finding #15. C TILE_DoDamage (TILE.C:174) guards ONLY on eitems==titems
        // (indestructible), NOT on tdead — so a tile that is tdead but still
        // destructible keeps taking splash. Godot previously had an extra Dead gate.
        var (tiles, _, _) = BuildState(count: 27, hits: 15, bounty: 50);
        tiles.Slice[11].Dead = true;               // dead but still destructible
        tiles.SyncTileSliceToBacking();
        tiles.Slice[10].Hits = -1;                 // center lethal
        tiles.SyncTileSliceToBacking();

        tiles.TileThinkAwardScan((ms, x, y) => { });

        Assert.Equal(10, tiles.Slice[11].Hits);    // 15 - 5: splash applied despite Dead
    }
}
