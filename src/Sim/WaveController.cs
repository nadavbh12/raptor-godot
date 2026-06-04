using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    private readonly DemoReplayController _demo = new();
    private readonly PlayerButtonInput _buttonInput = new();
    private InputState? _testInteractiveInput;
    private readonly Queue<ObjType> _testSpecialSelects = new();
    private bool _debugDemoReplay = false;
    // ── Capture/test-only hooks (env-gated, OFF by default → parity-inert) ──
    // Used only by the cosmetic visual-capture harness to force states that are
    // unreachable in the parity scenarios (reaching late-wave bosses, owning
    // detect/super-shield/megabomb, a low-health boss). None of these envs are
    // set by the 12-scenario L2a gate, so the default code path is unchanged.
    private bool _godmode = false;                       // RAPTOR_GODMODE: player invuln
    private int _bossLowHp = 0;                          // RAPTOR_BOSS_LOWHP: clamp boss hits at spawn
    private bool _forceSecret = false;                   // RAPTOR_FORCE_SECRET: unlock secret-tier enemies (ES_LASER)
    private readonly List<ObjType> _grantTypes = new();  // RAPTOR_GRANT: items to grant per wave

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
    // PlayerLogic is constructed in the WaveController ctor body (after Inventory)
    // so its Shield view shares the canonical Inventory (Task 4.2).
    public  PlayerLogic    PlayerLogic   { get; }
    /// <summary>
    /// Per-pilot inventory. Created once and reused across waves/loads.
    /// Declared before Shooter so Shooter's constructor can receive it.
    /// Tasks 3.2 (load) and 3.3 (PlayerShooter wiring) read this same instance.
    /// </summary>
    public  Inventory      Inventory     { get; } = new();
    // Shooter is initialized in the WaveController constructor body (after Inventory).
    public  PlayerShooter  Shooter       { get; }

    /// <summary>
    /// Muzzle world-positions recorded by the player shooter this tick.
    /// View-only / parity-inert passthrough — consumed by DebugRenderer to
    /// spawn GUNSTR_BLK muzzle-flash cosmetics. Never checkpointed.
    /// </summary>
    public IReadOnlyList<PlayerShooter.MuzzlePos> MuzzlesThisTick => Shooter.Muzzles;
    public  uint           Score { get; private set; } = 0;

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
    // Secret tiers (ENEMY.H): map levels 0/1/2 = E_SECRET_1/2/3 → these bits.
    // Only ever in cur_diff once the three secret areas are found (WINDOWS.C:1617);
    // the RAPTOR_FORCE_SECRET capture hook ORs them in to make the wave-8 ES_LASER
    // secret enemy spawnable for visual review.
    private const int EB_SECRET_1    = 1;
    private const int EB_SECRET_2    = 2;
    private const int EB_SECRET_3    = 4;
    private const int EB_EASY_LEVEL  = 8;
    private const int EB_MED_LEVEL   = 16;
    private const int EB_HARD_LEVEL  = 32;
    private const int EB_NOT_USED    = 64;
    // Default difficulty: DIFF_2 (Normal) → cur_diff = 24.
    private int _curDiff = EB_EASY_LEVEL | EB_MED_LEVEL;   // 24
    private int _curPlayerDiff = 2;

    /// <summary>
    /// Enemy-spawn level mask for a player difficulty (DIFF_0..DIFF_3). Mirrors
    /// RAP_SetPlayerDiff (LOADSAVE.C): DIFF_0/1 → EASY tier only, DIFF_2 →
    /// EASY|MED, DIFF_3 → EASY|MED|HARD. Pure/static for testability.
    /// </summary>
    internal static int SpawnMaskForDiff(int diff) => diff switch
    {
        <= 1 => EB_EASY_LEVEL,                               // DIFF_0 (train), DIFF_1 (rookie)
        >= 3 => EB_EASY_LEVEL | EB_MED_LEVEL | EB_HARD_LEVEL, // DIFF_3 (elite)
        _    => EB_EASY_LEVEL | EB_MED_LEVEL,                 // DIFF_2 (veteran)
    };

    /// <summary>
    /// Apply a player difficulty (DIFF_0..DIFF_3) to gameplay — the player
    /// damage/recharge tier (<see cref="_curPlayerDiff"/>) and the enemy-spawn
    /// level mask (<see cref="_curDiff"/>). Mirrors RAP_SetPlayerDiff. Called from
    /// the new-pilot (selected) and load-pilot (saved) paths; the secret-tier OR
    /// is applied separately at game-enter, so it is not re-added here.
    ///
    /// Parity note: the parity scenarios accept the default VETERAN selection
    /// (DIFF_2), so they keep _curPlayerDiff=2 / _curDiff=24 — identical to the
    /// field defaults above — and parity is unaffected.
    /// </summary>
    public void SetPlayerDiff(int diff)
    {
        if (diff < 0) diff = 0; else if (diff > 3) diff = 3;
        _curPlayerDiff = diff;
        _curDiff = SpawnMaskForDiff(diff);
    }

    /// <summary>
    /// Maps a raw CSPRITE.level value to the corresponding EB_ bitmask.
    /// Mirrors the switch in ENEMY_LoadSprites (LOADSAVE.C RAP_SetPlayerDiff).
    /// </summary>
    internal static int GetEbLevel(int rawLevel) => rawLevel switch
    {
        0 => EB_SECRET_1,   // E_SECRET_1
        1 => EB_SECRET_2,   // E_SECRET_2
        2 => EB_SECRET_3,   // E_SECRET_3
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
    public bool GameplayVisualActive => _waveActive || _demo.PendingScheduled;

    /// <summary>Current 1-based wave number (read-only; used by the View's music
    /// selection). Parity-inert — exposes existing state, mutates nothing.</summary>
    public int WaveNum => _waveNum;

    internal static int AnimationStartIterForSpawn(int currentGameLoopIter) => currentGameLoopIter + 1;
    internal static int AnimationAge(int currentGameLoopIter, int startIter) => currentGameLoopIter - startIter;

    // ── Collision scratch ─────────────────────────────────────────────────────
    private readonly List<(EnemyLogic enemy, int dmg)> _hitEnemies = new();
    private readonly List<CollisionDetection.EnemyBulletHit> _enemyBulletHits = new();
    private readonly List<BulletLogic> _shotDoneAfterCollision = new();
    private bool _playerHit;
    private int  _playerHitDmg;
    private readonly List<EnemyLogic> _bodyCrashEnemies = new();  // enemies that collided with player
    private readonly List<BonusLogic> _pickedUpBonuses  = new();  // bonuses overlapping player this tick
    private bool _playerWasAliveAtCollisionStart;

    // ── Shield recharge (OBJS_Think in C) ────────────────────────────────────
    private readonly ShieldHudController _shieldHud = new();
    public bool ShieldLowWarningVisible => _shieldHud.ShieldLowVisible;
    public bool SystemDamageWarningVisible => _shieldHud.SystemDamageVisible;

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

    // The scroll cursor (tilepos/tileyoff/tiley) and the enemy-spawn scheduling
    // live in the MapSpawnScroller collaborator. WaveController drives it in
    // phase order; scroll/spawn state are byte-exact with the original code.
    private readonly MapSpawnScroller _scroller =
        new(MAP_ROWS, MAP_ONSCREEN, MAP_COLS, MAP_BLOCKSIZE, MAP_LEFT);

    // On-screen tile state slice + destructibility backing arrays live in the
    // TileDamageState collaborator (mirrors C's tspots[] / hits[] / tdead[] etc.).
    // Rebuilt from _mapTiles + flats in PhaseSpawn whenever the scroll crosses a
    // row boundary; TileBomb / TileIsHit dispatches against the slice.
    private readonly TileDamageState _tiles = new();

    // ── Map sprite list for spawning ──────────────────────────────────────────
    // The sprite list + spawn cursor live in MapSpawnScroller; tile-grid data
    // stays here (read by the View and the tile-damage collaborator).
    private List<MapTileEntry>?   _mapTiles;

    /// <summary>Tile-grid data for the current wave (rows * cols entries, row-major).</summary>
    public IReadOnlyList<MapTileEntry>? MapTiles => _mapTiles;

    public int RenderedFlatFor(int mapspot) => _tiles.RenderedFlatFor(_mapTiles, mapspot);
    /// <summary>Current scroll Y offset (mirrors C's tileyoff).</summary>
    public int TileYOff => _scroller.TileYOff;
    /// <summary>Current top-of-screen row in the tile grid (mirrors C's tilepos).</summary>
    public int TilePos  => _scroller.TilePos;
    /// <summary>
    /// Whether the tile map is advancing this tick (mirrors C's scroll_flag,
    /// TILE.C:240/279/472). In C scroll_flag starts TRUE and goes FALSE only at
    /// the very end of the map (last_tile && tileyoff >= 0). The port stops
    /// advancing _tileyoff/_tilepos exactly when PhaseSpawn early-returns on
    /// _endWaveFlag, which also freezes the visible scroll — so !_endWaveFlag is
    /// true on precisely the ticks the map advances by 1px. Read-only / parity-inert.
    /// </summary>
    public bool IsScrolling => !_endWaveFlag;
    public int MapRows  => MAP_ROWS;
    public int MapCols  => MAP_COLS;
    public int MapOnScreen   => MAP_ONSCREEN;
    public int MapBlockSize  => MAP_BLOCKSIZE;
    public int MapLeftPx     => MAP_LEFT;
    private SpriteMetaLibrary?    _slib;
    private bool                  _endWaveFlag = false;
    private readonly PlayerDeathSequence _playerDeath = new();
    private readonly EndWaveSequencer _endWave = new();
    public int                    EndWaveCountdown => _endWave.Countdown;

    // ── Scheduler ─────────────────────────────────────────────────────────────
    private readonly GamePhaseScheduler _scheduler;

    // ── Megabomb-detonation View signal (parity-inert; never serialized) ──────
    private readonly Shots.MegaBombFlash _megaFlash = new();
    public bool ConsumeMegaBombDetonated() => _megaFlash.Consume();

    public WaveController()
    {
        // Shooter must be wired to this.Inventory (Task 3.3).
        // Initialised here (not in a property initialiser) because Inventory
        // must be constructed first, and C# property-init order follows
        // declaration order within the same class body.
        Shooter = new PlayerShooter(Inventory);
        PlayerLogic = new PlayerLogic(Inventory);   // shares the canonical Inventory (Task 4.2).
        _scheduler = new GamePhaseScheduler(this);
    }

    public void SeedRngForWave(int waveNum, string? seedOverride = null)
    {
        Rng.Seed = WaveRng.ComputeSeed(waveNum, seedOverride);
    }

    public override void _Ready()
    {
        SeedRngForWave(1, OS.GetEnvironment("RAPTOR_RNG_SEED_OVERRIDE"));

        _assetsRoot = ProjectSettings.GlobalizePath("res://assets");
        _debugDemoReplay = OS.GetEnvironment("RAPTOR_DEBUG_DEMO") == "1";
        var capture = CaptureHooks.Read(OS.GetEnvironment);
        _godmode = capture.Godmode;
        _bossLowHp = capture.BossLowHp;
        _forceSecret = capture.ForceSecret;
        _grantTypes.Clear();
        _grantTypes.AddRange(capture.Grants);

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
            _emitter.GetDemoGameNum = () => _demo.Replay?.Header.DemoGame ?? -1;
            _emitter.GetMenuDemoEmitSequence = () => MenuDemoEmitSequence;
        }
    }

    private int MenuDemoEmitSequence
    {
        get
        {
            if (_demo.PendingScheduled) return -1;
            if (_demo.Active) return _demo.RecordIndex;
            return int.MinValue;
        }
    }

    private void OnPilotCreated()
    {
        // New pilot starts with 10000 score and 75 shield (from C golden).
        Score = NewPilotScore;
        PlayerLogic.Reset();  // Resets position/pic; Shield is now the Energy slot (Task 4.2).
        // Seed inventory to match WINDOWS.C:989-1007:
        //   ForwardGuns + 3×Energy (→75) + GetNext() (→EquippedSpecial=null).
        // NOTE (Task 4.2): Reset() above writes the shared Energy slot to 75, but
        // Clear() immediately wipes it and SeedNewPilot() re-creates it (3×Energy=75).
        // Shield ends at 75 only because SeedNewPilot lands on the same value — keep
        // InitShield (75) and SeedNewPilot's energy seed in sync (cf. SetupDemoPlayer,
        // where SetShield is deliberately ordered AFTER Clear()).
        Inventory.Clear();
        Inventory.SeedNewPilot();
        // Apply the chosen difficulty. Read AcceptedPilotDiff (captured at accept
        // time), NOT the live DifficultyFieldId — the latter is reset to 3
        // (VETERAN) before this fires, which silently forced every pilot to DIFF_2.
        SetPlayerDiff(_menu?.AcceptedPilotDiff ?? 2);
        GD.Print($"WaveController: pilot created, score={Score}, shield={PlayerLogic.Shield}, diff={_curPlayerDiff}");
    }

    private void OnGameEnter(int gameNum)
    {
        // Defer the iter-0 body and wave activation by LoadCompFrames.
        // Concrete setup happens in _PhysicsProcess once the deferred frame
        // arrives. See LoadCompFrames docs for the C-side rationale.
        _pendingGameNum = gameNum;
        // Load the wave NOW — LoadWave does a full state reset (clears the
        // previous game's enemies/bullets/etc. and loads the new map), so during
        // the LoadComp wait the View renders the NEW mission's terrain instead of
        // the previous game's frozen state. Activation (iter 0) stays deferred:
        // nothing ticks or draws RNG until ApplyPendingGameEnter, so the gameplay
        // and parity timing are unchanged.
        _waveNum = ResolveStartWave(OS.GetEnvironment("RAPTOR_START_WAVE"), _pendingGameNum + 1);
        SeedRngForWave(_waveNum, OS.GetEnvironment("RAPTOR_RNG_SEED_OVERRIDE"));
        LoadWave(_waveNum);
        // RAPTOR_FORCE_DIFF: validation hook to force the player difficulty (0..3)
        // for headless enemy-count checks. Parity-inert (env unset in all scenarios).
        if (int.TryParse(OS.GetEnvironment("RAPTOR_FORCE_DIFF"), out int forcedDiff) && forcedDiff >= 0)
            SetPlayerDiff(forcedDiff);
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
        // The wave was already loaded in OnGameEnter (so its terrain renders
        // during the LoadComp wait). Now activate it and run iter 0.
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
        _demo.Schedule(replay, currentFrame + DemoLoadCompFrames);
    }

    private void ApplyPendingDemoStart()
    {
        if (!_demo.PendingScheduled) return;

        var replay = _demo.Begin();
        _buttonInput.ResetDemoLatches();

        _waveNum = replay.Header.DemoWave + 1;
        SeedRngForWave(replay.Header.DemoWave, OS.GetEnvironment("RAPTOR_RNG_SEED_OVERRIDE"));
        LoadWave(_waveNum);
        SetupDemoPlayer(replay.Header.DemoGame);
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
        HasSecretsDetector = true;

        // DEMO_MakePlayer calls OBJS_Clear() before granting the demo loadout.
        // Mirror that: start from a clean inventory so a demo set up after a live
        // wave (or another demo) on the same WaveController doesn't add on top of
        // dirty inventory state.
        Inventory.Clear();
        DemoLoadout.Apply(Shooter, game, registered: false);
        // Task 4.2: SetShield must run AFTER Clear()+loadout — Shield now lives in
        // the Inventory energy slot, so setting it before Clear() would be wiped.
        // The demo loadout grants no energy slot, so SetShield creates it (via Load).
        PlayerLogic.SetShield(PlayerLogic.MaxShield);
        _shieldHud.SyncOldShield(PlayerLogic.Shield);
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
        _tiles.ResetForWave();
        _shieldHud.ResetForWave(PlayerLogic.Shield);
        _playerHit = false;
        _endWaveFlag = false;
        _playerDeath.Reset();
        _endWave.Reset();
        DrawPlayer = true;
        _subTick = 0;
        _gameLoopIter = 0;

        // Reset scroll to start position (mirrors TILE_Init in C).
        _scroller.ResetForWave();
        // Lazy-load the FLATSG1_ITM table on first wave (G1 only — DOS Raptor
        // shipped one campaign; the FLATS struct is mission-independent).
        _tiles.LoadFlats(Path.Combine(_assetsRoot ?? "assets", "flats", "FLATSG1_ITM.json"));
        // Reset player position.
        PlayerLogic.Reset();
        // Reset weapon cooldowns; mirrors SHOTS_Init in RAP.C Init_Game.
        Shooter.Reset();
        // Seed PlayerShooter RNG deterministically off the wave seed so
        // DUMB_MISSLE scatter and MINI_GUN picks are replay-stable.
        _shooterRng = WaveRng.NewShooterRng(Rng.Seed);

        // Load sprite metadata library.
        string slibPath = Path.Combine(_assetsRoot ?? "assets", "sprites_meta", "SPRITE1_ITM.json");
        _slib = SpriteMetaLibrary.LoadFromFile(slibPath);

        // Load map sprite list.
        string mapPath = MazeLevelLoader.WaveMapPath(_assetsRoot ?? "assets", waveNum);
        var mapData    = MazeLevelLoader.Load(mapPath);
        var mapSprites = mapData.Sprites ?? new List<MapSpriteEntry>();
        _mapTiles      = mapData.Tiles   ?? new List<MapTileEntry>();
        _scroller.SetSprites(mapSprites);
        _tiles.InitializeTileBacking(_mapTiles);
        _tiles.RebuildTileSlice(_mapTiles, _scroller.TilePos, _scroller.TileYOff);

        GD.Print($"WaveController: loaded wave {waveNum}, {mapSprites.Count} sprites, tiley={_scroller.TileY}");

        // Immediately spawn enemies that are on-screen at game start (tiley=139).
        // In the C version, ENEMY_Clear() sets cur_enemy=csprite (index 0) and
        // ENEMY_DoSprites is called once before the main loop, spawning the initial
        // enemies. We replicate that here so fc=0 shows the correct enemy count.
        DoInitialSpawn();

        // Capture hook: grant items requested via RAPTOR_GRANT (parity-inert —
        // _grantTypes is empty unless the env is set). Applied after the resets
        // above so nothing wipes them; Inventory.Add respects each item's cap.
        foreach (var t in _grantTypes) Inventory.Add(t);
        // Granting Detect also flips the secrets-detector flag, mirroring the
        // bonus pickup (BonusEffectDispatcher) which sets both — the scanner /
        // boss-health bar (DrawScannerHud) is gated on HasSecretsDetector.
        if (_grantTypes.Contains(ObjType.Detect)) HasSecretsDetector = true;
        // Capture hook: unlock the three secret tiers so the wave-8 ES_LASER secret
        // enemy spawns (mirrors C WINDOWS.C:1617 finding all secret areas). Inert
        // unless RAPTOR_FORCE_SECRET is set — no scenario sets it.
        if (_forceSecret) _curDiff |= EB_SECRET_1 | EB_SECRET_2 | EB_SECRET_3;
    }

    private void DoInitialSpawn()
    {
        // The spawn loop (ENEMY_DoSprites) lives in MapSpawnScroller; it returns
        // whether the sprite list was exhausted, which sets _endWaveFlag here.
        if (_scroller.SpawnDueEnemies(_slib, _enemies, ShouldSpawn, _bossLowHp))
            _endWaveFlag = true;
    }

    public override void _PhysicsProcess(double _)
    {
        if (_pendingGameEnterFrame >= 0 && SimClock.Frame >= _pendingGameEnterFrame)
            ApplyPendingGameEnter();
        if (_demo.ShouldBegin(SimClock.Frame))
            ApplyPendingDemoStart();
        if (_playerDeath.ConsumeSentinel())
        {
            _waveActive = false;
            _menu?.PlayerDied(SimClock.Frame);
            return;
        }
        if (!_waveActive) return;

        // Iter 0 fires synchronously in OnGameEnter (matches C: ENEMY_Think
        // runs before GFX_FadeIn). After iter 0, hold for FadeInHoldFrames
        // frames to mirror C's blocking palette fade-in. Then run at strict
        // 3 fc/iter (the C steady-state cadence confirmed by position dumps).
        int fadeHoldFrames = _demo.Active ? DemoFadeInHoldFrames : FadeInHoldFrames;
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
        // Clear last tick's muzzle positions before any shooting is processed.
        // View-only / parity-inert (DebugRenderer muzzle-flash cosmetics).
        Shooter.ClearMuzzles();

        if (_demo.Active)
        {
            if (!_demo.TryNextFrame(out var frame))
            {
                _waveActive = false;
                return;
            }

            if (_debugDemoReplay && _demo.RecordIndex <= 40)
                GD.Print($"demo tick fc={SimClock.Frame} rec={_demo.RecordIndex - 1} px={frame.Px} py={frame.Py}");
            PlayerLogic.ApplyDemoFrame(frame.Px, frame.Py, frame.PlayerPic);
            _buttonInput.ApplyDemo(frame, Shooter, PlayerLogic, _weaponTargetEnemies, _enemies, _playerBullets, _shooterRng);
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
        if (EndWaveSequence.InputLocked(_endWave.Countdown))
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
        _buttonInput.ApplyLive(input.B1, input.B2, input.B3, Shooter, PlayerLogic, _waveActive,
            _weaponTargetEnemies, _enemies, _playerBullets, _shooterRng);

        // SHOTS.C:1035-1040 — cooldown decrement once per game iter. Done at
        // the end of input so the fire above sees the C-state cur_shoot.
        Shooter.TickCooldowns();
    }

    internal void SetInteractiveInputForTest(InputState input)
    {
        _testInteractiveInput = input;
    }

    internal void QueueSpecialSelectForTest(ObjType weapon)
    {
        _testSpecialSelects.Enqueue(weapon);
    }

    internal void PhaseSpawn()
    {
        if (!_scroller.HasSprites || _slib == null || _endWaveFlag) return;

        _tiles.RefreshTileSliceForThink(_mapTiles, _scroller.TilePos, _scroller.TileYOff);
        _tiles.ProcessTileDelayExplosions(AddTileExplosion);

        // This method combines Godot's spawn phase with C's TILE_Think scroll
        // advance. The collision tile slice above intentionally stays at the
        // pre-scroll tspots for the current SHOTS pass; the scroll cursor is
        // advanced after spawning for subsequent spawning/scroll state.
        if (_scroller.SpawnDueEnemies(_slib, _enemies, ShouldSpawn, _bossLowHp))
            _endWaveFlag = true;

        _scroller.AdvanceScroll();
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
                if (laserDmg > 0) ApplyPlayerDamage(laserDmg);
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

        // Detection extracted to CollisionDetection (E10); WaveController applies
        // the kill + per-bullet damage + impact flash in hit order so the
        // side-effect/RNG sequence is unchanged. Mirror ESHOT.C:521 flash
        // (A_SMALL_AIR_EXPLO at shot->x/y) wherever a bullet clips the player.
        _enemyBulletHits.Clear();
        CollisionDetection.CollectEnemyBulletHits(_enemyBullets, px, py, playerHw, playerHh, _enemyBulletHits);
        foreach (var hit in _enemyBulletHits)
        {
            hit.Bullet.Kill();
            _playerHit = true;
            _playerHitDmg += hit.Bullet.Damage;  // per-bullet damage from ESHOT_LIB
            AddExplosion(ExpAirSmall2, hit.ImpactX, hit.ImpactY);
        }

        // Player bullets vs enemy/tile collision. C SHOTS.C dispatches this as
        // per-hit-type if/else chains, so S_ALL/S_GROUND only test tiles after
        // the corresponding enemy damage call fails for that same bullet.
        // Beams are handled in a separate pass below (different damage model).
        _hitEnemies.Clear();
        PlayerBulletCollisionDispatcher.TraceIter = _gameLoopIter;
        TileDamageDispatcher.TraceIter = _gameLoopIter;
        var collision = PlayerBulletCollisionDispatcher.Collect(_playerBullets, _enemies, _tiles.Slice, MAP_COLS);
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
        _tiles.SyncTileSliceToBacking();
        foreach (int mapspot in collision.DestroyedTileMapSpots)
        {
            _tiles.SpawnTileExplosion(mapspot, AddTileExplosion);
            _tiles.ApplyTileExplosionDamage(mapspot, damage: 5, AddTileExplosion);
            _tiles.ScheduleTileDelayExplosion(mapspot);
        }
        _tiles.RefreshTileSliceValuesFromBacking();

        // Beam-vs-enemy column damage. SHOTS.C:1068-1088 — for each VerticalBeam,
        // find the first enemy whose x range contains the beam X and whose
        // y < player_cy (above the player) and y > -30. Damage it by the beam's
        // per-shot hits; the beam does NOT despawn on hit. LineBeam (TURRET)
        // bullets have BeamDamages=false because PlayerShooter already applied
        // damage at spawn — they exist only for the visual.
        CollisionDetection.CollectBeamEnemyHits(_playerBullets, _enemies, py, _hitEnemies);

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

    /// Pure mirror of ENEMY_GetBaseDamage (ENEMY.C:1270): average health-% of
    /// on-screen bosses (y+hly >= 0), or 0 when none. Integer arithmetic matches C.
    internal static int ComputeBaseDamage(System.Collections.Generic.IEnumerable<(bool boss, int y, int hly, int hits, int maxHits)> rows)
    {
        int total = 0, nums = 0;
        foreach (var r in rows)
        {
            if (!r.boss) continue;
            if (r.y + r.hly < 0) continue;
            // guard div-by-zero for standalone callers; via GetBaseDamage maxHits is always >= 1
            if (r.maxHits <= 0) continue;
            total += (r.hits * 100) / r.maxHits;
            nums++;
        }
        return nums > 0 ? total / nums : 0;
    }

    /// <summary>
    /// Average boss health-% over on-screen bosses (or 0 when none). Used by the
    /// view's scanner/boss-health bar. Pure read over the live enemy list.
    /// </summary>
    public int GetBaseDamage()
        => ComputeBaseDamage(GetEnemies().Select(
            e => (e.IsBoss, e.Y, e.HalfH, e.Hits, e.MaxHits)));

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
            ApplyPlayerDamage(bodyDmg);
            AddExplosion(ExpAirSmall2, PlayerLogic.X + 16, PlayerLogic.Y + 16);
            // ENEMY.C:1117 plays SND_Patch(FX_CRASH) on body contact. No-op in headless.
            SoundEmitter.Emit("sound.fx_crash", PlayerLogic.X + 16, PlayerLogic.Y + 16);
            if (wasAlive && !e.Alive)
            {
                Score += (uint)e.Meta.Money;
                ApplyEnemyDeathEffects(e);
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
        var result = ShotDoneDispatcher.Dispatch(b, _enemyBullets, _enemies, _shooterRng, _tiles.Slice);
        if (result.TileBounty > 0) Score += (uint)result.TileBounty;
        if (result.MegaBombDetonated) _megaFlash.Signal();
        _tiles.SyncTileSliceToBacking();
    }

    private void ApplyBonusEffect(int objType)
    {
        var r = Bonus.BonusEffectDispatcher.Apply(objType, Shooter, Inventory, PlayerLogic.MaxShield);
        if (r.HealAmount > 0)       PlayerLogic.Heal(r.HealAmount);
        if (r.ScoreAdd > 0)         Score += r.ScoreAdd;
        if (r.DetectorActivated)    HasSecretsDetector = true;
    }

    /// <summary>
    /// Single player-damage chokepoint, mirroring C where every shield hit flows
    /// through OBJS_SubEnergy. Applies the SubEnergy pre-drain gates
    /// (<see cref="GateSubEnergyDamage"/> — end-wave suppression + DIFF_0 halving)
    /// before draining the shield.
    /// </summary>
    private void ApplyPlayerDamage(int amt)
    {
        if (_godmode) return;  // RAPTOR_GODMODE capture hook: invuln (parity-inert; env off in all scenarios).
        int dmg = GateSubEnergyDamage(amt, endWaveActive: _endWave.Active, _curPlayerDiff);
        if (dmg > 0) PlayerLogic.TakeDamage(dmg);
    }

    internal void PhaseCollisionResolve()
    {
        // Apply player damage from enemy bullets.
        if (_playerHit && _playerHitDmg > 0)
        {
            // OBJS_Damage plays FX_SHIT under super shield, else FX_HIT
            // (OBJECTS.C:1240/1254); it returns early (no sound) during the
            // end-wave fly-off (OBJECTS.C:1229). No-op in headless.
            if (!_endWave.Active)
                SoundEmitter.Emit(Inventory.IsEquip(ObjType.SuperShield) ? "sound.fx_shit" : "sound.fx_hit");
            ApplyPlayerDamage(_playerHitDmg);
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
            // BONUS.C:246 plays SND_Patch(FX_BONUS) on pickup; no-op in headless.
            SoundEmitter.Emit("sound.fx_bonus");
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
            // RAP.C:581-582 plays FX_AIREXPLO + FX_AIREXPLO2 when the ship blows
            // up at END_EXPLODE. No-op in headless.
            SoundEmitter.Emit("sound3d.fx_airexplo",  PlayerLogic.X + 16, PlayerLogic.Y + 16);
            SoundEmitter.Emit("sound3d.fx_airexplo2", PlayerLogic.X + 16, PlayerLogic.Y + 16);
            _playerDeath.Trigger();
        }
    }

    // C exptype constants used for cosmetic-only explosion events (SOURCE/MAP.H).
    internal const int ExpAirSmall1 = 0;  // EXP_AIRSMALL1 → EXPLO2_BLK
    internal const int ExpAirLarge  = 2;  // EXP_AIRLARGE → LGFLAK_BLK
    internal const int ExpAirSmall2 = 10; // EXP_AIRSMALL2 → SMFLAK_BLK
    internal const int ExpAirMed2   = 10; // A_MED_AIR_EXPLO2 uses SMFLAK_BLK in ANIMS.C.

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
            ApplyEnemyDeathEffects(e);
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
        // ESHOT.C plays SND_3DPatch here; forward to the View (no-op in headless).
        SoundEmitter.Emit(label);
    }

    // ENEMY.C:391-392 arms startendwave = END_DURATION the instant the last
    // enemy is removed during an end-wave — there is NO explosion/ANIMS gate.
    // RAP.C:1039-1046 then just counts down. Explosions keep ticking/rendering
    // during the fly-off; they never block the wave from ending.

    // Explosion sink handed to TileDamageState so the tile-destruction cascade
    // can spawn ground explosions without owning the explosion list.
    private void AddTileExplosion(int expType, int x, int y) => AddExplosion(expType, x, y);

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
    private void ApplyEnemyDeathEffects(EnemyLogic e)
    {
        var fx = EnemyDeathEffects.Build(e, _shooterRng, AnimationStartIterForSpawn(_gameLoopIter));
        // ENEMY.C:1126 plays SND_3DPatch(FX_AIREXPLO) once per death; forward to
        // the View (no-op in headless). EnemyDeathEffects.Build stays a pure
        // builder — the playback side-effect lives here at the apply site.
        SoundEmitter.Emit("sound3d.fx_airexplo", e.X, e.Y);
        foreach (var ex in fx.Explosions)
            AddExplosion(ex.ExpType, ex.X, ex.Y, ex.StartDelayIters);
        foreach (var b in fx.Bonuses)
        {
            var bonus = new BonusLogic(b.ObjType, b.X, b.Y, b.InitialPos);
            _bonuses.Add(bonus);
            TraceBonus("add", bonus);
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

        var endWave = _endWave.Tick(
            _waveActive, _demo.Active, _endWaveFlag,
            PlayerLogic.Alive, PlayerLogic.X,
            _enemies.Exists(e => e.Alive || e.PendingRemovalDump));
        // RAP.C:601-604 plays SND_Patch(FX_FLYBY) the frame the countdown reaches
        // END_FLYOFF (shield > 0). The counter passes through FlyOff exactly once.
        if (PlayerLogic.Alive && _endWave.Countdown == EndWaveSequence.FlyOff)
            SoundEmitter.Emit("sound.fx_flyby");
        if (endWave.ForcedDx != 0 || endWave.ForcedDy != 0)
            PlayerLogic.ApplyForcedMove(endWave.ForcedDx, endWave.ForcedDy);
        if (endWave.MissionComplete)
        {
            _waveActive = false;
            _menu?.CompleteMission(SimClock.Frame);
        }
    }


    /// <summary>
    /// Pure-C# port of the pre-drain gates in OBJS_SubEnergy (OBJECTS.C:1227-1235):
    /// damage is suppressed entirely during the end-wave fly-off
    /// (startendwave != EMPTY), and halved for amounts &gt; 1 in training mode
    /// (curplr_diff == DIFF_0). The godmode gate is omitted — the port has no
    /// godmode input path. Returns the effective damage to apply.
    /// </summary>
    internal static int GateSubEnergyDamage(int amt, bool endWaveActive, int curPlayerDiff)
    {
        // C:1229  if (startendwave != EMPTY) return 0 — no damage during end-wave.
        if (endWaveActive) return 0;
        // C:1233-1234  if (curplr_diff == DIFF_0 && amt > 1) amt = amt>>1.
        if (curPlayerDiff == 0 && amt > 1) amt >>= 1;
        return amt;
    }

    internal void PhaseHud()
    {
        // ShieldHudController owns the shield-recharge counter, palette-stuff RNG,
        // and low-shield warning; PlayerDeathSequence owns the death countdown.
        // WaveController keeps phase-order control: it runs the death tick between
        // the wave's initial-skip frame and the main shield tick, as before.
        if (_shieldHud.TickSkipInitial(PlayerLogic, _curPlayerDiff,
                deathActive: _playerDeath.Active,
                endWaveActive: _endWave.Active))
            return;

        if (_playerDeath.Active)
        {
            _shooterRng ??= WaveRng.NewShooterRng(Rng.Seed);
            var death = _playerDeath.Tick(PlayerLogic.X, PlayerLogic.Y, _shooterRng);
            foreach (var e in death.Explosions)
                AddExplosion(e.ExpType, e.X, e.Y);
            if (death.HidePlayer) DrawPlayer = false;
        }

        _shieldHud.Tick(PlayerLogic, Inventory, _curPlayerDiff,
            deathActive: _playerDeath.Active,
            endWaveActive: _endWave.Active,
            gameLoopIter: _gameLoopIter, shooterRng: _shooterRng);
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
