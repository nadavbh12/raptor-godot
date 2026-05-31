using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Godot;
using Raptor.Sim.Bonus;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Raptor.Sim.MazeLevel;
using Raptor.Sim.Player;
// TileDamageDispatcher and TileState are in MazeLevel namespace (above).
using Raptor.Sim.Shots;
using Raptor.Test;

namespace Raptor.Sim;

/// <summary>
/// Pure C# phase dispatcher. Holds no Godot objects; safe to instantiate in
/// xUnit tests without the Godot native runtime.
///
/// Per spec §4.3: Input → Spawn → Movement → Collision collection
/// → Collision resolution → Cleanup → HUD → Checkpoint.
///
/// Entities (Player, Enemy, Bullet) do NOT use _PhysicsProcess directly;
/// they expose Tick{phase}() methods that WaveController invokes here.
/// </summary>
internal class WavePhaseScheduler
{
    /// <summary>
    /// Computes the deterministic per-wave seed.
    /// Returns the parsed seedOverride if non-null/non-empty and parseable as int;
    /// otherwise returns 1024 * waveNum.
    /// </summary>
    public static ulong ComputeSeed(int waveNum, string? seedOverride = null)
    {
        if (!string.IsNullOrEmpty(seedOverride) && int.TryParse(seedOverride, out int seed))
            return (ulong)seed;
        return (ulong)(1024 * waveNum);
    }

    /// <summary>Runs one full per-tick phase cycle. Calls virtual phase methods in spec order.</summary>
    public void Tick()
    {
        TickInput();
        TickSpawn();
        TickMovement();
        TickCollisionCollect();
        TickCollisionResolve();
        TickCleanup();
        TickHud();
        TickCheckpoint();
    }

    public virtual void TickInput()            { }
    public virtual void TickSpawn()            { }
    public virtual void TickMovement()         { }
    public virtual void TickCollisionCollect() { }
    public virtual void TickCollisionResolve() { }
    public virtual void TickCleanup()          { }
    public virtual void TickHud()              { }
    public virtual void TickCheckpoint()       { }
}

/// <summary>
/// Thin Godot Node wrapper around WavePhaseScheduler.
/// Bridges the Godot scene-tree lifecycle (_Ready, _PhysicsProcess)
/// to the pure-C# scheduler that contains the actual sim logic.
///
/// Holds the Godot RandomNumberGenerator (a GodotObject) so that it is
/// only constructed inside the Godot runtime (never in headless unit tests).
/// </summary>
public partial class WaveController : Node
{
    // ── RNG ──────────────────────────────────────────────────────────────────
    /// <summary>
    /// Deterministic per-wave RNG. Exposed so Stage 3+ tasks (Player, Enemy, Bullet)
    /// can share the single per-wave random stream.
    /// </summary>
    private RandomNumberGenerator? _rng;
    public RandomNumberGenerator Rng => _rng ??= new RandomNumberGenerator();

    // ── Wiring slots ─────────────────────────────────────────────────────────
    // Set by _Ready via GetNodeOrNull; the scheduler reads these.
    private MenuStateMachine?   _menu;
    private ParityEmitter?      _emitter;
    private Raptor.Test.PlaythroughDriver? _playthrough;
    private InteractiveInputController? _interactiveInput;
    private string?             _assetsRoot;

    // ── Wave state ────────────────────────────────────────────────────────────
    private bool _waveActive = false;
    private int  _gameEnterFc = 0;
    // C's GFX_FadeIn(gpal, 64) runs at the start of Do_Game AFTER iter 0's
    // ENEMY_Think, blocking the game loop for ~120 PIT framecount frames
    // (60 Hz vsync + 70 Hz framecount). Position dumps show C iter 1 ends at
    // rel=133 (after iter 0 at rel~14 + 119 frames of fade-in + 3 wait).
    // Hold=131 puts Godot iter 1 at frame 134 (after subTick cycle of 3).
    private const int FadeInHoldFrames = 131;
    private const int DemoFadeInHoldFrames = 153;

    // Frames between sector-select Return-apply and raptor_parity_game_enter
    // firing — i.e. how long the "load comp" beat lasts before iter 0 runs.
    // This sets the absolute-frame → game-iter mapping, which determines at
    // which iter a script-injected HELD input (e.g. death_wave3's `down Up`,
    // injected at a fixed script frame) starts affecting the player.
    //
    // Re-tuned 59 → 107 (2026-05-30 PM2): an L2a sweep showed 107 matches C's
    // movement-onset timing across scenarios — mission_start 98.5%→100%,
    // mission_long 97.4%→99.1%, death_wave3 gameplay near-exact (the held-Up
    // shield drift disappears), full_demo unchanged at 100%. The earlier 59 was
    // tuned for the *manual* L7 in-game visual-dump alignment (mission_start
    // dumps 05/06 were Δ=+42 off at 102); since 107 ≈ 102, those L7 dump
    // baselines need re-aligning ~48 frames — but L7 is NOT in ci/full.sh, while
    // the L2a suite it improves IS the gating signal. NOTE: 59-for-dumps vs
    // 107-for-input-onset both claim to match C, which means Godot's startup
    // frame accounting still has a ~48-frame internal discrepancy between
    // "dump time" and "iter-0 time" — a deeper mismodel left for later.
    // The parity comparator treats `fc` as advisory (parity_diff.py:52), so
    // only absolute fc values shift; iter-aligned content is what improved.
    private const int LoadCompFrames = 107;
    private const int DemoLoadCompFrames = 78;
    private int  _waveNum    = 1;

    // When MenuStateMachine.EnterGame fires we defer the iter-0 body and
    // _gameEnterFc anchoring to a future frame to mirror the C LoadComp
    // window above. -1 = no pending; otherwise the frame at which the wave
    // becomes active and iter 0 runs.
    private int  _pendingGameEnterFrame = -1;
    private int  _pendingGameNum = 0;
    private int  _pendingDemoStartFrame = -1;
    private DemoReplay? _pendingDemoReplay;
    private DemoReplay? _demoReplay;
    private int _demoRecordIndex = 0;
    private bool _demoB2Latch = false;
    private bool _demoB3Latch = false;
    private bool _inputB2Latch = false;
    private bool _inputB3Latch = false;
    private InputState? _testInteractiveInput;
    private readonly Queue<WeaponType> _testSpecialSelects = new();
    private bool _debugDemoReplay = false;
    private int _paletteStuffCnt = 0;
    private bool _skipInitialPaletteStuff = false;

    // Fired right after _gameLoopIter advances; PositionDumper subscribes to
    // emit at iter ends, mirroring C's parity_tick semantics.
    public event Action? OnIterEnd;
    public event Action<string>? OnBonusTrace;

    // ── Game-loop rate throttle ──────────────────────────────────────────────
    // The C game loop has `while (FRAME_COUNT - local_cnt < 3) legacy_pump();`
    // — a MINIMUM of 3 PIT frames per iteration, with body work pushing
    // steady-state to ~4. Score-milestone evidence:
    //   First kill: C @ fc=630 ; Godot d=4 @ fc=630 (match)
    //   Kill #2:    C @ fc=910 ; Godot d=4 @ fc=1050 (140 fc late)
    //   Kill #3:    C @ fc=1120; Godot d=4 @ fc=1260 (140 fc late)
    //   Kill #4:    C @ fc=1400; Godot d=4 @ fc=1750 (350 fc late)
    //   Kill #5:    C @ fc=1680; Godot d=4 @ fc=2100 (420 fc late)
    //
    // Pattern: first kill is exact, subsequent kills lag with growing gap.
    // Likely cause is that C's early loop iterations run closer to the 3-fc
    // minimum (fewer entities in the body, so less work per iter), then
    // settle into 4 fc/iter as the wave fills out. A perfect match needs
    // a variable cadence (e.g. 3 for the first ~100 iters, 4 thereafter)
    // or matching C's actual frame budget. d=4 is the best fixed value.
    private int _subTick = 0;
    private int _gameLoopIter = 0;
    public  int GameLoopIter => _gameLoopIter;
    // Position-dump comparison at MISSION_1 fc=560 (after spawn coord fix)
    // shows C top-left y = 138 vs Godot 130 for sprite #0 — an 8-px gap from
    // C running 144 game-ticks vs our 140 in the same 560 fc window. That's
    // 3.89 fc/tick (C) vs 4.0 (us). Approximating with 1 fast iter (3 fc) per
    // 8 slow iters (4 fc) gives average 35/9 ≈ 3.889 fc/tick. Each LoadWave
    // resets the iter counter so the fast slot lands on the same iter index.

    // ── Player ────────────────────────────────────────────────────────────────
    public  PlayerLogic    PlayerLogic   { get; } = new();
    public  PlayerShooter  Shooter       { get; } = new();
    public  uint           Score { get; private set; } = 0;
    /// <summary>
    /// Per-pilot inventory. Created once and reused across waves/loads.
    /// Tasks 3.2 (load) and 3.3 (PlayerShooter wiring) read this same instance.
    /// </summary>
    public  Inventory      Inventory     { get; } = new();

    /// <summary>Forcibly set the score (e.g. when loading a saved pilot).
    /// Bypasses the normal incremental score-from-enemy-kills path.</summary>
    public void SetScore(uint score) => Score = score;

    // C SHOTS_PlayerShoot uses libc `random()` for DUMB_MISSLE scatter and
    // MINI_GUN target picks. We share a dedicated Random seeded off the wave
    // RNG so PlayerShooter remains testable without a Godot RNG.
    private System.Random? _shooterRng;

    // Starting score for a new pilot (from golden: HANGAR shows score=10000).
    // In the C version, new pilots start with some credits from win_register.
    private const uint NewPilotScore = 10000;

    // ── Difficulty filtering (mirrors ENEMY.C + LOADSAVE.C) ──────────────────
    // Raw level values in the map JSON (CSPRITE.level raw field):
    //   3 = E_EASY_LEVEL → EB_EASY_LEVEL = 8
    //   4 = E_MED_LEVEL  → EB_MED_LEVEL  = 16
    //   5 = E_HARD_LEVEL → EB_HARD_LEVEL = 32
    //   other → EB_NOT_USED = 64
    // cur_diff for DIFF_2 (Normal, the default for a new pilot pressing Return on
    // the difficulty dialog default "MEDIUM"): EB_EASY_LEVEL | EB_MED_LEVEL = 24.
    private const int EB_EASY_LEVEL  = 8;
    private const int EB_MED_LEVEL   = 16;
    private const int EB_HARD_LEVEL  = 32;
    private const int EB_NOT_USED    = 64;
    // Default difficulty: DIFF_2 (Normal) → cur_diff = 24.
    private int _curDiff = EB_EASY_LEVEL | EB_MED_LEVEL;   // 24
    private int _curPlayerDiff = 2;

    /// <summary>
    /// Maps a raw CSPRITE.level value to the corresponding EB_ bitmask.
    /// Mirrors the switch in ENEMY_LoadSprites (LOADSAVE.C RAP_SetPlayerDiff).
    /// </summary>
    private static int GetEbLevel(int rawLevel) => rawLevel switch
    {
        3 => EB_EASY_LEVEL,
        4 => EB_MED_LEVEL,
        5 => EB_HARD_LEVEL,
        _ => EB_NOT_USED,
    };

    /// <summary>
    /// Returns true if the sprite should spawn given the current difficulty.
    /// Mirrors: if (curfld->level & cur_diff) ENEMY_Add(cur_enemy).
    /// </summary>
    private bool ShouldSpawn(MapSpriteEntry sprite) =>
        (GetEbLevel(sprite.Level) & _curDiff) != 0;

    // ── Enemy pool ────────────────────────────────────────────────────────────
    // Pure-C# list of alive EnemyLogic instances (no Godot nodes at runtime,
    // consistent with headless execution). Enemy nodes are created separately
    // in _Ready if needed for rendering; parity only needs the counts.
    private readonly List<EnemyLogic>   _enemies     = new();
    private readonly List<BulletLogic>  _playerBullets = new();
    private readonly List<BulletLogic>  _enemyBullets  = new();
    private readonly List<BonusLogic>   _bonuses       = new();
    private readonly List<EnemyLogic>   _weaponTargetEnemies = new();

    // Mirrors C's BONUS_Think static `gcnt` (BONUS.C:205): incremented once per
    // bonus-think pass (every game-loop iter), read as (gcnt & 1) to drive the
    // shared wobble/sprite-frame advance for ALL bonuses in lockstep. Like the C
    // static it is NOT reset per wave, so the phase stays continuous across waves.
    private int _bonusThinkCnt;

    // Active explosion animations spawned when an enemy dies. Each entry
    // records the C exptype (SOURCE/MAP.H), the center position, and the
    // game-loop iteration at which it should first display. C ANIMS_Think
    // advances curframe once per game-loop iteration, not once per framecount.
    public readonly record struct Explosion(int ExpType, int X, int Y, int StartIter);
    private readonly List<Explosion> _explosions = new();

    // Read-only accessors for debug rendering only — not parity-affecting.
    public IReadOnlyList<EnemyLogic>  GetEnemies()       => _enemies;
    public IReadOnlyList<BulletLogic> GetEnemyBullets()  => _enemyBullets;
    public IReadOnlyList<BulletLogic> GetPlayerBullets() => _playerBullets;
    public IReadOnlyList<Explosion>   GetExplosions()    => _explosions;
    public IReadOnlyList<BonusLogic>  GetBonuses()       => _bonuses;
    public bool DrawPlayer { get; private set; } = true;
    public bool GameplayVisualActive => _waveActive || _pendingDemoStartFrame >= 0;

    internal static int AnimationStartIterForSpawn(int currentGameLoopIter) => currentGameLoopIter + 1;
    internal static int AnimationAge(int currentGameLoopIter, int startIter) => currentGameLoopIter - startIter;

    // ── Collision scratch ─────────────────────────────────────────────────────
    private readonly List<(EnemyLogic enemy, int dmg)> _hitEnemies = new();
    private readonly List<BulletLogic> _shotDoneAfterCollision = new();
    private bool _playerHit;
    private int  _playerHitDmg;
    private readonly List<EnemyLogic> _bodyCrashEnemies = new();  // enemies that collided with player
    private readonly List<BonusLogic> _pickedUpBonuses  = new();  // bonuses overlapping player this tick
    private bool _playerWasAliveAtCollisionStart;

    // ── Shield recharge (OBJS_Think in C) ────────────────────────────────────
    // CHARGE_SHIELD = 24*4 = 96. When think_cnt > 96, heal 1 shield.
    // curplr_diff < DIFF_3 enables recharge.
    private const int ChargeShield = 96;
    private const int ShieldLow = 10;
    private int _thinkCnt = 0;
    private int _oldShieldForLowLoss = -1;
    private readonly View.HudWarning.State _hudWarningState = new();
    public bool ShieldLowWarningVisible { get; private set; }
    public bool SystemDamageWarningVisible { get; private set; }
    private static readonly Lazy<StreamWriter?> ShieldTrace = new(OpenShieldTrace);

    // ── Map scroll state (mirrors C's TILE.C) ─────────────────────────────────
    // tilepos starts at (MAP_ROWS - MAP_ONSCREEN) * MAP_COLS = 142 * 9 = 1278.
    // tiley = tilepos / MAP_COLS - 3.
    // tileyoff starts at 200 - MAP_ONSCREEN * MAP_BLOCKSIZE = 200 - 256 = -56.
    // Each physics tick: tileyoff++. When tileyoff > 0: tileyoff -= 32; tilepos -= 9.
    private const int MAP_ROWS       = 150;
    private const int MAP_ONSCREEN   = 8;
    private const int MAP_COLS       = 9;
    private const int MAP_BLOCKSIZE  = 32;
    private const int MAP_LEFT       = 16;

    private int _tilepos   = (MAP_ROWS - MAP_ONSCREEN) * MAP_COLS;
    private int _tileyoff  = 200 - MAP_ONSCREEN * MAP_BLOCKSIZE;  // = -56
    private int _tiley     = 0;  // current spawn row: tilepos/MAP_COLS - 3

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

    // ── Map sprite list for spawning ──────────────────────────────────────────
    private List<MapSpriteEntry>? _mapSprites;
    private List<MapTileEntry>?   _mapTiles;

    /// <summary>Tile-grid data for the current wave (rows * cols entries, row-major).</summary>
    public IReadOnlyList<MapTileEntry>? MapTiles => _mapTiles;

    public int RenderedFlatFor(int mapspot)
    {
        if (_mapTiles == null || mapspot < 0 || mapspot >= _mapTiles.Count)
            return 0;

        int flat = _mapTiles[mapspot].Flats;
        if (_flatLib == null || _tileDeadByMapSpot == null ||
            mapspot >= _tileDeadByMapSpot.Length || !_tileDeadByMapSpot[mapspot])
            return flat;

        return _flatLib.DestroyedFlatFor(flat);
    }
    /// <summary>Current scroll Y offset (mirrors C's tileyoff).</summary>
    public int TileYOff => _tileyoff;
    /// <summary>Current top-of-screen row in the tile grid (mirrors C's tilepos).</summary>
    public int TilePos  => _tilepos;
    public int MapRows  => MAP_ROWS;
    public int MapCols  => MAP_COLS;
    public int MapOnScreen   => MAP_ONSCREEN;
    public int MapBlockSize  => MAP_BLOCKSIZE;
    public int MapLeftPx     => MAP_LEFT;
    private SpriteMetaLibrary?    _slib;
    private int                   _spawnIdx = 0;   // index into _mapSprites
    private bool                  _endWaveFlag = false;
    private bool                  _missionCompleteNotified = false;
    private int                   _playerDeathCountdown = -1;
    // End-of-wave fly-off countdown. -1 = inactive. When the wave ends and no
    // enemies/explosions remain, this counts down from EndWaveSequence.Duration
    // (60). The player ship glides up by 4 px/iter (and centers horizontally)
    // once it crosses below EndWaveSequence.FlyOff (40). Mission complete fires
    // when the countdown reaches 0. Mirrors RAP.C:599-617, 1039-1047.
    private int                   _endWaveCountdown = -1;
    public int                    EndWaveCountdown => _endWaveCountdown;

    // ── Scheduler ─────────────────────────────────────────────────────────────
    private readonly GamePhaseScheduler _scheduler;

    public WaveController()
    {
        _scheduler = new GamePhaseScheduler(this);
    }

    public void SeedRngForWave(int waveNum, string? seedOverride = null)
    {
        Rng.Seed = WavePhaseScheduler.ComputeSeed(waveNum, seedOverride);
    }

    public override void _Ready()
    {
        SeedRngForWave(1, OS.GetEnvironment("RAPTOR_RNG_SEED_OVERRIDE"));

        _assetsRoot = ProjectSettings.GlobalizePath("res://assets");
        _debugDemoReplay = OS.GetEnvironment("RAPTOR_DEBUG_DEMO") == "1";

        var menuController = GetNodeOrNull<MenuController>("../MenuController");
        if (menuController != null)
        {
            _menu = menuController.Menu;
            _menu.OnPilotCreated += OnPilotCreated;
            _menu.OnGameEnter    += OnGameEnter;
        }

        // Found only when running under a parity playthrough; PhaseInput uses
        // PlayerInputX/Y to feed the player.
        _playthrough = GetNodeOrNull<Raptor.Test.PlaythroughDriver>("../PlaythroughDriver");
        _interactiveInput = GetNodeOrNull<InteractiveInputController>("../InteractiveInputController");

        _emitter = GetNodeOrNull<ParityEmitter>("../ParityEmitter");
        if (_emitter != null)
        {
            // Wire live-state delegates so ParityEmitter always reads from us,
            // even before the wave is active (HANGAR shows player stats).
            _emitter.GetPlayerX  = () => PlayerLogic.X;
            _emitter.GetPlayerY  = () => PlayerLogic.Y;
            _emitter.GetScore    = () => Score;
            _emitter.GetShield   = () => PlayerLogic.Shield;
            _emitter.GetEnemies  = () => _enemies.Count;
            _emitter.GetPbullets = () => _playerBullets.Count;
            _emitter.GetEbullets = () => _enemyBullets.Count;
            _emitter.GetGameIter = () => GameLoopIter;
            _emitter.GetGameAnchorFrame = () => _gameEnterFc;
            _emitter.GetDemoGameNum = () => _demoReplay?.Header.DemoGame ?? -1;
            _emitter.GetMenuDemoEmitSequence = () => MenuDemoEmitSequence;
        }
    }

    private int MenuDemoEmitSequence
    {
        get
        {
            if (_pendingDemoStartFrame >= 0) return -1;
            if (_demoReplay != null) return _demoRecordIndex;
            return int.MinValue;
        }
    }

    private void OnPilotCreated()
    {
        // New pilot starts with 10000 score and 75 shield (from C golden).
        Score = NewPilotScore;
        PlayerLogic.Reset();  // Reset sets Shield = InitShield = 75.
        // Seed inventory to match WINDOWS.C:989-1007:
        //   ForwardGuns + 3×Energy (→75) + GetNext() (→EquippedSpecial=null).
        Inventory.Clear();
        Inventory.SeedNewPilot();
        GD.Print($"WaveController: pilot created, score={Score}, shield={PlayerLogic.Shield}");
    }

    private void OnGameEnter(int gameNum)
    {
        // Defer the iter-0 body and wave activation by LoadCompFrames.
        // Concrete setup happens in _PhysicsProcess once the deferred frame
        // arrives. See LoadCompFrames docs for the C-side rationale.
        _pendingGameNum = gameNum;
        _pendingGameEnterFrame = SimClock.Frame + LoadCompFrames;
    }

    /// <summary>
    /// Resolves the episode-1 start wave from the RAPTOR_START_WAVE env override
    /// (1-9), falling back to <paramref name="defaultWave"/> when unset/invalid.
    /// Mirrors the C hook in RAP.C.
    /// </summary>
    internal static int ResolveStartWave(string? env, int defaultWave)
    {
        if (int.TryParse(env, out int w) && w >= 1 && w <= 9) return w;
        return defaultWave;
    }

    /// <summary>
    /// Fires the deferred iter-0 body and arms the wave. Splits out of the
    /// hot path so _PhysicsProcess stays readable.
    /// </summary>
    private void ApplyPendingGameEnter()
    {
        _waveNum    = ResolveStartWave(OS.GetEnvironment("RAPTOR_START_WAVE"), _pendingGameNum + 1);  // gameNum is 0-based; wave files are 1-based.
        SeedRngForWave(_waveNum, OS.GetEnvironment("RAPTOR_RNG_SEED_OVERRIDE"));
        LoadWave(_waveNum);
        _gameEnterFc = SimClock.Frame;
        _waveActive  = true;
        _pendingGameEnterFrame = -1;

        // Fire iter 0 immediately, mirroring C: in RAP.C the first ENEMY_Think
        // (and full loop body) runs BEFORE GFX_FadeIn(64) blocks. Position
        // dumps confirm C captures iter-0-done state at fc=0 (sprite->y=-148
        // = pre-move snapshot from iter 0's ENEMY_Think).
        _scheduler.Tick();
        _gameLoopIter++;
        OnIterEnd?.Invoke();
    }

    public void StartDemoPlayback(DemoReplay replay, int currentFrame)
    {
        _pendingDemoReplay = replay;
        _pendingDemoStartFrame = currentFrame + DemoLoadCompFrames;
    }

    private void ApplyPendingDemoStart()
    {
        if (_pendingDemoReplay == null) return;

        _demoReplay = _pendingDemoReplay;
        _pendingDemoReplay = null;
        _pendingDemoStartFrame = -1;
        _demoRecordIndex = 0;
        _demoB2Latch = false;
        _demoB3Latch = false;

        _waveNum = _demoReplay.Header.DemoWave + 1;
        SeedRngForWave(_demoReplay.Header.DemoWave, OS.GetEnvironment("RAPTOR_RNG_SEED_OVERRIDE"));
        LoadWave(_waveNum);
        SetupDemoPlayer(_demoReplay.Header.DemoGame);
        _gameEnterFc = SimClock.Frame;
        _waveActive = true;
        if (_debugDemoReplay)
            GD.Print($"demo start fc={SimClock.Frame} fade={DemoFadeInHoldFrames}");

        _scheduler.Tick();
        _gameLoopIter++;
        OnIterEnd?.Invoke();
    }

    private void SetupDemoPlayer(int game)
    {
        // DEMO_MakePlayer sets plr.diff[0..2] = DIFF_3 before
        // RAP_SetPlayerDiff(), so demo playback runs on hard difficulty.
        _curPlayerDiff = 3;
        _curDiff = EB_EASY_LEVEL | EB_MED_LEVEL | EB_HARD_LEVEL;

        Score = game switch
        {
            1 => NewPilotScore + 327683u,
            2 => NewPilotScore + 876543u,
            _ => NewPilotScore,
        };
        PlayerLogic.SetShield(PlayerLogic.MaxShield);
        _oldShieldForLowLoss = PlayerLogic.Shield;
        HasSecretsDetector = true;

        DemoLoadout.Apply(Shooter, game, registered: false);
    }

    /// <summary>
    /// Loads the wave's map data and initialises the spawn state.
    /// Public for testing; normally called via OnGameEnter.
    /// </summary>
    public void LoadWave(int waveNum)
    {
        _enemies.Clear();
        _playerBullets.Clear();
        _enemyBullets.Clear();
        _bonuses.Clear();
        _weaponTargetEnemies.Clear();
        _hitEnemies.Clear();
        _shotDoneAfterCollision.Clear();
        _pickedUpBonuses.Clear();
        _explosions.Clear();
        _tileDelayExplosions.Clear();
        _paletteStuffCnt = 0;
        _skipInitialPaletteStuff = true;
        _oldShieldForLowLoss = PlayerLogic.Shield;
        _playerHit = false;
        _endWaveFlag = false;
        _missionCompleteNotified = false;
        _playerDeathCountdown = -1;
        _endWaveCountdown = -1;
        DrawPlayer = true;
        _subTick = 0;
        _gameLoopIter = 0;

        // Reset scroll to start position (mirrors TILE_Init in C).
        _tilepos  = (MAP_ROWS - MAP_ONSCREEN) * MAP_COLS;
        _tileyoff = 200 - MAP_ONSCREEN * MAP_BLOCKSIZE;  // -56
        _tiley    = _tilepos / MAP_COLS - 3;             // = 139
        // Lazy-load the FLATSG1_ITM table on first wave (G1 only — DOS Raptor
        // shipped one campaign; the FLATS struct is mission-independent).
        if (_flatLib == null)
        {
            string flatsPath = Path.Combine(_assetsRoot ?? "assets", "flats", "FLATSG1_ITM.json");
            if (File.Exists(flatsPath))
                _flatLib = FlatLibrary.LoadFromFile(flatsPath);
        }
        // Reset player position.
        PlayerLogic.Reset();
        // Reset weapon cooldowns; mirrors SHOTS_Init in RAP.C Init_Game.
        Shooter.Reset();
        // Seed PlayerShooter RNG deterministically off the wave seed so
        // DUMB_MISSLE scatter and MINI_GUN picks are replay-stable.
        _shooterRng = new LegacyRandom((int)(Rng.Seed & 0x7FFFFFFFu));

        // Load sprite metadata library.
        string slibPath = Path.Combine(_assetsRoot ?? "assets", "sprites_meta", "SPRITE1_ITM.json");
        _slib = SpriteMetaLibrary.LoadFromFile(slibPath);

        // Load map sprite list.
        string mapPath = MazeLevelLoader.WaveMapPath(_assetsRoot ?? "assets", waveNum);
        var mapData    = MazeLevelLoader.Load(mapPath);
        _mapSprites    = mapData.Sprites ?? new List<MapSpriteEntry>();
        _mapTiles      = mapData.Tiles   ?? new List<MapTileEntry>();
        _spawnIdx      = 0;
        InitializeTileBacking();
        _tileSliceTilePos = int.MinValue;
        RebuildTileSlice();

        GD.Print($"WaveController: loaded wave {waveNum}, {_mapSprites.Count} sprites, tiley={_tiley}");

        // Immediately spawn enemies that are on-screen at game start (tiley=139).
        // In the C version, ENEMY_Clear() sets cur_enemy=csprite (index 0) and
        // ENEMY_DoSprites is called once before the main loop, spawning the initial
        // enemies. We replicate that here so fc=0 shows the correct enemy count.
        DoInitialSpawn();
    }

    private void DoInitialSpawn()
    {
        SpawnForTiley(_tiley);
    }

    /// <summary>
    /// Spawn all enemies whose y matches tiley, processing linked groups.
    /// Mirrors ENEMY.C ENEMY_DoSprites() while/for loop.
    /// </summary>
    private void SpawnForTiley(int tiley)
    {
        if (_mapSprites == null || _slib == null) return;
        while (_spawnIdx < _mapSprites.Count && !_endWaveFlag)
        {
            var sprite = _mapSprites[_spawnIdx];
            if (sprite.Y != tiley) break;

            for (;;)
            {
                if (_spawnIdx >= _mapSprites.Count)
                {
                    _endWaveFlag = true;
                    break;
                }
                var cur = _mapSprites[_spawnIdx];
                int oldLink = cur.Link;

                // Spawn this enemy only if it passes difficulty check.
                // Mirrors: if (cur_enemy->level != EB_NOT_USED) ENEMY_Add(cur_enemy).
                if (ShouldSpawn(cur) && _slib.Count > cur.Slib && cur.Slib >= 0)
                {
                    var meta = _slib.Get(cur.Slib);
                    // C ENEMY_Add (ENEMY.C lines 393-400):
                    //   new->y = tileyoff - (tiley-y)*32 - 97;
                    //   new->x = sprite.x*32 + MAP_LEFT;
                    //   new->x += 16; new->y += 16;
                    //   new->x -= hlx;  new->y -= hly;
                    // sprite->x/y is the sprite TOP-LEFT after these shifts.
                    // For SHIP01G1 (hlx=16=MAP_BLOCKSIZE/2), x cancels to
                    // `sprite.x*32 + MAP_LEFT` — what we already compute. For Y,
                    // hly=12 != 16 so we owe `+16 - hly = +4`. Generalised:
                    int spawnX = cur.X * MAP_BLOCKSIZE + MAP_LEFT
                                 + MAP_BLOCKSIZE / 2 - meta.HalfX;
                    int mapY   = _tileyoff - ((tiley - cur.Y) * MAP_BLOCKSIZE) - 97
                                 + MAP_BLOCKSIZE / 2 - meta.HalfY;
                    _enemies.Add(new EnemyLogic(meta, spawnX, mapY));
                }

                _spawnIdx++;
                if (_spawnIdx >= _mapSprites.Count) { _endWaveFlag = true; break; }
                // link==-1 (EMPTY) or ==1 → end of group
                if (oldLink == -1 || oldLink == 1) break;
                // link==0 → continue group (next sprite is part of this group, regardless of y)
            }
        }
    }

    public override void _PhysicsProcess(double _)
    {
        if (_pendingGameEnterFrame >= 0 && SimClock.Frame >= _pendingGameEnterFrame)
            ApplyPendingGameEnter();
        if (_pendingDemoStartFrame >= 0 && SimClock.Frame >= _pendingDemoStartFrame)
            ApplyPendingDemoStart();
        if (_playerDeathCountdown == -2)
        {
            _playerDeathCountdown = -1;
            _waveActive = false;
            _menu?.PlayerDied(SimClock.Frame);
            return;
        }
        if (!_waveActive) return;

        // Iter 0 fires synchronously in OnGameEnter (matches C: ENEMY_Think
        // runs before GFX_FadeIn). After iter 0, hold for FadeInHoldFrames
        // frames to mirror C's blocking palette fade-in. Then run at strict
        // 3 fc/iter (the C steady-state cadence confirmed by position dumps).
        int fadeHoldFrames = _demoReplay != null ? DemoFadeInHoldFrames : FadeInHoldFrames;
        if (SimClock.Frame - _gameEnterFc < fadeHoldFrames) return;

        _subTick++;
        if (_subTick < 3) return;
        _subTick = 0;
        _scheduler.Tick();
        _gameLoopIter++;
        OnIterEnd?.Invoke();
    }

    // ── Phase implementations (called by GamePhaseScheduler) ──────────────────

    internal void PhaseInput()
    {
        if (_demoReplay != null)
        {
            if (_demoRecordIndex >= _demoReplay.Records.Count)
            {
                _demoReplay = null;
                _waveActive = false;
                return;
            }

            var frame = _demoReplay.Records[_demoRecordIndex++];
            if (_debugDemoReplay && _demoRecordIndex <= 40)
                GD.Print($"demo tick fc={SimClock.Frame} rec={_demoRecordIndex - 1} px={frame.Px} py={frame.Py}");
            PlayerLogic.ApplyDemoFrame(frame.Px, frame.Py, frame.PlayerPic);
            ApplyDemoButtons(frame);
            Shooter.TickCooldowns();
            return;
        }

        // Under a parity playthrough, scripted `down NAME`/`up NAME` lines
        // populate PlaythroughDriver's held-key set. Without a playthrough,
        // consume the real Godot InputMap state from InteractiveInputController.
        var interactive = _testInteractiveInput
                          ?? (_interactiveInput?.Active == true ? _interactiveInput.Current : InputState.Idle);
        var input = LiveInputLogic.Resolve(
            usePlaythrough: _playthrough?.Active == true,
            playthroughDx: _playthrough?.PlayerInputX ?? 0,
            playthroughDy: _playthrough?.PlayerInputY ?? 0,
            playthroughFire: _playthrough?.IsFireHeld ?? false,
            playthroughSpecial: _playthrough?.IsFireSpHeld ?? false,
            playthroughMega: _playthrough?.IsMegaHeld ?? false,
            interactive);
        // During end-of-wave fly-off, C calls IPT_PauseControl(TRUE) and the
        // ship's motion comes entirely from RAP_DisplayStats' IPT_FMovePlayer
        // (applied later in PhaseCleanup). Zero out directional input so the
        // forced glide isn't fought by residual velocity.
        if (EndWaveSequence.InputLocked(_endWaveCountdown))
            PlayerLogic.Tick(0, 0);
        else
            PlayerLogic.Tick(input.Dx, input.Dy);

        // RAP.C SC_1..SC_MINUS — script-issued special-weapon switches.
        // Mirrors OBJS_MakeSpecial: silently ignored if the type isn't owned.
        if (_playthrough?.Active == true)
        {
            while (_playthrough.TryDequeueSpecialSelect(out var w))
                Shooter.SelectSpecial(w);
        }
        else
        {
            while (_interactiveInput?.TryDequeueSpecialSelect(out var w) == true)
                Shooter.SelectSpecial(w);
            while (_testSpecialSelects.Count > 0)
                Shooter.SelectSpecial(_testSpecialSelects.Dequeue());
        }

        // Mirrors RAP.C:1000 BUT_1 → OBJS_Use(S_FORWARD_GUNS/...) cascade. Order
        // matches C: fire happens BEFORE SHOTS_Think runs (which decrements
        // cooldowns), so the cooldown set this tick can't be cleared in the
        // same tick. C resets BUT_1=FALSE after firing — our edge model uses
        // the held state, which is what the demo records (b1 is a held flag).
        ApplyLiveButtons(input.B1, input.B2, input.B3);

        // SHOTS.C:1035-1040 — cooldown decrement once per game iter. Done at
        // the end of input so the fire above sees the C-state cur_shoot.
        Shooter.TickCooldowns();
    }

    private void ApplyLiveButtons(bool fireHeld, bool fireSpHeld, bool megaHeld)
    {
        int cx = PlayerLogic.X + 16;
        int cy = PlayerLogic.Y + 16;

        if (_waveActive && fireHeld)
        {
            var fired = Shooter.ApplyButton1(cx, cy, PlayerLogic.Pic, _weaponTargetEnemies, _shooterRng);
            foreach (var b in fired) _playerBullets.Add(b);
        }

        LiveInputLogic.ApplySpecialCycle(Shooter, fireSpHeld, ref _inputB2Latch);

        if (megaHeld)
        {
            if (!_inputB3Latch)
            {
                _inputB3Latch = true;
                var fired = new List<BulletLogic>(1);
                if (_waveActive
                    && Shooter.MegaBombCount > 0
                    && Shooter.Shoot(WeaponType.MegaBomb, cx, cy, PlayerLogic.Pic, fired, _enemies, _shooterRng))
                {
                    Shooter.ConsumeMegaBomb();
                    foreach (var b in fired) _playerBullets.Add(b);
                }
            }
        }
        else
        {
            _inputB3Latch = false;
        }
    }

    private void ApplyDemoButtons(DemoReplay.Frame frame)
    {
        int cx = PlayerLogic.X + 16;
        int cy = PlayerLogic.Y + 16;

        if (frame.B1 != 0)
        {
            var fired = Shooter.ApplyButton1(cx, cy, PlayerLogic.Pic, _weaponTargetEnemies, _shooterRng);
            foreach (var b in fired) _playerBullets.Add(b);
        }

        if (frame.B2 != 0)
        {
            if (!_demoB2Latch)
            {
                _demoB2Latch = true;
                Shooter.CycleSpecial();
            }
        }
        else
        {
            _demoB2Latch = false;
        }

        if (frame.B3 != 0)
        {
            if (!_demoB3Latch)
            {
                _demoB3Latch = true;
                var fired = new List<BulletLogic>(1);
                if (Shooter.MegaBombCount > 0
                    && Shooter.Shoot(WeaponType.MegaBomb, cx, cy, PlayerLogic.Pic, fired, _enemies, _shooterRng))
                {
                    Shooter.ConsumeMegaBomb();
                    foreach (var b in fired) _playerBullets.Add(b);
                }
            }
        }
        else
        {
            _demoB3Latch = false;
        }
    }

    internal void SetInteractiveInputForTest(InputState input)
    {
        _testInteractiveInput = input;
    }

    internal void QueueSpecialSelectForTest(WeaponType weapon)
    {
        _testSpecialSelects.Enqueue(weapon);
    }

    internal void PhaseSpawn()
    {
        if (_mapSprites == null || _slib == null || _endWaveFlag) return;

        RefreshTileSliceForThink();
        ProcessTileDelayExplosions();

        // This method combines Godot's spawn phase with C's TILE_Think scroll
        // advance. The collision tile slice above intentionally stays at the
        // pre-scroll tspots for the current SHOTS pass; _tilepos/_tileyoff are
        // advanced here for subsequent spawning/scroll state.
        SpawnForTiley(_tiley);

        _tileyoff++;
        if (_tileyoff > 0)
        {
            _tileyoff -= MAP_BLOCKSIZE;
            _tilepos  -= MAP_COLS;
            _tiley     = _tilepos / MAP_COLS - 3;
            if (_tilepos <= 0) _tilepos = 0;
        }
    }

    private void RefreshTileSliceForThink()
    {
        if (_tileSlice.Count == 0 || _tileSliceTilePos != _tilepos)
        {
            RebuildTileSlice();
            return;
        }

        // C TILE_Think writes tspots using the current tileyoff, then advances
        // tileyoff at the end of the same function. Later SHOTS_Think collides
        // against those pre-scroll tspots, so update the collision slice at the
        // start of PhaseSpawn and leave it unchanged after scrolling.
        for (int i = 0; i < _tileSlice.Count; i++)
            _tileSlice[i].ScreenY = _tileyoff + (i / MAP_COLS) * MAP_BLOCKSIZE;
    }

    /// <summary>
    /// Rebuild the on-screen tile slice from _mapTiles, _tilepos, _tileyoff,
    /// and _flatLib. Mirrors C TILE_Think's first pass (TILE.C:343-360) which
    /// populates tspots[] with (mapspot, x, y, item) for each visible tile.
    /// </summary>
    private void RebuildTileSlice()
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
            int mapspot  = _tilepos + slot;
            var t        = _tileSlice[slot];
            t.MapSpot    = mapspot;
            t.ScreenX    = MAP_LEFT + col * MAP_BLOCKSIZE;
            t.ScreenY    = _tileyoff + row * MAP_BLOCKSIZE;
            if (_mapTiles == null || mapspot < 0 || mapspot >= _mapTiles.Count ||
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
        _tileSliceTilePos = _tilepos;
    }

    private void InitializeTileBacking()
    {
        int count = _mapTiles?.Count ?? 0;
        _tileHitsByMapSpot = new int[count];
        _tileDeadByMapSpot = new bool[count];
        _tileDestructibleByMapSpot = new bool[count];
        _tileBountyByMapSpot = new int[count];

        if (_mapTiles == null || _flatLib == null) return;
        for (int mapspot = 0; mapspot < _mapTiles.Count; mapspot++)
        {
            int flatIdx = _mapTiles[mapspot].Flats;
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

    private void SyncTileSliceToBacking()
    {
        if (_tileHitsByMapSpot == null || _tileDeadByMapSpot == null) return;
        foreach (var t in _tileSlice)
        {
            if (t.MapSpot < 0 || t.MapSpot >= _tileHitsByMapSpot.Length) continue;
            _tileHitsByMapSpot[t.MapSpot] = t.Hits;
            _tileDeadByMapSpot[t.MapSpot] = t.Dead;
        }
    }

    private void RefreshTileSliceValuesFromBacking()
    {
        if (_tileHitsByMapSpot == null || _tileDeadByMapSpot == null) return;
        foreach (var t in _tileSlice)
        {
            if (t.MapSpot < 0 || t.MapSpot >= _tileHitsByMapSpot.Length) continue;
            t.Hits = _tileHitsByMapSpot[t.MapSpot];
            t.Dead = _tileDeadByMapSpot[t.MapSpot];
        }
    }

    private void ApplyTileExplosionDamage(int mapspot, int damage)
    {
        if (_tileHitsByMapSpot == null || _tileDeadByMapSpot == null ||
            _tileDestructibleByMapSpot == null)
            return;

        int ix = mapspot % MAP_COLS;
        ApplyTileExplosionNeighbor(mapspot - 1, ix - 1, damage);
        ApplyTileExplosionNeighbor(mapspot - MAP_COLS, ix, damage);
        ApplyTileExplosionNeighbor(mapspot + 1, ix + 1, damage);
    }

    private void ApplyTileExplosionNeighbor(int spot, int x, int damage)
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
            SpawnTileExplosion(spot);
            ApplyTileExplosionDamage(spot, damage: 5);
            ScheduleTileDelayExplosion(spot);
        }
    }

    private void ScheduleTileDelayExplosion(int mapspot)
    {
        _tileDelayExplosions.Add(new TileDelayExplosion(mapspot, 10));
    }

    private void ProcessTileDelayExplosions()
    {
        for (int i = 0; i < _tileDelayExplosions.Count; i++)
        {
            var td = _tileDelayExplosions[i];
            if (td.Frames < 0)
            {
                ApplyTileExplosionDamage(td.MapSpot, damage: 20);
                _tileDelayExplosions.RemoveAt(i);
                i--;
                continue;
            }

            _tileDelayExplosions[i] = td with { Frames = td.Frames - 1 };
        }
        RefreshTileSliceValuesFromBacking();
    }

    internal void PhaseMovement()
    {
        _playerWasAliveAtCollisionStart = PlayerLogic.Alive;

        // C aims ATPLAYER bullets at player CENTER (player_cx/cy = playerx + PLAYERWIDTH/2, playery + PLAYERHEIGHT/2).
        // Pass center coords so enemy.Tick / MakeBullet hands the bullet's Bresenham its true target.
        int px = PlayerLogic.X + 16;
        int py = PlayerLogic.Y + 16;

        // Tick each alive enemy; collect any bullets they fire.
        // C order: ENEMY_Think fires bullets, then ESHOT_Think ticks them (same frame).
        for (int i = 0; i < _enemies.Count; i++)
        {
            var enemy = _enemies[i];
            if (!enemy.Alive && !enemy.PendingRemovalDump) continue;
            var fired = enemy.Tick(px, py);
            if (fired != null)
                AddEnemyBullet(fired);
            // Multi-gun enemies (helicopters numguns=2, bosses up to 13) fire
            // one bullet per gun per shot tick — collect the extras.
            var extras = enemy.ExtraBulletsThisTick;
            if (extras != null)
                foreach (var b in extras) AddEnemyBullet(b);
        }

        // C handles body collision and enemy death side effects inside
        // ENEMY_Think, before BONUS_Think. A shot-killed enemy can still
        // body-crash this iter, then drop a bonus that immediately gets the
        // same BONUS_Think drift/pickup opportunity.
        ApplyBodyCrashCollisions(px, py);
        ProcessPendingEnemyRemovalsForParity();

        RebuildWeaponTargetSnapshot();

        // C reaches shot_done after the damage switch. Straight shots set both
        // move.done and doneflag when leaving the screen, so they remove before
        // damage. use_plot player shots set only move.done, so they get one
        // final collision pass at their current move.x/y target before removal
        // or delay re-targeting.
        _shotDoneAfterCollision.Clear();
        foreach (var b in _playerBullets)
        {
            if (!b.Alive || !b.PendingShotDone) continue;
            if (b.ReachedTarget && !b.DeferredDoneFlag && b.IsPlayerAimedBresenham)
            {
                b.SnapshotForPendingShotDonePass();
                _shotDoneAfterCollision.Add(b);
                continue;
            }

            HandleShotDone(b);
            b.ClearReachedTarget();
        }

        // SHOTS.C:1090-1098 — re-position player-tracking beams (fplrx/fplry)
        // to follow the ship each iter BEFORE the per-bullet Tick. Uses the
        // current player_cx/cy (top-left + half-dims), not the same coords as
        // the shot_done re-init above (which uses move.x).
        int pcx = PlayerLogic.X + 16;
        int pcy = PlayerLogic.Y + 16;
        foreach (var b in _playerBullets)
        {
            if (!b.Alive || !b.TracksPlayer) continue;
            b.ApplyFplr(pcx, pcy);
        }

        // Tick player bullets.
        foreach (var b in _playerBullets)
        {
            if (_shotDoneAfterCollision.Contains(b)) continue;
            b.Tick();
        }

        // Tick bonuses (BONUS_Think — drift down 1 px/iter, despawn at y > 200).
        // The wobble/sprite-frame advance fires on the global (gcnt & 1) phase,
        // shared across all bonuses; gcnt increments once per pass (BONUS.C:288).
        bool bonusAdvance = (_bonusThinkCnt & 1) != 0;
        foreach (var bn in _bonuses) bn.Tick(bonusAdvance);
        _bonusThinkCnt++;

        // Tick enemy bullets (includes newly fired ones from this frame, matching C's
        // ESHOT_Think which runs after ENEMY_Think in the same game-loop iteration).
        foreach (var b in _enemyBullets)
        {
            // ES_LASER (ESHOT.C:453-470): tracks the firing enemy, lives 4 passes,
            // and damages the player directly on horizontal alignment (NOT via the
            // AABB collision phase, which skips lasers). Mirrors C applying
            // OBJS_SubEnergy inside ESHOT_Think rather than at collision time.
            if (b.IsEnemyLaser)
            {
                int laserDmg = b.LaserTick(pcx, pcy);
                if (laserDmg > 0) PlayerLogic.TakeDamage(laserDmg);
                continue;
            }
            b.Tick();
            // ESHOT.C:534-539: shot->cnt++ then if (smokeflag && cnt&1) spawn
            // A_SMALL_SMOKE_UP at (shot->x + xoff, shot->y). For us the smoke
            // is a sentinel Explosion record with the dedicated SmokeExpType
            // so the view can age its SMOKTRAL_BLK frames over time.
            if (b.Alive && b.ShotType == EnemyShotType.Missile && (b.FrameCounter & 1) != 0)
                AddExplosion(SmokeExpType, b.X + 4, b.Y);
        }
    }

    // Sentinel exptype used only for missile smoke trails; the view maps it
    // to SMOKTRAL_BLK (ANIMS.C:203 A_SMALL_SMOKE_UP). Out of the EXP_ enum
    // range so it never collides with a real EXP_ value.
    private const int SmokeExpType = 100;
    private const int SparkBlueExpType = 101;
    private const int SparkOrangeExpType = 102;

    /// <summary>
    /// Per-type explosion lifetime in game-loop iters — the source of truth for
    /// when a sim explosion entity is culled, mirroring ANIMS_Register numframes
    /// (ANIMS.C:187-218). Each anim type lives a different number of frames; the
    /// renderer stops drawing at the same count, so culling here at the true
    /// length (instead of a flat 50) removes the entity exactly when it stops
    /// being visible. Mirrors the view's ExpAnim table (DebugRenderer.cs).
    /// </summary>
    internal static int AnimFramesFor(int expType) => expType switch
    {
        0  => 13,   // EXP_AIRSMALL1 → EXPLO2_BLK
        1  => 12,   // EXP_AIRMED    → LGFLAK_BLK
        2  => 12,   // EXP_AIRLARGE  → LGFLAK_BLK
        3  => 42,   // EXP_GRDSMALL  → GEXPLO_BLK
        4  => 42,   // EXP_GRDMED    → GEXPLO_BLK
        5  => 42,   // EXP_GRDLARGE  → GEXPLO_BLK
        8  => 12,   // EXP_ENERGY    → NRGBANG_BLK
        10 => 14,   // EXP_AIRSMALL2 → SMFLAK_BLK
        SmokeExpType => 4,        // SMOKTRAL_BLK
        SparkBlueExpType => 9,    // BSPARK_BLK
        SparkOrangeExpType => 9,  // OSPARK_BLK
        _ => 13,                  // EXPLO2_BLK default (unused EXP_ slots 6/7/9)
    };

    private void AddExplosion(int expType, int x, int y, int startDelayIters = 0)
    {
        int startIter = AnimationStartIterForSpawn(_gameLoopIter) + startDelayIters;
        _explosions.Add(new Explosion(expType, x, y, startIter));
    }

    internal void PhaseCollisionCollect()
    {
        // Enemy bullets vs player AABB.
        // C uses `dx < PLAYERWIDTH/2 && dy < PLAYERHEIGHT/2` (strict less-than).
        // PLAYERWIDTH = 32, PLAYERHEIGHT = 32 → half = 16.
        // C collision uses player_cx/player_cy = playerx + PLAYERWIDTH/2, playery + PLAYERHEIGHT/2.
        // PlayerLogic.X/Y are playerx/playery (top-left). Add half-dimensions for center.
        const int playerHw = 16; const int playerHh = 16;
        int px = PlayerLogic.X + playerHw;   // player_cx = playerx + PLAYERWIDTH/2
        int py = PlayerLogic.Y + playerHh;   // player_cy = playery + PLAYERHEIGHT/2

        _playerHit = false;
        _playerHitDmg = 0;

        foreach (var b in _enemyBullets)
        {
            if (!b.Alive) continue;
            // ES_LASER applies its own alignment-based damage in the tick phase
            // (ESHOT.C:462-465); it is not an AABB hit. Skip it here.
            if (b.IsEnemyLaser) continue;
            int dx = Math.Abs(b.X - px);
            int dy = Math.Abs(b.Y - py);
            if (dx < playerHw && dy < playerHh)
            {
                b.Kill();
                _playerHit = true;
                _playerHitDmg += b.Damage;  // use per-bullet damage from ESHOT_LIB
                // Mirror ESHOT.C:521: ANIMS_StartAnim(A_SMALL_AIR_EXPLO, shot->x, shot->y).
                // A small orange flash appears at the impact point — visible in C
                // wherever a bullet clips the player ship.
                AddExplosion(ExpAirSmall2, b.X, b.Y);
            }
        }

        // Player bullets vs enemy/tile collision. C SHOTS.C dispatches this as
        // per-hit-type if/else chains, so S_ALL/S_GROUND only test tiles after
        // the corresponding enemy damage call fails for that same bullet.
        // Beams are handled in a separate pass below (different damage model).
        _hitEnemies.Clear();
        PlayerBulletCollisionDispatcher.TraceIter = _gameLoopIter;
        TileDamageDispatcher.TraceIter = _gameLoopIter;
        var collision = PlayerBulletCollisionDispatcher.Collect(_playerBullets, _enemies, _tileSlice, MAP_COLS);
        foreach (var (x, y) in collision.RandomSparkPositions)
        {
            int spark = PlayerShooter.NextRandom(_shooterRng, 2, "spark.hit_color");
            AddExplosion(spark != 0 ? SparkBlueExpType : SparkOrangeExpType, x, y);
        }
        foreach (var (x, y) in collision.OrangeSparkPositions)
            AddExplosion(SparkOrangeExpType, x, y);
        foreach (var (x, y) in collision.BlueSparkPositions)
            AddExplosion(SparkBlueExpType, x, y);
        if (collision.TileBounty > 0) Score += (uint)collision.TileBounty;
        SyncTileSliceToBacking();
        foreach (int mapspot in collision.DestroyedTileMapSpots)
        {
            SpawnTileExplosion(mapspot);
            ApplyTileExplosionDamage(mapspot, damage: 5);
            ScheduleTileDelayExplosion(mapspot);
        }
        RefreshTileSliceValuesFromBacking();

        // Beam-vs-enemy column damage. SHOTS.C:1068-1088 — for each VerticalBeam,
        // find the first enemy whose x range contains the beam X and whose
        // y < player_cy (above the player) and y > -30. Damage it by the beam's
        // per-shot hits; the beam does NOT despawn on hit. LineBeam (TURRET)
        // bullets have BeamDamages=false because PlayerShooter already applied
        // damage at spawn — they exist only for the visual.
        foreach (var b in _playerBullets)
        {
            if (!b.Alive || !b.IsBeam || !b.BeamDamages) continue;
            foreach (var e in _enemies)
            {
                if (!e.Alive) continue;
                if (!HitTypeMatches(b.HitType, e)) continue;
                int ex  = e.X;
                int ex2 = e.X + 2 * e.HalfW - 1;
                if (b.X > ex && b.X < ex2 && e.Y < py && e.Y > -30)
                {
                    _hitEnemies.Add((e, b.Damage));
                    break;
                }
            }
        }

        // Bonus pickup. BONUS.C:207 — `cur->x > playerx && cur->x < playerx+PW
        // && cur->y > playery && cur->y < playery+PH`. The bonus CENTER (cur->x,
        // cur->y) must be inside the player's top-left-anchored rect.
        int plx = PlayerLogic.X;
        int ply = PlayerLogic.Y;
        bool playerAlive = PlayerLogic.Alive;
        _pickedUpBonuses.Clear();
        foreach (var bn in _bonuses)
        {
            if (BonusCollectible(playerAlive, bn, plx, ply))
                _pickedUpBonuses.Add(bn);
        }
    }

    /// <summary>
    /// Bonus pickup eligibility (BONUS.C:242-244): the overlap AABB
    /// (BonusLogic.CanBePickedUpBy) AND the C gate `OBJS_GetAmt(S_ENERGY) > 0`,
    /// i.e. the player must be alive (shield > 0). A depleted/dead ship cannot
    /// collect bonuses — the overlap is skipped entirely while shield == 0.
    /// </summary>
    internal static bool BonusCollectible(bool playerAlive, Raptor.Sim.Bonus.BonusLogic bn, int plx, int ply)
        => playerAlive && bn.CanBePickedUpBy(plx, ply);

    /// <summary>True when the player has picked up an S_DETECT bonus.
    /// Used by the view's secrets-locator UI. Persists across waves
    /// (C's p_objs[S_DETECT] survives RAP_LoadMap).</summary>
    public bool HasSecretsDetector { get; private set; }

    /// <summary>
    /// Returns true iff a bullet with the given HitType can damage this enemy.
    /// Mirrors the SHOTS.C SHOTS_Think `case lib->ht` branches:
    ///   S_ALL / S_GRALL — damage anything (ground OR air)
    ///   S_AIR           — air enemies only (FlightType not 3/4/5)
    ///   S_GROUND        — ground enemies only (FlightType 3/4/5)
    ///   S_GTILE         — only ground enemies (tiles handled separately)
    ///   S_SUCK          — energy-grab path; not yet wired
    /// </summary>
    private static bool HitTypeMatches(HitType ht, EnemyLogic e) => ht switch
    {
        HitType.All    => true,
        HitType.GrAll  => true,
        HitType.Air    => !e.IsGround,
        HitType.Ground => e.IsGround,
        HitType.GTile  => e.IsGround,
        HitType.Suck   => true,
        _              => true,
    };

    private void ApplyBodyCrashCollisions(int playerCx, int playerCy)
    {
        const int playerWidth2 = 16;   // PLAYERWIDTH/2 — subtracted from enemy hits on body crash

        _bodyCrashEnemies.Clear();
        foreach (var e in _enemies)
        {
            if (!e.Alive && !e.PendingRemovalDump) continue;
            if (EnemyBodyCrashContainsPlayer(e, playerCx, playerCy))
                _bodyCrashEnemies.Add(e);
        }

        foreach (var e in _bodyCrashEnemies)
        {
            if (!e.Alive && !e.PendingRemovalDump) continue;
            bool wasAlive = e.Alive;
            e.TakeDamage(playerWidth2);
            int bodyDmg = e.Meta.BodyCrashDamage;
            PlayerLogic.TakeDamage(bodyDmg);
            AddExplosion(ExpAirSmall2, PlayerLogic.X + 16, PlayerLogic.Y + 16);
            if (wasAlive && !e.Alive)
            {
                Score += (uint)e.Meta.Money;
                ConsumeEnemyDeathSoundRandom();
                SpawnExplosion(e);
                SpawnBonusFor(e);
            }
        }

        _bodyCrashEnemies.Clear();
    }

    internal static bool EnemyBodyCrashContainsPlayer(EnemyLogic e, int playerCx, int playerCy)
    {
        // ENEMY.C:1043 — `if (!sprite->groundflag)` guards body collision.
        // F_GROUND family (FlightType 3/4/5) sets groundflag=TRUE in C, so
        // ground enemies (bonuses, turrets, tanks) never crash with the player.
        if (e.Meta.FlightType >= 3 && e.Meta.FlightType <= 5) return false;
        int ex  = e.X;                    // sprite->x (top-left)
        int ex2 = e.X + 2 * e.HalfW - 1; // sprite->x2 (= sprite->x + width - 1)
        int ey  = e.Y;                    // sprite->y (top-left)
        int ey2 = e.Y + 2 * e.HalfH - 1; // sprite->y2 (= sprite->y + height - 1)
        return playerCx > ex && playerCx < ex2 && playerCy > ey && playerCy < ey2;
    }

    private void HandleShotDone(BulletLogic b)
    {
        var result = ShotDoneDispatcher.Dispatch(b, _enemyBullets, _enemies, _shooterRng, _tileSlice);
        if (result.TileBounty > 0) Score += (uint)result.TileBounty;
        SyncTileSliceToBacking();
    }

    private void ApplyBonusEffect(int objType)
    {
        var r = Bonus.BonusEffectDispatcher.Apply(objType, Shooter, PlayerLogic.MaxShield);
        if (r.HealAmount > 0)       PlayerLogic.Heal(r.HealAmount);
        if (r.ScoreAdd > 0)         Score += r.ScoreAdd;
        if (r.DetectorActivated)    HasSecretsDetector = true;
    }

    internal void PhaseCollisionResolve()
    {
        // Apply player damage from enemy bullets.
        if (_playerHit && _playerHitDmg > 0)
        {
            PlayerLogic.TakeDamage(_playerHitDmg);
        }

        // Apply enemy damage from player bullets.
        foreach (var (enemy, dmg) in _hitEnemies)
        {
            enemy.TakeDamage(dmg, deferRemovalForDump: true);
        }

        foreach (var b in _shotDoneAfterCollision)
        {
            if (!b.Alive || !b.ReachedTarget) continue;
            HandleShotDone(b);
            b.ClearReachedTarget();
        }
        _shotDoneAfterCollision.Clear();

        // Apply bonus pickup effects collected this tick (weapon → inventory,
        // S_ENERGY → heal). C's BONUS.C:208-216 path; we batch in
        // CollisionResolve so all death/pickup events fire after collection.
        foreach (var b in _pickedUpBonuses)
        {
            TraceBonus("pickup", b);
            ApplyBonusEffect(b.ObjType);
            if (Bonus.BonusEffectDispatcher.IsMoneyBonus(b.ObjType))
            {
                b.MarkPickedUpMoney();
                TraceBonus("pickup_money", b);
            }
            else
            {
                b.Kill();
                TraceBonus("pickup_remove", b);
            }
        }
        _pickedUpBonuses.Clear();

        if (_playerWasAliveAtCollisionStart && !PlayerLogic.Alive)
        {
            _playerDeathCountdown = EndDuration;
        }
    }

    internal const int EndDuration = 20 * 3;
    internal const int EndExplode = 24;

    // C exptype constants used for cosmetic-only explosion events (SOURCE/MAP.H).
    internal const int ExpAirSmall1 = 0;  // EXP_AIRSMALL1 → EXPLO2_BLK
    internal const int ExpAirLarge  = 2;  // EXP_AIRLARGE → LGFLAK_BLK
    private const int ExpGrdLarge  = 5;   // EXP_GRDLARGE → GEXPLO_BLK
    private const int ExpEnergy    = 8;   // EXP_ENERGY → NRGBANG_BLK + S_ITEMBUY6 bonus
    internal const int ExpAirSmall2 = 10; // EXP_AIRSMALL2 → SMFLAK_BLK
    internal const int ExpAirMed2   = 10; // A_MED_AIR_EXPLO2 uses SMFLAK_BLK in ANIMS.C.
    private const int ItemBuy6ObjType = 23;

    internal readonly record struct DeathExplosion(int ExpType, int X, int Y);

    internal static List<DeathExplosion> BuildPlayerDeathExplosions(
        int playerX,
        int playerY,
        int countdown,
        Random rng)
    {
        var explosions = new List<DeathExplosion>
        {
            new(ExpAirSmall1, playerX + PlayerShooter.NextRandom(rng, 32, "death.med.x"),
                playerY + PlayerShooter.NextRandom(rng, 32, "death.med.y")),
            new(ExpAirSmall2, playerX + PlayerShooter.NextRandom(rng, 32, "death.small.x"),
                playerY + PlayerShooter.NextRandom(rng, 32, "death.small.y")),
        };

        if (countdown == EndExplode)
        {
            explosions.Add(new DeathExplosion(ExpAirLarge, playerX + 16, playerY + 16));
            for (int i = 0; i < (PlayerLogic.SpriteWidth * PlayerLogic.SpriteHeight) / 2; i++)
            {
                int x = playerX - PlayerLogic.SpriteWidth / 2
                        + PlayerShooter.NextRandom(rng, PlayerLogic.SpriteWidth * 2, "death.burst.x");
                int y = playerY - PlayerLogic.SpriteHeight / 2
                        + PlayerShooter.NextRandom(rng, PlayerLogic.SpriteHeight * 2, "death.burst.y");
                explosions.Add(new DeathExplosion((i & 1) != 0 ? ExpAirLarge : ExpAirMed2, x, y));
            }
        }

        return explosions;
    }

    // Spawn explosion(s) at the enemy's death position. Mirrors ENEMY.C:1066-1115
    // — primary explosion at (x+hlx, y+hly), and for EXP_AIRLARGE the C code
    // also fires (width/16 * height/16) medium explosions at random offsets
    // inside the sprite bounds. We use a deterministic pattern (no RNG) so
    // we never consume sim entropy.
    //
    // ENEMY.C:1154-1155 ALSO drops a BONUS if the sprite's lib->bonus field
    // is set. That spawn happens here for cohesion: any path that called
    // SpawnExplosion also wants the C drop side-effect.
    private const int ExpAirLargeCode = 2;  // EXP_AIRLARGE (SOURCE/MAP.H)
    internal static int BonusSpawnXFromEnemyX(int enemyX) => enemyX + 16; // BONUS_Add adds MAP_LEFT.
    internal static int? BonusForExplosionType(int expType) => expType == ExpEnergy ? ItemBuy6ObjType : null;

    private void ConsumeEnemyDeathSoundRandom()
    {
        // ENEMY.C plays SND_3DPatch(FX_AIREXPLO, ...) before dispatching the
        // explosion animation. FX_AIREXPLO has random pitch, so this consumes
        // the shared rand() stream even when audio output is muted.
        PlayerShooter.NextRandom(_shooterRng, 40, "sound3d.fx_airexplo");
    }

    private void ProcessPendingEnemyRemovalsForParity()
    {
        // C applies player-shot damage in SHOTS_Think, but enemy removal,
        // score, death sound, explosion, and bonus spawn happen later in the
        // next ENEMY_Think pass. PendingRemovalDump keeps the dead sprite
        // visible in the current parity dump; this method performs the C
        // removal side effects at the start of the following movement phase.
        foreach (var e in _enemies)
        {
            if (e.Alive || !e.PendingRemovalDump) continue;
            Score += (uint)e.Meta.Money;
            ConsumeEnemyDeathSoundRandom();
            SpawnExplosion(e);
            SpawnBonusFor(e);
            e.ClearPendingRemovalDump();
        }
    }

    private void RebuildWeaponTargetSnapshot()
    {
        // C ENEMY_GetRandom samples the onscreen[] array populated by the last
        // ENEMY_Think pass, while player weapon use happens earlier in the next
        // game-loop body. Keep that phase boundary explicit instead of passing
        // the live enemy list directly to MiniGun/Turret targeting.
        _weaponTargetEnemies.Clear();
        foreach (var e in _enemies)
        {
            if (!e.Alive && !e.PendingRemovalDump) continue;
            if (e.Y + e.Meta.Height <= 0 || e.Y >= 200) continue;
            if (e.X + e.Meta.Width <= 0 || e.X >= 320) continue;
            _weaponTargetEnemies.Add(e);
        }
    }

    private void AddEnemyBullet(BulletLogic bullet)
    {
        ConsumeEnemyShotSoundRandomForParity(_shooterRng, bullet.ShotType);
        if (bullet.ShotType == EnemyShotType.Mines)
            bullet.SetEnemyMinePos(PlayerShooter.NextRandom(_shooterRng, 16, "enemy.mine.pos"));
        _enemyBullets.Add(bullet);
    }

    internal static void ConsumeEnemyShotSoundRandomForParity(System.Random? rng, EnemyShotType shotType)
    {
        // ESHOT.C calls SND_3DPatch for every enemy shot. All enemy shot FX
        // entries used here set rpflag=TRUE in FX.C, so they consume random(40)
        // for pitch even when the game is running with dummy/no audio.
        if (shotType == EnemyShotType.Coconuts)
            PlayerShooter.NextRandom(rng, 6, "sound3d.coconut.pick");

        string label = shotType switch
        {
            EnemyShotType.Missile => "sound3d.fx_enemymissle",
            EnemyShotType.Laser => "sound3d.fx_enemylaser",
            EnemyShotType.Plasma => "sound3d.fx_enemyplasma",
            EnemyShotType.Coconuts => "sound3d.fx_coconut",
            _ => "sound3d.fx_enemyshot",
        };
        PlayerShooter.NextRandom(rng, 40, label);
    }

    // ENEMY.C:391-392 arms startendwave = END_DURATION the instant the last
    // enemy is removed during an end-wave — there is NO explosion/ANIMS gate.
    // RAP.C:1039-1046 then just counts down. Explosions keep ticking/rendering
    // during the fly-off; they never block the wave from ending.
    internal static bool ShouldCompleteMission(bool waveActive,
                                               bool demoActive,
                                               bool endWave,
                                               bool playerAlive,
                                               bool enemiesRemaining) =>
        waveActive
        && !demoActive
        && endWave
        && playerAlive
        && !enemiesRemaining;

    private void SpawnTileExplosion(int mapspot)
    {
        foreach (var tile in _tileSlice)
        {
            if (tile.MapSpot != mapspot) continue;
            AddExplosion(ExpGrdLarge, tile.ScreenX + 16, tile.ScreenY + 16);
            return;
        }
    }

    private void SpawnBonusFor(EnemyLogic e)
    {
        if (e.Meta.Bonus < 0) return;
        SpawnBonus(e.Meta.Bonus, e.X, e.Y);
    }

    private void SpawnBonus(int objType, int enemyX, int enemyY)
    {
        // C: BONUS_Add(type, sprite->x, sprite->y), then BONUS_Add stores
        // cur->x = x + MAP_LEFT. The bonus X is therefore enemy x + 16.
        int initialPos = PlayerShooter.NextRandom(_shooterRng, 16, "bonus.pos");
        var bonus = new BonusLogic(objType, BonusSpawnXFromEnemyX(enemyX), enemyY, initialPos);
        _bonuses.Add(bonus);
        TraceBonus("add", bonus);
    }

    private void TraceBonus(string eventName, BonusLogic b)
    {
        OnBonusTrace?.Invoke(BonusTraceLine(eventName, b));
    }

    internal string BonusTraceLine(string eventName, BonusLogic b)
    {
        var (dx, dy) = View.BonusSprite.DrawOffset(b.Pos);
        int bx = b.X - BonusLogic.Width / 2 + dx;
        int by = b.Y - BonusLogic.Height / 2 + dy;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"i={GameLoopIter} event={eventName} type={b.ObjType} x={b.X} y={b.Y} bx={bx} by={by} pos={b.Pos} frame={b.Frame} glow={b.GlowFrame} d={(b.DisplayAsPickedUpMoney ? 1 : 0)} cnt={b.PickedUpMoneyCountdown} money={(Bonus.BonusEffectDispatcher.IsMoneyBonus(b.ObjType) ? 1 : 0)} px={PlayerLogic.X} py={PlayerLogic.Y}");
    }
    private void SpawnExplosion(EnemyLogic e)
    {
        int cx = e.X + e.Meta.HalfX;
        int cy = e.Y + e.Meta.HalfY;
        int startIter = AnimationStartIterForSpawn(_gameLoopIter);
        AddExplosion(e.Meta.ExpType, cx, cy);
        if (BonusForExplosionType(e.Meta.ExpType) is { } bonusType)
            SpawnBonus(bonusType, e.X, e.Y);
        if (e.Meta.ExpType == ExpAirLargeCode)
        {
            int w = e.Meta.Width;
            int h = e.Meta.Height;
            int count = (w >> 4) * (h >> 4);
            // Deterministic pseudo-random offsets so successive explosions land
            // at distinct positions inside the sprite. Mixing hash uses prime
            // multipliers — no RNG state mutated.
            uint hash = (uint)(e.X * 73856093 ^ e.Y * 19349663 ^ startIter * 83492791);
            for (int i = 0; i < count; i++)
            {
                hash = hash * 1103515245u + 12345u;
                int ox = (int)((hash >> 8) % (uint)System.Math.Max(1, w));
                hash = hash * 1103515245u + 12345u;
                int oy = (int)((hash >> 8) % (uint)System.Math.Max(1, h));
                int t = (i & 1) == 1 ? 1 /* EXP_AIRMED → A_MED_AIR_EXPLO */
                                     : 10 /* EXP_AIRSMALL2 → A_MED_AIR_EXPLO2 */;
                // Stagger start iteration slightly so the cascade doesn't appear
                // all at once (matches C's per-loop ANIMS_StartAnim spacing).
                AddExplosion(t, e.X + ox, e.Y + oy, i % 4);
            }
        }
    }

    internal void PhaseCleanup()
    {
        // Remove dead or out-of-bounds enemies.
        _enemies.RemoveAll(e => !e.Alive && !e.PendingRemovalDump);

        // Remove out-of-bounds bullets.
        _playerBullets.RemoveAll(b => !b.Alive);
        _enemyBullets.RemoveAll(b => !b.Alive);
        _bonuses.RemoveAll(b => !b.Alive);

        // Drop finished explosions. Frames-per-animation is determined by the
        // view's BlkInfo table; we cap at a conservative 50 frames so a missing
        // mapping can't leak an explosion forever. Smoke trails are short-
        // lived (SMOKTRAL_BLK has 4 frames) so we cull them aggressively.
        int currentIter = GameLoopIter;
        _explosions.RemoveAll(x =>
            AnimationAge(currentIter, x.StartIter) >= AnimFramesFor(x.ExpType));
        CompleteMissionIfWaveEnded();
    }

    private void CompleteMissionIfWaveEnded()
    {
        if (_missionCompleteNotified) return;
        bool enemiesRemaining = _enemies.Exists(e => e.Alive || e.PendingRemovalDump);

        // Once the wave-end conditions are met, start the fly-off countdown
        // instead of jumping straight to the hangar (mirrors RAP.C:1039-1047
        // where startendwave counts down from END_DURATION before end_wave fires).
        if (_endWaveCountdown < 0
            && ShouldCompleteMission(
                _waveActive,
                _demoReplay != null,
                _endWaveFlag,
                PlayerLogic.Alive,
                enemiesRemaining))
        {
            _endWaveCountdown = EndWaveSequence.Duration;
        }

        if (_endWaveCountdown < 0) return;

        // Per-tick fly-off displacement (RAP.C:599-617).
        var (dx, dy) = EndWaveSequence.PlayerDelta(
            _endWaveCountdown, PlayerLogic.X, PlayerLogic.Alive);
        if (dx != 0 || dy != 0)
            PlayerLogic.ApplyForcedMove(dx, dy);

        _endWaveCountdown--;
        if (_endWaveCountdown > 0) return;

        // Countdown hit zero: actually leave the wave (mirrors end_wave=TRUE
        // in RAP.C:1041-1046 ending the gameplay loop).
        _missionCompleteNotified = true;
        _waveActive = false;
        _menu?.CompleteMission(SimClock.Frame);
    }

    /// <summary>
    /// Pure-C# shield-recharge step (mirrors OBJS_Think, OBJECTS.C:1361-1368).
    /// think_cnt increments and, on crossing CHARGE_SHIELD, resets to 0; the
    /// heal itself fires only when the end sequence is inactive
    /// (startendwave == EMPTY) so the ship cannot recharge — or revive — during
    /// the death-explosion countdown or the end-of-wave fly-off.
    /// </summary>
    internal static (int thinkCnt, bool heal) ShieldRechargeStep(
        int thinkCnt, int diff, int chargeShield,
        bool deathActive, bool endWaveActive)
    {
        thinkCnt++;
        bool heal = false;
        if (diff < 3 && thinkCnt > chargeShield)
        {
            thinkCnt = 0;
            if (!deathActive && !endWaveActive) heal = true;
        }
        return (thinkCnt, heal);
    }

    internal void PhaseHud()
    {
        if (_skipInitialPaletteStuff)
        {
            _skipInitialPaletteStuff = false;
            // The skip mirrors C NOT drawing RAP_PaletteStuff's RNG on the
            // wave's first HUD pass — but C's OBJS_Think still runs that frame,
            // advancing think_cnt (the shield-recharge counter) every game loop
            // (OBJECTS.C:1361; skipped only on OBJS_Use). Dropping it here left
            // Godot's recharge 1 tick behind C, surfacing as a 1-bucket shield
            // transient (death_wave2 @ iter 1260). Advance it here too.
            var (skipTc, skipHeal) = ShieldRechargeStep(
                _thinkCnt, _curPlayerDiff, ChargeShield,
                deathActive: _playerDeathCountdown >= 0,
                endWaveActive: _endWaveCountdown >= 0);
            _thinkCnt = skipTc;
            if (skipHeal) PlayerLogic.Heal(1);
            return;
        }

        ProcessPlayerDeathExplosions();

        // RAP_PaletteStuff() consumes random(3) only on alternating calls
        // (`if (cnt & 1)`). It is visual, but C shares rand() with weapons.
        if ((_paletteStuffCnt & 1) != 0)
            PlayerShooter.NextRandom(_shooterRng, 3, "palette.stuff");
        _paletteStuffCnt++;

        // Shield recharge (mirrors OBJS_Think in OBJECTS.C).
        // CHARGE_SHIELD = 96. Every 97 game loops, heal 1 shield.
        // Only on curplr_diff < DIFF_3.
        var (newThinkCnt, heal) = ShieldRechargeStep(
            _thinkCnt, _curPlayerDiff, ChargeShield,
            deathActive: _playerDeathCountdown >= 0,
            endWaveActive: _endWaveCountdown >= 0);
        _thinkCnt = newThinkCnt;
        if (heal) PlayerLogic.Heal(1);

        bool systemDamaged = false;
        if (_oldShieldForLowLoss >= 0
            && PlayerLogic.Shield <= ShieldLow
            && PlayerLogic.Shield < _oldShieldForLowLoss)
        {
            systemDamaged = Shooter.LoseCurrentSpecialForShieldLow();
        }
        _hudWarningState.Tick(PlayerLogic.Shield, _gameLoopIter, systemDamaged);
        ShieldLowWarningVisible = _hudWarningState.ShieldLowVisible;
        SystemDamageWarningVisible = _hudWarningState.SystemDamageVisible;
        TraceShield();
        _oldShieldForLowLoss = PlayerLogic.Shield;
    }

    private void ProcessPlayerDeathExplosions()
    {
        if (_playerDeathCountdown < 0) return;

        _shooterRng ??= new LegacyRandom((int)(Rng.Seed & 0x7FFFFFFFu));
        foreach (var explosion in BuildPlayerDeathExplosions(
            PlayerLogic.X,
            PlayerLogic.Y,
            _playerDeathCountdown,
            _shooterRng))
        {
            AddExplosion(explosion.ExpType, explosion.X, explosion.Y);
        }

        if (_playerDeathCountdown == EndExplode)
            DrawPlayer = false;

        if (_playerDeathCountdown == 0)
        {
            _playerDeathCountdown = -2;
            return;
        }

        _playerDeathCountdown--;
    }

    private void TraceShield()
    {
        var trace = ShieldTrace.Value;
        if (trace == null) return;
        trace.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "i={0} shield={1} old={2}", _gameLoopIter, PlayerLogic.Shield, _oldShieldForLowLoss));
        trace.Flush();
    }

    private static StreamWriter? OpenShieldTrace()
    {
        string? path = System.Environment.GetEnvironmentVariable("RAPTOR_SHIELD_TRACE");
        if (string.IsNullOrWhiteSpace(path)) return null;
        return new StreamWriter(path) { AutoFlush = true };
    }

    // ── Internal scheduler ────────────────────────────────────────────────────

    private sealed class GamePhaseScheduler : WavePhaseScheduler
    {
        private readonly WaveController _wc;
        public GamePhaseScheduler(WaveController wc) { _wc = wc; }

        public override void TickInput()            => _wc.PhaseInput();
        public override void TickSpawn()            => _wc.PhaseSpawn();
        public override void TickMovement()         => _wc.PhaseMovement();
        public override void TickCollisionCollect() => _wc.PhaseCollisionCollect();
        public override void TickCollisionResolve() => _wc.PhaseCollisionResolve();
        public override void TickCleanup()          => _wc.PhaseCleanup();
        public override void TickHud()              => _wc.PhaseHud();
    }
}
