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
        if (_tileHitsByMapSpot == null || _tileDeadByMapSpot == null ||
            _tileDestructibleByMapSpot == null) return;
        foreach (var t in _tileSlice)
        {
            if (t.MapSpot < 0 || t.MapSpot >= _tileHitsByMapSpot.Length) continue;
            t.Hits = _tileHitsByMapSpot[t.MapSpot];
            t.Dead = _tileDeadByMapSpot[t.MapSpot];
            // TileThinkAwardScan/TileExplode flips a destroyed tile indestructible in
            // the backing (C eitems=titems). Mirror it into the slice so TileIsHit
            // (which reads the slice) stops absorbing bullets into the dead tile —
            // C makes the tile indestructible in TILE_Think before that iter's shots.
            t.IsDestructible = _tileDestructibleByMapSpot[t.MapSpot];
        }
    }

    /// <summary>
    /// The single per-iter award scan, mirroring C TILE_Think (TILE.C:386-398). Run
    /// once at the top of the tick BEFORE the bullet/collision phase (as C runs
    /// TILE_Think before SHOTS_Think). For each on-screen tile with hits&lt;0 &amp;&amp;
    /// !dead, in row-major scan order: invoke <paramref name="onDestroyed"/>
    /// (mapspot, screenX, screenY) so the caller can draw the FX_GEXPLO sound RNG +
    /// spawn the ground-explosion anim, splash 5 NON-recursively to the 3 neighbors,
    /// accumulate bounty, schedule the 10-frame fuse + flip the tile indestructible
    /// (TILE_Explode), and mark it dead. Returns the total bounty to add to Score.
    ///
    /// This is the SOLE award site (the inversion fixed in finding #1). Splash is
    /// non-recursive: a splashed neighbor is only awarded once a later scan finds it
    /// at hits&lt;0, so a connected structure dies ring-by-ring, never all at once.
    /// </summary>
    public int TileThinkAwardScan(Action<int, int, int> onDestroyed)
    {
        if (_tileHitsByMapSpot == null || _tileDeadByMapSpot == null ||
            _tileDestructibleByMapSpot == null || _tileBountyByMapSpot == null)
            return 0;

        int bounty = 0;
        foreach (var t in _tileSlice)   // row-major (slot) order, matching C tspots[]
        {
            int ms = t.MapSpot;
            if (ms < 0 || ms >= _tileHitsByMapSpot.Length) continue;
            if (_tileHitsByMapSpot[ms] < 0 && !_tileDeadByMapSpot[ms])
            {
                onDestroyed(ms, t.ScreenX, t.ScreenY);   // FX_GEXPLO sound RNG + anim
                TileDoDamage(ms, 5);                      // C TILE_DoDamage(ts, 5)
                bounty += _tileBountyByMapSpot[ms];       // plr.score += money[mapspot]
                TileExplode(ms, delay: 10);              // schedule fuse + indestructible
                _tileDeadByMapSpot[ms] = true;           // tdead = 1
            }
        }
        RefreshTileSliceValuesFromBacking();
        return bounty;
    }

    /// <summary>
    /// C TILE_DoDamage (TILE.C:154-186): NON-recursive splash to the left/up/right
    /// neighbors, decrementing hits. Guards ONLY on destructibility (eitems==titems),
    /// NOT on tdead (finding #15) — a tdead-but-still-destructible tile keeps taking
    /// splash in the one-iter window before TILE_Explode flips it indestructible.
    /// </summary>
    private void TileDoDamage(int mapspot, int damage)
    {
        if (_tileHitsByMapSpot == null || _tileDestructibleByMapSpot == null) return;
        int ix = mapspot % MAP_COLS;
        TileDoDamageNeighbor(mapspot - 1, ix - 1, damage);
        TileDoDamageNeighbor(mapspot - MAP_COLS, ix, damage);
        TileDoDamageNeighbor(mapspot + 1, ix + 1, damage);
    }

    private void TileDoDamageNeighbor(int spot, int x, int damage)
    {
        if (_tileHitsByMapSpot == null || _tileDestructibleByMapSpot == null ||
            _tileDeadByMapSpot == null) return;
        if (spot < 0 || spot >= _tileHitsByMapSpot.Length) return;
        if (x < 0 || x >= MAP_COLS) return;
        if (!_tileDestructibleByMapSpot[spot]) return;   // C: eitems==titems guard (no tdead gate)
        int before = _tileHitsByMapSpot[spot];
        _tileHitsByMapSpot[spot] -= damage;
        TileDamageDispatcher.TraceMapSpot("splash", spot, -1, -1, damage, before,
            _tileHitsByMapSpot[spot], _tileDeadByMapSpot[spot]);
    }

    /// <summary>
    /// C TILE_Explode delay branch (TILE.C:580-591): schedule the fuse and flip the
    /// tile indestructible immediately (eitems=titems) so further hits/splashes skip
    /// it before the fuse restores its destroyed graphic.
    /// </summary>
    private void TileExplode(int mapspot, int delay)
    {
        if (_tileDestructibleByMapSpot == null) return;
        ScheduleTileDelayExplosion(mapspot, delay);
        if (mapspot >= 0 && mapspot < _tileDestructibleByMapSpot.Length)
            _tileDestructibleByMapSpot[mapspot] = false;
    }

    public void ScheduleTileDelayExplosion(int mapspot, int delay = 10)
    {
        _tileDelayExplosions.Add(new TileDelayExplosion(mapspot, delay));
    }

    /// <summary>
    /// C TILE_Think delay processing (TILE.C:402-440): each fuse counts down, and on
    /// expiry fires a NON-recursive TILE_DoDamage(20) splash. Invokes
    /// <paramref name="onFuseFired"/> once per firing fuse so the caller can draw the
    /// spark random(8) (finding #3). Fuses do NOT award bounty — the next scan awards
    /// any neighbor the splash pushed below zero.
    /// </summary>
    public void ProcessTileDelayExplosions(Action onFuseFired)
    {
        for (int i = 0; i < _tileDelayExplosions.Count; i++)
        {
            var td = _tileDelayExplosions[i];
            if (td.Frames < 0)
            {
                onFuseFired();              // C: tx = x + 8 + random(8)
                TileDoDamage(td.MapSpot, 20);
                _tileDelayExplosions.RemoveAt(i);
                i--;
                continue;
            }

            _tileDelayExplosions[i] = td with { Frames = td.Frames - 1 };
        }
        RefreshTileSliceValuesFromBacking();
    }
}
