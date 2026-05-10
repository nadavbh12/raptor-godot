namespace Raptor.Sim.Bullet;

public enum BulletKind { Player, Enemy }

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

    // Direction unit vector (for axis-aligned bullets: exactly -1, 0, or +1 per axis).
    // For diagonal bullets, these are the fixed per-tick deltas (not unit vectors).
    private readonly int _dx;  // horizontal step per speed-unit
    private readonly int _dy;  // vertical step per speed-unit

    // Speed model: speed starts at _curSpeed, increments by 1 per Tick to _maxSpeed.
    private int  _curSpeed;
    private readonly int _maxSpeed;
    private readonly bool _accelerating;  // true for axis-aligned bullets (ATDOWN, ATPLAY)

    public bool Alive { get; private set; } = true;

    /// <summary>
    /// Create a bullet with a fixed per-tick velocity (no acceleration).
    /// Used for diagonal bullets (ANGLELEFT, ANGLERIGHT, MISSILE) or simple bullets.
    /// </summary>
    public BulletLogic(BulletKind kind, int x, int y, int velX, int velY, int damage = 2)
    {
        Kind = kind;
        X = x; Y = y;
        _dx = velX;
        _dy = velY;
        _curSpeed = 1;
        _maxSpeed = 1;
        _accelerating = false;
        Damage = damage;
    }

    /// <summary>
    /// Create an axis-aligned bullet with acceleration.
    /// Bullet moves by (dx*speed, dy*speed) per tick, speed starts at initSpeed,
    /// increases by 1 per tick up to maxSpeed.
    /// </summary>
    public BulletLogic(BulletKind kind, int x, int y, int dx, int dy, int initSpeed, int maxSpeed, int damage = 2)
    {
        Kind = kind;
        X = x; Y = y;
        _dx = dx;
        _dy = dy;
        _curSpeed = initSpeed;
        _maxSpeed = maxSpeed;
        _accelerating = true;
        Damage = damage;
    }

    public void Tick()
    {
        if (!Alive) return;
        X += _dx * _curSpeed;
        Y += _dy * _curSpeed;
        if (_accelerating && _curSpeed < _maxSpeed)
            _curSpeed++;
        // C ESHOT_Think: doneflag when y >= 200 or y < 0 or x >= 320 or x < 0.
        if (X < 0 || X >= 320 || Y < 0 || Y >= 200) Alive = false;
    }

    public void Kill() { Alive = false; }
}
