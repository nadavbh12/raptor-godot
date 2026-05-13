using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Raptor.Sim.Bonus;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Raptor.Sim.MazeLevel;
using Raptor.Sim.Player;
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
    public RandomNumberGenerator Rng { get; } = new();

    // ── Wiring slots ─────────────────────────────────────────────────────────
    // Set by _Ready via GetNodeOrNull; the scheduler reads these.
    private MenuStateMachine?   _menu;
    private ParityEmitter?      _emitter;
    private Raptor.Test.PlaythroughDriver? _playthrough;
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

    // C: between Return-on-shipcomp (raptor_parity_set_win_state(0) inside
    // menu_exit) and raptor_parity_game_enter being called, the main thread
    // runs WIN_LoadComp + RAP_LoadMap. Position-dump alignment under
    // mission_long places `down Up` arriving at iter 157 in C, which works
    // out to a ~102 fc lag between Return #6's apply tick and game_enter
    // here. Without this lag iter 0 fires in the same tick as the Return
    // application, and the player input pipeline picks up the held throttle
    // ~30 iters later than C does.
    private const int LoadCompFrames = 102;
    private int  _waveNum    = 1;

    // When MenuStateMachine.EnterGame fires we defer the iter-0 body and
    // _gameEnterFc anchoring to a future frame to mirror the C LoadComp
    // window above. -1 = no pending; otherwise the frame at which the wave
    // becomes active and iter 0 runs.
    private int  _pendingGameEnterFrame = -1;
    private int  _pendingGameNum = 0;

    // Fired right after _gameLoopIter advances; PositionDumper subscribes to
    // emit at iter ends, mirroring C's parity_tick semantics.
    public event Action? OnIterEnd;

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

    // Active explosion animations spawned when an enemy dies. Each entry
    // records the C exptype (SOURCE/MAP.H), the center position, and the sim
    // frame at which it was started; the view renders the right BLK frame as
    // SimClock.Frame - StartFc.
    public readonly record struct Explosion(int ExpType, int X, int Y, int StartFc);
    private readonly List<Explosion> _explosions = new();

    // Read-only accessors for debug rendering only — not parity-affecting.
    public IReadOnlyList<EnemyLogic>  GetEnemies()       => _enemies;
    public IReadOnlyList<BulletLogic> GetEnemyBullets()  => _enemyBullets;
    public IReadOnlyList<BulletLogic> GetPlayerBullets() => _playerBullets;
    public IReadOnlyList<Explosion>   GetExplosions()    => _explosions;
    public IReadOnlyList<BonusLogic>  GetBonuses()       => _bonuses;

    // ── Collision scratch ─────────────────────────────────────────────────────
    private readonly List<(EnemyLogic enemy, int dmg)> _hitEnemies = new();
    private bool _playerHit;
    private int  _playerHitDmg;
    private readonly List<EnemyLogic> _bodyCrashEnemies = new();  // enemies that collided with player
    private readonly List<BonusLogic> _pickedUpBonuses  = new();  // bonuses overlapping player this tick

    // ── Shield recharge (OBJS_Think in C) ────────────────────────────────────
    // CHARGE_SHIELD = 24*4 = 96. When think_cnt > 96, heal 1 shield.
    // curplr_diff < DIFF_3 enables recharge.
    private const int ChargeShield = 96;
    private int _thinkCnt = 0;

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

    // ── Map sprite list for spawning ──────────────────────────────────────────
    private List<MapSpriteEntry>? _mapSprites;
    private List<MapTileEntry>?   _mapTiles;

    /// <summary>Tile-grid data for the current wave (rows * cols entries, row-major).</summary>
    public IReadOnlyList<MapTileEntry>? MapTiles => _mapTiles;
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
        }
    }

    private void OnPilotCreated()
    {
        // New pilot starts with 10000 score and 75 shield (from C golden).
        Score = NewPilotScore;
        PlayerLogic.Reset();  // Reset sets Shield = InitShield = 75.
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
    /// Fires the deferred iter-0 body and arms the wave. Splits out of the
    /// hot path so _PhysicsProcess stays readable.
    /// </summary>
    private void ApplyPendingGameEnter()
    {
        _waveNum    = _pendingGameNum + 1;  // gameNum is 0-based; wave files are 1-based.
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
        _hitEnemies.Clear();
        _pickedUpBonuses.Clear();
        _explosions.Clear();
        _playerHit = false;
        _endWaveFlag = false;
        _subTick = 0;
        _gameLoopIter = 0;

        // Reset scroll to start position (mirrors TILE_Init in C).
        _tilepos  = (MAP_ROWS - MAP_ONSCREEN) * MAP_COLS;
        _tileyoff = 200 - MAP_ONSCREEN * MAP_BLOCKSIZE;  // -56
        _tiley    = _tilepos / MAP_COLS - 3;             // = 139

        // Reset player position.
        PlayerLogic.Reset();
        // Reset weapon cooldowns; mirrors SHOTS_Init in RAP.C Init_Game.
        Shooter.Reset();
        // Seed PlayerShooter RNG deterministically off the wave seed so
        // DUMB_MISSLE scatter and MINI_GUN picks are replay-stable.
        _shooterRng = new System.Random((int)(Rng.Seed & 0x7FFFFFFFu));

        // Load sprite metadata library.
        string slibPath = Path.Combine(_assetsRoot ?? "assets", "sprites_meta", "SPRITE1_ITM.json");
        _slib = SpriteMetaLibrary.LoadFromFile(slibPath);

        // Load map sprite list.
        string mapPath = MazeLevelLoader.WaveMapPath(_assetsRoot ?? "assets", waveNum);
        var mapData    = MazeLevelLoader.Load(mapPath);
        _mapSprites    = mapData.Sprites ?? new List<MapSpriteEntry>();
        _mapTiles      = mapData.Tiles   ?? new List<MapTileEntry>();
        _spawnIdx      = 0;

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

        if (!_waveActive) return;

        // Iter 0 fires synchronously in OnGameEnter (matches C: ENEMY_Think
        // runs before GFX_FadeIn). After iter 0, hold for FadeInHoldFrames
        // frames to mirror C's blocking palette fade-in. Then run at strict
        // 3 fc/iter (the C steady-state cadence confirmed by position dumps).
        if (SimClock.Frame - _gameEnterFc < FadeInHoldFrames) return;

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
        // Under a parity playthrough, scripted `down NAME`/`up NAME` lines
        // populate PlaythroughDriver's held-key set; read it as the player's
        // key state and let PlayerLogic.Tick run C's IPT_GetKeyBoard ramp.
        // No playthrough → idle (the default for a real interactive run; a
        // future stage will swap this for the Godot InputMap wiring).
        int dx = _playthrough?.PlayerInputX ?? 0;
        int dy = _playthrough?.PlayerInputY ?? 0;
        PlayerLogic.Tick(dx, dy);

        // RAP.C SC_1..SC_MINUS — script-issued special-weapon switches.
        // Mirrors OBJS_MakeSpecial: silently ignored if the type isn't owned.
        if (_playthrough != null)
        {
            while (_playthrough.TryDequeueSpecialSelect(out var w))
                Shooter.SelectSpecial(w);
        }

        // Mirrors RAP.C:1000 BUT_1 → OBJS_Use(S_FORWARD_GUNS/...) cascade. Order
        // matches C: fire happens BEFORE SHOTS_Think runs (which decrements
        // cooldowns), so the cooldown set this tick can't be cleared in the
        // same tick. C resets BUT_1=FALSE after firing — our edge model uses
        // the held state, which is what the demo records (b1 is a held flag).
        if (_waveActive && (_playthrough?.IsFireHeld ?? false))
        {
            int cx = PlayerLogic.X + 16;       // player_cx = playerx + PLAYERWIDTH/2
            int cy = PlayerLogic.Y + 16;
            var fired = Shooter.ApplyButton1(cx, cy, PlayerLogic.Pic, _enemies, _shooterRng);
            foreach (var b in fired) _playerBullets.Add(b);
        }

        // SHOTS.C:1035-1040 — cooldown decrement once per game iter. Done at
        // the end of input so the fire above sees the C-state cur_shoot.
        Shooter.TickCooldowns();
    }

    internal void PhaseSpawn()
    {
        if (_mapSprites == null || _slib == null || _endWaveFlag) return;

        // Order matches C: ENEMY_Think (which spawns) is called BEFORE
        // TILE_DisplayScreen (which advances tilepos) in RAP.C's main loop.
        // So we spawn for the current tilepos first, then advance scroll.
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

    internal void PhaseMovement()
    {
        // C aims ATPLAYER bullets at player CENTER (player_cx/cy = playerx + PLAYERWIDTH/2, playery + PLAYERHEIGHT/2).
        // Pass center coords so enemy.Tick / MakeBullet hands the bullet's Bresenham its true target.
        int px = PlayerLogic.X + 16;
        int py = PlayerLogic.Y + 16;

        // Tick each alive enemy; collect any bullets they fire.
        // C order: ENEMY_Think fires bullets, then ESHOT_Think ticks them (same frame).
        for (int i = 0; i < _enemies.Count; i++)
        {
            var enemy = _enemies[i];
            if (!enemy.Alive) continue;
            var fired = enemy.Tick(px, py);
            if (fired != null)
                _enemyBullets.Add(fired);
            // Multi-gun enemies (helicopters numguns=2, bosses up to 13) fire
            // one bullet per gun per shot tick — collect the extras.
            var extras = enemy.ExtraBulletsThisTick;
            if (extras != null)
                foreach (var b in extras) _enemyBullets.Add(b);
        }

        // Mirrors C SHOTS_Think shot_done branch (SHOTS.C:1218-1249) at the
        // top of each per-iter shot pass. A Bresenham player bullet that
        // reached its target in the previous Tick triggers either a re-init
        // (DUMB_MISSLE delayflag → straight-up scatter to y=0) or a per-
        // weapon detonation (MEGA_BOMB → damage-all + clear enemy bullets),
        // or a default Remove. Runs BEFORE the per-bullet Tick to match
        // C's ordering within SHOTS_Think.
        foreach (var b in _playerBullets)
        {
            if (!b.Alive || !b.ReachedTarget) continue;
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
            b.Tick();

        // Tick bonuses (BONUS_Think — drift down 1 px/iter, despawn at y > 200).
        foreach (var bn in _bonuses) bn.Tick();

        // Tick enemy bullets (includes newly fired ones from this frame, matching C's
        // ESHOT_Think which runs after ENEMY_Think in the same game-loop iteration).
        foreach (var b in _enemyBullets)
        {
            b.Tick();
            // ESHOT.C:534-539: shot->cnt++ then if (smokeflag && cnt&1) spawn
            // A_SMALL_SMOKE_UP at (shot->x + xoff, shot->y). For us the smoke
            // is a sentinel Explosion record with the dedicated SmokeExpType
            // so the view can age its SMOKTRAL_BLK frames over time.
            if (b.Alive && b.ShotType == EnemyShotType.Missile && (b.FrameCounter & 1) != 0)
                _explosions.Add(new Explosion(SmokeExpType, b.X + 4, b.Y, SimClock.Frame));
        }
    }

    // Sentinel exptype used only for missile smoke trails; the view maps it
    // to SMOKTRAL_BLK (ANIMS.C:203 A_SMALL_SMOKE_UP). Out of the EXP_ enum
    // range so it never collides with a real EXP_ value.
    private const int SmokeExpType = 100;

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
                _explosions.Add(new Explosion(ExpAirSmall2, b.X, b.Y, SimClock.Frame));
            }
        }

        // Player bullets vs enemy AABB. C SHOTS.C's per-tick collision honors
        // each shot's lib->ht (HIT_TYPE): S_AIR ignores ground, S_GROUND ignores
        // air, S_ALL / S_GRALL hit anything, S_GTILE only damages ground/tiles,
        // S_SUCK is the energy-grab path (deferred). Bullets carry their HitType
        // tag from PlayerShooter; we filter the candidate enemy set here.
        // Beams are handled in a separate pass below (different damage model).
        _hitEnemies.Clear();
        foreach (var b in _playerBullets)
        {
            if (!b.Alive || b.IsBeam) continue;
            foreach (var e in _enemies)
            {
                if (!e.Alive) continue;
                if (!HitTypeMatches(b.HitType, e)) continue;
                int dx = Math.Abs(b.X - e.X);
                int dy = Math.Abs(b.Y - e.Y);
                if (dx < e.HalfW && dy < e.HalfH)
                {
                    b.Kill();
                    _hitEnemies.Add((e, b.Damage));
                    break;
                }
            }
        }

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

        // Body collision: enemy bounding box vs player centre.
        // Mirrors ENEMY.C lines 1039-1057:
        //   if (player_cx > sprite->x && player_cx < sprite->x2)
        //     if (player_cy > sprite->y && player_cy < sprite->y2)
        // e.X/e.Y are top-left corner coordinates (= C's sprite->x/y).
        // C: sprite->x2 = sprite->x + width - 1 (set in ENEMY_Think per flight type).
        // width = 2*HalfW, height = 2*HalfH.
        _bodyCrashEnemies.Clear();
        foreach (var e in _enemies)
        {
            if (!e.Alive) continue;
            // ENEMY.C:1043 — `if (!sprite->groundflag)` guards body collision.
            // F_GROUND family (FlightType 3/4/5) sets groundflag=TRUE in C, so
            // ground enemies (bonuses, turrets, tanks) never crash with the player.
            if (e.Meta.FlightType >= 3 && e.Meta.FlightType <= 5) continue;
            int ex  = e.X;                    // sprite->x (top-left)
            int ex2 = e.X + 2 * e.HalfW - 1; // sprite->x2 (= sprite->x + width - 1)
            int ey  = e.Y;                    // sprite->y (top-left)
            int ey2 = e.Y + 2 * e.HalfH - 1; // sprite->y2 (= sprite->y + height - 1)
            if (px > ex && px < ex2 && py > ey && py < ey2)
                _bodyCrashEnemies.Add(e);
        }

        // Bonus pickup. BONUS.C:207 — `cur->x > playerx && cur->x < playerx+PW
        // && cur->y > playery && cur->y < playery+PH`. The bonus CENTER (cur->x,
        // cur->y) must be inside the player's top-left-anchored rect.
        const int playerW = 32; const int playerH = 32;
        int plx = PlayerLogic.X;
        int ply = PlayerLogic.Y;
        _pickedUpBonuses.Clear();
        foreach (var bn in _bonuses)
        {
            if (!bn.Alive) continue;
            if (bn.X > plx && bn.X < plx + playerW && bn.Y > ply && bn.Y < ply + playerH)
                _pickedUpBonuses.Add(bn);
        }
    }

    // OBJ_TYPE numeric constants for the non-weapon bonus types handled here.
    // Weapon-type dispatch (0..14) lives in PlayerShooter.GrantWeapon.
    private const int OBJ_SUPER_SHIELD  = 15;
    private const int OBJ_ENERGY        = 16;
    // S_DETECT (17) ignored — cosmetic. S_ITEMBUY1..6 (18..23) → money,
    // amount per slot from the C obj_lib (deferred — not yet exposed here).

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

    private void HandleShotDone(BulletLogic b)
        => ShotDoneDispatcher.Dispatch(b, _enemyBullets, _enemies, _shooterRng);

    private void ApplyBonusEffect(int objType)
    {
        if (Shooter.GrantWeapon(objType)) return;
        switch (objType)
        {
            case OBJ_SUPER_SHIELD:
                PlayerLogic.Heal(PlayerLogic.MaxShield);   // full restore
                break;
            case OBJ_ENERGY:
                // BONUS.C:214 — MAX_SHIELD/4. MaxShield=100 → +25.
                PlayerLogic.Heal(PlayerLogic.MaxShield / 4);
                break;
            default:
                // S_DETECT (17) and S_ITEMBUY1..6 (18..23) are not yet handled
                // here. Money pickups in particular need the per-slot value
                // table from obj_lib; deferring until a parity gap forces it.
                break;
        }
    }

    internal void PhaseCollisionResolve()
    {
        const int playerWidth2 = 16;   // PLAYERWIDTH/2 — subtracted from enemy hits on body crash

        // Apply player damage from enemy bullets.
        if (_playerHit && _playerHitDmg > 0)
        {
            PlayerLogic.TakeDamage(_playerHitDmg);
        }

        // Apply enemy damage from player bullets.
        foreach (var (enemy, dmg) in _hitEnemies)
        {
            enemy.TakeDamage(dmg);
            if (!enemy.Alive)
            {
                Score += (uint)enemy.Meta.Money;
                SpawnExplosion(enemy);
                SpawnBonusFor(enemy);
            }
        }

        // Body collision: enemy hits player (mirrors ENEMY.C lines 1039-1057).
        // sprite->hits -= PLAYERWIDTH/2 = 16. If hits ≤ 0, enemy dies → add money.
        // Player takes OBJS_SubEnergy(max(width,height) >> 2) = BodyCrashDamage.
        // C also fires A_SMALL_AIR_EXPLO at the collision point regardless of
        // whether the enemy dies (ENEMY.C:1054).
        bool playerWasAlive = PlayerLogic.Alive;
        foreach (var e in _bodyCrashEnemies)
        {
            if (!e.Alive) continue;
            e.TakeDamage(playerWidth2);
            int bodyDmg = e.Meta.BodyCrashDamage;
            PlayerLogic.TakeDamage(bodyDmg);
            _explosions.Add(new Explosion(ExpAirSmall2, PlayerLogic.X + 16, PlayerLogic.Y + 16, SimClock.Frame));
            if (!e.Alive)
            {
                Score += (uint)e.Meta.Money;
                SpawnExplosion(e);
                SpawnBonusFor(e);
            }
        }
        // Apply bonus pickup effects collected this tick (weapon → inventory,
        // S_ENERGY → heal). C's BONUS.C:208-216 path; we batch in
        // CollisionResolve so all death/pickup events fire after collection.
        foreach (var b in _pickedUpBonuses)
        {
            ApplyBonusEffect(b.ObjType);
            b.Kill();
        }
        _pickedUpBonuses.Clear();

        // Player just died this tick — spawn one large death explosion at the
        // player's center (mirrors RAP.C:583 A_LARGE_AIR_EXPLO at player_cx/cy).
        if (playerWasAlive && !PlayerLogic.Alive)
        {
            _explosions.Add(new Explosion(ExpAirLarge, PlayerLogic.X + 16, PlayerLogic.Y + 16, SimClock.Frame));
        }
    }

    // C exptype constants used for cosmetic-only explosion events (SOURCE/MAP.H).
    private const int ExpAirLarge  = 2;   // EXP_AIRLARGE → LGFLAK_BLK
    private const int ExpAirSmall2 = 10;  // EXP_AIRSMALL2 → SMFLAK_BLK

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
    private void SpawnBonusFor(EnemyLogic e)
    {
        if (e.Meta.Bonus < 0) return;
        // C: BONUS_Add(curlib->bonus, sprite->x, sprite->y). The bonus spawns
        // at the enemy's TOP-LEFT corner (sprite->x/y), not center.
        _bonuses.Add(new BonusLogic(e.Meta.Bonus, e.X, e.Y));
    }
    private void SpawnExplosion(EnemyLogic e)
    {
        int cx = e.X + e.Meta.HalfX;
        int cy = e.Y + e.Meta.HalfY;
        int fc = SimClock.Frame;
        _explosions.Add(new Explosion(e.Meta.ExpType, cx, cy, fc));
        if (e.Meta.ExpType == ExpAirLargeCode)
        {
            int w = e.Meta.Width;
            int h = e.Meta.Height;
            int count = (w >> 4) * (h >> 4);
            // Deterministic pseudo-random offsets so successive explosions land
            // at distinct positions inside the sprite. Mixing hash uses prime
            // multipliers — no RNG state mutated.
            uint hash = (uint)(e.X * 73856093 ^ e.Y * 19349663 ^ fc * 83492791);
            for (int i = 0; i < count; i++)
            {
                hash = hash * 1103515245u + 12345u;
                int ox = (int)((hash >> 8) % (uint)System.Math.Max(1, w));
                hash = hash * 1103515245u + 12345u;
                int oy = (int)((hash >> 8) % (uint)System.Math.Max(1, h));
                int t = (i & 1) == 1 ? 1 /* EXP_AIRMED → A_MED_AIR_EXPLO */
                                     : 10 /* EXP_AIRSMALL2 → A_MED_AIR_EXPLO2 */;
                // Stagger start frame slightly so the cascade doesn't appear
                // all at once (matches C's per-loop ANIMS_StartAnim spacing).
                _explosions.Add(new Explosion(t, e.X + ox, e.Y + oy, fc + (i % 4)));
            }
        }
    }

    internal void PhaseCleanup()
    {
        // Remove dead or out-of-bounds enemies.
        _enemies.RemoveAll(e => !e.Alive);

        // Remove out-of-bounds bullets.
        _playerBullets.RemoveAll(b => !b.Alive);
        _enemyBullets.RemoveAll(b => !b.Alive);
        _bonuses.RemoveAll(b => !b.Alive);

        // Drop finished explosions. Frames-per-animation is determined by the
        // view's BlkInfo table; we cap at a conservative 50 frames so a missing
        // mapping can't leak an explosion forever. Smoke trails are short-
        // lived (SMOKTRAL_BLK has 4 frames) so we cull them aggressively.
        const int MaxAnimFrames = 50;
        const int MaxSmokeFrames = 4;
        int fc = SimClock.Frame;
        _explosions.RemoveAll(x =>
            fc - x.StartFc >= (x.ExpType == SmokeExpType ? MaxSmokeFrames : MaxAnimFrames));
    }

    internal void PhaseHud()
    {
        // Shield recharge (mirrors OBJS_Think in OBJECTS.C).
        // CHARGE_SHIELD = 96. Every 97 game loops, heal 1 shield.
        // Only on curplr_diff < DIFF_3 (we're DIFF_2 by default).
        _thinkCnt++;
        if (_thinkCnt > ChargeShield)
        {
            _thinkCnt = 0;
            PlayerLogic.Heal(1);
        }
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
