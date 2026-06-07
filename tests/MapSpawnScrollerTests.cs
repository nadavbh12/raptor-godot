using System.Collections.Generic;
using Raptor.Sim;
using Raptor.Sim.Enemy;
using Raptor.Sim.MazeLevel;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// Unit coverage for the MapSpawnScroller collaborator extracted from
/// WaveController (E12). These pin the parity-fragile scroll arithmetic and
/// enemy-spawn geometry; the full L1-L4 parity gate covers the rest.
/// </summary>
public class MapSpawnScrollerTests
{
    // Same MAP_* values WaveController constructs the scroller with.
    private const int MapRows      = 150;
    private const int MapOnScreen  = 8;
    private const int MapCols      = 9;
    private const int MapBlockSize = 32;
    private const int MapLeft      = 16;

    private static MapSpawnScroller New()
        => new(MapRows, MapOnScreen, MapCols, MapBlockSize, MapLeft);

    private static SpriteMeta Meta(int w, int h, int bossFlag = 0) => new()
    {
        IName    = "TEST",
        Hits     = 1,
        Width    = w,
        Height   = h,
        BossFlag = bossFlag,
    };

    private static MapSpriteEntry Sprite(int x, int y, int slib, int level, int link) => new()
    {
        X = x, Y = y, Slib = slib, Level = level, Link = link,
    };

    // ── Scroll cursor init (TILE_Init) ────────────────────────────────────────

    [Fact]
    public void ResetForWave_InitsCursorToCStartValues()
    {
        var s = New();
        s.ResetForWave();

        // tilepos = (150-8)*9 = 1278; tileyoff = 200 - 8*32 = -56; tiley = 1278/9 - 3 = 139.
        Assert.Equal((MapRows - MapOnScreen) * MapCols, s.TilePos);   // 1278
        Assert.Equal(-56, s.TileYOff);
        Assert.Equal(s.TilePos / MapCols - 3, s.TileY);               // 139
        Assert.Equal(139, s.TileY);
    }

    // ── AdvanceScroll arithmetic ──────────────────────────────────────────────

    [Fact]
    public void AdvanceScroll_BelowZero_OnlyIncrementsTileYOff()
    {
        var s = New();
        s.ResetForWave();   // tileyoff = -56
        int posBefore  = s.TilePos;
        int tileyBefore = s.TileY;

        s.AdvanceScroll();

        Assert.Equal(-55, s.TileYOff);     // tileyoff++
        Assert.Equal(posBefore, s.TilePos);   // no row crossing yet
        Assert.Equal(tileyBefore, s.TileY);
    }

    [Fact]
    public void AdvanceScroll_CrossesRowBoundary_StepsTileposAndRecomputesTiley()
    {
        var s = New();
        s.ResetForWave();   // tileyoff = -56, tilepos = 1278, tiley = 139

        // 56 ticks bring tileyoff from -56 to 0 (still not > 0, no crossing).
        for (int i = 0; i < 56; i++) s.AdvanceScroll();
        Assert.Equal(0, s.TileYOff);
        Assert.Equal(1278, s.TilePos);
        Assert.Equal(139, s.TileY);

        // The 57th tick: tileyoff++ → 1 (> 0) → wrap.
        s.AdvanceScroll();
        Assert.Equal(1 - MapBlockSize, s.TileYOff);  // 1 - 32 = -31
        Assert.Equal(1278 - MapCols, s.TilePos);     // 1269
        Assert.Equal((1278 - MapCols) / MapCols - 3, s.TileY);  // 1269/9 - 3 = 141 - 3 = 138
        Assert.Equal(138, s.TileY);
    }

    [Fact]
    public void AdvanceScroll_StopsAtMapEnd_FreezesAndClearsScrollFlag()
    {
        var s = New();
        s.ResetForWave();
        Assert.True(s.ScrollFlag);   // C scroll_flag starts TRUE (TILE.C:240/279).

        // Drive far past the end of the map. C's TILE_Display (TILE.C:465-485)
        // stops the scroll once last_tile is reached (tilepos<=0): it sets
        // scroll_flag=FALSE and pins tileyoff at 0 — it does NOT keep advancing.
        // (The map advance is decoupled from enemy-spawn exhaustion: scrolling
        // continues through the end-wave fly-off until the map's own end —
        // finding #13. The old code ran forever and overshot tiley to -4.)
        for (int i = 0; i < 100000; i++) s.AdvanceScroll();

        Assert.False(s.ScrollFlag);  // scroll_flag FALSE at map end
        Assert.Equal(0, s.TilePos);
        Assert.Equal(0, s.TileYOff);
        // tiley settles at tilepos/9 - 3 = -3, computed the tick tilepos hit 0,
        // then frozen (C recomputes tiley=tilepos/9-3 in ENEMY_DoSprites every
        // iter, so 0/9-3 = -3 forever). NOT the -4 the run-forever code reached.
        Assert.Equal(-3, s.TileY);
    }

    // ── Spawn geometry (spawnX / mapY) ────────────────────────────────────────

    [Fact]
    public void SpawnDueEnemies_ComputesByteExactGeometry()
    {
        var s = New();
        s.ResetForWave();   // tileyoff = -56, tiley = 139
        // SHIP01G1-like: 32x24 → HalfX=16, HalfY=12.
        var slib = SpriteMetaLibrary.FromList(new[] { Meta(32, 24) });
        // One sprite at the current spawn row (y == tiley == 139), link=1 (end of group).
        s.SetSprites(new List<MapSpriteEntry> { Sprite(x: 3, y: 139, slib: 0, level: 4, link: 1) });

        var enemies = new List<EnemyLogic>();
        bool exhausted = s.SpawnDueEnemies(slib, enemies, _ => true, bossLowHp: 0);

        Assert.True(exhausted);              // single sprite consumed → list exhausted
        Assert.Single(enemies);
        // spawnX = 3*32 + 16 + 16 - 16 = 112;  mapY = -56 - (139-139)*32 - 97 + 16 - 12 = -149.
        Assert.Equal(3 * MapBlockSize + MapLeft + MapBlockSize / 2 - 16, enemies[0].X);  // 112
        Assert.Equal(112, enemies[0].X);
        Assert.Equal(-56 - 97 + MapBlockSize / 2 - 12, enemies[0].Y);                    // -149
        Assert.Equal(-149, enemies[0].Y);
    }

    [Fact]
    public void SpawnDueEnemies_OnlySpawnsSpritesOnCurrentRow()
    {
        var s = New();
        s.ResetForWave();   // tiley = 139
        var slib = SpriteMetaLibrary.FromList(new[] { Meta(32, 24) });
        // First sprite is on a future (lower) row → nothing spawns this call.
        s.SetSprites(new List<MapSpriteEntry> { Sprite(x: 0, y: 100, slib: 0, level: 4, link: 1) });

        var enemies = new List<EnemyLogic>();
        bool exhausted = s.SpawnDueEnemies(slib, enemies, _ => true, bossLowHp: 0);

        Assert.False(exhausted);     // cursor stalls on the not-yet-due sprite
        Assert.Empty(enemies);
    }

    [Fact]
    public void SpawnDueEnemies_DifficultyGateSkipsSpawnButStillAdvancesCursor()
    {
        var s = New();
        s.ResetForWave();
        var slib = SpriteMetaLibrary.FromList(new[] { Meta(32, 24) });
        s.SetSprites(new List<MapSpriteEntry> { Sprite(x: 0, y: 139, slib: 0, level: 4, link: 1) });

        var enemies = new List<EnemyLogic>();
        // Gate rejects everything → no enemy added, but the sprite is consumed,
        // so the list is exhausted (mirrors the original loop's cursor advance).
        bool exhausted = s.SpawnDueEnemies(slib, enemies, _ => false, bossLowHp: 0);

        Assert.True(exhausted);
        Assert.Empty(enemies);
    }

    // ── Link-group iteration (link==0 continues the group across rows) ─────────

    [Fact]
    public void SpawnDueEnemies_LinkZeroContinuesGroupAcrossRows()
    {
        var s = New();
        s.ResetForWave();   // tiley = 139
        var slib = SpriteMetaLibrary.FromList(new[] { Meta(32, 24) });
        // Sprite 0 is on the current row with link==0 → the next sprite is part of
        // the same group and spawns regardless of its (different) y. Sprite 1 has
        // link==1 → ends the group.
        s.SetSprites(new List<MapSpriteEntry>
        {
            Sprite(x: 0, y: 139, slib: 0, level: 4, link: 0),
            Sprite(x: 1, y:  50, slib: 0, level: 4, link: 1),
        });

        var enemies = new List<EnemyLogic>();
        bool exhausted = s.SpawnDueEnemies(slib, enemies, _ => true, bossLowHp: 0);

        Assert.True(exhausted);            // both consumed → list exhausted
        Assert.Equal(2, enemies.Count);    // grouped spawn ignores the y mismatch
    }

    [Fact]
    public void SpawnDueEnemies_LinkOneEndsGroup_LeavesNextRowUnspawned()
    {
        var s = New();
        s.ResetForWave();   // tiley = 139
        var slib = SpriteMetaLibrary.FromList(new[] { Meta(32, 24) });
        // Sprite 0: current row, link==1 → group ends after it. Sprite 1 is on a
        // different (future) row, so the outer while breaks → it stays unspawned.
        s.SetSprites(new List<MapSpriteEntry>
        {
            Sprite(x: 0, y: 139, slib: 0, level: 4, link: 1),
            Sprite(x: 1, y:  50, slib: 0, level: 4, link: 1),
        });

        var enemies = new List<EnemyLogic>();
        bool exhausted = s.SpawnDueEnemies(slib, enemies, _ => true, bossLowHp: 0);

        Assert.False(exhausted);           // sprite 1 still pending
        Assert.Single(enemies);
    }

    // ── Difficulty masks the wave-1 iter-0 group (the extra-enemy bug) ─────────

    [Theory]
    [InlineData(1, 2)]   // ROOKIE/DIFF_1 (mask EB_EASY=8): MED idx0 filtered → 2 (== C)
    [InlineData(0, 2)]   // TRAINING/DIFF_0 (mask 8): same as ROOKIE → 2
    [InlineData(2, 3)]   // VETERAN/DIFF_2 (mask EASY|MED=24): MED idx0 spawns → 3
    [InlineData(3, 3)]   // ELITE/DIFF_3 (mask 56): MED idx0 spawns → 3
    public void SpawnDueEnemies_Wave1Iter0Group_CountDependsOnDifficultyMask(int diff, int expected)
    {
        // The exact wave-1 iter-0 link-group from assets/levels/MAP1G1_MAP.json:
        //   idx0: x=4 y=139 level=4 (E_MED_LEVEL)  link=0  (group head, tiley=139)
        //   idx1: x=5 y=138 level=3 (E_EASY_LEVEL) link=0  (continues group)
        //   idx2: x=3 y=138 level=3 (E_EASY_LEVEL) link=1  (ends group)
        // idx0 is MED-tier, so it spawns only when the difficulty mask carries the
        // MED bit. At ROOKIE (mask 8) it is filtered (16 & 8 == 0) → 2 enemies, the
        // C ground truth; at VETERAN+ it spawns → 3.
        var s = New();
        s.ResetForWave();   // tiley = 139
        var slib = SpriteMetaLibrary.FromList(new[] { Meta(32, 24) });
        s.SetSprites(new List<MapSpriteEntry>
        {
            Sprite(x: 4, y: 139, slib: 0, level: 4, link: 0),
            Sprite(x: 5, y: 138, slib: 0, level: 3, link: 0),
            Sprite(x: 3, y: 138, slib: 0, level: 3, link: 1),
        });

        int mask = WaveController.SpawnMaskForDiff(diff);
        var enemies = new List<EnemyLogic>();
        s.SpawnDueEnemies(
            slib, enemies,
            sprite => (WaveController.GetEbLevel(sprite.Level) & mask) != 0,
            bossLowHp: 0);

        Assert.Equal(expected, enemies.Count);
    }

    [Fact]
    public void SpawnDueEnemies_BossLowHpClampsBossHits()
    {
        var s = New();
        s.ResetForWave();
        // Boss meta with many hits; clamp to 2.
        var bossMeta = Meta(64, 64, bossFlag: 1);
        bossMeta.Hits = 999;
        var slib = SpriteMetaLibrary.FromList(new[] { bossMeta });
        s.SetSprites(new List<MapSpriteEntry> { Sprite(x: 0, y: 139, slib: 0, level: 4, link: 1) });

        var enemies = new List<EnemyLogic>();
        s.SpawnDueEnemies(slib, enemies, _ => true, bossLowHp: 2);

        Assert.Single(enemies);
        Assert.True(enemies[0].IsBoss);
        Assert.Equal(2, enemies[0].Hits);
    }
}
