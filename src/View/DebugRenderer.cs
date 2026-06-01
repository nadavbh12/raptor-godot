using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Raptor.Sim;
using Raptor.Sim.Bonus;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Raptor.Sim.Shots;

namespace Raptor.View;

public partial class DebugRenderer : Node2D
{
    internal const float GroundShadowAlpha = 0.45f;

    private WaveController? _wave;
    private MenuStateMachine? _menu;
    private string? _shotDir;
    private string? _shotMapPath;
    private bool _interactiveUi;
    private int _lastShotSec = -1;
    private int _scriptDumpSeq = 0;
    private string? _pendingScriptDumpLabel;
    private int _lastDrawnFc = -1;
    private int _lastDrawnIter = -1;
    private uint _lastDrawnScore = 0;
    private int _lastDrawnShield = 0;
    private int _lastDrawnEnemies = 0;
    private int _lastDrawnPbullets = 0;
    private int _lastDrawnEbullets = 0;
    private readonly HudScannerIndicator.State _scannerState = new();
    private int _lastScannerFrame = -1;
    private readonly ViewEffects _effects = new();
    private int _lastMuzzleFrame = -1;
    // Megabomb white-out flash: GameLoopIter at which detonation fired, and the
    // number of iters the flash eases out over. Parity-inert View cosmetic.
    private int _megaFadeStartIter = int.MinValue;
    private const int MegaFadeFrames = 8;
    private const float MegaFadeMaxAlpha = 0.85f;

    private readonly Dictionary<string, Texture2D> _spriteCache = new();
    private string? _agxRoot;
    private Color[]? _palette;
    private readonly Dictionary<string, BitmapFont> _bitmapFonts = new();
    private readonly Dictionary<string, SwdWindow?> _swdCache = new();
    private SwdHost? _swdHostInstance;
    private SwdHost _swdHost => _swdHostInstance ??= new SwdHost(this);
    private readonly Dictionary<string, string> _spritePaths = new();
    // Multi-frame enemy sprites keyed by iname. The PNG extractor writes each
    // consecutive GLB item with the same iname under sequential indices, e.g.
    // 0309_SHIP07G1_PIC.png and 0310_SHIP07G1_PIC.png for the helicopter's
    // two rotor frames. We collect all paths sharing an iname here.
    private readonly Dictionary<string, List<string>> _spritePathsAll = new();
    // Flat-silhouette shadow textures, keyed by source Texture2D. Each is a
    // black image whose alpha tracks the source sprite's mask, replicating the
    // C SHADOW_Draw behaviour where shadows are solid dark patches with no
    // interior detail (the C version shades the underlying screen pixels, not
    // the sprite, but a flat-color silhouette is the closest CanvasItem-only
    // approximation without a shader).
    private readonly Dictionary<Texture2D, Texture2D> _shadowCache = new();

    // C's FLAME_Up/FLAME_Down call GFX_Shade with a light table built by
    // GFX_MakeLightTable at positive intensity, which BRIGHTENS the underlying
    // pixels (no fixed colour). Over teal water the flame trail reads as light
    // blue; over brown tiles as warm tan. To replicate that, we draw flames on
    // an additive-blend child Node2D so a white quad lightens whatever is
    // behind it instead of painting a fixed colour over it.
    private Node2D? _flameLayer;
    private readonly List<(Rect2 Rect, Color Col)> _flameQuads = new();
    // _playerTex[0..6] corresponds to playerpic 0..6 (LPLAYER_PIC 0058..0064).
    // Index 3 is neutral (playerbasepic).
    private readonly Texture2D?[] _playerTex = new Texture2D?[7];
    // o_engine[7] from C RAP.C:67. Maps playerpic → engine-x offset from
    // player_cx; used by FLAME_Down placement (RAP.C:1075-1076).
    private static readonly int[] OEngine = { 0, 1, 2, 3, 2, 1, 0 };

    // Explosion BLK family + frame count keyed by C exptype (SOURCE/MAP.H).
    // The handle mapping mirrors ENEMY.C:1066-1115's switch on curlib->exptype,
    // resolved through ANIMS.C:187-199's ANIMS_Register table:
    //   EXP_AIRSMALL1 → A_MED_AIR_EXPLO  → EXPLO2_BLK (13)
    //   EXP_AIRMED    → A_LARGE_AIR_EXPLO→ LGFLAK_BLK (12)
    //   EXP_AIRLARGE  → A_LARGE_AIR_EXPLO→ LGFLAK_BLK (12)
    //   EXP_AIRSMALL2 → A_MED_AIR_EXPLO2 → SMFLAK_BLK (14)
    //   EXP_ENERGY    → A_ENERGY_AIR_EXPLO→ NRGBANG_BLK (12)
    private static readonly (string Family, int Frames)[] ExpAnim = new (string, int)[]
    {
        ("EXPLO2_BLK",  13),  // 0 EXP_AIRSMALL1
        ("LGFLAK_BLK",  12),  // 1 EXP_AIRMED
        ("LGFLAK_BLK",  12),  // 2 EXP_AIRLARGE
        ("GEXPLO_BLK",  42),  // 3 EXP_GRDSMALL
        ("GEXPLO_BLK",  42),  // 4 EXP_GRDMED
        ("GEXPLO_BLK",  42),  // 5 EXP_GRDLARGE
        ("EXPLO2_BLK",  13),  // 6 (unused)
        ("EXPLO2_BLK",  13),  // 7 (unused)
        ("NRGBANG_BLK", 12),  // 8 EXP_ENERGY
        ("EXPLO2_BLK",  13),  // 9 (unused)
        ("SMFLAK_BLK",  14),  // 10 EXP_AIRSMALL2
    };
    private readonly Dictionary<(string, int), Texture2D?> _blkCache = new();
    private string? _blkRoot;
    private readonly Dictionary<int, Texture2D?> _tileCache = new();
    private string? _tilesRoot;
    private Texture2D? _enemyBulletTex;
    private Texture2D? _playerBulletTex;
    private readonly Dictionary<(string Family, int Frame), Texture2D?> _playerBulletFrames = new();
    // Per-EnemyShotType BLK animation. Each entry is the multi-frame sequence
    // ESHOT.C ESHOT_Init builds (ESHOT_BLK has 2 frames, EMISLE_BLK has 2,
    // ELASER_BLK has 4, MINE_BLK has 2). Null entries fall back to ESHOT_BLK_00.
    private readonly Texture2D?[][] _shotTypeFrames = new Texture2D?[9][];
    // Smoke-trail puffs trailing missile-type bullets. Sim doesn't emit
    // smoke entities (would need a new collection); we synthesise a short
    // history by drawing 4 SMOKTRAL_BLK frames stacked behind the missile.
    private readonly Texture2D?[] _smokeFrames = new Texture2D?[4];
    private readonly Texture2D?[] _bonusGlowFrames = new Texture2D?[4];
    // Score-digit sprites: numbers[0..9] = N0..N9, numbers[10] = N$.
    private readonly Texture2D?[] _digitTex = new Texture2D?[11];

    public override void _Ready()
    {
        var wc = GetNodeOrNull<WaveController>("../WaveController");
        if (wc != null) _wave = wc;

        var menu = GetNodeOrNull<MenuController>("../MenuController");
        if (menu != null) _menu = menu.Menu;

        _interactiveUi = string.IsNullOrEmpty(OS.GetEnvironment("RAPTOR_PLAYTHROUGH"))
                         || OS.GetEnvironment("RAPTOR_RENDER_MENUS") == "1";

        _shotDir = OS.GetEnvironment("RAPTOR_SHOT_DIR");
        if (!string.IsNullOrEmpty(_shotDir))
        {
            DirAccess.MakeDirRecursiveAbsolute(_shotDir);
            _shotMapPath = Path.Combine(_shotDir, "shot_map.tsv");
            File.WriteAllText(_shotMapPath,
                "file\tsaved_fc\tdrawn_fc\tdrawn_iter\tscore\tshield\tenemies\tpbullets\tebullets\n");
        }

        BuildSpriteIndex();
        _agxRoot = ProjectSettings.GlobalizePath("res://assets/agx");
        _tilesRoot = ProjectSettings.GlobalizePath("res://assets/tiles");
        // Bullet sprites: first frame of each animated _BLK sequence.
        // ESHOT.C ESHOT_Init: enemy "ES_ATPLAYER/ATDOWN/ANGLELEFT/ANGLERIGHT"
        // bullets all use cur->item = ESHOT_BLK. Player forward gun uses NMSHOT_BLK
        // (SHOTS.C: slib[S_FORWARD_GUNS].lumpnum = NMSHOT_BLK).
        string bulletsRoot = ProjectSettings.GlobalizePath("res://assets/bullets");
        _enemyBulletTex  = LoadSpriteFromPath(Path.Combine(bulletsRoot, "ESHOT_BLK_00.png"));
        _playerBulletTex = LoadSpriteFromPath(Path.Combine(bulletsRoot, "NMSHOT_BLK_00.png"));
        _blkRoot = bulletsRoot;  // _BLK sprite frames live under assets/bullets/
        // ESHOT.C ESHOT_Init: each shoot type binds to a specific BLK library.
        // num_frames per library: ESHOT_BLK=2, EMISLE_BLK=2, MINE_BLK=2,
        // ELASER_BLK=4. Per-tick shot->curframe++ → modulo num_frames gives
        // the visible bullet animation (ESHOT_BLK is the visible size oscillation).
        var eshotFrames = LoadBlkSeries(bulletsRoot, "ESHOT_BLK", 2);
        _shotTypeFrames[(int)EnemyShotType.AtPlayer]  = eshotFrames;
        _shotTypeFrames[(int)EnemyShotType.AtDown]    = eshotFrames;
        _shotTypeFrames[(int)EnemyShotType.AngleLeft] = eshotFrames;
        _shotTypeFrames[(int)EnemyShotType.AngleRight]= eshotFrames;
        _shotTypeFrames[(int)EnemyShotType.Missile]   = LoadBlkSeries(bulletsRoot, "EMISLE_BLK", 2);
        _shotTypeFrames[(int)EnemyShotType.Mines]     = LoadBlkSeries(bulletsRoot, "MINE_BLK",   2);
        _shotTypeFrames[(int)EnemyShotType.Laser]     = LoadBlkSeries(bulletsRoot, "ELASER_BLK", 4);
        // Smoke trail for ES_MISSLE — A_SMALL_SMOKE_UP = SMOKTRAL_BLK (4 frames,
        // ANIMS.C:203). Drawn behind the missile to approximate the smoke
        // entities C spawns every other tick via ANIMS_StartAAnim.
        for (int i = 0; i < 4; i++)
            _smokeFrames[i] = LoadSpriteFromPath(Path.Combine(bulletsRoot, $"SMOKTRAL_BLK_{i:D2}.png"));
        for (int i = 0; i < 4; i++)
            _bonusGlowFrames[i] = LoadSpriteFromPath(Path.Combine(bulletsRoot, $"ICNGLW_BLK_{i:D2}.png"));

        // Score-digit sprite array (RAP.C: numbers[0..10] = N0..N9 + $).
        string spritesRoot = ProjectSettings.GlobalizePath("res://assets/sprites");
        for (int i = 0; i <= 9; i++)
            _digitTex[i] = LoadSpriteFromPath(Path.Combine(spritesRoot, $"{i + 1:D4}_N{i}_PIC.png"));
        _digitTex[10] = LoadSpriteFromPath(Path.Combine(spritesRoot, "0011_N$_PIC.png"));
        // Player has 7 LPLAYER_PIC frames (0058..0064) for the bank angles
        // when steering left/right. Index 3 (0061) is neutral (playerbasepic).
        for (int i = 0; i < 7; i++)
            _playerTex[i] = LoadSpriteFromPath(
                Path.Combine(spritesRoot, $"{58 + i:D4}_LPLAYER_PIC.png"));

        ZIndex = 100;

        // Additive-blend layer for engine flames. Drawing white quads here
        // brightens the underlying scene rather than overwriting it.
        _flameLayer = new Node2D { Name = "FlameLayer", ZIndex = 50 };
        var mat = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
        _flameLayer.Material = mat;
        _flameLayer.Draw += OnFlameLayerDraw;
        AddChild(_flameLayer);
    }

    private void OnFlameLayerDraw()
    {
        if (_flameLayer == null) return;
        foreach (var (rect, col) in _flameQuads)
            _flameLayer.DrawRect(rect, col);
    }

    private void BuildSpriteIndex()
    {
        // Files look like "0303_SHIP01G1_PIC.png". Sequential numeric prefixes
        // for the same iname are successive frames of that sprite's animation
        // (e.g. SHIP07G1_PIC has frame 0 at index 309 and frame 1 at 310).
        string root = ProjectSettings.GlobalizePath("res://assets/sprites");
        if (!Directory.Exists(root)) return;

        foreach (string path in Directory.GetFiles(root, "*.png").OrderBy(s => s))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int u = name.IndexOf('_');
            if (u < 0) continue;
            string iname = name.Substring(u + 1);
            if (!_spritePaths.ContainsKey(iname)) _spritePaths[iname] = path;
            if (!_spritePathsAll.TryGetValue(iname, out var list))
            {
                list = new List<string>();
                _spritePathsAll[iname] = list;
            }
            list.Add(path);
        }
    }

    private Texture2D? LoadSprite(string iname)
    {
        if (_spriteCache.TryGetValue(iname, out var cached)) return cached;
        if (!_spritePaths.TryGetValue(iname, out var path)) return null;
        return LoadSpriteFromPath(path);
    }

    /// <summary>
    /// Load the Nth frame of a multi-frame enemy sprite (e.g. helicopter rotor
    /// animation). Falls back to frame 0 if the requested frame doesn't exist.
    /// </summary>
    private Texture2D? LoadSpriteFrame(string iname, int frame)
    {
        if (!_spritePathsAll.TryGetValue(iname, out var list) || list.Count == 0)
            return LoadSprite(iname);
        if (frame < 0 || frame >= list.Count) frame = 0;
        return LoadSpriteFromPath(list[frame]);
    }

    /// <summary>
    /// Pick the current animation frame for a multi-frame enemy sprite.
    /// Mirrors C ENEMY.C:727-774 frame-rate timer + curframe advance + rewind.
    /// Derived from SimClock.Frame so all enemies of the same type animate in
    /// sync — adequate for the helicopter rotor effect.
    /// </summary>
    private static int EnemyFrameIndex(SpriteMeta meta)
    {
        if (meta.NumFrames <= 1) return 0;
        int period = meta.FrameRate + 1;             // ticks per frame
        int step = SimClock.Frame / System.Math.Max(1, period);
        // C ENEMY.C:740 — when curframe >= num_frames, curframe -= rewind.
        // For most enemies rewind==num_frames so this is plain modulo.
        // For rewind < num_frames the cycle has a tail that holds the last
        // (num_frames - rewind) frames; the simple modulo is the common case.
        int rewind = System.Math.Max(1, meta.Rewind);
        if (rewind >= meta.NumFrames) return step % meta.NumFrames;
        // General case: a frame sequence of length (num_frames + rewind*k)
        // doesn't repeat cleanly — approximate with modulo on num_frames.
        return step % meta.NumFrames;
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

    /// <summary>
    /// Smoke trail behind a missile bullet. Mirrors ESHOT.C:536 — when
    /// smokeflag is set and cnt&amp;1, the C version calls ANIMS_StartAAnim(
    /// A_SMALL_SMOKE_UP, shot->x+xoff, shot->y). The actual ANIMS would
    /// spawn a SMOKTRAL_BLK that ages 4 frames upward. We approximate by
    /// stacking the 4 frames in place every tick — fixed positions behind
    /// the missile, fading alpha — so the trail reads correctly without
    /// porting the full ANIMS smoke system.
    /// </summary>
    private void DrawMissileSmoke(BulletLogic b)
    {
        // Smoke puffs sit behind the missile (toward the top of screen,
        // since missiles fall downward). Step of 4 px between puffs. C's
        // ANIMS_StartAAnim renders SMOKTRAL_BLK with GFX_ShadeShape(LIGHT,...)
        // — a faint transparent puff. Keep alpha low so the trail reads as
        // a hint of exhaust, not a stack of mini-missiles.
        for (int i = 0; i < 4; i++)
        {
            var tex = _smokeFrames[i];
            if (tex == null) continue;
            float alpha = 0.22f - 0.05f * i;
            int sx = b.X + 4 - (int)tex.GetWidth() / 2;
            int sy = b.Y - 6 - i * 4;
            DrawTexture(tex, new Vector2(sx, sy), new Color(1, 1, 1, alpha));
        }
    }

    private Texture2D?[] LoadBlkSeries(string root, string family, int count)
    {
        var frames = new Texture2D?[count];
        for (int i = 0; i < count; i++)
            frames[i] = LoadSpriteFromPath(Path.Combine(root, $"{family}_{i:D2}.png"));
        return frames;
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
        _flameLayer?.QueueRedraw();
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

    private async void WriteShotBurst(int seq, int off, string label)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var img = GetViewport().GetTexture().GetImage();
        if (img == null) return;
        // Include abs FC in the filename so labeled dumps interleave correctly
        // with periodic fcNNNNN_secNNN.png dumps when sorted alphabetically.
        int fc = SimClock.Frame;
        string path = off == 0
            ? $"{_shotDir}/fc{fc:D5}_label_{label}.png"
            : $"{_shotDir}/fc{fc:D5}_label_{label}_p{off:D2}.png";
        img.SavePng(path);
        AppendShotMap(path, fc);
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

    private async void WriteShot(int sec, int fc)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var img = GetViewport().GetTexture().GetImage();
        if (img == null) return;
        fc = SimClock.Frame;
        sec = fc / 70;
        string path = $"{_shotDir}/fc{fc:D5}_sec{sec:D3}.png";
        img.SavePng(path);
        AppendShotMap(path, fc);
    }

    private void AppendShotMap(string path, int savedFc)
    {
        if (string.IsNullOrEmpty(_shotMapPath)) return;
        string fileName = Path.GetFileName(path);
        File.AppendAllText(_shotMapPath,
            $"{fileName}\t{savedFc}\t{_lastDrawnFc}\t{_lastDrawnIter}\t{_lastDrawnScore}\t{_lastDrawnShield}\t{_lastDrawnEnemies}\t{_lastDrawnPbullets}\t{_lastDrawnEbullets}\n");
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
                int flats = _wave.RenderedFlatFor(mapspot);
                var tex = LoadTile(t.FGame, flats);
                if (tex != null) DrawTexture(tex, new Vector2(x, y));
            }
        }
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(0, 0, 320, 200), new Color(0, 0, 0, 1));

        if (_menu != null && ShouldDrawMenuOverlayForState(
            _interactiveUi,
            _menu.InGame,
            _wave?.GameplayVisualActive == true))
        {
            // Menus paint on a black background; the in-game playfield tint
            // is irrelevant here and would bleed through transparent areas
            // of menu sprites (SHIPCOMP_PIC's display window, etc.).
            DrawMenuOverlay(_menu);
            RecordDrawnState();
            return;
        }

        // In-game playfield gets the dark-blue base behind the tile map.
        DrawRect(new Rect2(16, 0, 288, 200), new Color(0.05f, 0.05f, 0.1f, 1));

        if (_wave == null)
        {
            RecordDrawnState();
            return;
        }

        _flameQuads.Clear();
        DrawTileMap();

        var px = _wave.PlayerLogic.X;
        var py = _wave.PlayerLogic.Y;
        int pic = _wave.PlayerLogic.Pic;
        if (pic < 0) pic = 0; else if (pic > 6) pic = 6;
        var playerTex = _playerTex[pic];
        const int PlayerW = 32, PlayerH = 32;

        // Ground shadows: SHADOW_GAdd in C (SHADOWS.C:177-193) stores at
        // (x-3, y+4) and SHADOW_DisplayGround draws via GFX_ShadeShape(DARK,...).
        // Unlike sky shadows, the ground variant has no 3D projection — the
        // shadow is the sprite silhouette darkened at a small offset.
        foreach (var e in _wave.GetEnemies())
        {
            if (!e.Alive) continue;
            if (e.Meta.Shadow == 0 || !e.IsGround) continue;
            var tex = LoadSprite(e.Meta.IName);
            if (tex == null) continue;
            DrawGroundShadow(tex, e.X, e.Y);
        }

        // Sky shadows mirror C's render order: TILE_Display → SHADOW_DisplaySky
        // → ENEMY_DisplaySky → player. RAP.C also adds the player shadow with
        // SHADOW_Add before the sky pass (RAP.C:1060), so we draw it here too.
        foreach (var e in _wave.GetEnemies())
        {
            if (!e.Alive) continue;
            if (e.Meta.Shadow == 0 || e.IsGround) continue;
            var tex = LoadSprite(e.Meta.IName);
            if (tex == null) continue;
            DrawSkyShadow(tex, e.X, e.Y, e.HalfW * 2, e.HalfH * 2);
        }
        if (_wave.DrawPlayer && playerTex != null)
            DrawSkyShadow(playerTex, px, py, PlayerW, PlayerH);

        // C's eframe ^= 1 per ENEMY_DisplaySky call (one per sim tick).
        // Derive from SimClock.Frame parity so the view doesn't mutate sim state.
        int eframe = SimClock.Frame & 1;
        foreach (var e in _wave.GetEnemies())
        {
            if (!e.Alive) continue;
            // Multi-frame sprites (helicopter rotor, etc.) cycle frames per C
            // ENEMY.C:727 logic — pick the right one for this tick.
            int frameIdx = EnemyFrameIndex(e.Meta);
            var tex = frameIdx > 0
                ? LoadSpriteFrame(e.Meta.IName, frameIdx)
                : LoadSprite(e.Meta.IName);
            if (tex != null)
            {
                DrawWorldTexture(tex, e.X, e.Y);
            }
            else
            {
                DrawRect(new Rect2(e.X, e.Y, e.HalfW * 2, e.HalfH * 2),
                    new Color(1, 0.3f, 0.3f, 0.7f));
            }
            // Sky enemies get engine-flame puffs trailing upward (toward top of
            // screen, since ships fly downward). Ground enemies (groundflag != 0)
            // never call FLAME_Up in C.
            if (!e.IsGround && e.Meta.NumEngs > 0)
                DrawEngineFlames(e, eframe);
        }

        // Sim X/Y are TOP-LEFT (matching C's sprite->x/y semantics — see ENEMY_Add
        // comments in WaveController). C's GFX_PutSprite renders at top-left, so
        // we draw directly at (X, Y) without subtracting half-size. The player
        // is drawn AFTER enemies in C (RAP.C:1077), so it occludes them.
        if (_wave.DrawPlayer && playerTex != null)
        {
            // Player engine flames — FLAME_Down at (player_cx ± o_engine[pic] - {3,2},
            // player_cy + 15) in C (RAP.C:1075-1076).
            int pcx = px + PlayerW / 2;
            int pcy = py + PlayerH / 2;
            int oeng = OEngine[pic];
            DrawFlameDown(pcx - oeng - 3, pcy + 15, 4, eframe);
            DrawFlameDown(pcx + oeng - 2, pcy + 15, 4, eframe);
            DrawTexture(playerTex, new Vector2(px, py));
        }
        else if (_wave.DrawPlayer)
        {
            DrawRect(new Rect2(px, py, 32, 32), new Color(0, 1, 0, 0.7f));
        }

        // Bullet sim X/Y are also top-left (ESHOT_Shoot: cur->move.x -= xoff).
        // Pick a per-type sprite + per-tick animation frame so the yellow
        // ESHOT diamond visibly oscillates (ESHOT_BLK has 2 frames cycling).
        // Sim uses a fixed BulletXOff/YOff = 2, but C uses h->width>>1 /
        // h->height>>1 (= 4 for 8×8 sprites, 4×8 for the 8×16 EMISLE). The
        // view compensates with a half-size delta so each sprite's centre
        // lines up with where C would draw it.
        const int SimBulletXOff = 2, SimBulletYOff = 2;
        foreach (var b in _wave.GetEnemyBullets())
        {
            if (!b.Alive) continue;
            // ES_LASER beam column (ESHOT.C:558-573). Draws ELASER_BLK every 3px
            // from shot.y down to move.y2 (exclusive), the ELASEPOW_BLK power glow
            // at the gun (shot.x, shot.y), and the DRAYHIT_BLK impact sprite at
            // (shot.x - h.width/4, move.y2 - 8) when that y is on-screen.
            // Parity-inert: ES_LASER is secret-cheat-gated and never fires in any
            // parity/test scenario; this replaces only the laser draw.
            if (b.IsEnemyLaser)
            {
                int fi = System.Math.Clamp(b.FrameCounter - 1, 0, 3); // C: curframe-1
                var colTex = LoadBlkFrame("ELASER_BLK", fi);
                if (colTex != null)
                    foreach (int ly in LaserBeam.ColumnYs(b.Y, b.Y2))
                        DrawWorldTexture(colTex, b.X, ly);
                var powTex = LoadBlkFrame("ELASEPOW_BLK", fi);
                if (powTex != null) DrawWorldTexture(powTex, b.X, b.Y);
                var hitTex = LoadBlkFrame("DRAYHIT_BLK", fi);
                if (hitTex != null)
                {
                    int hy = b.Y2 - 8;
                    if (hy > 0 && hy < 200)
                        DrawWorldTexture(hitTex, b.X - (int)hitTex.GetWidth() / 4, hy);
                }
                continue;
            }
            int ti = (int)b.ShotType;
            var frames = (ti >= 0 && ti < _shotTypeFrames.Length) ? _shotTypeFrames[ti] : null;
            Texture2D? tex = null;
            if (frames != null && frames.Length > 0)
                tex = frames[b.FrameCounter % frames.Length];
            tex ??= _enemyBulletTex;
            if (tex != null)
            {
                int dx = (int)tex.GetWidth()  / 2 - SimBulletXOff;
                int dy = (int)tex.GetHeight() / 2 - SimBulletYOff;
                DrawWorldTexture(tex, b.X - dx, b.Y - dy);
            }
            else
                DrawWorldRect(b.X, b.Y, 4, 4, new Color(1, 1, 0));
            // Smoke trail for missiles (ESHOT.C:536 — every other tick when
            // smokeflag is set). Approximate by stacking 4 SMOKTRAL frames
            // above the missile with fading alpha.
        }

        foreach (var b in _wave.GetPlayerBullets())
        {
            if (!b.Alive) continue;
            Texture2D? tex = b.PlayerWeapon is ObjType weapon
                ? LoadPlayerBulletTexture(weapon, b.FrameCounter)
                : _playerBulletTex;
            if (tex != null)
                DrawWorldTexture(tex, b.X, b.Y);
            else
                DrawWorldRect(b.X, b.Y, 4, 4, new Color(0, 1, 1));
        }

        DrawBonuses();

        // Spawn muzzle-flash cosmetics once per sim tick.
        if (_lastMuzzleFrame != SimClock.Frame)
        {
            _lastMuzzleFrame = SimClock.Frame;
            int spawnIter = _wave.GameLoopIter;
            foreach (var m in _wave.MuzzlesThisTick)
                _effects.Spawn("GUNSTR_BLK", totalFrames: 4, x: m.X, y: m.Y, spawnIter: spawnIter, ground: false);
            // Megabomb detonation: consume the one-shot sim signal exactly once
            // per tick, start the white-out flash, and spawn the SHIPGLOW_BLK
            // glow (drawn through DrawViewEffects). Mirrors C SHOTS.C:1273-1274's
            // startfadeflag + ANIMS_StartAnim(A_SUPER_SHIELD) + RAP.C:1127 fade.
            if (_wave.ConsumeMegaBombDetonated())
            {
                _megaFadeStartIter = _wave.GameLoopIter;
                _effects.Spawn("SHIPGLOW_BLK", totalFrames: 4, x: 160, y: 100, spawnIter: _wave.GameLoopIter, ground: false);
            }
            // Boss low-health smoke: ENEMY.C:1076-1085 — a boss with hits<50 emits
            // A_SMALL_AIR_EXPLO (SMFLAK_BLK, 14 frames) every other game-loop pass
            // (gl_cnt & 2) at a within-bounds offset. Under the deterministic RNG
            // random(n)==n/2, so the offset is (width/2, height/2) — no RNG draw.
            // Parity-inert View cosmetic: spawned into _effects, never _explosions.
            int glCnt = _wave.GameLoopIter;
            foreach (var e in _wave.GetEnemies())
            {
                if (!e.IsBoss) continue;
                if (!BossSmoke.ShouldSpawn(e.Hits, glCnt)) continue;
                var (sx, sy) = BossSmoke.SpawnPoint(e.X, e.Y, e.Meta.Width, e.Meta.Height);
                _effects.Spawn("SMFLAK_BLK", totalFrames: 14, x: sx, y: sy, spawnIter: glCnt, ground: false);
            }
            _effects.Prune(_wave.GameLoopIter);
        }

        DrawExplosions();
        DrawViewEffects();
        DrawScoreHud();
        DrawShieldHud();
        DrawCurrentWeaponHud();
        DrawMegaBombHud();
        DrawScannerHud();
        DrawWarningHud();

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

        // Megabomb white-out flash sits over everything (HUD included), matching
        // C's full-screen GFX_FadeOut after detonation.
        DrawMegaBombFlash();
        RecordDrawnState();
    }

    /// <summary>
    /// Full-screen white-out overlay for a megabomb detonation. C does a palette
    /// fade toward near-white (GFX_FadeOut(63,60,60,1), RAP.C:1127); we approximate
    /// with a fading white quad over the playfield. Parity-inert: driven purely by
    /// the consumed sim signal, touches no sim state.
    /// </summary>
    private void DrawMegaBombFlash()
    {
        if (_wave == null) return;
        int age = _wave.GameLoopIter - _megaFadeStartIter;
        if (age < 0 || age >= MegaFadeFrames) return;
        // White-out toward (63,60,60)/63 ≈ (1,0.95,0.95), strongest at age 0, easing out.
        float t = 1f - (age / (float)MegaFadeFrames);
        DrawRect(new Rect2(0, 0, 320, 200), new Color(1f, 0.95f, 0.95f, MegaFadeMaxAlpha * t));
    }

    private void RecordDrawnState()
    {
        _lastDrawnFc = SimClock.Frame;
        if (_wave == null)
        {
            _lastDrawnIter = -1;
            _lastDrawnScore = 0;
            _lastDrawnShield = 0;
            _lastDrawnEnemies = 0;
            _lastDrawnPbullets = 0;
            _lastDrawnEbullets = 0;
            return;
        }

        _lastDrawnIter = _wave.GameLoopIter <= 0 ? -1 : _wave.GameLoopIter - 1;
        _lastDrawnScore = _wave.Score;
        _lastDrawnShield = _wave.PlayerLogic.Shield;
        _lastDrawnEnemies = _wave.GetEnemies().Count;
        _lastDrawnPbullets = _wave.GetPlayerBullets().Count;
        _lastDrawnEbullets = _wave.GetEnemyBullets().Count;
    }

    public static bool ShouldDrawMenuOverlayForState(
        bool interactiveUi,
        bool menuInGame,
        bool gameplayVisualActive)
    {
        return interactiveUi && !menuInGame && !gameplayVisualActive;
    }

    private void DrawMenuOverlay(MenuStateMachine menu)
    {
        var font = ThemeDB.FallbackFont;
        DrawRect(new Rect2(0, 0, 320, 200), Colors.Black);

        // BACKGRND_PIC (stars + Earth) only belongs behind the main menu.
        // Hangar and ship-computer screens own fullscreen sprites that may
        // have transparent regions — in C those reveal the cleared-to-black
        // framebuffer (GFX_FadeOut before SHIPCOMP_SWD), not the title art.
        bool isMainMenu = menu.State == WinState.Menu
                          && menu.PilotCreateStep == 0
                          && !menu.InSectorSelect;
        bool isMenuVisual = isMainMenu
                            || (menu.State == WinState.Unknown && !menu.InSectorSelect);
        if (isMenuVisual)
            DrawUiSprite(MenuChrome.Background);

        if (menu.State == WinState.Hangar)
        {
            DrawUiSprite(MenuChrome.Hangar);
            if ((SimClock.Frame / 5) % 3 == 0)
                DrawUiSprite(MenuChrome.HangarPilot);
            DrawHangarOverlay(font, menu);
            if (menu.InAskBool)
                DrawAskBoolOverlay(menu);
            return;
        }

        if (menu.State == WinState.Death)
        {
            DrawDeathMovieOverlay(menu);
            return;
        }

        if (menu.InSectorSelect)
        {
            DrawShipComputerOverlay(font);
            return;
        }

        if (menu.State == WinState.Store && menu.Store != null)
        {
            DrawStoreOverlay(menu.Store);
            return;
        }

        if (menu.State == WinState.Help)
        {
            DrawHelpOverlay(menu);
            return;
        }

        if (menu.State == WinState.Credits)
        {
            DrawCreditsOverlay(font);
            return;
        }

        if (menu.PilotCreateStep > 0)
        {
            if (menu.PilotCreateStep == 3)
                DrawDifficultyOverlay(font, menu);
            else
                DrawRegisterOverlay(font, menu);
            return;
        }

        DrawMainMenuOverlay(menu);
        if (menu.InLoadMission)
            DrawLoadMissionOverlay(menu);
        if (menu.InOptions)
            DrawOptionsOverlay(menu);
        if (menu.InAskBool)
            DrawAskBoolOverlay(menu);
        if (menu.InWinMsg)
            DrawWinMsgOverlay(menu);
    }

    private void DrawDeathMovieOverlay(MenuStateMachine menu)
    {
        if (string.IsNullOrEmpty(_agxRoot)) return;

        int elapsed = SimClock.Frame - menu.StateEnteredFrame;
        string? path = null;
        float alpha = 1f;
        if (AgxMovieSequence.TrySelectDeathFrame(_agxRoot, elapsed, out var frame))
        {
            path = frame.Path;
        }
        else if (elapsed < AgxMovieSequence.DeathTotalFrames)
        {
            path = Path.Combine(_agxRoot, $"SDEATH_AGX_{AgxMovieSequence.DeathGroundFrames - 1:D2}.png");
            int fadeElapsed = elapsed - AgxMovieSequence.DeathContentFrames;
            alpha = 1f - System.Math.Clamp(fadeElapsed / (float)AgxMovieSequence.DeathFadeOutFrames, 0f, 1f);
        }

        if (path == null) return;
        var tex = LoadSpriteFromPath(path);
        if (tex != null)
            DrawTexture(tex, Vector2.Zero, new Color(1f, 1f, 1f, alpha));
    }

    private void DrawMainMenuOverlay(MenuStateMachine menu)
    {
        DrawUiSprite(MenuChrome.RaptorLogo);
        DrawUiSprite(MenuChrome.Copyright);
        for (int i = 0; i < MenuChrome.MainVisibleItems.Count; i++)
        {
            var item = MenuChrome.MainVisibleItems[i];
            // C uses GFX_ShadeShape(LIGHT, ...) on the selected SWD field; this
            // approximates the palette-lighten by scaling R/G/B unevenly so the
            // dim orange (146,52,12) maps roughly to the brighter (190,85,44).
            var modulate = i == menu.CurrentItem
                ? new Color(1.30f, 1.60f, 3.30f)
                : Colors.White;
            DrawUiSprite(item, modulate);
        }
    }

    private void DrawOptionsOverlay(MenuStateMachine menu)
    {
        var swd = LoadSwd("OPTS_SWD");
        if (swd == null) return;

        SwdRenderer.Draw(_swdHost, swd);

        if (!menu.OptionDetailHigh)
        {
            var detail = swd.Fields[6];
            DrawDosFont("LOW DETAIL",
                swd.Window.X + detail.X + 27,
                swd.Window.Y + detail.Y + 2,
                detail.FontName,
                detail.FontBaseColor);
        }

        var music = swd.Fields[11];
        var fx = swd.Fields[12];
        DrawUiSprite(MenuChrome.Slider with
        {
            X = swd.Window.X + music.X + menu.OptionMusicVolume - 2,
            Y = swd.Window.Y + music.Y
        });
        DrawUiSprite(MenuChrome.Slider with
        {
            X = swd.Window.X + fx.X + menu.OptionFxVolume - 2,
            Y = swd.Window.Y + fx.Y
        });

        if (menu.OptionsField >= 0 && menu.OptionsField <= 2)
        {
            int fieldIndex = menu.OptionsField switch { 0 => 3, 1 => 4, _ => 5 };
            var target = swd.Fields[fieldIndex];
            DrawUiSprite(MenuChrome.Pointer with
            {
                X = swd.Window.X + target.X,
                Y = swd.Window.Y + target.Y
            });
        }
    }

    private void DrawAskBoolOverlay(MenuStateMachine menu)
    {
        var swd = LoadSwd("ASK_SWD");
        if (swd == null) return;

        int selectedFieldId = menu.AskBoolYesSelected ? 2 : 3; // YES id=2, NO id=3.
        SwdRenderer.Draw(_swdHost, swd, selectedFieldId: selectedFieldId);

        DrawDragBarText(swd, menu.AskBoolQuestion);
    }

    private void DrawWinMsgOverlay(MenuStateMachine menu)
    {
        // C WIN_Msg has its own SWD but it's not extracted; reuse ASK_SWD's
        // dragbar layout for a centered message panel, skipping the YES/NO
        // button fields (indices 6 and 7, ids 2 and 3).
        var swd = LoadSwd("ASK_SWD");
        if (swd == null) return;

        var skip = new HashSet<int> { 6, 7 };
        SwdRenderer.Draw(_swdHost, swd, selectedFieldId: -1, skipFieldIndices: skip);

        DrawDragBarText(swd, menu.WinMsgText);
    }

    /// <summary>
    /// Render text centered inside a window's DRAGBAR field (index 5 in both
    /// ASK_SWD and other DRAGBAR-bearing SWDs). C's SWD_PutField centers the
    /// dragbar title via `text_x = (lx - GFX_StrPixelLen)/2 + fld_x`; without
    /// this Godot draws every AskBool/WinMsg title left-aligned at the field's
    /// x, which had read as a ~46 px (~15%) offset on `02_save_dialog`.
    /// </summary>
    private void DrawDragBarText(SwdWindow swd, string text)
    {
        var dragbar = swd.Fields[5];
        int tw = _swdHost.MeasureText(text, dragbar.FontName);
        int fh = _swdHost.FontHeight(dragbar.FontName);
        int x = swd.Window.X + dragbar.X + (dragbar.Lx - tw) / 2;
        int y = swd.Window.Y + dragbar.Y + (dragbar.Ly - fh) / 2;
        DrawDosFont(text, x, y, dragbar.FontName, dragbar.FontBaseColor);
    }

    private void DrawLoadMissionOverlay(MenuStateMachine menu)
    {
        var swd = LoadSwd("LOAD_SWD");
        if (swd == null) return;

        var skip = new HashSet<int> { 1, 9, 10, 12 };
        SwdRenderer.Draw(_swdHost, swd, selectedFieldId: 2, skipFieldIndices: skip);

        var pilot = menu.LoadMissionPilot;
        if (pilot == null) return;

        var idField = swd.Fields[1];
        string portrait = pilot.IdPic switch
        {
            1 => "BMALE_PIC",
            2 => "WFEMALE_PIC",
            3 => "BFEMALE_PIC",
            _ => "WMALE_PIC",
        };
        var portraitTex = _swdHost.LoadSprite(portrait);
        if (portraitTex != null)
            DrawTexture(portraitTex, new Vector2(swd.Window.X + idField.X, swd.Window.Y + idField.Y));

        var name = swd.Fields[9];
        DrawDosFont(pilot.Name,
            swd.Window.X + name.X,
            swd.Window.Y + name.Y,
            name.FontName,
            name.FontBaseColor);

        var call = swd.Fields[10];
        DrawDosFont(pilot.Callsign,
            swd.Window.X + call.X,
            swd.Window.Y + call.Y,
            call.FontName,
            call.FontBaseColor);

        var credits = swd.Fields[12];
        DrawDosFont(pilot.CreditsText,
            swd.Window.X + credits.X,
            swd.Window.Y + credits.Y,
            credits.FontName,
            credits.FontBaseColor);
    }

    private void DrawRegisterOverlay(Font font, MenuStateMachine menu)
    {
        DrawUiSprite(MenuChrome.Register);
        DrawUiSprite(MenuChrome.RegisterPortrait);
        DrawRegisterFieldText(ThemeDB.FallbackFont, menu);
        // REG_TEXT runtime content is "   CHANGE ID PICTURE" — see
        // WINDOWS.C regtext[1]; draw from the field's actual x=61.
        DrawDosFont("   CHANGE ID PICTURE", 61, 181, "FONT1_FNT", 66);
        // CURSOR_PIC (4-point compass star) at REG_VIEWID center — mirrors C's
        // SWD_SetFieldPtr(window, REG_VIEWID) → PTR_SetPos to the badge center.
        DrawUiSprite(MenuChrome.Cursor with { X = 37, Y = 118 });
    }

    private void DrawRegisterFieldText(Font font, MenuStateMachine menu)
    {
        var ink = Colors.Black;
        if (!string.IsNullOrEmpty(menu.PilotName))
            DrawMenuText(menu.PilotName, 188, 137, 7, ink);
        if (!string.IsNullOrEmpty(menu.Callsign))
            DrawMenuText(menu.Callsign, 188, 153, 7, ink);

        int caretX = 188;
        int caretY = menu.PilotCreateStep == 2 ? 144 : 128;
        string text = menu.PilotCreateStep == 2 ? menu.Callsign : menu.PilotName;
        if (!string.IsNullOrEmpty(text))
            caretX += MenuTextWidth(text, 7) + 1;
        DrawRect(new Rect2(caretX, caretY, 1, 9), ink);
    }

    private void DrawHangarOverlay(Font font, MenuStateMachine menu)
    {
        // C's WIN_Hangar maps HangarPosition -> FLD_VIEWAREA field name in
        // HANGAR_SWD, and the cursor + caption follow the active field:
        //   poslookup[4] = { HANG_MISSION, HANG_SUPPLIES, HANG_MAIN_MENU,
        //                    HANG_QSAVE };
        //   hangtext[4]  = { "FLY MISSION", "SUPPLY ROOM",
        //                    "EXIT HANGAR", "SAVE PILOT" };
        // The FLD_VIEWAREA fields are indices 2/3/4/5 in HANGAR_SWD.json,
        // and the caption goes through HANG_TEXT (index 1).
        var swd = LoadSwd("HANGAR_SWD");
        if (swd == null) return;

        int viewIdx = menu.HangarPosition switch
        {
            0 => 2,  // HANG_MISSION
            1 => 3,  // HANG_SUPPLIES
            2 => 4,  // HANG_MAIN_MENU
            _ => 5,  // HANG_QSAVE
        };
        string caption = menu.HangarPosition switch
        {
            0 => "FLY MISSION",
            1 => "SUPPLY ROOM",
            2 => "EXIT HANGAR",
            _ => "SAVE PILOT",
        };

        // Cursor lands at the active FLD_VIEWAREA's center: PTR_SetPos(
        // fld.x + lx/2, fld.y + ly/2). CURSOR_PIC content centered at
        // sprite-local (7, 8).
        var area = swd.Fields[viewIdx];
        int cx = swd.Window.X + area.X + area.Lx / 2;
        int cy = swd.Window.Y + area.Y + area.Ly / 2;
        DrawUiSprite(MenuChrome.Cursor with { X = cx - 7, Y = cy - 8 });

        // HANG_TEXT field (index 1): FONT1_FNT basecolor=66 at field origin.
        var caption_fld = swd.Fields[1];
        DrawDosFont(caption,
            swd.Window.X + caption_fld.X,
            swd.Window.Y + caption_fld.Y,
            caption_fld.FontName,
            caption_fld.FontBaseColor);
    }

    private void DrawStoreOverlay(StoreLogic store)
    {
        // STORE_SWD: window paints STORE_PIC; we feed it through SwdRenderer
        // and then override the fields whose content C sets dynamically per
        // pilot / per item via SWD_SetFieldText / SWD_SetFieldItem (STOR_*
        // constants in SOURCE/STORE.INC).
        var swd = LoadSwd("STORE_SWD");
        if (swd == null) return;

        // Field indices in STORE_SWD.json (matches STORE.INC):
        //   1 STOR_CALLSIGN  4 STOR_BUY     6 STOR_SELL    9  STOR_COST
        //   5 STOR_BUYIT     7 STOR_COMP    10 STOR_SCORE  12 STOR_TEXTCOST
        //   8 STOR_TEXT      13 STOR_NUM    11 STOR_STATS
        // During the Harrold greeting C blanks the BUY/SELL/BUYIT/PREV/NEXT
        // button items + STAT/TEXTCOST/NUM/COST text (STORE.C:200-214). We
        // mirror by skipping all of those + the SWD's STAT/TEXTCOST defaults.
        var overrides = new System.Collections.Generic.HashSet<int>
            { 1, 4, 5, 6, 7, 9, 10, 11, 12, 13 };
        if (store.ShowingGreeting)
            overrides.UnionWith(new[] { 2, 3 });  // hide PREV / NEXT too
        SwdRenderer.Draw(_swdHost, swd, selectedFieldId: -1,
            skipFieldIndices: overrides);

        bool buyMode = store.CurrentMode == StoreLogic.Mode.Buy;

        // STOR_CALLSIGN (field 1): bare callsign text. Always shown (the
        // pilot card sits outside the Harrold blanking).
        var cs = swd.Fields[1];
        DrawSwdCenteredText(cs, store.Callsign, cs.FontName, cs.FontBaseColor);

        // STOR_SCORE (field 10): 7-digit money "0010000". Always shown.
        var score = swd.Fields[10];
        DrawDosFont(store.Money.ToString("D7"),
            swd.Window.X + score.X, swd.Window.Y + score.Y,
            score.FontName, score.FontBaseColor);

        if (!store.ShowingGreeting)
        {
            // STOR_BUY (field 4): BUYLGT_PIC when buying, BUYDRK_PIC when selling.
            // STOR_SELL (field 6): mirror of STOR_BUY. STOR_BUYIT (field 5):
            // BUYITEM_PIC vs SELLITEM_PIC.
            DrawSwdItemSprite(swd.Fields[4], buyMode ? "BUYLGT_PIC" : "BUYDRK_PIC");
            DrawSwdItemSprite(swd.Fields[6], buyMode ? "SELLDRK_PIC" : "SELLGT_PIC");
            DrawSwdItemSprite(swd.Fields[5], buyMode ? "BUYITEM_PIC" : "SELLITEM_PIC");

            // STOR_STATS (field 11) "YOU HAVE" (SWD default text).
            var stats = swd.Fields[11];
            if (stats.Text.Length > 0)
                DrawDosFont(stats.Text,
                    swd.Window.X + stats.X, swd.Window.Y + stats.Y,
                    stats.FontName, stats.FontBaseColor);

            // STOR_TEXTCOST (field 12): "COST" or "RESALE" per saying[mode].
            var textcost = swd.Fields[12];
            DrawDosFont(buyMode ? "COST" : "RESALE",
                swd.Window.X + textcost.X, swd.Window.Y + textcost.Y,
                textcost.FontName, textcost.FontBaseColor);

            // STOR_COST (field 9): cost / resale value.
            var cost = swd.Fields[9];
            DrawDosFont(store.CurrentCost.ToString("D2"),
                swd.Window.X + cost.X, swd.Window.Y + cost.Y,
                cost.FontName, cost.FontBaseColor);

            // STOR_NUM (field 13): owned count.
            var num = swd.Fields[13];
            DrawDosFont(store.OwnedCount.ToString("D2"),
                swd.Window.X + num.X, swd.Window.Y + num.Y,
                num.FontName, num.FontBaseColor);
        }

        // STOR_COMP (field 7): center display. Showing HAR1_TXT on entry
        // until any nav input, then the current item's ITEM??_TXT.
        var comp = swd.Fields[7];
        string? streamText;
        if (store.ShowingGreeting)
            streamText = SwdTextStream.LoadText("HAR1_TXT");
        else
        {
            var obj = store.CurrentObject;
            streamText = obj is ObjType t
                ? SwdTextStream.LoadText($"ITEM{(int)t:D2}_TXT")
                : null;
        }
        if (streamText != null)
            SwdTextStream.Render(_swdHost, streamText,
                swd.Window.X + comp.X, swd.Window.Y + comp.Y,
                comp.Lx, comp.Ly,
                comp.FontName, comp.FontBaseColor);
    }

    private void DrawSwdItemSprite(SwdWindow.Field f, string itemName)
    {
        var tex = _swdHost.LoadSprite(itemName);
        if (tex != null)
            DrawTexture(tex, new Vector2(f.X, f.Y));
    }

    private void DrawSwdCenteredText(SwdWindow.Field f, string text, string fontName, int basecolor)
    {
        var font = LoadBitmapFont(fontName);
        if (font == null) return;
        int tw = font.Measure(text);
        int fh = font.Height;
        int x = f.X + (f.Lx - tw) / 2;
        int y = f.Y + (f.Ly - fh) / 2;
        DrawDosFont(text, x, y, fontName, basecolor);
    }

    private void DrawShipComputerOverlay(Font font)
    {
        // Drive entirely off SHIPCOMP_SWD: window paints SHIPCOMP_PIC, the
        // SWD fields handle the sector buttons (FONT1_FNT, FLD_BUTTON
        // INVISABLE = text-only), the indicator lights (LIGHTOFF_PIC at the
        // three FLD_ICON slots), and the action buttons (BUTTON1-4_PIC).
        // AUTO-PILOT field already carries basecolor=64 in the SWD (vs the
        // other sectors' 82), so the brighter palette ramp comes through.
        var swd = LoadSwd("SHIPCOMP_SWD");
        if (swd == null) return;

        // WIN_ShipComp does SWD_SetActiveField(SHIPCOMP_SWD, COMP_AUTO)
        // followed by PTR_SetPos to its center. COMP_AUTO is field id=4.
        const int CompAutoId = 4;
        SwdRenderer.Draw(_swdHost, swd, selectedFieldId: CompAutoId);

        foreach (var f in swd.Fields)
        {
            if (f.Id != CompAutoId) continue;
            int cx = swd.Window.X + f.X + f.Lx / 2;
            int cy = swd.Window.Y + f.Y + f.Ly / 2;
            DrawUiSprite(MenuChrome.Cursor with { X = cx - 7, Y = cy - 8 });
            break;
        }
    }

    private void DrawDifficultyOverlay(Font font, MenuStateMachine menu)
    {
        // In C, ASKDIFF is pushed on top of REGISTER — the registration page
        // (badge, bottom prompt) stays visible underneath. The mouse cursor
        // moves to the active field's center per WIN_AskDiff:
        //   SWD_SetActiveField(ASKDIFF_SWD, OKREG_MED);
        //   SWD_GetFieldXYL(...); PTR_SetPos(px+lx/2, py+ly/2);
        DrawUiSprite(MenuChrome.Register);
        DrawUiSprite(MenuChrome.RegisterPortrait);
        // REG_TEXT field is at x=61, lx=181. The runtime sets the text to
        // regtext[1] = "   CHANGE ID PICTURE" (3 leading spaces). Each space
        // advances by width(9) + fontspacing(1) = 10, so 'C' lands at x=91.
        DrawDosFont("   CHANGE ID PICTURE", 61, 181, "FONT1_FNT", 66);

        var swd = LoadSwd("ASKDIFF_SWD");
        if (swd != null)
        {
            // OKREG_MED == field index 8 (id=3) → VETERAN. C's default new
            // pilot starts there. SWD_PutField applies GFX_ShadeShape(LIGHT)
            // on the active field; our SwdRenderer mirrors that via the
            // per-channel modulate.
            SwdRenderer.Draw(_swdHost, swd, selectedFieldId: menu.DifficultyFieldId);

            foreach (var f in swd.Fields)
            {
                if (f.Id != menu.DifficultyFieldId) continue;
                int cx = swd.Window.X + f.X + f.Lx / 2;
                int cy = swd.Window.Y + f.Y + f.Ly / 2;
                DrawUiSprite(MenuChrome.Cursor with { X = cx - 7, Y = cy - 8 });
                break;
            }
        }
    }

    private void DrawHelpOverlay(MenuStateMachine menu)
    {
        var swd = LoadSwd("HELP_SWD");
        if (swd == null) return;

        var skip = menu.HelpTextName == "RAP1_TXT"
            ? new HashSet<int> { 8, 9 }
            : new HashSet<int> { 8 };
        SwdRenderer.Draw(_swdHost, swd, skipFieldIndices: skip);

        if (menu.HelpTextName == "RAP1_TXT")
            DrawDosFont("PAGE : 31", swd.Window.X + 247, swd.Window.Y + 10, "FONT2_FNT", 64);

        string? text = SwdTextStream.LoadText(menu.HelpTextName);
        if (string.IsNullOrEmpty(text)) return;

        var field = swd.Fields[8];
        SwdTextStream.Render(_swdHost, text,
            swd.Window.X + field.X,
            swd.Window.Y + field.Y,
            field.Lx,
            field.Ly,
            field.FontName,
            field.FontBaseColor);
    }

    private void DrawCreditsOverlay(Font font)
    {
        var swd = LoadSwd("CREDIT_SWD");
        if (swd == null) return;

        var skip = new HashSet<int> { 0 };
        SwdRenderer.Draw(_swdHost, swd, skipFieldIndices: skip);

        string? text = SwdTextStream.LoadText("CREDITS_TXT");
        if (string.IsNullOrEmpty(text)) return;

        var field = swd.Fields[0];
        SwdTextStream.Render(_swdHost, text,
            swd.Window.X + field.X,
            swd.Window.Y + field.Y,
            field.Lx,
            field.Ly,
            field.FontName,
            field.FontBaseColor);
    }

    private void DrawUiSprite(MenuSpriteSpec spec) => DrawUiSprite(spec, Colors.White);

    private void DrawUiSprite(MenuSpriteSpec spec, Color modulate)
    {
        var tex = LoadUiSprite(spec);
        if (tex != null)
            DrawTexture(tex, new Vector2(spec.X, spec.Y), modulate);
    }

    private Texture2D? LoadUiSprite(MenuSpriteSpec spec)
    {
        string path = Path.Combine(ProjectSettings.GlobalizePath("res://assets/sprites"), spec.FileName);
        return LoadSpriteFromPath(path);
    }

    private void DrawRegisterIdTag(bool showCrosshair)
    {
        DrawSwdPanel(4, 110, 121, 58, new Color(0.10f, 0.10f, 0.10f), MenuChrome.MenuMid);
        DrawUiSprite(MenuChrome.RegisterPortrait);
        DrawMenuText("PILOT ID", 62, 120, 8, MenuChrome.MenuOrange);
        DrawMenuText("RAPTOR", 62, 134, 8, new Color(0.72f, 0.70f, 0.62f));
        if (showCrosshair)
        {
            DrawLine(new Vector2(38, 112), new Vector2(38, 164), MenuChrome.MenuOrange, 1);
            DrawLine(new Vector2(18, 138), new Vector2(58, 138), MenuChrome.MenuOrange, 1);
        }
    }

    private void DrawBottomPrompt(string text)
    {
        DrawSwdPanel(68, 181, 183, 17, new Color(0.08f, 0.08f, 0.08f), MenuChrome.MenuMid);
        DrawMenuText(text, 75, 195, 12, MenuChrome.MenuOrange);
    }

    private void DrawSwdPanel(int x, int y, int w, int h, Color fill, Color edge)
    {
        DrawRect(new Rect2(x, y, w, h), fill);
        DrawLine(new Vector2(x, y), new Vector2(x + w - 1, y), edge, 1);
        DrawLine(new Vector2(x, y), new Vector2(x, y + h - 1), edge, 1);
        DrawLine(new Vector2(x, y + h - 1), new Vector2(x + w - 1, y + h - 1), Colors.Black, 1);
        DrawLine(new Vector2(x + w - 1, y), new Vector2(x + w - 1, y + h - 1), Colors.Black, 1);
    }

    private void DrawMenuText(string text, int x, int y, int size, Color color)
    {
        DrawString(ThemeDB.FallbackFont, new Vector2(x, y), text, HorizontalAlignment.Left, -1, size, color);
    }

    // DOS bitmap-font text. basecolor is the palette index of the brightest
    // glyph color; darker shades come from palette[basecolor+1], [+2], ...
    // (matching C's GFX_Print: it decrements basecolor once then adds per-pixel
    // offsets stored in the glyph data).
    private void DrawDosFont(string text, int x, int y, string fontName, int basecolor, Color? modulate = null)
    {
        var font = LoadBitmapFont(fontName);
        if (font == null)
        {
            // Atlas missing — fall back so screenshots still render something.
            DrawMenuText(text, x, y, 8, MenuChrome.MenuOrange);
            return;
        }
        font.Draw(this, text, x, y, basecolor, modulate);
    }

    private SwdWindow? LoadSwd(string name)
    {
        if (_swdCache.TryGetValue(name, out var cached)) return cached;
        string path = $"res://assets/swd/{name}.json";
        SwdWindow? loaded = null;
        if (Godot.FileAccess.FileExists(path))
            loaded = SwdWindow.Load(path);
        _swdCache[name] = loaded;
        return loaded;
    }

    // Adapter passed to SwdRenderer so it can draw onto this node without
    // depending on Godot types directly.
    private sealed class SwdHost : SwdRenderer.IHost, SwdTextStream.IHost
    {
        private readonly DebugRenderer _r;
        public SwdHost(DebugRenderer r) { _r = r; }

        public void DrawCanvasTexture(Texture2D tex, Vector2 pos, Color modulate)
            => _r.DrawTexture(tex, pos, modulate);

        public void DrawCanvasTextureRegion(Texture2D tex, Rect2 dst, Rect2 src, Color modulate)
            => _r.DrawTextureRectRegion(tex, dst, src, modulate);

        public void DrawCanvasRect(Rect2 rect, Color color)
            => _r.DrawRect(rect, color);

        public Texture2D? LoadSprite(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return null;
            // assets/sprites/ files are NNNN_<itemName>.png — we don't know
            // the sequence index here, so glob by name. Cache the first hit.
            if (_r._spriteCache.TryGetValue(itemName, out var cached)) return cached;
            string dir = ProjectSettings.GlobalizePath("res://assets/sprites");
            foreach (var p in System.IO.Directory.GetFiles(dir, $"*_{itemName}.png"))
            {
                var tex = _r.LoadSpriteFromPath(p);
                if (tex != null)
                {
                    _r._spriteCache[itemName] = tex;
                    return tex;
                }
            }
            return null;
        }

        public void DrawText(string text, int x, int y, string fontName, int basecolor, Color? modulate = null)
            => _r.DrawDosFont(text, x, y, fontName, basecolor, modulate);

        public int MeasureText(string text, string fontName)
        {
            var font = _r.LoadBitmapFont(fontName);
            return font?.Measure(text) ?? 0;
        }

        public int FontHeight(string fontName)
        {
            var font = _r.LoadBitmapFont(fontName);
            return font?.Height ?? 0;
        }
    }

    private BitmapFont? LoadBitmapFont(string name)
    {
        if (_bitmapFonts.TryGetValue(name, out var cached)) return cached;
        _palette ??= BitmapFont.LoadPalette("res://assets/fonts/palette.json");
        string atlas = $"res://assets/fonts/{name}.png";
        string meta = $"res://assets/fonts/{name}.json";
        if (!Godot.FileAccess.FileExists(atlas) || !Godot.FileAccess.FileExists(meta))
            return null;
        var font = BitmapFont.Load(atlas, meta, _palette);
        _bitmapFonts[name] = font;
        return font;
    }

    private static int MenuTextWidth(string text, int size)
    {
        return (int)ThemeDB.FallbackFont.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
    }

    /// <summary>
    /// Render all active sim-side explosions. Mirrors C ANIMS_DisplaySky:
    /// each explosion's current frame is (WaveController.GameLoopIter - StartIter); the BLK
    /// family + frame count is resolved from ExpAnim[ExpType]. C drew at the
    /// pre-offset (x - xoff, y - yoff) from ANIMS_StartAnim; since our table
    /// doesn't track those offsets we center the texture on the death point.
    /// </summary>
    // Sentinel ExpType used only for missile smoke trails — matches the
    // SmokeExpType constant in WaveController. Maps to SMOKTRAL_BLK (4 frames).
    private const int SmokeExpType = 100;
    private const int SparkBlueExpType = 101;
    private const int SparkOrangeExpType = 102;

    private void DrawViewEffects()
    {
        if (_wave == null || _blkRoot == null) return;
        foreach (var e in _effects.Active(_wave.GameLoopIter))
        {
            var tex = LoadBlkFrame(e.Family, e.Frame);
            if (tex == null) continue;
            DrawTexture(tex, new Vector2(e.X - (int)tex.GetWidth() / 2, e.Y - (int)tex.GetHeight() / 2));
        }
    }

    private void DrawExplosions()
    {
        if (_wave == null || _blkRoot == null) return;
        int gameIter = _wave.GameLoopIter;
        foreach (var ex in _wave.GetExplosions())
        {
            if (ex.ExpType == SmokeExpType)
            {
                int age = WaveController.AnimationAge(gameIter, ex.StartIter);
                if (age < 0 || age >= 4) continue;
                var stex = _smokeFrames[age];
                if (stex == null) continue;
                // SMOKTRAL_BLK frames are 8×16. Smoke is spawned every 2 ticks
                // and the missile only moves ~6 px in that span — naively
                // overlapping each puff by 10 px gives a single bright blob
                // rather than a trail. Drift each older puff up by 8 px per
                // age so consecutive puffs sit just above one another, giving
                // a properly extended column. (C technically only drifts 1
                // px/tick via A_MOVEUP, but it also uses GFX_ShadeShape's LIGHT
                // table which produces a much subtler blend — the visible
                // result is a spread-out trail either way.)
                int sx = ex.X - (int)stex.GetWidth() / 2;
                int sy = ex.Y - age * 8 - (int)stex.GetHeight() / 2;
                DrawTexture(stex, new Vector2(sx, sy), new Color(1, 1, 1, 0.20f));
                continue;
            }
            if (ex.ExpType == SparkBlueExpType || ex.ExpType == SparkOrangeExpType)
            {
                int age = WaveController.AnimationAge(gameIter, ex.StartIter);
                if (age < 0 || age >= 9) continue;
                string sparkFamily = ex.ExpType == SparkBlueExpType ? "BSPARK_BLK" : "OSPARK_BLK";
                var sparkTex = LoadBlkFrame(sparkFamily, age);
                if (sparkTex == null) continue;
                DrawTexture(sparkTex, new Vector2(
                    ex.X - (int)sparkTex.GetWidth() / 2,
                    ex.Y - (int)sparkTex.GetHeight() / 2));
                continue;
            }
            {
                int idx = (ex.ExpType >= 0 && ex.ExpType < ExpAnim.Length)
                    ? ex.ExpType : 0;
                var (family, total) = ExpAnim[idx];
                int age = WaveController.AnimationAge(gameIter, ex.StartIter);
                if (age < 0 || age >= total) continue;
                var tex = LoadBlkFrame(family, age);
                if (tex == null) continue;
                int dw = (int)tex.GetWidth();
                int dh = (int)tex.GetHeight();
                // ANIMS.C:418 — GROUND-family anims drift +1px Y per frame while the
                // map is scrolling. Applied as a render-time offset only; the sim's
                // stored explosion position is untouched (parity-inert).
                int driftY = IsGroundFamily(family) ? GroundExplosionDrift.YOffset(age, _wave.IsScrolling) : 0;
                DrawTexture(tex, new Vector2(ex.X - dw / 2, ex.Y - dh / 2 + driftY));
            }
        }
    }

    /// <summary>
    /// ANIMS.C ANIMS_Init GROUND registrations: GEXPLO_BLK, BOOM_PIC, SPLAT_BLK,
    /// BIGSPLAT_BLK, EXPLO2_BLK, FLARE_PIC, SPARKLE_PIC. Of those, only the two
    /// below are reachable through this renderer's ExpAnim table (the others are
    /// never produced by the sim explosion path). LGFLAK_BLK / SMFLAK_BLK /
    /// NRGBANG_BLK in ExpAnim are HIGH_AIR in C, so they do NOT drift.
    /// </summary>
    private static bool IsGroundFamily(string f) =>
        f is "GEXPLO_BLK" or "EXPLO2_BLK";

    private Texture2D? LoadBlkFrame(string family, int frame)
    {
        var key = (family, frame);
        if (_blkCache.TryGetValue(key, out var cached)) return cached;
        string path = Path.Combine(_blkRoot!, $"{family}_{frame:D2}.png");
        Texture2D? tex = null;
        if (File.Exists(path))
        {
            var img = Image.LoadFromFile(path);
            if (img != null) tex = ImageTexture.CreateFromImage(img);
        }
        _blkCache[key] = tex;  // cache misses too
        return tex;
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
        // Use a flat-silhouette shadow texture so internal sprite detail
        // (engines, stripes) doesn't bleed through. C's SHADOW_Draw applies a
        // 6-step palette light table to the underlying screen pixels — a flat
        // dark silhouette is the closest approximation without a shader.
        var shadowTex = GetOrCreateShadow(tex);
        DrawTextureRect(shadowTex, rect, false, new Color(1, 1, 1, 0.3f));
    }

    /// <summary>
    /// Ground enemy shadow at (x-3, y+4) — no 3D projection. Mirrors
    /// SHADOW_GAdd + SHADOW_DisplayGround (SHADOWS.C:177-241) which call
    /// GFX_ShadeShape(DARK, pic, x-3, y+4) at the original sprite size.
    /// </summary>
    private void DrawGroundShadow(Texture2D tex, int x, int y)
    {
        var shadowTex = GetOrCreateShadow(tex);
        DrawTexture(shadowTex, new Vector2(x - 3, y + 4), new Color(1, 1, 1, GroundShadowAlpha));
    }

    /// <summary>
    /// Return a cached flat-black silhouette texture matching the given
    /// sprite's alpha mask. The result is solid black where the source has
    /// alpha &gt; 0 and transparent elsewhere — used for sky shadows.
    /// </summary>
    private Texture2D GetOrCreateShadow(Texture2D src)
    {
        if (_shadowCache.TryGetValue(src, out var cached)) return cached;
        var img = src.GetImage();
        int w = img.GetWidth();
        int h = img.GetHeight();
        var shadow = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float a = img.GetPixel(x, y).A;
                shadow.SetPixel(x, y, new Color(0, 0, 0, a));
            }
        }
        var tex = ImageTexture.CreateFromImage(shadow);
        _shadowCache[src] = tex;
        return tex;
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
            // Additive white that brightens the underlying background — matches
            // C GFX_Shade with a positive-intensity light table. Alpha tuned so
            // the brightest row lightens the bg ~30% (not washing to white).
            var col = new Color(1f, 1f, 1f, 0.30f * t + 0.03f);
            _flameQuads.Add((new Rect2(ix, iy + row, width, 1), col));
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
            // Same additive-white approach as DrawFlameDown — brightens
            // background instead of painting orange. Brightest at the engine
            // exit (bottom row).
            for (int row = 0; row < height; row++)
            {
                float t = (row + 1) / (float)height;  // 0 → 1, brightest at base
                var col = new Color(1f, 1f, 1f, 0.30f * t + 0.03f);
                _flameQuads.Add((new Rect2(bx, topY + row, width, 1), col));
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

    private void DrawShieldHud()
    {
        if (_wave == null) return;
        const int MapRight = 320 - 16;  // SOURCE/MAP.H
        foreach (var segment in HudShieldBar.Build(MapRight + 4, _wave.PlayerLogic.Shield))
        {
            DrawRect(new Rect2(segment.X, segment.Y, segment.Width, segment.Height),
                ShieldPaletteColor(segment.PaletteIndex));
        }
    }

    private static Color ShieldPaletteColor(int paletteIndex)
    {
        if (paletteIndex == 0) return new Color(0, 0, 0, 1);
        var rgb = HudPalette.Color(paletteIndex);
        return new Color(rgb.R / 255f, rgb.G / 255f, rgb.B / 255f, 1);
    }

    private void DrawCurrentWeaponHud()
    {
        if (_wave?.Inventory.EquippedSpecial is not ObjType weapon) return;
        const int MapTop = 2;           // SOURCE/MAP.H
        const int MapRight = 320 - 16;  // SOURCE/MAP.H
        string spriteName = HudWeaponIcon.SpriteNameFor(weapon);
        if (!_spritePaths.TryGetValue(spriteName, out string? path)) return;
        var tex = LoadSpriteFromPath(path);
        if (tex != null)
            DrawTexture(tex, new Vector2(MapRight - 18, MapTop));
    }

    private void DrawMegaBombHud()
    {
        if (_wave == null) return;
        int megaBombCount = _wave.Inventory.GetAmt(ObjType.MegaBomb);
        if (megaBombCount <= 0) return;
        if (!_spritePaths.TryGetValue("SMBOMB_PIC", out string? path)) return;
        var tex = LoadSpriteFromPath(path);
        if (tex == null) return;
        foreach (var pos in HudMegaBombIndicator.Build(megaBombCount))
            DrawTexture(tex, new Vector2(pos.X, pos.Y));
    }

    private void DrawScannerHud()
    {
        if (_wave == null || !_wave.HasSecretsDetector) return;
        int dmg = _wave.GetBaseDamage();
        if (dmg > 0)
        {
            foreach (var b in HudScannerIndicator.BuildDamage(dmg))
                DrawRect(new Rect2(b.X, b.Y, b.W, b.H), ScannerPaletteColor(b.PaletteIndex));
        }
        else
        {
            foreach (var line in HudScannerIndicator.BuildIdle(_scannerState.CurrentDpos))
                DrawRect(new Rect2(line.X, line.Y, 1, line.Height),
                    ScannerPaletteColor(line.PaletteIndex));
        }
        if (_lastScannerFrame != SimClock.Frame)
        {
            _scannerState.AfterSimTick();
            _lastScannerFrame = SimClock.Frame;
        }
        else
        {
            _scannerState.AfterRenderFrame();
        }
    }

    private void DrawWarningHud()
    {
        if (_wave == null) return;
        if (!_wave.ShieldLowWarningVisible) return;
        if (_wave.SystemDamageWarningVisible && _spritePaths.TryGetValue("WEPDEST_PIC", out string? damagePath))
        {
            var damageTex = LoadSpriteFromPath(damagePath);
            if (damageTex != null)
                DrawTexture(damageTex, new Vector2(HudWarning.CenterX((int)damageTex.GetWidth()), HudWarning.SystemDamageY));
        }

        if (!_spritePaths.TryGetValue("SHLDLOW_PIC", out string? path)) return;
        var tex = LoadSpriteFromPath(path);
        if (tex == null) return;
        DrawTexture(tex, new Vector2(HudWarning.CenterX((int)tex.GetWidth()), HudWarning.MapBottom));
    }

    private static Color ScannerPaletteColor(int paletteIndex)
    {
        var rgb = HudPalette.Color(paletteIndex);
        return new Color(rgb.R / 255f, rgb.G / 255f, rgb.B / 255f, 1);
    }

    private Texture2D? LoadPlayerBulletTexture(ObjType weapon, int frameCounter)
    {
        var (family, frame) = PlayerBulletSprite.FrameFor(weapon, frameCounter);
        var key = (family, frame);
        if (_playerBulletFrames.TryGetValue(key, out var cached)) return cached;

        string path;
        if (family.EndsWith("_PIC", System.StringComparison.Ordinal))
        {
            if (!_spritePaths.TryGetValue(family, out path!))
            {
                _playerBulletFrames[key] = null;
                return null;
            }
        }
        else
        {
            string root = _blkRoot ?? ProjectSettings.GlobalizePath("res://assets/bullets");
            path = Path.Combine(root, $"{family}_{frame:D2}.png");
        }
        var tex = LoadSpriteFromPath(path);
        _playerBulletFrames[key] = tex;
        return tex;
    }

    private void DrawBonuses()
    {
        if (_wave == null) return;
        foreach (var bonus in _wave.GetBonuses())
        {
            if (!bonus.Alive) continue;
            var (dx, dy) = BonusSprite.DrawOffset(bonus.Pos);
            if (bonus.DisplayAsPickedUpMoney)
            {
                var moneyTex = _digitTex[10];
                if (moneyTex == null && _spritePaths.TryGetValue(BonusSprite.PickedUpMoneySpriteName, out string? moneyPath))
                {
                    moneyTex = LoadSpriteFromPath(moneyPath);
                }
                if (moneyTex != null)
                    DrawWorldTexture(moneyTex, bonus.X - BonusLogic.Width / 2 + dx,
                        bonus.Y - BonusLogic.Height / 2 + dy);
                continue;
            }
            string spriteName = BonusSprite.SpriteNameFor(bonus.ObjType, bonus.Frame);
            Texture2D? tex = LoadSpriteFrame(spriteName, bonus.Frame);
            Texture2D? glow = _bonusGlowFrames[bonus.GlowFrame % _bonusGlowFrames.Length];
            if (tex != null)
                DrawWorldTexture(tex, bonus.X - BonusLogic.Width / 2 + dx,
                    bonus.Y - BonusLogic.Height / 2 + dy);
            if (glow != null)
            {
                int gx = bonus.X - (int)glow.GetWidth() / 2 + dx;
                int gy = bonus.Y - (int)glow.GetHeight() / 2 + dy;
                DrawTexture(glow, new Vector2(gx, gy), new Color(1, 1, 1, BonusSprite.GlowAlpha));
            }
        }
    }

    private void DrawWorldTexture(Texture2D tex, int x, int y)
        => DrawWorldTexture(tex, x, y, Colors.White);

    private void DrawWorldTexture(Texture2D tex, int x, int y, Color modulate)
    {
        int w = (int)tex.GetWidth();
        int h = (int)tex.GetHeight();
        if (!WorldClipper.TryClipHorizontal(x, w, out var clip)) return;

        var dst = new Rect2(clip.DestX, y, clip.Width, h);
        var src = new Rect2(clip.SourceX, 0, clip.Width, h);
        DrawTextureRectRegion(tex, dst, src, modulate);
    }

    private void DrawWorldRect(int x, int y, int w, int h, Color color)
    {
        if (!WorldClipper.TryClipHorizontal(x, w, out var clip)) return;
        DrawRect(new Rect2(clip.DestX, y, clip.Width, h), color);
    }
}
