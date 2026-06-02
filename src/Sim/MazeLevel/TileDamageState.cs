using System;
using System.Collections.Generic;
using System.IO;

namespace Raptor.Sim.MazeLevel;

/// <summary>
/// Owns the tile-destructibility subsystem extracted from WaveController:
/// the per-map-spot backing arrays (hits / dead / destructible / bounty), the
/// on-screen tile slice (mirrors C's tspots[]), the delayed-explosion fuse list,
/// and the FLATS library. WaveController stays the owner of the per-tick phase
/// order and calls these helpers at the same sites in the same order.
///
/// Scroll state (_tilepos / _tileyoff) lives in WaveController and is passed in
/// to the slice-rebuild helpers; this collaborator does not own the scroll.
/// </summary>
internal sealed class TileDamageState
{
    private const int MAP_ONSCREEN  = 8;
    private const int MAP_COLS      = 9;
    private const int MAP_BLOCKSIZE = 32;
    private const int MAP_LEFT      = 16;

    // C exptype constant for the ground-tile destruction explosion (SOURCE/MAP.H).
    private const int ExpGrdLarge = 5;   // EXP_GRDLARGE → GEXPLO_BLK

    // On-screen tile state slice (MAP_ONSCREEN * MAP_COLS = 72 entries).
    // Mirrors C's tspots[]. Rebuilt from _mapTiles + _flatLib in PhaseSpawn
    // whenever the scroll crosses a row boundary; TileBomb / TileIsHit
    // dispatches against this slice. Destructibility / Hits / Bounty are
    // looked up by (MapTileEntry.FGame, MapTileEntry.Flats) in _flatLib.
    private readonly List<TileState> _tileSlice = new();
    private int _tileSliceTilePos = int.MinValue;
    private int[]? _tileHitsByMapSpot;
    private bool[]? _tileDeadByMapSpot;
    private bool[]? _tileDestructibleByMapSpot;
    private int[]? _tileBountyByMapSpot;
    private readonly List<TileDelayExplosion> _tileDelayExplosions = new();
    private FlatLibrary? _flatLib;
    private record struct TileDelayExplosion(int MapSpot, int Frames);

    /// <summary>On-screen tile slice (passed to dispatchers and read by the View).</summary>
    public List<TileState> Slice => _tileSlice;

    /// <summary>Lazy-load the FLATS library the first time it is requested (G1 only).</summary>
    public void LoadFlats(string path)
    {
        if (_flatLib == null && File.Exists(path))
            _flatLib = FlatLibrary.LoadFromFile(path);
    }

    /// <summary>Clears the delay-explosion fuses and resets the slice cache for a new wave.</summary>
    public void ResetForWave()
    {
        _tileDelayExplosions.Clear();
        _tileSliceTilePos = int.MinValue;
    }

    public int RenderedFlatFor(IReadOnlyList<MapTileEntry>? mapTiles, int mapspot)
    {
        if (mapTiles == null || mapspot < 0 || mapspot >= mapTiles.Count)
            return 0;

        int flat = mapTiles[mapspot].Flats;
        if (_flatLib == null || _tileDeadByMapSpot == null ||
            mapspot >= _tileDeadByMapSpot.Length || !_tileDeadByMapSpot[mapspot])
            return flat;

        return _flatLib.DestroyedFlatFor(flat);
    }

    public void RefreshTileSliceForThink(IReadOnlyList<MapTileEntry>? mapTiles, int tilepos, int tileyoff)
    {
        if (_tileSlice.Count == 0 || _tileSliceTilePos != tilepos)
        {
            RebuildTileSlice(mapTiles, tilepos, tileyoff);
            return;
        }

        // C TILE_Think writes tspots using the current tileyoff, then advances
        // tileyoff at the end of the same function. Later SHOTS_Think collides
        // against those pre-scroll tspots, so update the collision slice at the
        // start of PhaseSpawn and leave it unchanged after scrolling.
        for (int i = 0; i < _tileSlice.Count; i++)
            _tileSlice[i].ScreenY = tileyoff + (i / MAP_COLS) * MAP_BLOCKSIZE;
    }

    /// <summary>
    /// Rebuild the on-screen tile slice from _mapTiles, _tilepos, _tileyoff,
    /// and _flatLib. Mirrors C TILE_Think's first pass (TILE.C:343-360) which
    /// populates tspots[] with (mapspot, x, y, item) for each visible tile.
    /// </summary>
    public void RebuildTileSlice(IReadOnlyList<MapTileEntry>? mapTiles, int tilepos, int tileyoff)
    {
        if (_tileSlice.Count == 0)
        {
            for (int i = 0; i < MAP_ONSCREEN * MAP_COLS; i++)
                _tileSlice.Add(new TileState());
        }
        for (int row = 0; row < MAP_ONSCREEN; row++)
        for (int col = 0; col < MAP_COLS;     col++)
        {
            int slot     = row * MAP_COLS + col;
            int mapspot  = tilepos + slot;
            var t        = _tileSlice[slot];
            t.MapSpot    = mapspot;
            t.ScreenX    = MAP_LEFT + col * MAP_BLOCKSIZE;
            t.ScreenY    = tileyoff + row * MAP_BLOCKSIZE;
            if (mapTiles == null || mapspot < 0 || mapspot >= mapTiles.Count ||
                _flatLib == null || _tileHitsByMapSpot == null ||
                _tileDeadByMapSpot == null || _tileDestructibleByMapSpot == null ||
                _tileBountyByMapSpot == null)
            {
                t.IsDestructible = false; t.Hits = 1; t.Bounty = 0;
                t.Dead = false;
                continue;
            }
            t.IsDestructible = _tileDestructibleByMapSpot[mapspot];
            t.Hits           = _tileHitsByMapSpot[mapspot];
            t.Bounty         = _tileBountyByMapSpot[mapspot];
            t.Dead           = _tileDeadByMapSpot[mapspot];
        }
        _tileSliceTilePos = tilepos;
    }

    public void InitializeTileBacking(IReadOnlyList<MapTileEntry>? mapTiles)
    {
        int count = mapTiles?.Count ?? 0;
        _tileHitsByMapSpot = new int[count];
        _tileDeadByMapSpot = new bool[count];
        _tileDestructibleByMapSpot = new bool[count];
        _tileBountyByMapSpot = new int[count];

        if (mapTiles == null || _flatLib == null) return;
        for (int mapspot = 0; mapspot < mapTiles.Count; mapspot++)
        {
            int flatIdx = mapTiles[mapspot].Flats;
            if (flatIdx < 0 || flatIdx >= _flatLib.Count)
            {
                _tileHitsByMapSpot[mapspot] = 1;
                continue;
            }

            bool destructible = _flatLib.IsDestructible(flatIdx);
            _tileDestructibleByMapSpot[mapspot] = destructible;
            _tileHitsByMapSpot[mapspot] = _flatLib.HitsFor(flatIdx);
            _tileBountyByMapSpot[mapspot] = _flatLib.BountyFor(flatIdx);
        }
    }

    public void SyncTileSliceToBacking()
    {
        if (_tileHitsByMapSpot == null || _tileDeadByMapSpot == null) return;
        foreach (var t in _tileSlice)
        {
            if (t.MapSpot < 0 || t.MapSpot >= _tileHitsByMapSpot.Length) continue;
            _tileHitsByMapSpot[t.MapSpot] = t.Hits;
            _tileDeadByMapSpot[t.MapSpot] = t.Dead;
        }
    }

    public void RefreshTileSliceValuesFromBacking()
    {
        if (_tileHitsByMapSpot == null || _tileDeadByMapSpot == null) return;
        foreach (var t in _tileSlice)
        {
            if (t.MapSpot < 0 || t.MapSpot >= _tileHitsByMapSpot.Length) continue;
            t.Hits = _tileHitsByMapSpot[t.MapSpot];
            t.Dead = _tileDeadByMapSpot[t.MapSpot];
        }
    }

    public void ApplyTileExplosionDamage(int mapspot, int damage, Action<int, int, int> addExplosion)
    {
        if (_tileHitsByMapSpot == null || _tileDeadByMapSpot == null ||
            _tileDestructibleByMapSpot == null)
            return;

        int ix = mapspot % MAP_COLS;
        ApplyTileExplosionNeighbor(mapspot - 1, ix - 1, damage, addExplosion);
        ApplyTileExplosionNeighbor(mapspot - MAP_COLS, ix, damage, addExplosion);
        ApplyTileExplosionNeighbor(mapspot + 1, ix + 1, damage, addExplosion);
    }

    private void ApplyTileExplosionNeighbor(int spot, int x, int damage, Action<int, int, int> addExplosion)
    {
        if (_tileHitsByMapSpot == null || _tileDeadByMapSpot == null ||
            _tileDestructibleByMapSpot == null)
            return;
        if (spot < 0 || spot >= _tileHitsByMapSpot.Length) return;
        if (x < 0 || x >= MAP_COLS) return;
        if (!_tileDestructibleByMapSpot[spot]) return;
        if (_tileDeadByMapSpot[spot]) return;

        int before = _tileHitsByMapSpot[spot];
        _tileHitsByMapSpot[spot] -= damage;
        TileDamageDispatcher.TraceMapSpot("splash", spot, -1, -1, damage, before,
            _tileHitsByMapSpot[spot], _tileDeadByMapSpot[spot]);
        if (before >= 0 && _tileHitsByMapSpot[spot] < 0)
        {
            _tileDeadByMapSpot[spot] = true;
            SpawnTileExplosion(spot, addExplosion);
            ApplyTileExplosionDamage(spot, damage: 5, addExplosion);
            ScheduleTileDelayExplosion(spot);
        }
    }

    public void ScheduleTileDelayExplosion(int mapspot)
    {
        _tileDelayExplosions.Add(new TileDelayExplosion(mapspot, 10));
    }

    public void ProcessTileDelayExplosions(Action<int, int, int> addExplosion)
    {
        for (int i = 0; i < _tileDelayExplosions.Count; i++)
        {
            var td = _tileDelayExplosions[i];
            if (td.Frames < 0)
            {
                ApplyTileExplosionDamage(td.MapSpot, damage: 20, addExplosion);
                _tileDelayExplosions.RemoveAt(i);
                i--;
                continue;
            }

            _tileDelayExplosions[i] = td with { Frames = td.Frames - 1 };
        }
        RefreshTileSliceValuesFromBacking();
    }

    public void SpawnTileExplosion(int mapspot, Action<int, int, int> addExplosion)
    {
        foreach (var tile in _tileSlice)
        {
            if (tile.MapSpot != mapspot) continue;
            addExplosion(ExpGrdLarge, tile.ScreenX + 16, tile.ScreenY + 16);
            return;
        }
    }
}
