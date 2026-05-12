using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Raptor.Sim;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;

namespace Raptor.View;

public partial class DebugRenderer : Node2D
{
    private WaveController? _wave;
    private MenuStateMachine? _menu;
    private string? _shotDir;
    private int _lastShotSec = -1;
    private int _scriptDumpSeq = 0;
    private string? _pendingScriptDumpLabel;

    private readonly Dictionary<string, Texture2D> _spriteCache = new();
    private readonly Dictionary<string, string> _spritePaths = new();
    private Texture2D? _playerTex;
    private readonly Dictionary<int, Texture2D?> _tileCache = new();
    private string? _tilesRoot;
    private Texture2D? _enemyBulletTex;
    private Texture2D? _playerBulletTex;
    // Score-digit sprites: numbers[0..9] = N0..N9, numbers[10] = N$.
    private readonly Texture2D?[] _digitTex = new Texture2D?[11];

    public override void _Ready()
    {
        var wc = GetNodeOrNull<WaveController>("../WaveController");
        if (wc != null) _wave = wc;

        var menu = GetNodeOrNull<MenuController>("../MenuController");
        if (menu != null) _menu = menu.Menu;

        _shotDir = OS.GetEnvironment("RAPTOR_SHOT_DIR");
        if (!string.IsNullOrEmpty(_shotDir))
        {
            DirAccess.MakeDirRecursiveAbsolute(_shotDir);
        }

        BuildSpriteIndex();
        _tilesRoot = ProjectSettings.GlobalizePath("res://assets/tiles");
        // Bullet sprites: first frame of each animated _BLK sequence.
        // ESHOT.C ESHOT_Init: enemy "ES_ATPLAYER/ATDOWN/ANGLELEFT/ANGLERIGHT"
        // bullets all use cur->item = ESHOT_BLK. Player forward gun uses NMSHOT_BLK
        // (SHOTS.C: slib[S_FORWARD_GUNS].lumpnum = NMSHOT_BLK).
        string bulletsRoot = ProjectSettings.GlobalizePath("res://assets/bullets");
        _enemyBulletTex  = LoadSpriteFromPath(Path.Combine(bulletsRoot, "ESHOT_BLK_00.png"));
        _playerBulletTex = LoadSpriteFromPath(Path.Combine(bulletsRoot, "NMSHOT_BLK_00.png"));

        // Score-digit sprite array (RAP.C: numbers[0..10] = N0..N9 + $).
        string spritesRoot = ProjectSettings.GlobalizePath("res://assets/sprites");
        for (int i = 0; i <= 9; i++)
            _digitTex[i] = LoadSpriteFromPath(Path.Combine(spritesRoot, $"{i + 1:D4}_N{i}_PIC.png"));
        _digitTex[10] = LoadSpriteFromPath(Path.Combine(spritesRoot, "0011_N$_PIC.png"));
        // Player has 7 LPLAYER_PIC frames (0058..0064) for the bank angles
        // when steering left/right. Index 3 (0061) is the neutral straight-
        // ahead pose, which is the right default while we don't model bank.
        _playerTex = LoadSpriteFromPath(
            Path.Combine(ProjectSettings.GlobalizePath("res://assets/sprites"),
                         "0061_LPLAYER_PIC.png"));

        ZIndex = 100;
    }

    private void BuildSpriteIndex()
    {
        // Files look like "0303_SHIP01G1_PIC.png". Map iname → first PNG path
        // (lowest sequential prefix becomes the canonical first frame).
        string root = ProjectSettings.GlobalizePath("res://assets/sprites");
        if (!Directory.Exists(root)) return;

        foreach (string path in Directory.GetFiles(root, "*.png").OrderBy(s => s))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int u = name.IndexOf('_');
            if (u < 0) continue;
            string iname = name.Substring(u + 1);
            if (!_spritePaths.ContainsKey(iname)) _spritePaths[iname] = path;
        }
    }

    private Texture2D? LoadSprite(string iname)
    {
        if (_spriteCache.TryGetValue(iname, out var cached)) return cached;
        if (!_spritePaths.TryGetValue(iname, out var path)) return null;
        return LoadSpriteFromPath(path);
    }

    /// <summary>
    /// Load tile graphic for the given (game-index, flats-index) pair.
    /// game=0 → tiles/g1/NNNN.png (mirrors C's TILE_Init: titems[i] = startflat[fgame] + flats).
    /// Returns null (cached) if the file is missing.
    /// </summary>
    private Texture2D? LoadTile(int game, int flats)
    {
        int key = (game << 16) | (flats & 0xffff);
        if (_tileCache.TryGetValue(key, out var cached)) return cached;
        if (_tilesRoot == null) return null;
        string path = Path.Combine(_tilesRoot, $"g{game + 1}", $"{flats:D4}.png");
        Texture2D? tex = null;
        if (File.Exists(path))
        {
            var img = Image.LoadFromFile(path);
            if (img != null) tex = ImageTexture.CreateFromImage(img);
        }
        _tileCache[key] = tex;  // cache misses too — avoid retrying every frame
        return tex;
    }

    private Texture2D? LoadSpriteFromPath(string path)
    {
        if (_spriteCache.TryGetValue(path, out var cached)) return cached;
        var img = Image.LoadFromFile(path);
        if (img == null) return null;
        var tex = ImageTexture.CreateFromImage(img);
        _spriteCache[path] = tex;
        return tex;
    }

    public override void _Process(double delta)
    {
        QueueRedraw();
        MaybeShoot();
        MaybeFirePendingScriptDump();
    }

    private void MaybeFirePendingScriptDump()
    {
        if (_pendingScriptDumpLabel == null || _pendingShotFireFrame < 0) return;
        if (SimClock.Frame < _pendingShotFireFrame) return;
        // Bump sequence on first frame (offset 0) so all bursts share the seq.
        if (_burstFrameOffset == 0) _scriptDumpSeq++;
        // Capture all needed state NOW; CallDeferred runs later but receives
        // these values directly so it can't race with later burst ticks.
        string label = _pendingScriptDumpLabel;
        int seq = _scriptDumpSeq;
        int off = _burstFrameOffset;
        CallDeferred(nameof(WriteShotBurst), seq, off, label);
        if (_burstRemaining > 0)
        {
            _burstRemaining--;
            _burstFrameOffset++;
            _pendingShotFireFrame = SimClock.Frame + 1;
        }
        else
        {
            _pendingShotFireFrame = -1;
            _pendingScriptDumpLabel = null;
        }
    }

    private void WriteShotBurst(int seq, int off, string label)
    {
        var img = GetViewport().GetTexture().GetImage();
        if (img == null) return;
        // Include abs FC in the filename so labeled dumps interleave correctly
        // with periodic fcNNNNN_secNNN.png dumps when sorted alphabetically.
        int fc = SimClock.Frame;
        string path = off == 0
            ? $"{_shotDir}/fc{fc:D5}_label_{label}.png"
            : $"{_shotDir}/fc{fc:D5}_label_{label}_p{off:D2}.png";
        img.SavePng(path);
    }

    private void MaybeShoot()
    {
        if (string.IsNullOrEmpty(_shotDir)) return;
        // RAPTOR_SHOT_EVERY_FC=N: dump every N sim frames (fine-grained, for video).
        // RAPTOR_SHOT_EVERY_SEC=N: dump every N simulated seconds (legacy default 1).
        var fcEvery = OS.GetEnvironment("RAPTOR_SHOT_EVERY_FC");
        if (!string.IsNullOrEmpty(fcEvery) && int.TryParse(fcEvery, out var nfc) && nfc > 0)
        {
            int bucket = SimClock.Frame / nfc;
            if (bucket == _lastShotSec) return;
            _lastShotSec = bucket;
            CallDeferred(nameof(WriteShot), SimClock.Frame / 70, SimClock.Frame);
            return;
        }
        int interval = 1;
        var iv = OS.GetEnvironment("RAPTOR_SHOT_EVERY_SEC");
        if (!string.IsNullOrEmpty(iv) && int.TryParse(iv, out var n) && n > 0) interval = n;
        int sec = SimClock.Frame / 70;
        if (sec / interval == _lastShotSec) return;
        _lastShotSec = sec / interval;
        CallDeferred(nameof(WriteShot), sec, SimClock.Frame);
    }

    private void WriteShot(int sec, int fc)
    {
        var img = GetViewport().GetTexture().GetImage();
        if (img == null) return;
        string path = $"{_shotDir}/fc{fc:D5}_sec{sec:D3}.png";
        img.SavePng(path);
    }

    /// <summary>
    /// Called by PlaythroughDriver when a `dump LABEL` script command fires.
    /// With RAPTOR_SHOT_BURST=N, dumps every sim frame for N frames after the
    /// command (and N before, retroactively impossible, so just after). Use
    /// the burst to find which frame best matches C visually.
    /// </summary>
    public void RequestScriptDump(string label)
    {
        if (string.IsNullOrEmpty(_shotDir)) return;
        _pendingScriptDumpLabel = label;
        int burst = 0;
        var b = OS.GetEnvironment("RAPTOR_SHOT_BURST");
        if (!string.IsNullOrEmpty(b) && int.TryParse(b, out var n) && n > 0) burst = n;
        _burstRemaining = burst;
        _burstFrameOffset = 0;
        _pendingShotFireFrame = SimClock.Frame;
    }

    private int _pendingShotFireFrame = -1;
    private int _burstRemaining = 0;
    private int _burstFrameOffset = 0;

    /// <summary>
    /// Render the scrolling tile background. Mirrors C's TILE_Think layout:
    ///   for loopy in 0..MAP_ONSCREEN, y starting at tileyoff:
    ///     for loopx in 0..MAP_COLS, x starting at MAP_LEFT:
    ///       draw titems[mapspot] at (x, y)
    /// titems[mapspot] = startflat[fgame] + flats — we resolve that via
    /// LoadTile(fgame, flats) which reads assets/tiles/g{fgame+1}/{flats:D4}.png.
    /// </summary>
    private void DrawTileMap()
    {
        if (_wave?.MapTiles == null) return;
        var tiles = _wave.MapTiles;
        int cols  = _wave.MapCols;
        int rows  = _wave.MapRows;
        int onscr = _wave.MapOnScreen;
        int bs    = _wave.MapBlockSize;
        int left  = _wave.MapLeftPx;

        int y       = _wave.TileYOff;
        int mapspot = _wave.TilePos;

        for (int ly = 0; ly < onscr; ly++, y += bs)
        {
            int x = left;
            for (int lx = 0; lx < cols; lx++, x += bs, mapspot++)
            {
                if (mapspot < 0 || mapspot >= tiles.Count) continue;
                var t = tiles[mapspot];
                var tex = LoadTile(t.FGame, t.Flats);
                if (tex != null) DrawTexture(tex, new Vector2(x, y));
            }
        }
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(0, 0, 320, 200), new Color(0, 0, 0, 1));
        DrawRect(new Rect2(16, 0, 288, 200), new Color(0.05f, 0.05f, 0.1f, 1));

        if (_wave == null) return;

        DrawTileMap();

        var px = _wave.PlayerLogic.X;
        var py = _wave.PlayerLogic.Y;
        const int PlayerW = 32, PlayerH = 32;

        // Sky shadows mirror C's render order: TILE_Display → SHADOW_DisplaySky
        // → ENEMY_DisplaySky → player. RAP.C also adds the player shadow with
        // SHADOW_Add before the sky pass (RAP.C:1060), so we draw it here too.
        foreach (var e in _wave.GetEnemies())
        {
            if (!e.Alive) continue;
            if (e.Meta.Shadow == 0 || e.Meta.Ground != 0) continue;
            var tex = LoadSprite(e.Meta.IName);
            if (tex == null) continue;
            DrawSkyShadow(tex, e.X, e.Y, e.HalfW * 2, e.HalfH * 2);
        }
        if (_playerTex != null)
            DrawSkyShadow(_playerTex, px, py, PlayerW, PlayerH);

        // C's eframe ^= 1 per ENEMY_DisplaySky call (one per sim tick).
        // Derive from SimClock.Frame parity so the view doesn't mutate sim state.
        int eframe = SimClock.Frame & 1;
        foreach (var e in _wave.GetEnemies())
        {
            if (!e.Alive) continue;
            var tex = LoadSprite(e.Meta.IName);
            if (tex != null)
            {
                DrawTexture(tex, new Vector2(e.X, e.Y));
            }
            else
            {
                DrawRect(new Rect2(e.X, e.Y, e.HalfW * 2, e.HalfH * 2),
                    new Color(1, 0.3f, 0.3f, 0.7f));
            }
            // Sky enemies get engine-flame puffs trailing upward (toward top of
            // screen, since ships fly downward). Ground enemies (groundflag != 0)
            // never call FLAME_Up in C.
            if (e.Meta.Ground == 0 && e.Meta.NumEngs > 0)
                DrawEngineFlames(e, eframe);
        }

        // Sim X/Y are TOP-LEFT (matching C's sprite->x/y semantics — see ENEMY_Add
        // comments in WaveController). C's GFX_PutSprite renders at top-left, so
        // we draw directly at (X, Y) without subtracting half-size. The player
        // is drawn AFTER enemies in C (RAP.C:1077), so it occludes them.
        if (_playerTex != null)
        {
            // Player engine flames — FLAME_Down at (player_cx ± o_engine[pic] - {3,2},
            // player_cy + 15) in C (RAP.C:1075-1076). o_engine for the neutral pose
            // (LPLAYER_PIC frame 3) is 11 (see RAP.C o_engine[]); fixed for now since
            // banking frames aren't wired yet.
            int pcx = px + PlayerW / 2;
            int pcy = py + PlayerH / 2;
            DrawFlameDown(pcx - 11 - 3, pcy + 15, 4, eframe);
            DrawFlameDown(pcx + 11 - 2, pcy + 15, 4, eframe);
            DrawTexture(_playerTex, new Vector2(px, py));
        }
        else
        {
            DrawRect(new Rect2(px, py, 32, 32), new Color(0, 1, 0, 0.7f));
        }

        // Bullet sim X/Y are also top-left (ESHOT_Shoot: cur->move.x -= xoff).
        foreach (var b in _wave.GetEnemyBullets())
        {
            if (!b.Alive) continue;
            if (_enemyBulletTex != null)
                DrawTexture(_enemyBulletTex, new Vector2(b.X, b.Y));
            else
                DrawRect(new Rect2(b.X, b.Y, 4, 4), new Color(1, 1, 0));
        }

        foreach (var b in _wave.GetPlayerBullets())
        {
            if (!b.Alive) continue;
            if (_playerBulletTex != null)
                DrawTexture(_playerBulletTex, new Vector2(b.X, b.Y));
            else
                DrawRect(new Rect2(b.X, b.Y, 4, 4), new Color(0, 1, 1));
        }

        DrawScoreHud();

        // Bottom debug overlay is only useful for visual-parity debugging;
        // it intrudes on the rendered scene in screenshots. Set
        // RAPTOR_VIEW_DEBUG_HUD=1 to enable it.
        if (OS.GetEnvironment("RAPTOR_VIEW_DEBUG_HUD") == "1")
        {
            var sf = SimClock.Frame;
            var win = _menu?.State.ToString() ?? "?";
            var hud = $"fc={sf}  win={win}  shield={_wave.PlayerLogic.Shield}  score={_wave.Score}  E={_wave.GetEnemies().Count}  EB={_wave.GetEnemyBullets().Count}  PB={_wave.GetPlayerBullets().Count}";
            var font = ThemeDB.FallbackFont;
            DrawString(font, new Vector2(4, 195), hud, HorizontalAlignment.Left, -1, 8, new Color(1, 1, 1));
        }
    }

    /// <summary>
    /// Project an air-sprite to its shadow position on the ground and draw a
    /// darkened silhouette. Mirrors SHADOW_Draw (SOURCE/SHADOWS.C) which uses
    /// GFX_3DPoint with viewx=160, viewy=100, viewz=1000, G3D_DIST=200, and
    /// shadow plane z=MAXZ=1280. Effective scale = 200/(1280-1000) = 5/7.
    /// Pre-projection offset: x-=10, y+=20.
    /// </summary>
    private void DrawSkyShadow(Texture2D tex, int x, int y, int w, int h)
    {
        const float Scale = 200f / 280f;   // G3D_DIST / (MAXZ - viewz)
        const int ViewX = 160, ViewY = 100;
        int ox = x - 10;
        int oy = y + 20;
        float sx = Scale * (ox - ViewX) + ViewX;
        float sy = Scale * (oy - ViewY) + ViewY;
        float sx2 = Scale * (ox + w - 1 - ViewX) + ViewX;
        float sy2 = Scale * (oy + h - 1 - ViewY) + ViewY;
        var rect = new Rect2(sx, sy, sx2 - sx + 1, sy2 - sy + 1);
        DrawTextureRect(tex, rect, false, new Color(0, 0, 0, 0.5f));
    }

    /// <summary>
    /// Player engine flame trailing downward. Mirrors FLAME_Down
    /// (SOURCE/FLAME.C): height 8 (frame 0) / 12 (frame 1); brightest at top
    /// (engine exit), dimming downward. The (ix, iy) point is the top-left of
    /// the flame in C.
    /// </summary>
    private void DrawFlameDown(int ix, int iy, int width, int frame)
    {
        if (width <= 0) return;
        int height = frame == 0 ? 8 : 12;
        for (int row = 0; row < height; row++)
        {
            float t = 1f - row / (float)height;  // 1 at top, 0 at bottom
            var col = new Color(1.0f, 0.4f + 0.6f * t, 0.0f, 0.85f * t + 0.15f);
            DrawRect(new Rect2(ix, iy + row, width, 1), col);
        }
    }

    /// <summary>
    /// Engine-flame puff under a sky enemy. Mirrors FLAME_Up (SOURCE/FLAME.C):
    /// height alternates 5 px (frame 0) and 10 px (frame 1); the row at the
    /// bottom is the brightest and the top row is the dimmest. The flame's
    /// bottom-row baseline sits at (engine_x, engine_y) where the engine point
    /// is enemy.(X, Y) + (engx[i], engy[i]). The C version uses palette light
    /// tables; the view approximates with a vertical orange→yellow gradient.
    /// </summary>
    private void DrawEngineFlames(EnemyLogic e, int frame)
    {
        int height = frame == 0 ? 5 : 10;
        var meta = e.Meta;
        for (int i = 0; i < meta.NumEngs; i++)
        {
            int width = meta.EngLx[i];
            if (width <= 0) continue;
            int bx = e.X + meta.EngX[i];
            int by = e.Y + meta.EngY[i];
            int topY = by - (height - 1);  // mirrors C: iy -= (height-1)
            // Draw rows top→bottom: top row dim, bottom row bright.
            for (int row = 0; row < height; row++)
            {
                float t = (row + 1) / (float)height;  // 0 → 1, brightest at base
                var col = new Color(1.0f, 0.4f + 0.6f * t, 0.0f, 0.85f * t + 0.15f);
                DrawRect(new Rect2(bx, topY + row, width, 1), col);
            }
        }
    }

    /// <summary>
    /// Draw the "$NNNNNNNN" score readout. Mirrors RAP.C lines 661-662:
    ///   sprintf(temp, "%08u", plr.score);
    ///   RAP_PrintNum(119, MAP_TOP, temp);
    /// RAP_PrintNum draws the $ sprite (numbers[10]) at (x, y) then steps +9
    /// pixels, then each digit advances +8 pixels.
    /// </summary>
    private void DrawScoreHud()
    {
        if (_wave == null) return;
        const int MapTop = 2;  // SOURCE/MAP.H
        int x = 119;
        // "$" prefix.
        if (_digitTex[10] != null) DrawTexture(_digitTex[10]!, new Vector2(x, MapTop));
        x += 9;
        string score = _wave.Score.ToString("D8");
        foreach (char c in score)
        {
            int d = c - '0';
            if (d >= 0 && d <= 9 && _digitTex[d] != null)
                DrawTexture(_digitTex[d]!, new Vector2(x, MapTop));
            x += 8;
        }
    }
}
