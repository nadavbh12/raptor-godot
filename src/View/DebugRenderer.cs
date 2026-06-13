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
    private int _shotIterMin = int.MinValue;
    private int _shotIterMax = int.MaxValue;
    private uint _lastDrawnScore = 0;
    private int _lastDrawnShield = 0;
    private int _lastDrawnEnemies = 0;
    private int _lastDrawnPbullets = 0;
    private int _lastDrawnEbullets = 0;
    private readonly HudRenderer _hudRenderer = new();
    private readonly MenuRenderer _menuRenderer = new();
    private readonly TileRenderer _tileRenderer = new();
    private readonly ViewEffects _effects = new();
    private int _lastSpawnIter = -1;
    // Megabomb white-out flash: GameLoopIter at which detonation fired, and the
    // number of iters the flash eases out over. Parity-inert View cosmetic.
    // Sentinel is a clean out-of-range value so age = GameLoopIter - (-10000)
    // is always >> MegaFadeFrames until a real detonation sets it.
    private int _megaFadeStartIter = -10000;
    // Tuned to C's GFX_FadeOut(63,60,60,1) palette fade (RAP.C:1127): captured C
    // peaks near-white (~247/255) at detonation and decays over ~17 game-loop
    // iters. The earlier 8-frame / 0.85-alpha approximation was too dim + short.
    private const int MegaFadeFrames = 17;
    private const float MegaFadeMaxAlpha = 0.96f;

    private readonly Dictionary<string, Texture2D> _spriteCache = new();
    private string? _agxRoot;
    private string? _spritesRoot;
    private Color[]? _palette;
    private readonly Dictionary<string, BitmapFont> _bitmapFonts = new();
    private readonly Dictionary<string, SwdWindow?> _swdCache = new();
    private SwdHost? _swdHostInstance;
    private SwdHost _swdHost => _swdHostInstance ??= new SwdHost(this);
    private MenuHost? _menuHostInstance;
    private MenuHost _menuHost => _menuHostInstance ??= new MenuHost(this);
    private readonly ShadowRenderer _shadowRenderer = new();
    private readonly Dictionary<string, string> _spritePaths = new();
    // Multi-frame enemy sprites keyed by iname. The PNG extractor writes each
    // consecutive GLB item with the same iname under sequential indices, e.g.
    // 0309_SHIP07G1_PIC.png and 0310_SHIP07G1_PIC.png for the helicopter's
    // two rotor frames. We collect all paths sharing an iname here.
    private readonly Dictionary<string, List<string>> _spritePathsAll = new();
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
    // Player air-missile down-smoke — A_SMALL_SMOKE_DOWN = SSMOKE_BLK+4 (5 frames,
    // ANIMS.C:202), drifts downward. Spawned per tick by the sim (SmokeDownExpType).
    private readonly Texture2D?[] _smokeDownFrames = new Texture2D?[5];
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
        // Optional capture window by game-loop iter, so a headed capture can skip
        // the whole wave and only dump a small end-wave slice. Parity-inert (View).
        var iterMin = OS.GetEnvironment("RAPTOR_SHOT_ITER_MIN");
        if (!string.IsNullOrEmpty(iterMin) && int.TryParse(iterMin, out var imin)) _shotIterMin = imin;
        var iterMax = OS.GetEnvironment("RAPTOR_SHOT_ITER_MAX");
        if (!string.IsNullOrEmpty(iterMax) && int.TryParse(iterMax, out var imax)) _shotIterMax = imax;

        BuildSpriteIndex();
        _agxRoot = ProjectSettings.GlobalizePath("res://assets/agx");
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
        // Player air-missile smoke = A_SMALL_SMOKE_DOWN = SSMOKE_BLK frames 4..8.
        for (int i = 0; i < 5; i++)
            _smokeDownFrames[i] = LoadSpriteFromPath(Path.Combine(bulletsRoot, $"SSMOKE_BLK_{i + 4:D2}.png"));
        for (int i = 0; i < 4; i++)
            _bonusGlowFrames[i] = LoadSpriteFromPath(Path.Combine(bulletsRoot, $"ICNGLW_BLK_{i:D2}.png"));

        // Score-digit sprite array (RAP.C: numbers[0..10] = N0..N9 + $).
        string spritesRoot = ProjectSettings.GlobalizePath("res://assets/sprites");
        _spritesRoot = spritesRoot;
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
        // The flame layer is part of the in-game playfield render. _flameQuads is
        // only cleared+repopulated in _Draw's playfield branch, so when _Draw takes a
        // cutscene/menu/briefing branch (e.g. on death → death movie → main menu) the
        // quads from the last gameplay frame go stale. _flameLayer.QueueRedraw still
        // fires every frame (_Process), so without this gate those stale additive-white
        // flames bleed as vertical white bars over the death cutscene and the menu.
        if (!ShouldRenderPlayfield(
                _wave != null,
                _wave?.InLoadCompBriefing == true,
                _interactiveUi,
                _menu?.InGame == true,
                _wave?.GameplayVisualActive == true))
            return;

        foreach (var (rect, col) in _flameQuads)
            _flameLayer.DrawRect(rect, col);

        // Player air-missile smoke (SmokeDownExpType). C draws SSMOKE via
        // GFX_ShadeShape(LIGHT) — it BRIGHTENS the background by a small amount (a
        // light haze), not an opaque dark puff. Draw it here on the additive layer
        // so the dark-grey SSMOKE_BLK sprite (mean ~75) lightens the scene like C
        // instead of darkening it. Alpha tuned so one puff ≈ +25 brighten.
        if (_wave == null) return;
        int gameIter = _wave.GameLoopIter;
        foreach (var ex in _wave.GetExplosions())
        {
            if (ex.ExpType != SmokeDownExpType) continue;
            int age = WaveController.AnimationAge(gameIter, ex.StartIter);
            if (age < 0 || age >= 5) continue;
            var stex = _smokeDownFrames[age];
            if (stex == null) continue;
            // A_SMALL_SMOKE_DOWN drifts down ~2px/tick (ANIMS.C A_MOVEDOWN); the
            // column trails below the climbing missile.
            int sx = ex.X - (int)stex.GetWidth() / 2;
            int sy = ex.Y + age * 2 - (int)stex.GetHeight() / 2;
            _flameLayer.DrawTexture(stex, new Vector2(sx, sy), new Color(1, 1, 1, 0.42f));
        }
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
        if (_wave != null && (_shotIterMin != int.MinValue || _shotIterMax != int.MaxValue))
        {
            int it = _wave.GameLoopIter;
            if (it < _shotIterMin || it > _shotIterMax) return;
        }
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
            FormatShotMapRow(fileName, savedFc, _lastDrawnFc, _lastDrawnIter, _lastDrawnScore,
                _lastDrawnShield, _lastDrawnEnemies, _lastDrawnPbullets, _lastDrawnEbullets));
    }

    /// <summary>
    /// Format one shot_map.tsv row with InvariantCulture. Critical: a Hebrew/RTL
    /// system locale otherwise prefixes negative ints (e.g. iter=-1 before the
    /// first in-game frame) with a bidi mark (U+200E), which breaks the int()
    /// parse in the visual-audit alignment tools. Pure/testable.
    /// </summary>
    internal static string FormatShotMapRow(string fileName, int savedFc, int drawnFc, int drawnIter,
        uint score, int shield, int enemies, int pbullets, int ebullets)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return string.Join("\t",
            fileName,
            savedFc.ToString(ci), drawnFc.ToString(ci), drawnIter.ToString(ci),
            score.ToString(ci), shield.ToString(ci), enemies.ToString(ci),
            pbullets.ToString(ci), ebullets.ToString(ci)) + "\n";
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

    public override void _Draw()
    {
        DrawRect(new Rect2(0, 0, 320, 200), new Color(0, 0, 0, 1));

        // Post-sector-select LoadComp wait: the original shows the mission
        // briefing (LOADCOMP window) here, not the playfield. Render it and bail
        // before the menu/playfield branches. Parity-inert (View-only).
        if (_wave != null && _wave.InLoadCompBriefing)
        {
            DrawLoadCompBriefing();
            RecordDrawnState();
            return;
        }

        if (_menu != null && ShouldDrawMenuOverlayForState(
            _interactiveUi,
            _menu.InGame,
            _wave?.GameplayVisualActive == true))
        {
            // Menus paint on a black background; the in-game playfield tint
            // is irrelevant here and would bleed through transparent areas
            // of menu sprites (SHIPCOMP_PIC's display window, etc.).
            _menuRenderer.DrawMenuOverlay(_menu, _menuHost);
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
        _tileRenderer.DrawTileMap(this, _wave);

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
            _shadowRenderer.DrawGroundShadow(this, tex, e.X, e.Y);
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
            _shadowRenderer.DrawSkyShadow(this, tex, e.X, e.Y, e.HalfW * 2, e.HalfH * 2);
        }
        if (_wave.DrawPlayer && playerTex != null)
            _shadowRenderer.DrawSkyShadow(this, playerTex, px, py, PlayerW, PlayerH);

        // C's eframe ^= 1 per ENEMY_DisplaySky call (one per sim tick).
        // Derive from SimClock.Frame parity so the view doesn't mutate sim state.
        int eframe = SimClock.Frame & 1;
        foreach (var e in _wave.GetEnemies())
        {
            if (!e.Alive) continue;
            // Multi-frame sprites (helicopter rotor, etc.) cycle frames per C
            // ENEMY.C:727 logic — pick the right one for this tick. GANIM_MULTI sprites
            // (SHIP22 boss, COW) drive a per-enemy curframe state machine the global
            // SimClock cadence can't reproduce (num_frames mutates mid-life), so take
            // their frame straight from the sim's DisplayFrame (ENEMY.C:791).
            int frameIdx = e.UsesMultiAnim ? e.DisplayFrame : EnemyFrameIndex(e.Meta);
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

        // Sky-layer cosmetics (muzzle flash, explosions, boss smoke) draw BEFORE
        // the player ship, mirroring C's ANIMS_DisplaySky → ship order (RAP.C:1071
        // →1077): the ship sprite occludes the inner half of the gun muzzle flash,
        // so drawing the flash over the ship made it read too large. Spawn + prune
        // once per game-loop tick, then draw.
        SpawnTickEffects();
        DrawExplosions();
        DrawViewEffects();

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
                int fi = System.Math.Clamp(b.FrameCounter - 1, 0, b.NumFrames - 1); // C: curframe-1
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

        // (Muzzle-flash + explosion cosmetics are spawned and drawn ABOVE, before
        //  the player ship, to mirror C's ANIMS_DisplaySky → ship → DisplayHigh
        //  z-order — the ship occludes the inner half of the gun flash. See
        //  SpawnTickEffects()/DrawExplosions()/DrawViewEffects() in DrawScene.)
        _hudRenderer.Draw(this, _wave, LoadSprite, _digitTex);

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

        // Mission-start fade-in: fade the playfield (HUD included) up from black
        // across C's post-iter-0 GFX_FadeIn(64) hold. Parity-inert: driven by a
        // SimClock-derived signal, touches no sim state.
        float missionFadeBlack = _wave.MissionFadeInBlackAlpha;
        if (missionFadeBlack > 0f)
            DrawRect(new Rect2(0, 0, 320, 200), new Color(0, 0, 0, missionFadeBlack));

        if (_menu?.AbortPromptActive == true)
            _menuRenderer.DrawAbortPrompt(_menu, _menuHost);

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

    /// <summary>
    /// True when <see cref="_Draw"/> renders the in-game playfield this frame, so the
    /// additive engine-flame layer should draw. Mirrors _Draw's branch order: a LoadComp
    /// briefing or a menu/cutscene overlay both pre-empt the playfield, and the playfield
    /// needs a live wave. When false the flame layer must draw NOTHING — otherwise the
    /// last gameplay frame's flame quads freeze as white bars over the cutscene/menu.
    /// </summary>
    public static bool ShouldRenderPlayfield(
        bool hasWave,
        bool inLoadCompBriefing,
        bool interactiveUi,
        bool menuInGame,
        bool gameplayVisualActive)
    {
        if (!hasWave) return false;
        if (inLoadCompBriefing) return false;
        if (ShouldDrawMenuOverlayForState(interactiveUi, menuInGame, gameplayVisualActive)) return false;
        return true;
    }

    /// <summary>
    /// Mission briefing shown during the LoadComp wait (LOADCOMP_SWD): SHIPCOMP_PIC
    /// chrome + the SCREEN_PIC inner display ("APPROACHING DESTINATION" + status
    /// bars) + status lights and buttons, with the dynamic SECTOR / WAVE text
    /// filled in (C sets these via SWD_SetFieldText). The G1 campaign is BRAVO
    /// SECTOR. Parity-inert — View-only, mirrors C's WIN_ShowWindow(LOADCOMP_SWD).
    /// </summary>
    private void DrawLoadCompBriefing()
    {
        var swd = LoadSwd("LOADCOMP_SWD");
        if (swd == null) return;
        SwdRenderer.Draw(_swdHost, swd, selectedFieldId: -1);

        // Field 9 SECTOR, field 10 WAVE (FONT2_FNT). C fills these per mission.
        var sector = swd.Fields[9];
        DrawDosFont("BRAVO SECTOR",
            swd.Window.X + sector.X, swd.Window.Y + sector.Y,
            sector.FontName, sector.FontBaseColor);

        var wave = swd.Fields[10];
        DrawDosFont($"WAVE {_wave!.WaveNum}",
            swd.Window.X + wave.X, swd.Window.Y + wave.Y,
            wave.FontName, wave.FontBaseColor);

        // Loading bar: C's WIN_SetLoadLevel fills the LCOMP_LEVEL field with
        // GFX_ColorBox(g_x, g_y, lx*level/100 + 1, g_ly, 85) as RAP_LoadMap loads
        // (WINDOWS.C:1655-1658). Our load is synchronous, so fill it across the
        // deferral via LoadCompProgress. Color 85 is the DOS palette's orange.
        _palette ??= BitmapFont.LoadPalette("res://assets/fonts/palette.json");
        var level = swd.Fields[11];
        int barW = (int)(level.Lx * _wave!.LoadCompProgress) + 1;
        DrawRect(new Rect2(swd.Window.X + level.X, swd.Window.Y + level.Y, barW, level.Ly),
                 _palette[85]);
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
            { 0, 1, 4, 5, 6, 7, 9, 10, 11, 12, 13 };  // 0 = STOR_ID portrait (drawn per pilot below)
        if (store.ShowingGreeting)
            overrides.UnionWith(new[] { 2, 3 });  // hide PREV / NEXT too
        SwdRenderer.Draw(_swdHost, swd, selectedFieldId: -1,
            skipFieldIndices: overrides);

        // STOR_ID (field 0): the pilot's SELECTED portrait (C STORE.C:257
        // id_pics[plr.id_pic]). Without this the SWD default (WMALE) always showed.
        DrawSwdItemSprite(swd.Fields[0], StorePortraitName(store.IdPic));

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

    /// <summary>Store pilot-portrait sprite for an id_pic. C STORE.C:257 uses the
    /// LARGE, axis-aligned `id_pics[]` (WINDOWS.C:62) — NOT the small tilted `sid_pics[]`
    /// ID badges the registration screen uses. Order: 0=WMALE 1=BMALE 2=WFEMALE 3=BFEMALE.</summary>
    private static string StorePortraitName(int idPic) => idPic switch
    {
        1 => "BMALE_PIC",
        2 => "WFEMALE_PIC",
        3 => "BFEMALE_PIC",
        _ => "WMALE_PIC",
    };

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

    // Adapter passed to MenuRenderer so it draws onto this node and reuses the
    // shared SWD/font/sprite caches without duplicating them. Mirrors SwdHost.
    private sealed class MenuHost : MenuRenderer.IHost
    {
        private readonly DebugRenderer _r;
        public MenuHost(DebugRenderer r) { _r = r; }

        public CanvasItem Canvas => _r;
        public string? AgxRoot => _r._agxRoot;
        public string? SpritesRoot => _r._spritesRoot;
        public SwdRenderer.IHost SwdRenderHost => _r._swdHost;
        public SwdTextStream.IHost SwdTextHost => _r._swdHost;
        public SwdWindow? LoadSwd(string name) => _r.LoadSwd(name);
        public void DrawDosFont(string text, int x, int y, string fontName, int basecolor, Color? modulate = null)
            => _r.DrawDosFont(text, x, y, fontName, basecolor, modulate);
        public void DrawMenuText(string text, int x, int y, int size, Color color)
            => _r.DrawMenuText(text, x, y, size, color);
        public void DrawUiSprite(MenuSpriteSpec spec) => _r.DrawUiSprite(spec);
        public void DrawUiSprite(MenuSpriteSpec spec, Color modulate) => _r.DrawUiSprite(spec, modulate);
        public Texture2D? LoadSpriteFromPath(string path) => _r.LoadSpriteFromPath(path);
        public Texture2D? LoadSprite(string itemName) => _r._swdHost.LoadSprite(itemName);
        public int MeasureText(string text, string fontName) => _r._swdHost.MeasureText(text, fontName);
        public int FontHeight(string fontName) => _r._swdHost.FontHeight(fontName);
        public void DrawShipComputerOverlay(Font font) => _r.DrawShipComputerOverlay(font);
        public void DrawStoreOverlay(StoreLogic store) => _r.DrawStoreOverlay(store);
        public void DrawCreditsOverlay(Font font) => _r.DrawCreditsOverlay(font);
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
    // Player air-missile down-smoke sentinel — matches WaveController.SmokeDownExpType.
    private const int SmokeDownExpType = 103;

    /// <summary>
    /// Spawn the per-game-loop-tick View cosmetics (muzzle flash, megabomb glow,
    /// boss smoke) into the shared effects list and prune expired ones. Gated to
    /// run once per GameLoopIter (the loop runs ~3 SimClock.Frame per iter). Called
    /// before the player ship is drawn so the gun flash renders behind it (C's
    /// ANIMS_DisplaySky → ship order).
    /// </summary>
    private void SpawnTickEffects()
    {
        if (_wave == null || _wave.GameLoopIter == _lastSpawnIter) return;
        _lastSpawnIter = _wave.GameLoopIter;
        int spawnIter = _wave.GameLoopIter;
        // GUNSTR_BLK is C's only playerflag anim (ANIMS.C:208) — it rides with the
        // jet. Store the muzzle as an offset from the player; DrawViewEffects adds
        // the live player position back so it tracks strafing instead of lagging.
        int pmx = _wave.PlayerLogic.X, pmy = _wave.PlayerLogic.Y;
        foreach (var m in _wave.MuzzlesThisTick)
            _effects.Spawn("GUNSTR_BLK", totalFrames: 4, x: m.X - pmx, y: m.Y - pmy,
                           spawnIter: spawnIter, ground: false, followPlayer: true);
        // Megabomb detonation: consume the one-shot sim signal exactly once per
        // tick, start the white-out flash, and spawn the SHIPGLOW_BLK glow. Mirrors
        // C SHOTS.C:1273-1274 startfadeflag + A_SUPER_SHIELD + RAP.C:1127 fade.
        if (_wave.ConsumeMegaBombDetonated())
        {
            _megaFadeStartIter = _wave.GameLoopIter;
            _effects.Spawn("SHIPGLOW_BLK", totalFrames: 4, x: 160, y: 100, spawnIter: _wave.GameLoopIter, ground: false);
        }
        // Boss low-health smoke (ENEMY.C:1076-1085): a boss with hits<50 emits
        // A_SMALL_AIR_EXPLO (SMFLAK_BLK, 14 frames) every other pass (gl_cnt & 2).
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

    private void DrawViewEffects()
    {
        if (_wave == null || _blkRoot == null) return;
        foreach (var e in _effects.Active(_wave.GameLoopIter))
        {
            var tex = LoadBlkFrame(e.Family, e.Frame);
            if (tex == null) continue;
            // FollowPlayer anims store an offset from the player; re-anchor to the
            // live position so the muzzle flash tracks the jet (C's playerflag).
            var (ex, ey) = ViewEffects.ResolvePos(e, _wave.PlayerLogic.X, _wave.PlayerLogic.Y);
            DrawTexture(tex, new Vector2(ex - (int)tex.GetWidth() / 2, ey - (int)tex.GetHeight() / 2));
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
            // SmokeDownExpType (player air-missile smoke) is drawn on the ADDITIVE
            // flame layer (OnFlameLayerDraw), not here — C's GFX_ShadeShape(LIGHT)
            // BRIGHTENS the background rather than painting an opaque dark puff.
            if (ex.ExpType == SmokeDownExpType) continue;
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
