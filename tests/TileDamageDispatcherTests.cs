using System.Collections.Generic;
using Raptor.Sim.MazeLevel;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// Unit tests for the C TILE_IsHit / TILE_Bomb dispatch port.
/// Mirrors SOURCE/TILE.C:458-540.
/// </summary>
public class TileDamageDispatcherTests
{
    /// <summary>Build an on-screen slice of (rows × cols) tiles at given screen positions.</summary>
    private static List<TileState> Slice(int rows, int cols)
    {
        var s = new List<TileState>(rows * cols);
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
            s.Add(new TileState
            {
                ScreenX = 16 + c * 32,
                ScreenY = -56 + r * 32,
                IsDestructible = false,
                Hits = 1,
                Bounty = 0,
            });
        return s;
    }

    [Fact]
    public void TileIsHit_returns_false_outside_any_tile_box()
    {
        var s = Slice(8, 9);
        s[0].IsDestructible = true;  s[0].Hits = 10;
        // Hit (1000, 1000) — well outside the slice.
        var r = TileDamageDispatcher.TileIsHit(s, x: 1000, y: 1000, damage: 5);
        Assert.False(r.Hit);
        Assert.Equal(-1, r.HitIndex);
        Assert.Equal(10, s[0].Hits);  // unchanged
    }

    [Fact]
    public void TileIsHit_returns_false_when_tile_is_indestructible()
    {
        var s = Slice(8, 9);
        // tile[0] at (16, -56)..(48, -24). Hit center (32, -40). IsDestructible=false.
        s[0].IsDestructible = false; s[0].Hits = 10;
        var r = TileDamageDispatcher.TileIsHit(s, x: 32, y: -40, damage: 5);
        Assert.False(r.Hit);
        Assert.Equal(10, s[0].Hits);
    }

    [Fact]
    public void TileIsHit_damages_destructible_tile_at_screen_coords()
    {
        var s = Slice(8, 9);
        s[0].IsDestructible = true; s[0].Hits = 10; s[0].Bounty = 200;
        // Center of tile[0]: (16+16, -56+16) = (32, -40).
        var r = TileDamageDispatcher.TileIsHit(s, x: 32, y: -40, damage: 5);
        Assert.True(r.Hit);
        Assert.Equal(0, r.HitIndex);
        Assert.Equal(5, s[0].Hits);
        Assert.False(s[0].Dead);   // decrement only — award/explode deferred to the scan
    }

    [Fact]
    public void TileIsHit_decrements_below_zero_without_marking_dead_or_awarding()
    {
        // C TILE_IsHit (TILE.C:505-521) decrements only; even a killing blow leaves
        // the tile alive-but-negative until the next TILE_Think scan destroys it.
        var s = Slice(8, 9);
        s[0].IsDestructible = true; s[0].Hits = 5; s[0].Bounty = 250;
        var r = TileDamageDispatcher.TileIsHit(s, x: 32, y: -40, damage: 6);
        Assert.True(r.Hit);
        Assert.Equal(-1, s[0].Hits);
        Assert.False(s[0].Dead);   // NOT killed at hit time (was Bug E)
    }

    [Fact]
    public void TileIsHit_skips_exploded_indestructible_tile()
    {
        // After TILE_Think destroys a tile it sets eitems=titems (IsDestructible=false),
        // so TILE_IsHit's `eitems != titems` check skips it on later passes.
        var s = Slice(8, 9);
        s[0].IsDestructible = false; s[0].Hits = -1; s[0].Dead = true;

        var r = TileDamageDispatcher.TileIsHit(s, x: 32, y: -40, damage: 5);

        Assert.False(r.Hit);
        Assert.Equal(-1, s[0].Hits);
    }

    [Fact]
    public void TileIsHit_at_exactly_zero_hits_is_not_yet_lethal()
    {
        // C destroys on hits<0, so hits reaching exactly 0 is still alive.
        var s = Slice(8, 9);
        s[0].IsDestructible = true; s[0].Hits = 1; s[0].Bounty = 250;

        var r = TileDamageDispatcher.TileIsHit(s, x: 32, y: -40, damage: 1);

        Assert.True(r.Hit);
        Assert.False(s[0].Dead);
        Assert.Equal(0, s[0].Hits);
    }

    [Fact]
    public void TileBomb_splashes_half_damage_to_tile_above()
    {
        // Tile[9] is at (16, -56+32) = (16, -24) — one row below tile[0].
        // TILE_Bomb on tile[9] should also damage tile[0] (mapspot - MAP_COLS).
        var s = Slice(8, 9);
        s[0].IsDestructible = true; s[0].Hits = 100;
        s[9].IsDestructible = true; s[9].Hits = 100;
        // Hit center of tile[9]: (32, -8).
        var r = TileDamageDispatcher.TileBomb(s, x: 32, y: -8, damage: 20, mapCols: 9);
        Assert.True(r.Hit);
        Assert.Equal(9, r.HitIndex);
        Assert.Equal(80, s[9].Hits);   // 100 - 20
        Assert.Equal(90, s[0].Hits);   // 100 - (20 >> 1) = 90
    }

    [Fact]
    public void TileBomb_does_not_splash_top_row()
    {
        // Hitting tile[0] (top row) — i < mapCols so no splash above.
        var s = Slice(8, 9);
        s[0].IsDestructible = true; s[0].Hits = 100;
        var r = TileDamageDispatcher.TileBomb(s, x: 32, y: -40, damage: 20, mapCols: 9);
        Assert.True(r.Hit);
        Assert.Equal(80, s[0].Hits);
    }

    [Fact]
    public void TileBomb_skips_splash_when_tile_above_is_indestructible()
    {
        var s = Slice(8, 9);
        s[0].IsDestructible = false; s[0].Hits = 100;
        s[9].IsDestructible = true;  s[9].Hits = 100;
        var r = TileDamageDispatcher.TileBomb(s, x: 32, y: -8, damage: 20, mapCols: 9);
        Assert.True(r.Hit);
        Assert.Equal(80, s[9].Hits);
        Assert.Equal(100, s[0].Hits);  // unchanged — not destructible
    }

    [Fact]
    public void TileIsHit_finds_correct_tile_on_x_axis()
    {
        var s = Slice(8, 9);
        s[3].IsDestructible = true; s[3].Hits = 10;
        // Tile[3] is at col=3, row=0 → x in [16+96, 16+128) = [112, 144).
        var r = TileDamageDispatcher.TileIsHit(s, x: 120, y: -40, damage: 1);
        Assert.True(r.Hit);
        Assert.Equal(3, r.HitIndex);
    }

    [Fact]
    public void TileIsHit_uses_exclusive_bottom_edge_from_current_tspot()
    {
        // C TILE_Think builds tspots before incrementing tileyoff. At the
        // mission_fight boundary, top-row y=-8 means a shot at y=24 is exactly
        // on the exclusive bottom edge and must not hit; using post-scroll y=-7
        // would incorrectly include it.
        var s = Slice(1, 9);
        s[4].ScreenY = -8;
        s[4].IsDestructible = true;
        s[4].Hits = 10;

        var r = TileDamageDispatcher.TileIsHit(s, x: 162, y: 24, damage: 1);

        Assert.False(r.Hit);
        Assert.Equal(10, s[4].Hits);
    }

    [Fact]
    public void TileIsHit_skips_final_slice_slot_71_matching_C_lastspot_bound()
    {
        // C TILE_IsHit (TILE.C:500) walks `while ( ts != lastspot )` with
        // lastspot = tspots + (MAX_STILES-1) = tspots+71 (TILE.C:277). The body
        // never runs for the final slot, so a bullet over slot 71 is never
        // hit-tested. Godot's loop must mirror that: `i < tiles.Count - 1`.
        var s = Slice(8, 9);                       // 72 tiles, slots 0..71
        // Slot 71 = row 7, col 8: ScreenX=16+8*32=272 (box[272,304)),
        // ScreenY=-56+7*32=168 (box[168,200)); center (288,184).
        s[71].IsDestructible = true; s[71].Hits = 10;

        var r = TileDamageDispatcher.TileIsHit(s, x: 288, y: 184, damage: 5);

        Assert.False(r.Hit);
        Assert.Equal(-1, r.HitIndex);
        Assert.Equal(10, s[71].Hits);              // never tested → unchanged
    }

    [Fact]
    public void TileIsHit_still_hits_slot_70_the_last_C_tested_slot()
    {
        // Boundary pin: slot 70 (row 7, col 7) IS tested by C's loop, so the
        // bound must be exactly 71, not lower. center (256,184).
        var s = Slice(8, 9);
        s[70].IsDestructible = true; s[70].Hits = 10;

        var r = TileDamageDispatcher.TileIsHit(s, x: 256, y: 184, damage: 5);

        Assert.True(r.Hit);
        Assert.Equal(70, r.HitIndex);
        Assert.Equal(5, s[70].Hits);
    }

    [Fact]
    public void TileBomb_skips_final_slice_slot_71_matching_C_lastspot_bound()
    {
        // TILE_Bomb (TILE.C:543) has the identical `while ( ts != lastspot )` bound.
        var s = Slice(8, 9);
        s[71].IsDestructible = true; s[71].Hits = 10;

        var r = TileDamageDispatcher.TileBomb(s, x: 288, y: 184, damage: 20, mapCols: 9);

        Assert.False(r.Hit);
        Assert.Equal(-1, r.HitIndex);
        Assert.Equal(10, s[71].Hits);
    }
}
