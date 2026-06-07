using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Raptor.Sim.MazeLevel;

/// <summary>
/// Mutable per-tile state for the on-screen tile slice. Mirrors the C parallel
/// arrays in TILE.C:
///   hits[MAP_SIZE]   — remaining tile HP
///   eitems[MAP_SIZE] — "destroyed" graphic item id
///   titems[MAP_SIZE] — "original" graphic item id (==eitems ⇒ indestructible)
///   money[MAP_SIZE]  — score awarded on destruction (lib->bounty)
///   tdead[MAP_SIZE]  — destruction completion flag
///
/// One <see cref="TileState"/> entry per (tilepos, on-screen-slot) for the
/// MAP_ONSCREEN × MAP_COLS visible tiles. The view-renderer also reads ScreenX/Y
/// to know where to draw the destroyed-tile graphic.
/// </summary>
public sealed class TileState
{
    /// <summary>Absolute MAP_SIZE index for this tile (mirrors TILESPOT.mapspot).</summary>
    public int MapSpot { get; set; } = -1;
    /// <summary>Top-left screen X of this tile (= MAP_LEFT + col*32).</summary>
    public int ScreenX { get; set; }
    /// <summary>Top-left screen Y of this tile (= tileyoff + row*32).</summary>
    public int ScreenY { get; set; }
    /// <summary>
    /// True iff this tile can be damaged (mirrors C `eitems != titems`).
    /// When false, TileIsHit/TileBomb always return false and don't decrement Hits.
    /// </summary>
    public bool IsDestructible { get; set; }
    /// <summary>Remaining HP before destruction (mirrors C hits[mapspot]).</summary>
    public int Hits { get; set; }
    /// <summary>Score awarded on destruction (mirrors C money[mapspot]).</summary>
    public int Bounty { get; set; }
    /// <summary>True once the destruction completion fires (mirrors C tdead[mapspot]).</summary>
    public bool Dead { get; set; }
}

/// <summary>
/// Pure-C# helper for the C TILE_IsHit / TILE_Bomb dispatch (TILE.C:455-540).
///
/// TILE_IsHit (called for S_GROUND bullets that missed ground enemies):
///   Walks the on-screen tile slice; if a bullet at (x, y) is inside a tile's
///   32×32 box AND the tile is destructible, decrement that tile's hits by
///   `damage` and return true. Otherwise false.
///
/// TILE_Bomb (called for S_GTILE bullets):
///   Same hit test as TILE_IsHit, but on success also damages the tile ONE
///   ROW ABOVE by damage/2 (TILE.C:519-521).
///
/// Tile destruction (hits ≤ 0) is processed by the view's tile renderer when
/// it observes <see cref="TileState.Hits"/> drop to or below zero. The C side
/// fires score+explosion+anim in TILE_Think; we surface Bounty through the
/// dispatcher's <see cref="DamageResult"/> so WaveController can update Score.
/// </summary>
public static class TileDamageDispatcher
{
    private static readonly Lazy<StreamWriter?> TileTrace = new(OpenTileTrace);
    public static int TraceIter { get; set; } = -1;

    public struct DamageResult
    {
        /// <summary>True iff a destructible tile was hit and damaged.</summary>
        public bool Hit;
        /// <summary>Tile index in the on-screen slice (TileState list index), or -1.</summary>
        public int HitIndex;
        /// <summary>Absolute MAP_SIZE index of the damaged tile, or -1.</summary>
        public int MapSpot;
    }

    /// <summary>
    /// Tile-IsHit dispatch. Ports SOURCE/TILE.C:492-529. ONLY decrements the tile's
    /// hits (+ the random(2) spark, drawn by the caller). The award / explode / tdead
    /// are deferred to the per-iter TileThinkAwardScan (finding #1) — never done here.
    /// `tiles` is the on-screen tile slice (MAP_ONSCREEN * MAP_COLS entries,
    /// top-left → bottom-right, row-major).
    /// </summary>
    public static DamageResult TileIsHit(IList<TileState> tiles, int x, int y, int damage)
        => DispatchHit(tiles, x, y, damage, splashAbove: false, splashDamage: 0, mapCols: 9);

    /// <summary>
    /// Tile-Bomb dispatch. Ports SOURCE/TILE.C:534-565. Decrements the tile and
    /// splashes (damage &gt;&gt; 1) onto the tile above. Award/explode deferred to the scan.
    /// </summary>
    public static DamageResult TileBomb(IList<TileState> tiles, int x, int y, int damage, int mapCols)
        => DispatchHit(tiles, x, y, damage, splashAbove: true, splashDamage: damage >> 1, mapCols: mapCols);

    private static DamageResult DispatchHit(IList<TileState> tiles, int x, int y, int damage,
                                            bool splashAbove, int splashDamage, int mapCols)
    {
        // C: `while ( ts != lastspot )` where lastspot = tspots + (MAX_STILES-1)
        // (TILE.C:277, 500, 543). The loop body never runs for the final slice
        // element (slot 71), so a bullet over the last on-screen tile is never
        // hit-tested. Mirror that with `i < tiles.Count - 1`.
        for (int i = 0; i < tiles.Count - 1; i++)
        {
            var t = tiles[i];
            if (x < t.ScreenX || x >= t.ScreenX + 32) continue;
            if (y < t.ScreenY || y >= t.ScreenY + 32) continue;
            if (!t.IsDestructible) continue;   // C: eitems != titems (exploded tiles are skipped)
            int hitsBefore = t.Hits;
            t.Hits -= damage;                  // decrement only — TILE_Think awards later
            Trace("hit", t, x, y, damage, hitsBefore, t.Hits);

            // Splash to the tile one row above (i - mapCols). C: `if (ts->mapspot > MAP_COLS)`.
            if (splashAbove && i >= mapCols && tiles[i - mapCols].IsDestructible)
            {
                int splashBefore = tiles[i - mapCols].Hits;
                tiles[i - mapCols].Hits -= splashDamage;
                Trace("splash", tiles[i - mapCols], -1, -1, splashDamage, splashBefore, tiles[i - mapCols].Hits);
            }

            return new DamageResult { Hit = true, HitIndex = i, MapSpot = t.MapSpot };
        }
        return new DamageResult { Hit = false, HitIndex = -1, MapSpot = -1 };
    }

    internal static void TraceMapSpot(string kind, int mapSpot, int x, int y, int damage, int before, int after, bool dead)
    {
        var trace = TileTrace.Value;
        if (trace == null) return;
        trace.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "i={0} kind={1} mapspot={2} x={3} y={4} damage={5} before={6} after={7} dead={8}",
            TraceIter, kind, mapSpot, x, y, damage, before, after, dead ? 1 : 0));
        trace.Flush();
    }

    private static void Trace(string kind, TileState tile, int x, int y, int damage, int before, int after)
        => TraceMapSpot(kind, tile.MapSpot, x, y, damage, before, after, tile.Dead);

    private static StreamWriter? OpenTileTrace()
    {
        string? path = System.Environment.GetEnvironmentVariable("RAPTOR_TILE_TRACE");
        if (string.IsNullOrWhiteSpace(path)) return null;
        return new StreamWriter(path) { AutoFlush = true };
    }
}
