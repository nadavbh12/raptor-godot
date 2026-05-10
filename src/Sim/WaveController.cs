using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Raptor.Sim.MazeLevel;
using Raptor.Sim.Player;
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
    private string?             _assetsRoot;

    // ── Wave state ────────────────────────────────────────────────────────────
    private bool _waveActive = false;
    private int  _waveNum    = 1;

    // ── Game-loop rate throttle ──────────────────────────────────────────────
    // The C game loop runs at ~23 Hz: it waits for 3 framecount increments
    // before each iteration (RAPTOR_TEST_DETERMINISTIC mode, 70 Hz timer).
    // Our Godot physics ticks at 70 Hz.  To match countdown/movement/bullet
    // cadence we only advance sim logic every 3rd physics tick.
    private int _subTick = 0;
    // C's game loop: 3 pumps (while FC-local_cnt < 3) + 1 TILE_DisplayScreen = 4 fc/loop
    // on average (loop 2 is "free" after GFX_FadeIn(64) blast, but the steady-state is 4).
    // Empirically derived from the parity golden: spawn of second enemy wave at fc=420 maps
    // to game loop 89, which at 4 fc/loop fires at tick=356 (after fc=350 sample, before fc=420).
    private const int GameLoopPhysicsTicksPerStep = 4;

    // ── Player ────────────────────────────────────────────────────────────────
    public  PlayerLogic  PlayerLogic { get; } = new();
    public  uint         Score { get; private set; } = 0;

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

    // ── Collision scratch ─────────────────────────────────────────────────────
    private readonly List<(EnemyLogic enemy, int dmg)> _hitEnemies = new();
    private bool _playerHit;
    private int  _playerHitDmg;
    private readonly List<EnemyLogic> _bodyCrashEnemies = new();  // enemies that collided with player

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
        _waveNum   = gameNum + 1;  // gameNum is 0-based; wave files are 1-based.
        LoadWave(_waveNum);
        _waveActive = true;
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
        _hitEnemies.Clear();
        _playerHit = false;
        _endWaveFlag = false;

        // Reset scroll to start position (mirrors TILE_Init in C).
        _tilepos  = (MAP_ROWS - MAP_ONSCREEN) * MAP_COLS;
        _tileyoff = 200 - MAP_ONSCREEN * MAP_BLOCKSIZE;  // -56
        _tiley    = _tilepos / MAP_COLS - 3;             // = 139

        // Reset player position.
        PlayerLogic.Reset();

        // Load sprite metadata library.
        string slibPath = Path.Combine(_assetsRoot ?? "assets", "sprites_meta", "SPRITE1_ITM.json");
        _slib = SpriteMetaLibrary.LoadFromFile(slibPath);

        // Load map sprite list.
        string mapPath = MazeLevelLoader.WaveMapPath(_assetsRoot ?? "assets", waveNum);
        var mapData    = MazeLevelLoader.Load(mapPath);
        _mapSprites    = mapData.Sprites ?? new List<MapSpriteEntry>();
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
                    var meta   = _slib.Get(cur.Slib);
                    int spawnX = cur.X * MAP_BLOCKSIZE + MAP_LEFT;
                    // C's mapY formula (ENEMY_Add):
                    //   new->y = tileyoff - ((tiley - sprite.y) * 32) - 97
                    // This is the initial screen y used for the countdown, not the flight origin.
                    int mapY   = _tileyoff - ((tiley - cur.Y) * MAP_BLOCKSIZE) - 97;
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
        if (!_waveActive) return;
        _subTick++;
        if (_subTick < GameLoopPhysicsTicksPerStep) return;
        _subTick = 0;
        _scheduler.Tick();
    }

    // ── Phase implementations (called by GamePhaseScheduler) ──────────────────

    internal void PhaseInput()
    {
        // Passive mission: player doesn't move or fire.
        // (PlayerInputBuffer would supply dx/dy/fire in a full implementation.)
        PlayerLogic.Tick(0, 0);
    }

    internal void PhaseSpawn()
    {
        if (_mapSprites == null || _slib == null || _endWaveFlag) return;

        // Advance scroll: mirrors TILE.C tileyoff/tilepos update.
        _tileyoff++;
        if (_tileyoff > 0)
        {
            _tileyoff -= MAP_BLOCKSIZE;
            _tilepos  -= MAP_COLS;
            _tiley     = _tilepos / MAP_COLS - 3;
            if (_tilepos <= 0) _tilepos = 0;
        }

        // Spawn all enemies whose y matches current tiley.
        SpawnForTiley(_tiley);
    }

    internal void PhaseMovement()
    {
        int px = PlayerLogic.X;
        int py = PlayerLogic.Y;

        // Tick each alive enemy; collect any bullets they fire.
        for (int i = 0; i < _enemies.Count; i++)
        {
            var enemy = _enemies[i];
            if (!enemy.Alive) continue;
            var fired = enemy.Tick(px, py);
            if (fired != null)
                _enemyBullets.Add(fired);
        }

        // Tick player bullets.
        foreach (var b in _playerBullets)
            b.Tick();

        // Tick enemy bullets.
        foreach (var b in _enemyBullets)
            b.Tick();
    }

    internal void PhaseCollisionCollect()
    {
        // Enemy bullets vs player AABB.
        // C uses `dx < PLAYERWIDTH/2 && dy < PLAYERHEIGHT/2` (strict less-than).
        // PLAYERWIDTH = 32, PLAYERHEIGHT = 32 → half = 16.
        const int playerHw = 16; const int playerHh = 16;
        int px = PlayerLogic.X;
        int py = PlayerLogic.Y;

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
            }
        }

        // Player bullets vs enemy AABB.
        _hitEnemies.Clear();
        foreach (var b in _playerBullets)
        {
            if (!b.Alive) continue;
            foreach (var e in _enemies)
            {
                if (!e.Alive) continue;
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

        // Body collision: enemy bounding box vs player centre.
        // Mirrors ENEMY.C lines 1039-1057:
        //   if (player_cx > sprite->x && player_cx < sprite->x2)
        //     if (player_cy > sprite->y && player_cy < sprite->y2)
        // where sprite->x = enemy_centre_x - hlx (top-left corner).
        // In our system, enemy centre = (e.X, e.Y); top-left = (e.X - e.HalfW, e.Y - e.HalfH).
        _bodyCrashEnemies.Clear();
        foreach (var e in _enemies)
        {
            if (!e.Alive) continue;
            int ex  = e.X - e.HalfW;   // sprite->x
            int ex2 = e.X + e.HalfW;   // sprite->x2  (= sprite->x + width)
            int ey  = e.Y - e.HalfH;   // sprite->y
            int ey2 = e.Y + e.HalfH;   // sprite->y2  (= sprite->y + height)
            if (px > ex && px < ex2 && py > ey && py < ey2)
                _bodyCrashEnemies.Add(e);
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
                Score += (uint)enemy.Meta.Money;
        }

        // Body collision: enemy hits player (mirrors ENEMY.C lines 1039-1057).
        // sprite->hits -= PLAYERWIDTH/2 = 16. If hits ≤ 0, enemy dies → add money.
        // Player takes OBJS_SubEnergy(max(width,height) >> 2) = BodyCrashDamage.
        foreach (var e in _bodyCrashEnemies)
        {
            if (!e.Alive) continue;
            e.TakeDamage(playerWidth2);
            int bodyDmg = e.Meta.BodyCrashDamage;
            PlayerLogic.TakeDamage(bodyDmg);
            if (!e.Alive)
                Score += (uint)e.Meta.Money;
        }
    }

    internal void PhaseCleanup()
    {
        // Remove dead or out-of-bounds enemies.
        _enemies.RemoveAll(e => !e.Alive);

        // Remove out-of-bounds bullets.
        _playerBullets.RemoveAll(b => !b.Alive);
        _enemyBullets.RemoveAll(b => !b.Alive);
    }

    internal void PhaseHud()
    {
        // Shield recharge (mirrors OBJS_Think in OBJECTS.C).
        // CHARGE_SHIELD = 96. Every 97 game loops, heal 1 shield.
        // Only on curplr_diff < DIFF_3 (we're DIFF_2 by default).
        // Disabled temporarily to isolate bullet damage behavior.
        // TODO: re-enable once bullet collision is tuned.
        //_thinkCnt++;
        //if (_thinkCnt > ChargeShield)
        //{
        //    _thinkCnt = 0;
        //    PlayerLogic.Heal(1);
        //}
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
