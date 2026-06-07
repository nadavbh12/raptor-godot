using System;
using System.Collections.Generic;
using Raptor.Sim.Enemy;
using Raptor.Sim.MazeLevel;

namespace Raptor.Sim;

/// <summary>
/// Owns the map-scroll cursor (mirrors C's TILE.C tilepos/tileyoff/tiley) and the
/// enemy-spawn scheduling (ENEMY.C ENEMY_DoSprites). WaveController owns this
/// collaborator and drives it in phase order: the per-tick spawn (SpawnDueEnemies)
/// runs in PhaseSpawn, then AdvanceScroll advances the scroll cursor — exactly the
/// pre-extraction order (tile-refresh → tile-delay → spawn → scroll-advance).
///
/// Parity-fragile: the spawn geometry (spawnX/mapY arithmetic), link-group
/// iteration order, and scroll cadence MUST be byte-exact with the original
/// WaveController code. No RNG is involved in spawn/scroll.
///
/// Seam: the spawn-exhausted flag (_endWaveFlag) stays in WaveController. The
/// spawn method does NOT mutate it — it returns whether the sprite list was
/// exhausted (the condition that previously set _endWaveFlag = true), and
/// WaveController applies it. The enemy list and the boss-HP clamp / slib lookup
/// stay in WaveController; the list and the values are passed in. The difficulty
/// gate (ShouldSpawn) stays in WaveController and is passed as a delegate so the
/// difficulty logic isn't pulled out.
/// </summary>
internal sealed class MapSpawnScroller
{
    // MAP_* mirror constants live on WaveController; passed in at construction so
    // there's a single source of truth (cf. CLAUDE.md "prefer passing over
    // duplication"). Used for the scroll-init / scroll-advance / spawn geometry.
    private readonly int _mapRows;
    private readonly int _mapOnScreen;
    private readonly int _mapCols;
    private readonly int _mapBlockSize;
    private readonly int _mapLeft;

    // ── Map scroll state (mirrors C's TILE.C) ─────────────────────────────────
    // tilepos starts at (MAP_ROWS - MAP_ONSCREEN) * MAP_COLS = 142 * 9 = 1278.
    // tiley = tilepos / MAP_COLS - 3.
    // tileyoff starts at 200 - MAP_ONSCREEN * MAP_BLOCKSIZE = 200 - 256 = -56.
    // Each physics tick: tileyoff++. When tileyoff > 0: tileyoff -= 32; tilepos -= 9.
    private int _tilepos;
    private int _tileyoff;
    private int _tiley;   // current spawn row: tilepos/MAP_COLS - 3
    // Mirrors C's TILE.C scroll_flag/last_tile (TILE.C:30-31). scroll_flag starts
    // TRUE and goes FALSE only at the map's own end (last_tile && tileyoff>=0,
    // TILE.C:469-472); last_tile latches when tilepos reaches 0. The map scroll is
    // decoupled from enemy-spawn exhaustion — it keeps advancing through the
    // end-wave fly-off until the map ends (finding #13).
    private bool _lastTile;
    private bool _scrollFlag;

    // ── Map sprite list for spawning ──────────────────────────────────────────
    private List<MapSpriteEntry>? _mapSprites;
    private int _spawnIdx = 0;   // index into _mapSprites

    public MapSpawnScroller(int mapRows, int mapOnScreen, int mapCols, int mapBlockSize, int mapLeft)
    {
        _mapRows      = mapRows;
        _mapOnScreen  = mapOnScreen;
        _mapCols      = mapCols;
        _mapBlockSize = mapBlockSize;
        _mapLeft      = mapLeft;
    }

    /// <summary>Current scroll Y offset (mirrors C's tileyoff).</summary>
    public int TileYOff => _tileyoff;
    /// <summary>Current top-of-screen row in the tile grid (mirrors C's tilepos).</summary>
    public int TilePos  => _tilepos;
    /// <summary>Current spawn row in the tile grid (mirrors C's tiley).</summary>
    public int TileY    => _tiley;
    /// <summary>
    /// Whether the map is still scrolling (mirrors C's scroll_flag, TILE.C:30).
    /// TRUE from wave start until the map's own end; stays TRUE through the
    /// end-wave fly-off (the scroll is NOT frozen by enemy-spawn exhaustion).
    /// </summary>
    public bool ScrollFlag => _scrollFlag;

    /// <summary>Whether a wave's sprite list has been loaded (SetSprites called).</summary>
    public bool HasSprites => _mapSprites != null;

    /// <summary>
    /// Reset the scroll cursor to the start position (mirrors TILE_Init in C).
    /// </summary>
    public void ResetForWave()
    {
        _tilepos  = (_mapRows - _mapOnScreen) * _mapCols;
        _tileyoff = 200 - _mapOnScreen * _mapBlockSize;  // -56
        _tiley    = _tilepos / _mapCols - 3;             // = 139
        _lastTile = false;     // C TILE.C:241/280
        _scrollFlag = true;    // C TILE.C:240/279
    }

    /// <summary>
    /// Set the wave's sprite list and reset the spawn cursor to the start.
    /// </summary>
    public void SetSprites(List<MapSpriteEntry> sprites)
    {
        _mapSprites = sprites;
        _spawnIdx   = 0;
    }

    /// <summary>
    /// Spawn all enemies whose y matches the current tiley, processing linked groups.
    /// Mirrors ENEMY.C ENEMY_DoSprites() while/for loop.
    /// Returns true if the sprite list was exhausted (the condition that previously
    /// set _endWaveFlag = true in WaveController). WaveController applies the flag.
    /// </summary>
    public bool SpawnDueEnemies(SpriteMetaLibrary? slib, List<EnemyLogic> enemies,
        Func<MapSpriteEntry, bool> shouldSpawn, int bossLowHp, int curPlayerDiff = 2)
    {
        if (_mapSprites == null || slib == null) return false;
        int tiley = _tiley;
        // Local stand-in for the _endWaveFlag the loop used to set. The original
        // method ran with _endWaveFlag == false on entry (PhaseSpawn early-returns
        // if it was already set; LoadWave clears it before the initial spawn), and
        // nothing else mutated it between loop iterations — so a local that the loop
        // sets and the method returns is equivalent to the original guard/sentinel.
        bool reachedEnd = false;
        while (_spawnIdx < _mapSprites.Count && !reachedEnd)
        {
            var sprite = _mapSprites[_spawnIdx];
            if (sprite.Y != tiley) break;

            for (;;)
            {
                if (_spawnIdx >= _mapSprites.Count)
                {
                    reachedEnd = true;
                    break;
                }
                var cur = _mapSprites[_spawnIdx];
                int oldLink = cur.Link;

                // Spawn this enemy only if it passes difficulty check.
                // Mirrors: if (cur_enemy->level != EB_NOT_USED) ENEMY_Add(cur_enemy).
                if (shouldSpawn(cur) && slib.Count > cur.Slib && cur.Slib >= 0)
                {
                    var meta = slib.Get(cur.Slib);
                    // C ENEMY_Add (ENEMY.C lines 393-400):
                    //   new->y = tileyoff - (tiley-y)*32 - 97;
                    //   new->x = sprite.x*32 + MAP_LEFT;
                    //   new->x += 16; new->y += 16;
                    //   new->x -= hlx;  new->y -= hly;
                    // sprite->x/y is the sprite TOP-LEFT after these shifts.
                    // For SHIP01G1 (hlx=16=MAP_BLOCKSIZE/2), x cancels to
                    // `sprite.x*32 + MAP_LEFT` — what we already compute. For Y,
                    // hly=12 != 16 so we owe `+16 - hly = +4`. Generalised:
                    int spawnX = cur.X * _mapBlockSize + _mapLeft
                                 + _mapBlockSize / 2 - meta.HalfX;
                    int mapY   = _tileyoff - ((tiley - cur.Y) * _mapBlockSize) - 97
                                 + _mapBlockSize / 2 - meta.HalfY;
                    var enemy = new EnemyLogic(meta, spawnX, mapY, curPlayerDiff);
                    if (bossLowHp > 0 && enemy.IsBoss) enemy.DebugClampHits(bossLowHp);
                    enemies.Add(enemy);
                }

                _spawnIdx++;
                if (_spawnIdx >= _mapSprites.Count) { reachedEnd = true; break; }
                // link==-1 (EMPTY) or ==1 → end of group
                if (oldLink == -1 || oldLink == 1) break;
                // link==0 → continue group (next sprite is part of this group, regardless of y)
            }
        }
        return reachedEnd;
    }

    /// <summary>
    /// Advance the scroll cursor by one tick (mirrors C's TILE_Think scroll
    /// advance): tileyoff++, and when it crosses a block boundary, step tilepos
    /// up one row and recompute tiley.
    /// </summary>
    public void AdvanceScroll()
    {
        // Faithful port of C TILE_Display's scroll block (TILE.C:465-485). The
        // map keeps advancing until last_tile (tilepos reached 0), then freezes
        // (scroll_flag=FALSE, tileyoff pinned at 0). Before this fix the scroll
        // never stopped here — it was frozen externally by WaveController's
        // _endWaveFlag guard, which also froze it prematurely at enemy-spawn
        // exhaustion (finding #13). Now the stop matches the map's own end.
        _tileyoff++;
        if (_tileyoff > 0)
        {
            if (_lastTile)
            {
                _tileyoff   = 0;
                _scrollFlag = false;
            }
            else
            {
                _tileyoff -= _mapBlockSize;
                _tilepos  -= _mapCols;
                _tiley     = _tilepos / _mapCols - 3;
            }
            if (_tilepos <= 0)
            {
                _tilepos  = 0;
                _lastTile = true;
            }
        }
    }
}
