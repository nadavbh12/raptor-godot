using System;
using Raptor.Sim.Bullet;

namespace Raptor.Sim.Enemy;

/// <summary>
/// Pure-C# enemy state machine. Mirrors C SOURCE/RAP.C + ENEMY.C movement.
///
/// Movement uses a Bresenham-style line traversal (MoveSobj) from the
/// current position to the target, advancing by `speed` pixels per tick.
///
/// FlightType handling:
///   0 (REPEAT)  — cycle through flightx[0..NumFlight-1], wrap forever.
///   1 (LINEAR)  — walk flightx[0..NumFlight-1] once, then mark Done.
/// Other flight types stubbed to LINEAR for now.
/// </summary>
public sealed class EnemyLogic
{
    public SpriteMeta Meta { get; }

    // Current screen position (rendered/collision center).
    public int X { get; private set; }
    public int Y { get; private set; }

    // Half-width / half-height from actual sprite dimensions.
    public int HalfW => Meta.HalfX;
    public int HalfH => Meta.HalfY;

    public int Hits { get; private set; }
    public bool Done { get; private set; }
    public bool Alive => Hits > 0 && !Done;

    // ── Flight-path state ────────────────────────────────────────────────────
    private int _flightIdx;   // index into next flight step (0-based)
    // Bresenham movement state (mirrors MOVEOBJ in C).
    private int _tgtX, _tgtY;   // target screen position for current step
    private int _mx, _my;        // current movement position (= X, Y initially)
    private int _addX, _addY;    // direction increments (+1 or -1)
    private int _delX, _delY;    // absolute distance to target
    private int _err;            // Bresenham error accumulator
    private int _maxloop;        // remaining ticks in this step (delX or delY)
    private bool _moveDone;      // current step completed
    private bool _pathInitialised;

    // ── Shooting state ───────────────────────────────────────────────────────
    // The C version uses a `countdown` that starts at curlib->countdown + (-move.y_initial).
    // move.y_initial is the enemy's initial screen y position (before flight setup overwrites sy).
    // A negative y_initial (enemy above screen) gives a large positive countdown.
    // At speed 2/tick, the countdown expires when countdown -= 2*tick_count < 1.
    // We implement this as _shootCountdown: starts at countdown value, decrements by
    // movespeed each tick. Fire is enabled when _shootCountdown < 1.
    private int _shootCountdown;   // mirrors C's sprite->countdown
    private bool _shootOn;         // mirrors C's sprite->shoot_on
    private int _shootFlag;        // mirrors C's sprite->shootflag (counts down to fire)
    private int _shootCount;       // mirrors C's sprite->shootcount (bursts)
    private int _shootAgain;       // mirrors C's sprite->shootagain (inter-burst delay)

    // Home base for flight-path deltas (mirrors C's new->sy = 100 - new->hly).
    private int _sx;  // home X = map-derived spawn X (never changes)
    private int _sy;  // home Y = 100 - HalfH (flight-path origin for REPEAT)

    /// <summary>
    /// Create an enemy.
    ///
    /// <paramref name="spawnX"/> is the tile-derived screen X (= tile_x * 32 + MAP_LEFT + 16).
    /// <paramref name="mapY"/> is the C-style initial screen Y from the tile map (can be
    /// negative for off-screen enemies).
    ///
    /// C's ENEMY_Add sequence:
    ///   new->move.x = new->sx = new->x = mapX   (line 403)
    ///   new->move.y = new->sy = new->y = mapY   (line 404)
    ///   new->countdown = lib->countdown + (-mapY)  (line 408)
    ///   [flight-type switch overrides new->sy = 100 - hly]
    ///   new->move.x2 = new->sx + flightx[0]
    ///   new->move.y2 = new->sy + flighty[0]   (uses overridden sy!)
    ///   InitMobj(&new->move)   // from (mapX, mapY) to (mapX, sy_home + flighty[0])
    ///   MoveMobj(&new->move)   // one step
    /// </summary>
    public EnemyLogic(SpriteMeta meta, int spawnX, int mapY)
    {
        Meta = meta;
        Hits = meta.Hits > 0 ? meta.Hits : 1;

        _sx = spawnX;
        _sy = 100 - meta.HalfY;   // C: sy = 100 - new->hly (uses actual sprite half-height)

        // Countdown mirrors C's: countdown = lib->countdown + (-new->move.y)
        // where new->move.y = mapY_raw + 16 - hly. Our mapY == mapY_raw (no corrections),
        // so: countdown = lib->countdown + (-(mapY + 16 - hly)).
        _shootCountdown = meta.Countdown + (-(mapY + 16 - meta.HalfY));
        _shootOn    = false;
        _shootFlag  = meta.ShootStart;
        _shootCount = meta.ShootCnt > 0 ? meta.ShootCnt : 1;
        _shootAgain = -1;  // NORM_SHOOT = -1

        // Initial position (C: new->move.x = mapX, new->move.y = mapY
        // before the flight-type switch overrides sy).
        X = _sx;
        Y = mapY;

        // Initialise flight: Bresenham from (mapX, mapY) to first target.
        InitFlight(mapY);
    }

    /// <summary>
    /// One tick. Returns a BulletLogic if a bullet was fired, else null.
    /// playerX/playerY are the player's current position (for aimed shots).
    /// </summary>
    public BulletLogic? Tick(int playerX = 144, int playerY = 160)
    {
        if (!Alive) return null;
        AdvancePath();
        return MaybeFire(playerX, playerY);
    }

    public void TakeDamage(int dmg)
    {
        if (!Alive) return;
        Hits -= dmg;
        if (Hits < 0) Hits = 0;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Initialise the Bresenham flight path.
    /// Mirrors ENEMY_Add's flight-type switch for F_LINEAR/F_REPEAT/F_KAMI:
    ///   1. Override sy = 100 - hly  (already set as _sy in constructor)
    ///   2. move.x2 = sx + flightx[0]
    ///   3. move.y2 = _sy + flighty[0]
    ///   4. InitMobj from (sx, mapY) to (x2, y2)
    ///   5. MoveMobj (one step)
    /// The Bresenham starts from (sx, mapY) — the enemy is initially off-screen.
    /// </summary>
    /// <param name="mapY">The C map-derived Y (can be negative for off-screen spawn).</param>
    private void InitFlight(int mapY)
    {
        _flightIdx = 0;
        int n = Math.Min(Meta.NumFlight,
                Math.Min(Meta.FlightX.Length, Meta.FlightY.Length));
        if (n <= 0)
        {
            // No flight path: position stays at mapY, will likely exit immediately.
            _mx = _sx;
            _my = mapY;
            _pathInitialised = true;
            return;
        }

        // First target (C: move.x2 = sx + flightx[0], move.y2 = _sy + flighty[0]).
        int tgtX = _sx + Meta.FlightX[0];
        int tgtY = _sy + Meta.FlightY[0];
        // Initialise Bresenham from (sx, mapY) → (tgtX, tgtY).
        InitBresenhamForTarget(_sx, mapY, tgtX, tgtY);
        // C calls MoveMobj once immediately after InitMobj.
        MoveOneStep();
        X = _mx;
        Y = _my;
        _flightIdx = 1;
        _pathInitialised = true;
    }

    private void AdvancePath()
    {
        if (!_pathInitialised) return;

        // C ENEMY_Think: sprite->x/y is set from move.x/y BEFORE MoveEobj runs.
        // We capture the pre-move position here so collision checks see the same
        // position that C does.
        X = _mx;
        Y = _my;

        int speed = Meta.MoveSpeed > 0 ? Meta.MoveSpeed : 1;

        // Move `speed` pixels along the current Bresenham line.
        for (int s = 0; s < speed; s++)
        {
            if (_moveDone || _maxloop <= 0)
            {
                // Current step is complete; advance to next flight index.
                AdvanceFlightIndex();
                if (Done) return;
            }
            MoveOneStep();
        }

        // C removes enemies only when the flight path completes (doneflag after
        // movepos > numflight), not on out-of-bounds position.
        // We only remove if VERY far off-screen to prevent memory leak from
        // stray enemies that somehow never complete their path.
        if (_my < -500 || _my > 500 || _mx < -500 || _mx > 800)
            Done = true;
    }

    private void AdvanceFlightIndex()
    {
        int n = Math.Min(Meta.NumFlight,
                Math.Min(Meta.FlightX.Length, Meta.FlightY.Length));

        if (_flightIdx >= n)
        {
            if (Meta.FlightType == 0)  // REPEAT: wrap around
                _flightIdx = 0;
            else  // LINEAR (1) or other: done after last segment
            {
                Done = true;
                return;
            }
        }

        // For REPEAT/LINEAR, next target = _sy + flighty[_flightIdx], _sx + flightx[_flightIdx].
        // This mirrors C's F_REPEAT: move.x2 = sx + flightx[movepos], move.y2 = sy + flighty[movepos].
        // And F_LINEAR:             move.x2 = sx + flightx[movepos], move.y2 = sy + flighty[movepos].
        int newTgtX = _sx + Meta.FlightX[_flightIdx];
        int newTgtY = _sy + Meta.FlightY[_flightIdx];
        _flightIdx++;
        InitBresenhamForTarget(_mx, _my, newTgtX, newTgtY);
    }

    private void InitBresenhamForTarget(int fromX, int fromY, int toX, int toY)
    {
        _mx = fromX;
        _my = fromY;
        _tgtX = toX;
        _tgtY = toY;

        _addX = 1; _addY = 1;
        _delX = toX - fromX;
        _delY = toY - fromY;

        if (_delX < 0) { _delX = -_delX; _addX = -1; }
        if (_delY < 0) { _delY = -_delY; _addY = -1; }

        if (_delX >= _delY)
        {
            _err     = -(_delY >> 1);
            _maxloop = _delX + 1;
        }
        else
        {
            _err     = _delX >> 1;
            _maxloop = _delY + 1;
        }
        _moveDone = (_maxloop == 0);
    }

    private void MoveOneStep()
    {
        // Mirrors C's MoveEobj: decrement maxloop first; if it reaches 0,
        // mark done WITHOUT advancing position (C: "if maxloop==0 { done; return; }").
        _maxloop--;
        if (_maxloop <= 0) { _moveDone = true; return; }

        if (_delX >= _delY)
        {
            _mx  += _addX;
            _err += _delY;
            if (_err > 0) { _my += _addY; _err -= _delX; }
        }
        else
        {
            _my  += _addY;
            _err += _delX;
            if (_err > 0) { _mx += _addX; _err -= _delY; }
        }
    }

    // Mirrors C's #define values for shootagain state machine.
    private const int ShootAgainNorm  = -1;  // NORM_SHOOT in ENEMY.C
    private const int ShootAgainStart =  0;  // START_SHOOT in ENEMY.C

    // ESHOT_TYPE enum values (from ESHOT.H).
    private const int ES_ATPLAYER  = 0;
    private const int ES_ATDOWN    = 1;
    private const int ES_ANGLELEFT = 2;
    private const int ES_ANGLERIGHT= 3;
    private const int ES_MISSLE    = 4;

    // Bullet image half-dimensions (mirrors C's ESHOT_LIB xoff/yoff = image_width>>1).
    // LIB_NORMAL (ESHOT_BLK sprite) and LIB_ATPLAY use the same sprite; typical size is 4×4.
    // Applying xoff=2, yoff=2 centres the bullet on the gun position.
    private const int BulletXOff = 2;
    private const int BulletYOff = 2;

    // ESHOT_LIB.hits values (from ESHOT.C ESHOT_Init):
    private const int HitsNormal  = 2;  // LIB_NORMAL  — ES_ATDOWN, ES_ANGLELEFT, ES_ANGLERIGHT
    private const int HitsAtPlay  = 1;  // LIB_ATPLAY  — ES_ATPLAYER
    private const int HitsMissile = 4;  // LIB_MISSLE  — ES_MISSLE
    private const int HitsLaser   = 12; // LIB_LASER
    private const int HitsPlasma  = 15; // LIB_PLASMA
    private const int HitsMines   = 16; // LIB_MINES
    private const int HitsCoco    = 1;  // LIB_COCO    — ES_COCONUTS

    /// <summary>
    /// Creates a bullet based on shoot_type (mirrors ESHOT.C ESHOT_Shoot switch).
    ///
    /// Gun position (bx, by) is already in screen-centre coordinates (computed relative
    /// to top-left corner of the sprite in the caller). Here we subtract the bullet
    /// image half-size (BulletXOff, BulletYOff) to match C's "cur->move.x -= xoff".
    /// </summary>
    private BulletLogic MakeBullet(int shootType, int bx, int by, int playerX, int playerY)
    {
        // Apply bullet-centre offset (mirrors: cur->move.x -= xoff; cur->move.y -= yoff;).
        int sx = bx - BulletXOff;
        int sy = by - BulletYOff;

        switch ((EshotType)shootType)
        {
            case EshotType.ES_ATDOWN:
                // Fires straight down: x2=x, y2=200. speed = lib->speed>>1 = 6>>1 = 3.
                return new BulletLogic(BulletKind.Enemy, sx, sy,
                    dx: 0, dy: 1, initSpeed: 3, maxSpeed: 6, damage: HitsNormal);

            case EshotType.ES_ANGLELEFT:
                // Diagonal left-down: target=(x-32, y+32), speed=3. Fixed velocity.
                return new BulletLogic(BulletKind.Enemy, sx, sy,
                    velX: -3, velY: 3, damage: HitsNormal);

            case EshotType.ES_ANGLERIGHT:
                // Diagonal right-down: target=(x+32, y+32), speed=3.
                return new BulletLogic(BulletKind.Enemy, sx, sy,
                    velX: 3, velY: 3, damage: HitsNormal);

            case EshotType.ES_MISSLE:
                // Fires straight down at higher speed (enemy.speed+1, max=lib->speed).
                // Approximate with speed 3 for now.
                return new BulletLogic(BulletKind.Enemy, sx, sy,
                    dx: 0, dy: 1, initSpeed: 3, maxSpeed: 10, damage: HitsMissile);

            case EshotType.ES_ATPLAYER:
            default:
                // Aimed at player: speed starts at 1, accelerates to 6.
                // Direction is normalised toward (playerX, playerY).
                {
                    int ddx = playerX - sx;
                    int ddy = playerY - sy;
                    double dist = Math.Sqrt((double)(ddx * ddx + ddy * ddy));
                    int vx = 0, vy = 1;
                    if (dist > 0)
                    {
                        vx = (int)Math.Round(ddx / dist);
                        vy = (int)Math.Round(ddy / dist);
                        if (vx == 0 && vy == 0) vy = 1;
                    }
                    int dmg = (shootType == (int)EshotType.ES_ATPLAYER) ? HitsAtPlay : HitsNormal;
                    return new BulletLogic(BulletKind.Enemy, sx, sy,
                        dx: vx, dy: vy, initSpeed: 1, maxSpeed: 6, damage: dmg);
                }
        }
    }

    private enum EshotType
    {
        ES_ATPLAYER  = 0,
        ES_ATDOWN    = 1,
        ES_ANGLELEFT = 2,
        ES_ANGLERIGHT= 3,
        ES_MISSLE    = 4,
        ES_LASER     = 5,
        ES_MINES     = 6,
        ES_PLASMA    = 7,
        ES_COCONUTS  = 8,
    }

    private BulletLogic? MaybeFire(int playerX, int playerY)
    {
        if (Meta.NumGuns <= 0 || Meta.ShootFrame <= 0) return null;

        // Countdown / shoot_on update (mirrors C's "num_frames==1" else branch, lines 801-808).
        // This runs every tick regardless of shoot_on.
        if (!_shootOn)
        {
            if (_shootCountdown < 1)
            {
                _shootCountdown = -1;
                _shootOn = true;
            }
            else
            {
                _shootCountdown -= Meta.MoveSpeed > 0 ? Meta.MoveSpeed : 1;
                return null;
            }
        }

        // Shooting state machine (mirrors ENEMY.C lines 980-1011).
        // shootagain == NORM_SHOOT (-1): active firing mode.
        // shootagain == START_SHOOT (0): one-tick re-init after inter-burst wait.
        // shootagain > 0: inter-burst delay countdown.
        BulletLogic? fired = null;

        if (_shootAgain == ShootAgainStart)
        {
            // START_SHOOT: re-initialise for next burst.
            _shootAgain = ShootAgainNorm;
            _shootCount = Meta.ShootCnt > 0 ? Meta.ShootCnt : 1;
            _shootFlag  = Meta.ShotSpace;
        }
        else if (_shootAgain == ShootAgainNorm)
        {
            // NORM_SHOOT: count down shootflag; fire when it goes negative.
            _shootFlag--;
            if (_shootFlag < 0)
            {
                _shootFlag = Meta.ShotSpace;

                // Fire one bullet per gun (gun 0 only for now).
                // X/Y here are top-left corner coordinates (= C's sprite->x/y after ENEMY_Add).
                // Gun position = sprite top-left + shootx/shooty offset (same as C's
                //   gun_x = enemy->x + enemy->lib->shootx[gun_num]).
                const int gunIdx = 0;
                int bx = X + (gunIdx < Meta.ShootX.Length ? Meta.ShootX[gunIdx] : 0);
                int by = Y + (gunIdx < Meta.ShootY.Length ? Meta.ShootY[gunIdx] : 0);

                // Determine bullet trajectory based on shoot_type (mirrors ESHOT.C).
                int shootType = (gunIdx < Meta.ShootType.Length) ? Meta.ShootType[gunIdx] : 0;
                fired = MakeBullet(shootType, bx, by, playerX, playerY);

                _shootCount--;
                if (_shootCount < 1)
                    _shootAgain = Meta.ShootFrame;  // start inter-burst wait
            }
        }
        else
        {
            // default: inter-burst delay (shootagain > 0), count down.
            _shootAgain--;
            if (_shootAgain == 0)
                _shootAgain = ShootAgainStart;  // transition to START_SHOOT next tick
        }

        return fired;
    }
}
