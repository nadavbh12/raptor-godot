using Raptor.Sim.Shots;

namespace Raptor.Sim.Bullet;

public enum BulletKind { Player, Enemy }

/// <summary>
/// C SOURCE/ESHOT.H ESHOT_TYPE enum. Determines the bullet's BLK sprite for
/// view rendering (e.g. ES_MISSLE → EMISLE_BLK; the rest → ESHOT_BLK).
/// </summary>
public enum EnemyShotType
{
    AtPlayer  = 0,
    AtDown    = 1,
    AngleLeft = 2,
    AngleRight= 3,
    Missile   = 4,
    Laser     = 5,
    Mines     = 6,
    Plasma    = 7,
    Coconuts  = 8,
}

/// <summary>
/// Pure-C# bullet. Position advances each Tick by current speed in the given direction.
/// Speed accelerates from _initSpeed to _maxSpeed by 1 per tick (mirrors ESHOT.C).
///
/// For ES_ATDOWN bullets (straight down): dirX=0, dirY=+1, initSpeed=3, maxSpeed=6.
/// For ES_ATPLAYER bullets (aimed):       direction set toward player, initSpeed=1, maxSpeed=6.
/// For ES_ANGLELEFT/RIGHT (diagonal):     fixed velocity (no acceleration needed).
///
/// ESHOT.C ESHOT_Think despawn conditions: y >= 200 or y &lt; 0 or x >= 320 or x &lt; 0.
/// </summary>
public sealed class BulletLogic
{
    public BulletKind Kind { get; }
    /// <summary>
    /// Which entities this bullet can damage (mirrors SHOT_LIB.ht in C). Player
    /// bullets carry their weapon's hit type so collision can filter air/ground
    /// enemies. Enemy bullets default to All — they always hit the player.
    /// </summary>
    public HitType HitType { get; set; } = HitType.All;
    /// <summary>
    /// Enemy shot type for view rendering. Defaults to AtPlayer (0) which uses
    /// the ESHOT_BLK sprite. Set explicitly by EnemyLogic.MakeBullet so the
    /// view can pick the right BLK family (EMISLE_BLK, ELASER_BLK, ...).
    /// </summary>
    public EnemyShotType ShotType { get; set; } = EnemyShotType.AtPlayer;
    /// <summary>
    /// Per-bullet animation counter (mirrors ESHOT.C: shot->curframe++ each tick).
    /// View modulos by num_frames per shot type to pick the BLK frame; ESHOT_BLK
    /// has 2 frames so the bullet visibly oscillates in size.
    /// </summary>
    public int FrameCounter { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }

    /// <summary>
    /// Hit-point damage dealt to the player on contact.
    /// Mirrors ESHOT_LIB.hits:
    ///   LIB_NORMAL (ES_ATDOWN, ES_ANGLELEFT, ES_ANGLERIGHT, ES_MISSLE) = 2
    ///   LIB_ATPLAY (ES_ATPLAYER)  = 1
    ///   LIB_LASER                 = 12
    ///   LIB_PLASMA                = 15
    ///   LIB_MINES                 = 16
    ///   LIB_COCO (ES_COCONUTS)    = 1
    /// </summary>
    public int Damage { get; }

    // Per-tick velocity. Stored as float so ATPLAYER aim doesn't collapse to
    // cardinal directions when the unit vector has |component| < 0.5.
    // Position is exposed as int (matches C, parity-affecting); the float
    // accumulator is private state.
    private readonly float _dx;
    private readonly float _dy;
    private float _fx;  // sub-pixel accumulator for X
    private float _fy;  // sub-pixel accumulator for Y

    // Speed model: speed starts at _curSpeed, increments by 1 per Tick to _maxSpeed.
    private int  _curSpeed;
    private readonly int _maxSpeed;
    private readonly bool _accelerating;  // true for axis-aligned bullets (ATDOWN, ATPLAY)

    // Bresenham mode (mirrors C InitMobj/MoveSobj). Used for ATPLAYER and
    // COCONUTS where C aims integer-precise toward (x2, y2). When _bresenham
    // is true, Tick() walks the integer Bresenham step instead of using
    // float dx/dy + sub-pixel accumulator. Off-by-one parity divergences in
    // float aim caused late-game shield drift in mission_long.
    private readonly bool _bresenham;
    private int _bx, _by;          // current Bresenham position (= move.x, move.y)
    private int _baddX, _baddY;    // ±1 step direction
    private int _bdelX, _bdelY;    // absolute distance to target
    private int _berr;             // Bresenham error accumulator
    private int _bmaxloop;         // remaining steps until target reached
    // Mirrors C `shot->move.done`. Set true when Bresenham completes
    // (_bmaxloop hits 0 during the Tick that reaches the target), or when a
    // straight player shot's move.y crosses the top edge at the end of the
    // tick. The SHOTS.C shot_done block (line 1218) reads this at the start of
    // the NEXT iter; WaveController.PhaseMovement does the same.
    private bool _bresenhamDone;
    private readonly bool _playerAimed;
    private readonly int _baHlx, _baHly;
    private bool _playerStraightDone;
    private bool _deferredDoneFlag;
    /// <summary>True when C `shot->move.done` is set.</summary>
    public bool ReachedTarget => _bresenhamDone || _playerStraightDone;
    /// <summary>True when C will dispatch shot_done at the start of the next SHOTS_Think pass.</summary>
    public bool PendingShotDone => ReachedTarget || _deferredDoneFlag;
    /// <summary>True when C `shot->doneflag` was set by a hit in this pass.</summary>
    public bool DeferredDoneFlag => _deferredDoneFlag;
    /// <summary>Diagnostic view of C `shot->doneflag` after the current pass.</summary>
    public bool DoneFlagForDump => _deferredDoneFlag || _playerStraightDone;
    /// <summary>True for player use_plot shots (MiniGun, DumbMissile, MegaBomb).</summary>
    public bool IsPlayerAimedBresenham => _bresenham && _playerAimed;
    /// <summary>
    /// Mirrors C `cur->delayflag` (initial true for DUMB_MISSLE only). When
    /// true, the first ReachedTarget event re-targets the bullet to a new
    /// (x2, y2) instead of removing it (SHOTS.C:1220-1227).
    /// </summary>
    public bool Delayed { get; set; }
    /// <summary>
    /// Identifies which player weapon spawned this bullet (null for enemy bullets
    /// or generic constructions). WaveController reads this at the shot_done
    /// step to dispatch MegaBomb detonation (SHOTS.C:1232-1241) and DumbMissile
    /// scatter, mirroring the C `switch (lib->type)` branch.
    /// </summary>
    public ObjType? PlayerWeapon { get; set; }
    /// <summary>
    /// Mirrors C SHOT_LIB.type for dump comparison. This normally matches
    /// PlayerWeapon, except the original C table stores S_MISSLE_PODS in the
    /// S_AIR_MISSLE entry.
    /// </summary>
    public ObjType? CWeaponTypeForDump { get; set; }
    /// <summary>Clears the ReachedTarget flag after the controller dispatched its event.</summary>
    public void ClearReachedTarget()
    {
        _bresenhamDone = false;
        _playerStraightDone = false;
        _deferredDoneFlag = false;
    }
    /// <summary>
    /// Snapshot display from current move.x/y without advancing. C reaches
    /// shot_done after the damage switch, so use_plot shots with move.done set
    /// from the previous pass still get one final collision at their target.
    /// </summary>
    public void SnapshotForPendingShotDonePass()
    {
        if (_bresenham)
        {
            X = _playerAimed ? _bx - _baHlx : _bx;
            Y = _playerAimed ? _by - _baHly : _by;
        }
    }

    /// <summary>
    /// Mirrors C `shot->doneflag = TRUE` after an enemy hit. That flag is not
    /// `move.done` yet, so the current dump/display pass still sees the bullet
    /// with reached=0; the next SHOTS_Think pass promotes it to shot_done.
    /// </summary>
    public void MarkDoneFlagForNextPass() => _deferredDoneFlag = true;
    /// <summary>
    /// Re-initialize the Bresenham target. Mirrors C `InitMobj(&shot->move)` in
    /// the DUMB_MISSLE delayflag branch (SHOTS.C:1224). Keeps current position
    /// (_bx, _by) as the new start; recomputes step direction toward (x2, y2).
    /// </summary>
    public void ReInitBresenhamTarget(int x2, int y2)
    {
        if (!_bresenham) return;
        _baddX = 1; _baddY = 1;
        _bdelX = x2 - _bx;
        _bdelY = y2 - _by;
        if (_bdelX < 0) { _bdelX = -_bdelX; _baddX = -1; }
        if (_bdelY < 0) { _bdelY = -_bdelY; _baddY = -1; }
        if (_bdelX >= _bdelY) { _berr = -(_bdelY >> 1); _bmaxloop = _bdelX + 1; }
        else                  { _berr =  (_bdelX >> 1); _bmaxloop = _bdelY + 1; }
        _bresenhamDone = false;
    }

    // Player-straight mode (mirrors SHOTS.C SHOTS_Think for the S_SHOOT/!use_plot
    // path used by FORWARD_GUNS, PLASMA_GUNS, MICRO_MISSLE, MISSLE_PODS, etc.).
    // C's order per tick (SHOTS.C:1052-1267):
    //   1) shot->y = move.y - hly   (display)
    //   2) speed++ up to maxspeed   (BEFORE the move — unlike ESHOT_Think!)
    //   3) move.y -= speed          (or += for downward bullets)
    // Distinct from _bresenham (Bresenham aim) and the float ESHOT model.
    private readonly bool _playerStraight;
    private int _psHlx, _psHly;    // sprite half-dims for display offset
    private int _psMoveX, _psMoveY; // C shot->move.x, shot->move.y
    private int _psSign;           // +1 for downward bullets, -1 for upward
    private bool _psSmoke;         // SMOKE trail flag (lib->smoke)

    // Beam mode. Stationary projectile that despawns after a fixed number of
    // ticks (C SHOTS_Think for !move_flag bullets: curframe++ each iter; when
    // curframe >= numframes, set move.done = TRUE). Column damage is applied
    // by WaveController each tick — BulletLogic just counts down. fplrX/Y
    // (lib->fplrx / lib->fplry) signal that the beam should follow the player
    // each iter; WaveController applies the displacement.
    private readonly bool _beam;
    private readonly bool _enemyMine;
    private int _mineMoveX, _mineMoveY, _minePos, _mineFuse;
    private static readonly int[] MineXOffset = { -1, 0, 1, 2, 3, 3, 3, 2, 1, 0, -1, -2, -3, -3, -3, -2 };
    private static readonly int[] MineYOffset = { -3, -3, -3, -2, -1, 0, 1, 2, 3, 3, 3, 2, 1, 0, -1, -2 };
    private int _beamLife;            // remaining ticks before despawn
    private readonly bool _beamDamages; // false for LineBeam (TURRET, already damaged at spawn); true for VerticalBeam
    // C SHOTS.C:1090-1098 fplrx/fplry path. _baseX/_baseY = beam position at
    // spawn (pre-fplr). _startPlayerX/_startPlayerY = player_cx/cy at spawn.
    // Each iter the beam re-renders at base + (current_player - start_player)
    // so it follows the ship rigidly.
    private readonly int _baseX, _baseY;
    private readonly int _startPlayerX, _startPlayerY;
    private readonly bool _fplrX, _fplrY;
    /// <summary>True iff this bullet is a stationary beam (S_LINE or S_BEAM).</summary>
    public bool IsBeam => _beam;
    /// <summary>True iff this beam applies per-tick column damage (false for TURRET line).</summary>
    public bool BeamDamages => _beamDamages;
    /// <summary>True iff this beam tracks player position each tick (lib->fplrx / lib->fplry).</summary>
    public bool TracksPlayer => _beam && (_fplrX || _fplrY);
    /// <summary>
    /// Re-positions a player-tracking beam each iter. Mirrors SHOTS.C:1090-1098:
    ///   if (fplrx) shot->x += (player_cx - shot->startx);
    ///   if (fplry) shot->y += (player_cy - shot->starty);
    /// We compute the absolute position rather than incrementally because the
    /// beam's pre-fplr base is fixed (move doesn't change for !move_flag).
    /// </summary>
    public void ApplyFplr(int playerCx, int playerCy)
    {
        if (!_beam) return;
        X = _baseX + (_fplrX ? (playerCx - _startPlayerX) : 0);
        Y = _baseY + (_fplrY ? (playerCy - _startPlayerY) : 0);
    }

    /// <summary>Current per-tick speed (used by BulletDumper for parity comparison).</summary>
    public int CurSpeed => _curSpeed;
    /// <summary>
    /// C SHOTS_DumpForParity writes SHOTS.cnt, which is a display-only beam
    /// counter. Non-beam player shots animate through curframe and keep cnt=0.
    /// </summary>
    public int CCounterForDump => _beam ? (FrameCounter % 4) : 0;
    /// <summary>Pre-advanced move target (mirrors C move.x). Used by BulletDumper.</summary>
    public int Mx => _playerStraight ? _psMoveX : _bresenham ? _bx : _enemyMine ? _mineMoveX : (int)_fx;
    /// <summary>Pre-advanced move target (mirrors C move.y). Used by BulletDumper.</summary>
    public int My => _playerStraight ? _psMoveY : _bresenham ? _by : _enemyMine ? _mineMoveY : (int)_fy;
    /// <summary>Number of animation frames in this bullet's sprite (mirrors ESHOT_LIB.num_frames).</summary>
    public int NumFrames => ShotType switch
    {
        EnemyShotType.Laser => 4,
        EnemyShotType.Plasma => 1,
        EnemyShotType.Coconuts => 4,
        _ => 2,
    };

    public bool Alive { get; private set; } = true;

    /// <summary>
    /// Create a bullet with a fixed per-tick velocity (no acceleration).
    /// Used for diagonal bullets (ANGLELEFT, ANGLERIGHT, MISSILE) or simple bullets.
    /// </summary>
    public BulletLogic(BulletKind kind, int x, int y, int velX, int velY, int damage = 2)
        : this(kind, x, y, (float)velX, (float)velY, initSpeed: 1, maxSpeed: 1, accelerating: false, damage)
    {
    }

    /// <summary>
    /// Create an axis-aligned bullet with acceleration.
    /// Bullet moves by (dx*speed, dy*speed) per tick, speed starts at initSpeed,
    /// increases by 1 per tick up to maxSpeed.
    /// </summary>
    public BulletLogic(BulletKind kind, int x, int y, int dx, int dy, int initSpeed, int maxSpeed, int damage = 2)
        : this(kind, x, y, (float)dx, (float)dy, initSpeed, maxSpeed, accelerating: true, damage)
    {
    }

    /// <summary>
    /// Create a bullet with float per-axis velocity (used for ATPLAYER aim
    /// where dx/dy is a normalized unit vector with sub-integer components).
    ///
    /// Mirrors C ESHOT_Shoot ending with InitMobj + MoveSobj(&amp;move, 1):
    /// the move-position is pre-advanced by 1 step so that the first
    /// ESHOT_Think iter snapshots a one-step-from-spawn displayed position.
    /// </summary>
    public BulletLogic(BulletKind kind, int x, int y, float dx, float dy,
                       int initSpeed, int maxSpeed, bool accelerating, int damage = 2)
    {
        Kind = kind;
        X = x; Y = y;
        _dx = dx;
        _dy = dy;
        // Pre-advance the move position by 1 step (C: MoveSobj(&move, 1) in ESHOT_Shoot).
        _fx = x + dx;
        _fy = y + dy;
        _curSpeed = initSpeed;
        _maxSpeed = maxSpeed;
        _accelerating = accelerating;
        Damage = damage;
        _bresenham = false;
    }

    /// <summary>
    /// Create a Bresenham-aimed bullet. Mirrors C ESHOT_Shoot for ATPLAYER and
    /// COCONUTS: starts at (x, y), targets (x2, y2), uses InitMobj/MoveSobj
    /// integer steps. Pre-advances 1 step at construction (C: MoveSobj(&move, 1)).
    /// Speed starts at initSpeed and increments by 1 per Tick up to maxSpeed.
    /// </summary>
    public static BulletLogic AimedAt(BulletKind kind, int x, int y, int x2, int y2,
                                      int initSpeed, int maxSpeed, int damage)
        => new BulletLogic(kind, x, y, x2, y2, initSpeed, maxSpeed, damage,
                           preAdvance: true, playerAimed: false, hlx: 0, hly: 0,
                           bresenhamMarker: true);

    public static BulletLogic EnemyMine(int x, int y, int pos = 0, int fuseTicks = 150)
        => new BulletLogic(x, y, pos, fuseTicks, enemyMineMarker: true);

    public void SetEnemyMinePos(int pos)
    {
        if (!_enemyMine) return;
        _minePos = ((pos % 16) + 16) % 16;
    }

    public static BulletLogic PlayerAimedAt(int x, int y, int x2, int y2,
                                            int initSpeed, int maxSpeed,
                                            int hlx, int hly, int damage)
        => new BulletLogic(BulletKind.Player, x, y, x2, y2, initSpeed, maxSpeed, damage,
                           preAdvance: false, playerAimed: true, hlx: hlx, hly: hly,
                           bresenhamMarker: true);

    /// <summary>
    /// Create a player straight-fire bullet (FORWARD_GUNS / PLASMA_GUNS /
    /// MICRO_MISSLE / MISSLE_PODS / etc.). Mirrors SHOTS_PlayerShoot init +
    /// SHOTS_Think movement (SHOTS.C:619-1268).
    /// Spawn coords are the C `cur->x, cur->y = player_cx + gun_off, player_cy`
    /// values. Internal move.y advances by speed (downward) or -speed (upward).
    /// Displayed (X, Y) lag move by (hlx, hly) — the sprite top-left corner.
    /// </summary>
    public static BulletLogic PlayerStraight(int spawnX, int spawnY,
                                             int initSpeed, int maxSpeed,
                                             int hlx, int hly, int damage,
                                             bool upward = true, bool smoke = false)
        => new BulletLogic(spawnX, spawnY, initSpeed, maxSpeed, hlx, hly, damage,
                           upward, smoke, playerStraightMarker: true);

    private BulletLogic(int spawnX, int spawnY, int initSpeed, int maxSpeed,
                        int hlx, int hly, int damage, bool upward, bool smoke,
                        bool playerStraightMarker)
    {
        Kind = BulletKind.Player;
        // Mirrors C SHOTS_PlayerShoot init (SHOTS.C:650-684). At spawn (BEFORE
        // any SHOTS_Think iteration runs), C stores:
        //   cur->x = player_cx + gun_offset   ← raw spawn, NOT yet display-corrected
        //   cur->y = player_cy
        //   cur->move.x/y = cur->x/y
        //   cur->speed = lib->speed (initSpeed)
        // The FIRST SHOTS_Think in the same iter then snapshots display
        // (= move - hl) and advances move/speed. We treat one Tick() as one
        // SHOTS_Think iteration so PhaseMovement on iter N produces iter-N
        // post-think state. X/Y here is therefore the raw pre-think spawn
        // position; nothing reads it before the first Tick.
        _psMoveX = spawnX;
        _psMoveY = spawnY;
        _psHlx = hlx;
        _psHly = hly;
        _psSign = upward ? -1 : +1;
        _psSmoke = smoke;
        X = spawnX;
        Y = spawnY;
        _curSpeed = initSpeed;
        _maxSpeed = maxSpeed;
        _accelerating = true;
        Damage = damage;
        _playerStraight = true;
        _bresenham = false;
    }

    // Enemy laser (ES_LASER). A short-lived beam that tracks the firing enemy's
    // gun each tick and damages the player on horizontal alignment (ESHOT.C:
    // 453-470). Unlike normal enemy bullets it is NOT subject to AABB collision:
    // WaveController calls LaserTick each iter and applies the returned damage
    // directly (mirrors OBJS_SubEnergy inside ESHOT_Think). Vertical-column
    // RENDERING (ESHOT.C:558+) is a View concern and is NOT modeled here.
    private readonly bool _enemyLaser;
    private readonly Raptor.Sim.Enemy.EnemyLogic? _laserEnemy;
    private readonly int _laserShootX, _laserShootY, _laserNumFrames;
    private const int PlayerWidth2 = 16;   // PLAYERWIDTH/2

    /// <summary>True iff this is an enemy ES_LASER beam (driven via LaserTick).</summary>
    public bool IsEnemyLaser => _enemyLaser;

    /// <summary>
    /// Spawn an enemy ES_LASER beam (ESHOT.C:385-393). It tracks the firing
    /// <paramref name="enemy"/>'s gun (offsets <paramref name="gunShootX"/>/
    /// <paramref name="gunShootY"/> = the enemy's shootx[gun]/shooty[gun]) and
    /// lives <paramref name="numFrames"/> passes, damaging the player by
    /// <paramref name="hits"/> (LIB_LASER.hits = 12).
    /// </summary>
    public static BulletLogic EnemyLaser(Raptor.Sim.Enemy.EnemyLogic enemy,
                                         int gunShootX, int gunShootY,
                                         int numFrames = 4, int hits = 12)
        => new BulletLogic(enemy, gunShootX, gunShootY, numFrames, hits);

    private BulletLogic(Raptor.Sim.Enemy.EnemyLogic enemy, int gunShootX, int gunShootY,
                        int numFrames, int hits)
    {
        Kind = BulletKind.Enemy;
        ShotType = EnemyShotType.Laser;
        _enemyLaser = true;
        _laserEnemy = enemy;
        _laserShootX = gunShootX;
        _laserShootY = gunShootY;
        _laserNumFrames = numFrames;
        Damage = hits;
        _accelerating = false;
        _bresenham = false;
        // Initial beam tip (ESHOT.C:456-457: x = en.x + shootx - 4, y = en.y + shooty).
        X = enemy.X + gunShootX - 4;
        Y = enemy.Y + gunShootY;
    }

    /// <summary>
    /// One ESHOT_Think pass for an ES_LASER beam (ESHOT.C:449/453-470). Tracks
    /// the firing enemy's gun, lives num_frames passes (curframe++ each), and on
    /// each of the first (num_frames-1) passes damages the player by Damage when
    /// horizontally aligned (|x - player_cx| &lt; PLAYERWIDTH/2) and above the
    /// player (y &lt; player_cy). Returns the damage to apply this pass (0 if
    /// none); sets Alive=false when the beam expires. The deterministic
    /// random(4)-2 jitter on move.y2 (ESHOT.C:464) only nudges the rendered beam
    /// endpoint (=0 under RAPTOR_DETERMINISTIC_RNG) and is not modeled here.
    /// </summary>
    public int LaserTick(int playerCx, int playerCy)
    {
        if (!Alive || !_enemyLaser || _laserEnemy == null) return 0;
        FrameCounter++;                                   // shot->curframe++ (ESHOT.C:449)
        if (FrameCounter < _laserNumFrames)               // ESHOT.C:454
        {
            X = _laserEnemy.X + _laserShootX - 4;          // ESHOT.C:456
            Y = _laserEnemy.Y + _laserShootY;              // ESHOT.C:457
            if (System.Math.Abs(X - playerCx) < PlayerWidth2 && Y < playerCy)  // ESHOT.C:462
                return Damage;                             // OBJS_SubEnergy(lib->hits) (ESHOT.C:465)
            return 0;
        }
        Alive = false;                                     // ESHOT.C:469 doneflag
        return 0;
    }

    private BulletLogic(int x, int y, int pos, int fuseTicks, bool enemyMineMarker)
    {
        Kind = BulletKind.Enemy;
        X = x;
        Y = y;
        _mineMoveX = x;
        _mineMoveY = y;
        _minePos = ((pos % 16) + 16) % 16;
        _mineFuse = fuseTicks;
        _curSpeed = fuseTicks;
        _maxSpeed = fuseTicks;
        _accelerating = false;
        Damage = 16;
        ShotType = EnemyShotType.Mines;
        _enemyMine = true;
        _bresenham = false;
    }

    /// <summary>True if this is a player straight-fire bullet (SHOTS movement model).</summary>
    public bool IsPlayerStraight => _playerStraight;
    /// <summary>True if this bullet leaves a smoke trail each tick (lib->smoke).</summary>
    public bool LeavesSmoke => _psSmoke;

    /// <summary>
    /// Spawn a one-tick LINE beam (TURRET — S_LINE). C SHOTS_PlayerShoot for
    /// TURRET damages the enemy immediately and creates a bullet that exists
    /// for a single SHOTS_Think pass before SHOTS_Display removes it. We model
    /// it as a beam with life=1 and zero column-damage (the damage already
    /// happened upstream in PlayerShooter). lib->fplrx = FALSE.
    /// </summary>
    public static BulletLogic LineBeam(int x, int y, int damage)
        => new BulletLogic(beamX: x, beamY: y, life: 1, damages: false, damage: damage,
                           fplrX: false, fplrY: false, startPlayerX: x, startPlayerY: y,
                           beamMarker: true);

    /// <summary>
    /// Spawn a multi-tick VERTICAL beam (FORWARD_LASER / DEATH_RAY — S_BEAM).
    /// Sits at (x, y), damages first eligible enemy in its column each tick
    /// (per SHOTS.C:1073 — beam.x &gt; enemy.x &amp;&amp; beam.x &lt; enemy.x2),
    /// despawns after `life` ticks (= lib->numframes).
    /// startPlayer{X,Y} record player_cx/cy at spawn; WaveController calls
    /// <see cref="ApplyFplr"/> each iter to translate the beam with the ship
    /// (mirrors SHOTS.C:1090-1098 lib->fplrx/fplry block).
    /// </summary>
    public static BulletLogic VerticalBeam(int x, int y, int life, int damage,
                                           int startPlayerX, int startPlayerY)
        => new BulletLogic(beamX: x, beamY: y, life: life, damages: true, damage: damage,
                           fplrX: true, fplrY: true,
                           startPlayerX: startPlayerX, startPlayerY: startPlayerY,
                           beamMarker: true);

    private BulletLogic(int beamX, int beamY, int life, bool damages, int damage,
                        bool fplrX, bool fplrY, int startPlayerX, int startPlayerY,
                        bool beamMarker)
    {
        Kind = BulletKind.Player;
        X = beamX; Y = beamY;
        _baseX = beamX; _baseY = beamY;
        _startPlayerX = startPlayerX; _startPlayerY = startPlayerY;
        _fplrX = fplrX; _fplrY = fplrY;
        Damage = damage;
        _beam = true;
        _beamLife = life;
        _beamDamages = damages;
        // Beams don't accelerate or move; the speed/maxspeed fields are unused.
        _accelerating = false;
        _bresenham = false;
        _playerStraight = false;
    }

    private BulletLogic(BulletKind kind, int x, int y, int x2, int y2,
                        int initSpeed, int maxSpeed, int damage, bool preAdvance,
                        bool playerAimed, int hlx, int hly, bool bresenhamMarker)
    {
        Kind = kind;
        X = x; Y = y;
        _dx = 0; _dy = 0;
        _fx = x; _fy = y;
        _curSpeed = initSpeed;
        _maxSpeed = maxSpeed;
        _accelerating = true;
        Damage = damage;
        _bresenham = true;
        _playerAimed = playerAimed;
        _baHlx = hlx;
        _baHly = hly;

        // InitMobj (RAP.C:339-371).
        _bx = x; _by = y;
        _baddX = 1; _baddY = 1;
        _bdelX = x2 - x;
        _bdelY = y2 - y;
        if (_bdelX < 0) { _bdelX = -_bdelX; _baddX = -1; }
        if (_bdelY < 0) { _bdelY = -_bdelY; _baddY = -1; }
        if (_bdelX >= _bdelY) { _berr = -(_bdelY >> 1); _bmaxloop = _bdelX + 1; }
        else                  { _berr =  (_bdelX >> 1); _bmaxloop = _bdelY + 1; }
        if (preAdvance)
            BresenhamStep();
    }

    private void BresenhamStep()
    {
        if (_bdelX >= _bdelY)
        {
            _bx += _baddX;
            _berr += _bdelY;
            if (_berr > 0) { _by += _baddY; _berr -= _bdelX; }
        }
        else
        {
            _by += _baddY;
            _berr += _bdelX;
            if (_berr > 0) { _bx += _baddX; _berr -= _bdelY; }
        }
        _bmaxloop--;
        // Mirrors C MoveSobj: it does not stop the current speed loop when
        // maxloop reaches zero; it marks done after completing all steps.
        if (_bmaxloop < 1) _bresenhamDone = true;
    }

    public void Tick()
    {
        if (!Alive) return;
        // ES_LASER is driven by WaveController via LaserTick(player_cx, player_cy),
        // not the generic movement path (it tracks the firing enemy + needs the
        // player centre for its alignment-damage rule). Ignore a stray Tick().
        if (_enemyLaser) return;
        if (_enemyMine)
        {
            FrameCounter++;
            _mineFuse--;
            _curSpeed = _mineFuse;
            if (_mineFuse <= 0)
            {
                Alive = false;
                return;
            }
            X = _mineMoveX + MineXOffset[_minePos];
            Y = _mineMoveY + MineYOffset[_minePos];
            _mineMoveY++;
            _minePos = (_minePos + 1) & 15;
            if (X < 0 || X >= 320 || Y < 0 || Y >= 200) Alive = false;
            return;
        }
        if (_beam)
        {
            // SHOTS_Think for !move_flag bullets (SHOTS.C:1109-1127): curframe++
            // each iter, despawn when curframe >= numframes. We just decrement.
            _beamLife--;
            FrameCounter++;
            if (_beamLife <= 0) Alive = false;
            return;
        }
        // C ESHOT_Think order (ESHOT.C:476-485):
        //   shot->x = shot->move.x;   // snapshot displayed pos BEFORE movement
        //   shot->y = shot->move.y;
        //   MoveSobj(&shot->move, shot->speed);
        //   if (shot->speed < lib->speed) shot->speed++;
        if (_playerStraight)
        {
            // SHOTS.C:1052-1267 SHOTS_Think for S_SHOOT / !use_plot:
            //   shot->y = move.y - hly  (display)
            //   speed++ up to maxspeed  (BEFORE the move)
            //   move.y -= speed         (sign by upward/downward direction)
            X = _psMoveX - _psHlx;
            Y = _psMoveY - _psHly;
            // SHOTS.C:1100 — despawn check on displayed pos before movement.
            if ((Y + 16) < 0 || X < 0 || X > 320 || Y > 200)
            {
                Alive = false;
                return;
            }
            if (_accelerating && _curSpeed < _maxSpeed)
                _curSpeed++;
            _psMoveY += _psSign * _curSpeed;
            FrameCounter++;
            // SHOTS.C:1262 — move.y < 0 marks move.done/doneflag AFTER this
            // iter's display position was snapped. Do not kill immediately:
            // C keeps the shot live for the current display/dump pass, then
            // removes it through shot_done at the start of the next pass.
            if (_psSign < 0 && _psMoveY < 0)
            {
                _playerStraightDone = true;
            }
            return;
        }
        if (_bresenham)
        {
            X = _playerAimed ? _bx - _baHlx : _bx;
            Y = _playerAimed ? _by - _baHly : _by;
            if (_playerAimed && !Delayed && _curSpeed < _maxSpeed)
                _curSpeed++;
            for (int s = 0; s < _curSpeed; s++) BresenhamStep();
            if (!_playerAimed && _accelerating && _curSpeed < _maxSpeed)
                _curSpeed++;
            FrameCounter++;
            if (!_playerAimed && (X < 0 || X >= 320 || Y < 0 || Y >= 200)) Alive = false;
            return;
        }
        else
        {
            X = (int)_fx;
            Y = (int)_fy;
            _fx += _dx * _curSpeed;
            _fy += _dy * _curSpeed;
        }
        if (_accelerating && _curSpeed < _maxSpeed)
            _curSpeed++;
        FrameCounter++;
        // C ESHOT_Think (ESHOT.C:510-514): doneflag when shot->y (the
        // just-snapped displayed pos) is out of bounds.
        if (X < 0 || X >= 320 || Y < 0 || Y >= 200) Alive = false;
    }

    public void Kill() { Alive = false; }
}
