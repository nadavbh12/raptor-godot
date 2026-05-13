using System.Collections.Generic;

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
    public struct DamageResult
    {
        /// <summary>True iff a destructible tile was hit and damaged.</summary>
        public bool Hit;
        /// <summary>Tile index in the on-screen slice (TileState list index), or -1.</summary>
        public int HitIndex;
        /// <summary>Score to add when the tile is fully destroyed (Hits ≤ 0).</summary>
        public int Bounty;
        /// <summary>True iff this hit reduced the tile's Hits to ≤ 0 for the first time.</summary>
        public bool JustDestroyed;
    }

    /// <summary>
    /// Tile-IsHit dispatch. Ports SOURCE/TILE.C:458-493.
    /// `tiles` is the on-screen tile slice (MAP_ONSCREEN * MAP_COLS entries
    /// from top-left → bottom-right, row-major).
    /// </summary>
    public static DamageResult TileIsHit(IList<TileState> tiles, int x, int y, int damage)
        => DispatchHit(tiles, x, y, damage, splashAbove: false, splashDamage: 0);

    /// <summary>
    /// Tile-Bomb dispatch. Ports SOURCE/TILE.C:499-540.
    /// On hit also splashes (damage &gt;&gt; 1) onto the tile above (mapspot − cols).
    /// </summary>
    public static DamageResult TileBomb(IList<TileState> tiles, int x, int y, int damage, int mapCols)
        => DispatchHit(tiles, x, y, damage, splashAbove: true, splashDamage: damage >> 1, mapCols: mapCols);

    private static DamageResult DispatchHit(IList<TileState> tiles, int x, int y, int damage,
                                            bool splashAbove, int splashDamage, int mapCols = 9)
    {
        for (int i = 0; i < tiles.Count; i++)
        {
            var t = tiles[i];
            if (x < t.ScreenX || x >= t.ScreenX + 32) continue;
            if (y < t.ScreenY || y >= t.ScreenY + 32) continue;
            if (!t.IsDestructible) continue;

            int hitsBefore = t.Hits;
            t.Hits -= damage;
            bool justDestroyed = hitsBefore > 0 && t.Hits <= 0;

            // Splash to the tile one row above (i - mapCols). C: `if (ts->mapspot > MAP_COLS)`.
            if (splashAbove && i >= mapCols && tiles[i - mapCols].IsDestructible)
                tiles[i - mapCols].Hits -= splashDamage;

            return new DamageResult
            {
                Hit = true,
                HitIndex = i,
                Bounty = justDestroyed ? t.Bounty : 0,
                JustDestroyed = justDestroyed,
            };
        }
        return new DamageResult { Hit = false, HitIndex = -1 };
    }
}
