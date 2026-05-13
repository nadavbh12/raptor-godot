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

    /// <summary>Current per-tick speed (used by BulletDumper for parity comparison).</summary>
    public int CurSpeed => _curSpeed;
    /// <summary>Pre-advanced move target (mirrors C move.x). Used by BulletDumper.</summary>
    public int Mx => _bresenham ? _bx : (int)_fx;
    /// <summary>Pre-advanced move target (mirrors C move.y). Used by BulletDumper.</summary>
    public int My => _bresenham ? _by : (int)_fy;
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
        => new BulletLogic(kind, x, y, x2, y2, initSpeed, maxSpeed, damage, bresenhamMarker: true);

    private BulletLogic(BulletKind kind, int x, int y, int x2, int y2,
                        int initSpeed, int maxSpeed, int damage, bool bresenhamMarker)
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

        // InitMobj (RAP.C:339-371).
        _bx = x; _by = y;
        _baddX = 1; _baddY = 1;
        _bdelX = x2 - x;
        _bdelY = y2 - y;
        if (_bdelX < 0) { _bdelX = -_bdelX; _baddX = -1; }
        if (_bdelY < 0) { _bdelY = -_bdelY; _baddY = -1; }
        if (_bdelX >= _bdelY) { _berr = -(_bdelY >> 1); _bmaxloop = _bdelX + 1; }
        else                  { _berr =  (_bdelX >> 1); _bmaxloop = _bdelY + 1; }
        // C ESHOT_Shoot ends with MoveSobj(&move, 1) — pre-advance 1 step.
        BresenhamStep();
    }

    private void BresenhamStep()
    {
        if (_bmaxloop == 0) return;
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
    }

    public void Tick()
    {
        if (!Alive) return;
        // C ESHOT_Think order (ESHOT.C:476-485):
        //   shot->x = shot->move.x;   // snapshot displayed pos BEFORE movement
        //   shot->y = shot->move.y;
        //   MoveSobj(&shot->move, shot->speed);
        //   if (shot->speed < lib->speed) shot->speed++;
        if (_bresenham)
        {
            X = _bx;
            Y = _by;
            for (int s = 0; s < _curSpeed; s++) BresenhamStep();
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
