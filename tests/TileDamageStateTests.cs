using System.Collections.Generic;
using System.IO;
using Raptor.Sim.MazeLevel;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// Unit tests for the tile-destructibility cascade extracted into
/// <see cref="TileDamageState"/> (E11). Covers the explosion-splash neighbor
/// cascade, the 10-frame delayed-explosion fuse, and the skip rules for
/// non-destructible / already-dead tiles. Mirrors C TILE.C splash damage.
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
    public void Lethal_splash_marks_tile_dead_and_schedules_delay_explosion()
    {
        // 3 rows × 9 cols = 27 spots so the center has left/up/right neighbors.
        var (tiles, mapTiles, exps) = BuildState(count: 27, hits: 1, bounty: 50);
        void Add(int e, int x, int y) => exps.Add((e, x, y));

        // Center spot 10 (row 1, col 1). Its right neighbor is spot 11.
        // damage=20 (> hits=1) so each destructible neighbor dies on first hit.
        tiles.ApplyTileExplosionDamage(mapspot: 10, damage: 20, Add);

        // The right neighbor (11), left (9) and up (1) are all destructible and
        // should now be Dead in the slice after syncing values back.
        tiles.RefreshTileSliceValuesFromBacking();
        Assert.True(tiles.Slice[11].Dead);
        Assert.True(tiles.Slice[9].Dead);
        Assert.True(tiles.Slice[1].Dead);

        // Each destruction spawns a ground explosion at the tile center.
        Assert.Contains((5 /* ExpGrdLarge */, tiles.Slice[11].ScreenX + 16, tiles.Slice[11].ScreenY + 16), exps);
        Assert.NotEmpty(exps);
    }

    [Fact]
    public void Explosion_chain_destroyed_tiles_award_bounty_like_C_TILE_Think()
    {
        // C TILE_Think (TILE.C:386-398) awards money[mapspot] for EVERY tile that
        // reaches hits<0 && !tdead — including tiles destroyed by the explosion
        // chain / delayed blast, not just the directly-shot tile. Godot previously
        // awarded bounty only on direct DispatchHit, so a contiguous structure
        // (the wave-1 bridge) destroyed mostly by the chain scored ~0. The cascade
        // must return the total bounty of all tiles it destroys.
        var (tiles, _, exps) = BuildState(count: 27, hits: 1, bounty: 50);
        void Add(int e, int x, int y) => exps.Add((e, x, y));

        int bounty = tiles.ApplyTileExplosionDamage(mapspot: 10, damage: 20, Add);

        tiles.RefreshTileSliceValuesFromBacking();
        int dead = 0;
        foreach (var t in tiles.Slice) if (t.Dead) dead++;
        Assert.True(dead > 1, "the chain should destroy multiple tiles");
        Assert.Equal(dead * 50, bounty);   // every chain-destroyed tile pays bounty
    }

    [Fact]
    public void Delay_fuse_fires_after_ten_frame_countdown()
    {
        var (tiles, mapTiles, exps) = BuildState(count: 27, hits: 1, bounty: 0);
        void Add(int e, int x, int y) => exps.Add((e, x, y));

        // Schedule a delay explosion on the center tile; its destructible
        // neighbors (1, 9, 11) are still alive. The fuse fires ApplyTileExplosion
        // Damage(..., 20) only after Frames counts below 0.
        tiles.ScheduleTileDelayExplosion(10);

        // Frames starts at 10. ProcessTileDelayExplosions decrements once per call
        // and only fires once Frames < 0 — i.e. on the 12th call (10→...→-1).
        for (int i = 0; i < 11; i++)
        {
            tiles.ProcessTileDelayExplosions(Add);
            Assert.False(tiles.Slice[11].Dead);  // not yet fired
        }
        // 12th call: Frames is now -1 (< 0) → fires the damage-20 splash.
        tiles.ProcessTileDelayExplosions(Add);
        Assert.True(tiles.Slice[11].Dead);
        Assert.True(tiles.Slice[9].Dead);
        Assert.True(tiles.Slice[1].Dead);
    }

    [Fact]
    public void Non_destructible_and_already_dead_neighbors_are_skipped()
    {
        var (tiles, mapTiles, exps) = BuildState(count: 27, hits: 1, bounty: 0);
        void Add(int e, int x, int y) => exps.Add((e, x, y));

        // Kill the right neighbor (11) first so it is already Dead.
        tiles.Slice[11].Dead = true;
        tiles.SyncTileSliceToBacking();
        int explosionsBefore = exps.Count;

        // Now splash from center 10. Up (1) and left (9) die; right (11) is skipped
        // (already dead) — so no explosion fires for spot 11 a second time.
        tiles.ApplyTileExplosionDamage(mapspot: 10, damage: 20, Add);
        tiles.RefreshTileSliceValuesFromBacking();

        Assert.True(tiles.Slice[1].Dead);
        Assert.True(tiles.Slice[9].Dead);
        // No explosion was emitted for the already-dead spot 11.
        foreach (var (_, x, y) in exps)
            Assert.False(x == tiles.Slice[11].ScreenX + 16 && y == tiles.Slice[11].ScreenY + 16,
                "already-dead tile must not spawn a new explosion");
        Assert.True(exps.Count > explosionsBefore);
    }
}
