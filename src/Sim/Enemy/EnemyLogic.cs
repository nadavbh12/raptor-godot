using System;
using System.Collections.Generic;
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
///   2 (KAMI)    — walk waypoints, then chase player_cx/player_cy, then
///                 fly past until off-screen (KAMI_FLY → KAMI_CHASE → KAMI_END).
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
    /// <summary>
    /// True iff this enemy is a ground-flight family (F_GROUND/F_GROUNDLEFT/
    /// F_GROUNDRIGHT — FlightType 3/4/5). Used by collision to honor C's
    /// per-shot HIT_TYPE filter (S_AIR ignores ground, S_GROUND ignores air).
    /// </summary>
    public bool IsGround => Meta.FlightType >= 3 && Meta.FlightType <= 5;

    public int Hits { get; private set; }
    /// <summary>True iff this enemy is flagged as a boss (C SPRITE.bossflag != 0).</summary>
    public bool IsBoss => Meta.BossFlag != 0;
    /// <summary>
    /// Base/max hit points (C lib->hits). Matches the constructor's clamp so
    /// Hits/MaxHits stay consistent for the boss-health-% computation.
    /// </summary>
    public int MaxHits => Meta.Hits > 0 ? Meta.Hits : 1;
    public bool Done { get; private set; }
    public bool Alive => Hits > 0 && !Done;
    public bool PendingRemovalDump { get; private set; }

    public bool ContainsPointStrict(int x, int y)
    {
        int x2 = X + Meta.Width - 1;
        int y2 = Y + Meta.Height - 1;
        return x > X && x < x2 && y > Y && y < y2;
    }

    // ── Flight-path state ────────────────────────────────────────────────────
    // C kami states: KAMI_FLY=0 (walk waypoints), KAMI_CHASE=1 (target player),
    // KAMI_END=2 (continue past, terminate when off-screen).
    private const int KamiFly = 0;
    private const int KamiChase = 1;
    private const int KamiEnd = 2;
    private int _kami = KamiFly;
    private int _flightIdx;   // index into next flight step (0-based)
    // F_REPEAT direction. C ENEMY.C uses E_FORWARD/E_BACKWARD; we use +1/-1.
    // F_REPEAT walks waypoints forward from 0 to numflight-1, then BACKWARD
    // to `repos`, then forward again, ping-ponging forever.
    private int _flightStep = +1;
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
    /// <summary>
    /// Compute the initial shoot countdown the way C's ENEMY_Add does:
    ///   new->countdown = lib->countdown + (-new->move.y)
    /// where new->move.y is the post-correction screen Y assigned at ENEMY_Add
    /// time (already includes the +16 - hly shift). In our pipeline,
    /// WaveController.SpawnForTiley supplies the post-correction value as
    /// mapY, so the countdown is simply lib.countdown - mapY.
    /// </summary>
    public static int InitialShootCountdown(int libCountdown, int mapY) =>
        libCountdown - mapY;

    public EnemyLogic(SpriteMeta meta, int spawnX, int mapY)
    {
        Meta = meta;
        Hits = meta.Hits > 0 ? meta.Hits : 1;

        _sx = spawnX;
        _sy = 100 - meta.HalfY;   // C: sy = 100 - new->hly (uses actual sprite half-height)

        // Countdown mirrors C's ENEMY_Add (ENEMY.C:408): countdown = lib->countdown
        // + (-new->move.y) where new->move.y is the corrected screen Y. Our
        // caller (WaveController.SpawnForTiley) already applied the +16 - hly
        // correction, so mapY IS new->move.y and we do not re-apply it here.
        _shootCountdown = InitialShootCountdown(meta.Countdown, mapY);
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
    /// Use Fire (out var bullets) for multi-gun enemies that fire several
    /// bullets per tick.
    /// </summary>
    public BulletLogic? Tick(int playerX = 144, int playerY = 160)
    {
        _firedThisTick = null;  // reset every tick so extras aren't re-emitted
        if (!Alive && !PendingRemovalDump) return null;
        AdvancePath(playerX, playerY);
        var fired = MaybeFireAll(playerX, playerY);
        if (fired == null || fired.Count == 0) return null;
        _firedThisTick = fired.Count > 1 ? fired : null;
        return fired[0];
    }

    /// <summary>
    /// Returns extra bullets fired this tick beyond the first one returned
    /// by Tick. Mirrors ENEMY.C:993 — for (loop=0; loop<numguns; loop++)
    /// ESHOT_Shoot(sprite, loop). Helicopters with numguns=2 should drop
    /// two missiles per shot frame.
    /// </summary>
    public IReadOnlyList<BulletLogic>? ExtraBulletsThisTick
    {
        get
        {
            if (_firedThisTick == null || _firedThisTick.Count <= 1) return null;
            return _firedThisTick.GetRange(1, _firedThisTick.Count - 1);
        }
    }
    private List<BulletLogic>? _firedThisTick;

    public void TakeDamage(int dmg, bool deferRemovalForDump = false)
    {
        if (Done) return;
        Hits -= dmg;
        if (Hits <= 0)
        {
            PendingRemovalDump |= deferRemovalForDump;
        }
    }

    public void ClearPendingRemovalDump() => PendingRemovalDump = false;

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
        // F_GROUND family (FlightType 3/4/5): no Bresenham, just scroll-driven y
        // increment per tick (ENEMY.C:940-977). x2/y2 are target bounds.
        if (Meta.FlightType >= 3 && Meta.FlightType <= 5)
        {
            // C ENEMY.C:465 (F_GROUNDRIGHT): new->x -= new->width (spawn at left, slide right).
            // C ENEMY.C:473 (F_GROUNDLEFT):  new->x += new->width (spawn at right, slide left).
            // For F_GROUND (3) the spawn x is unchanged.
            int offsetX = Meta.FlightType == 5 ?  -Meta.Width
                        : Meta.FlightType == 4 ?  +Meta.Width
                        : 0;
            _mx = _sx + offsetX;
            _my = mapY;
            X = _mx; Y = _my;  // ensure dump reflects post-init position
            // C ENEMY.C:461 — F_GROUND target is (x, 211). F_GROUNDRIGHT/LEFT
            // use 335/-hlx for x. We only need y2 for the doneflag check; x2
            // is set per-tick from x in C and we mirror that.
            _tgtY = 211;
            _tgtX = Meta.FlightType == 5 ? 335 : Meta.FlightType == 4 ? -Meta.HalfX : _mx;
            _pathInitialised = true;
            return;
        }
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

    private void AdvancePath(int playerX = 144, int playerY = 160)
    {
        if (!_pathInitialised) return;

        // F_GROUND family: scroll-driven y++, then bounds check. No Bresenham.
        // Mirrors ENEMY.C:940-977. F_GROUNDLEFT/RIGHT also slide x by movespeed
        // once y >= 0; F_GROUND just falls straight.
        if (Meta.FlightType >= 3 && Meta.FlightType <= 5)
        {
            _my++;
            if (Meta.FlightType == 5 && _my >= 0)        // F_GROUNDRIGHT
            {
                _mx += Math.Max(Meta.MoveSpeed, 1);
                if (_mx > _tgtX) Done = true;
            }
            else if (Meta.FlightType == 4 && _my >= 0)   // F_GROUNDLEFT
            {
                _mx -= Math.Max(Meta.MoveSpeed, 1);
                if (_mx < _tgtX) Done = true;
            }
            X = _mx;
            Y = _my;
            if (_my > _tgtY) Done = true;
            return;
        }

        // C ENEMY_Think (ENEMY.C:813-814): sprite->x/y is set from move.x/y
        // BEFORE MoveEobj runs. We capture the pre-move position here so
        // collision checks see the same position that C does.
        X = _mx;
        Y = _my;

        int speed = Meta.MoveSpeed > 0 ? Meta.MoveSpeed : 1;

        // C ENEMY_Think (ENEMY.C:820): speed = MoveEobj(&sprite->move, speed).
        int leftover = MoveEobjSteps(speed);

        // C ENEMY_Think (ENEMY.C:822-853): if move.done, snap to old target,
        // advance to the next flight segment, do one free MoveMobj step, then
        // consume the leftover speed via MoveEobj on the new segment.
        //
        // C ENEMY.C:928 gates this block on `kami != KAMI_END`: once a KAMI
        // orb has its final player-chase target the bresenham must keep
        // walking past it (MoveEobjSteps still steps because the while loop is
        // gated by speed, not maxloop) so the off-screen check below can fire.
        // Running the snap-and-advance dance in KAMI_END would revert each
        // post-bresenham step back to the chase target and short-circuit out
        // via AdvanceFlightSegment, leaving the orb glued near the player.
        bool inKamiEnd = Meta.FlightType == 2 && _kami == KamiEnd;
        if (_moveDone && !inKamiEnd)
        {
            _mx = _tgtX;
            _my = _tgtY;
            if (!AdvanceFlightSegment(playerX, playerY)) return;
            MoveMobjStep();
            MoveEobjSteps(leftover);
        }

        // F_KAMI in KAMI_END terminates on tight off-screen bounds (ENEMY.C:913-925).
        if (inKamiEnd)
        {
            if (_my > 201 || _mx > 320 + Meta.HalfX
                || _my + Meta.Width < 0 || _mx + Meta.Width < 0)
            {
                Done = true;
                return;
            }
        }

        // C removes enemies only when the flight path completes (doneflag after
        // movepos > numflight), not on out-of-bounds position. We only remove
        // if VERY far off-screen to prevent leaking stray enemies.
        if (_my < -500 || _my > 500 || _mx < -500 || _mx > 800)
            Done = true;
    }

    /// <summary>
    /// Mirrors C's MoveEobj (ENEMY.C:50-111). Walks the current Bresenham
    /// segment by up to <paramref name="speed"/> steps. Returns the unused
    /// speed; sets <see cref="_moveDone"/> when maxloop reaches 0.
    /// </summary>
    private int MoveEobjSteps(int speed)
    {
        if (speed <= 0) return 0;
        while (speed > 0)
        {
            speed--;
            _maxloop--;
            if (_maxloop == 0)
            {
                _moveDone = true;
                return speed;
            }
            BresenhamStep();
        }
        if (_maxloop < 1) _moveDone = true;
        return speed;
    }

    /// <summary>
    /// Mirrors C's MoveMobj (RAP.C:374-411). Takes exactly one Bresenham
    /// step regardless of speed; sets <see cref="_moveDone"/> if maxloop
    /// is already 0 (no movement in that case).
    /// </summary>
    private void MoveMobjStep()
    {
        if (_maxloop == 0) { _moveDone = true; return; }
        BresenhamStep();
        _maxloop--;
    }

    private void BresenhamStep()
    {
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

    /// <summary>
    /// Set up the next flight segment. Mirrors C's F_REPEAT/F_LINEAR branches
    /// in ENEMY_Think (ENEMY.C:836-853): bump movepos, compute new
    /// move.x2/y2 from sx + flightx, sy + flighty, then InitMobj.
    /// Returns false if the path completed (Done = true).
    /// </summary>
    private bool AdvanceFlightSegment(int playerX = 144, int playerY = 160)
    {
        int n = Math.Min(Meta.NumFlight,
                Math.Min(Meta.FlightX.Length, Meta.FlightY.Length));

        // F_KAMI: KAMI_END = no more target changes. The bresenham is
        // exhausted; the off-screen check in AdvancePath handles termination.
        if (Meta.FlightType == 2 && _kami == KamiEnd)
            return false;

        // F_KAMI: KAMI_CHASE → KAMI_END transition. Target the player one
        // final time (mirrors ENEMY.C:935-940).
        if (Meta.FlightType == 2 && _kami == KamiChase)
        {
            _kami = KamiEnd;
            InitBresenhamForTarget(_mx, _my, playerX, playerY);
            return true;
        }

        // F_REPEAT ping-pong (ENEMY.C:883-900). Walk the waypoint array
        // forward to numflight-1, then BACKWARD to `repos`, then forward
        // again. Direction is _flightStep (±1).
        if (Meta.FlightType == 0)
        {
            int repos = System.Math.Max(0, Meta.Repos);
            // _flightIdx already points one past the just-completed waypoint.
            // Check whether the new index is out of bounds in the current
            // direction, and reverse if so.
            if (_flightStep > 0 && _flightIdx >= n)
            {
                _flightStep = -1;
                _flightIdx = n - 1;  // C: movepos = numflight - 1
            }
            else if (_flightStep < 0 && _flightIdx <= repos)
            {
                _flightStep = +1;
                _flightIdx = repos;
            }
        }
        else if (_flightIdx >= n)
        {
            if (Meta.FlightType == 2)  // KAMI: out of waypoints → target player, enter KAMI_END
            {
                _kami = KamiEnd;
                InitBresenhamForTarget(_mx, _my, playerX, playerY);
                return true;
            }
            else  // LINEAR (1) or other: done after last segment
            {
                Done = true;
                return false;
            }
        }

        int newTgtX = _sx + Meta.FlightX[_flightIdx];
        int newTgtY = _sy + Meta.FlightY[_flightIdx];
        _flightIdx += Meta.FlightType == 0 ? _flightStep : 1;
        // F_KAMI: after assigning the LAST waypoint as the target, mark
        // KAMI_CHASE so the NEXT move.done transitions to chasing the player
        // (mirrors ENEMY.C:951-953).
        if (Meta.FlightType == 2 && _kami == KamiFly && _flightIdx >= n)
            _kami = KamiChase;
        InitBresenhamForTarget(_mx, _my, newTgtX, newTgtY);
        return true;
    }

    private void InitBresenhamForTarget(int fromX, int fromY, int toX, int toY)
    {
        _mx = fromX;
        _my = fromY;
        _tgtX = toX;
        _tgtY = toY;

        // Zero-length segment (target == current position). F_REPEAT hits this
        // immediately after a direction reversal, because C re-uses the last
        // waypoint as the next target on the reversal tick. Marking done here
        // lets AdvanceFlightSegment fire again immediately on the next tick.
        if (fromX == toX && fromY == toY)
        {
            _addX = _addY = 0;
            _delX = _delY = 0;
            _err = 0;
            _maxloop = 0;
            _moveDone = true;
            return;
        }

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

    /// <summary>
    /// Legacy single-step helper used by <see cref="InitFlight"/> (mirroring
    /// C's MoveMobj after the initial InitMobj). Identical to
    /// <see cref="MoveMobjStep"/>.
    /// </summary>
    private void MoveOneStep() => MoveMobjStep();

    // Mirrors C's #define values for shootagain state machine.
    private const int ShootAgainNorm  = -1;  // NORM_SHOOT in ENEMY.C
    private const int ShootAgainStart =  0;  // START_SHOOT in ENEMY.C

    // ESHOT_TYPE enum values (from ESHOT.H).
    private const int ES_ATPLAYER  = 0;
    private const int ES_ATDOWN    = 1;
    private const int ES_ANGLELEFT = 2;
    private const int ES_ANGLERIGHT= 3;
    private const int ES_MISSLE    = 4;

    // Bullet image half-dimensions (mirrors C's ESHOT_LIB xoff/yoff = image_width>>1
    // and ditto for yoff, set in ESHOT_Init from each lib's pic[0] dimensions).
    // ESHOT_BLK, ELASER_BLK, MINE_BLK, PLASMA_BLK, COCONUT_PIC are all 8x8 → 4,4.
    // EMISLE_BLK is 8x16 → xoff=4 only (ESHOT.C:366 omits the yoff subtraction
    // for ES_MISSLE so the missile's move.y starts exactly at gunY).
    private static (int xoff, int yoff) BulletOffsets(EshotType t) => t switch
    {
        EshotType.ES_MISSLE => (4, 0),   // missile: anchor y at gunY (no yoff)
        EshotType.ES_PLASMA => (4, 0),   // plasma: ESHOT.C:398 subtracts xoff only
        _ => (4, 4),                      // all other ESHOT_BLK family bullets
    };

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
        var (xoff, yoff) = BulletOffsets((EshotType)shootType);
        int sx = bx - xoff;
        int sy = by - yoff;

        BulletLogic b;
        switch ((EshotType)shootType)
        {
            case EshotType.ES_ATDOWN:
                // Fires straight down: x2=x, y2=200. speed = lib->speed>>1 = 6>>1 = 3.
                b = new BulletLogic(BulletKind.Enemy, sx, sy,
                    dx: 0, dy: 1, initSpeed: 3, maxSpeed: 6, damage: HitsNormal);
                break;

            case EshotType.ES_ANGLELEFT:
                // ESHOT.C:341-350 — 45° down-left. C targets (move.x-32,
                // move.y+32) but MoveSobj (RAP.C:416) walks past the 32px
                // target, so it just sets a 45° direction; speed =
                // LIB_NORMAL.speed>>1 = 3 and ramps +1/tick to LIB_NORMAL.speed
                // = 6 (ESHOT_Think default branch, ESHOT.C:476-484). A constant
                // (-3,+3) made the shot ~2× too slow → lingered on screen.
                b = new BulletLogic(BulletKind.Enemy, sx, sy,
                    dx: -1, dy: 1, initSpeed: 3, maxSpeed: 6, damage: HitsNormal);
                break;

            case EshotType.ES_ANGLERIGHT:
                // ESHOT.C:352-361 — 45° down-right; speed ramps 3->6 as above.
                b = new BulletLogic(BulletKind.Enemy, sx, sy,
                    dx: 1, dy: 1, initSpeed: 3, maxSpeed: 6, damage: HitsNormal);
                break;

            case EshotType.ES_MISSLE:
                // Fires straight down. C ESHOT.C:369: cur->speed = enemy->speed+1.
                // enemy->speed is set in ENEMY_Add to curlib->movespeed, so the
                // missile's initial speed is the firing enemy's movespeed + 1
                // (max = LIB_MISSLE.speed = 10).
                b = new BulletLogic(BulletKind.Enemy, sx, sy,
                    dx: 0, dy: 1,
                    initSpeed: (Meta.MoveSpeed > 0 ? Meta.MoveSpeed : 1) + 1,
                    maxSpeed: 10, damage: HitsMissile);
                break;

            case EshotType.ES_MINES:
                b = BulletLogic.EnemyMine(bx, by, pos: 0, fuseTicks: 150);
                break;

            case EshotType.ES_LASER:
                // ESHOT.C:385-393 — a beam that TRACKS this firing enemy's gun
                // each tick (x = en.x + shootx[gun] - 4, y = en.y + shooty[gun]),
                // lives LIB_LASER.num_frames=4 passes, and damages the player by
                // LIB_LASER.hits=12 on horizontal alignment (NOT AABB). The caller
                // passed bx = X + ShootX[gun], by = Y + ShootY[gun], so the gun
                // offset is recovered as (bx - X, by - Y). Driven by
                // WaveController.LaserTick, not the generic bullet movement path.
                // NOTE: only secret-gated wave-8 enemies fire this; no parity
                // scenario exercises it, and the vertical-column rendering needs
                // visual review (see EnemyLaserTests / ES_LASER notes).
                b = BulletLogic.EnemyLaser(this, gunShootX: bx - X, gunShootY: by - Y);
                break;

            case EshotType.ES_PLASMA:
                // ESHOT.C:395-403 — vertical descent (move.x2 = move.x, y2 = 200),
                // cur->speed = 8, LIB_PLASMA.speed = 10, hits = 15. Distinct from
                // ES_ATPLAYER: plasma does NOT home on the player.
                b = new BulletLogic(BulletKind.Enemy, sx, sy,
                    dx: 0, dy: 1, initSpeed: 8, maxSpeed: 10, damage: HitsPlasma);
                break;

            case EshotType.ES_ATPLAYER:
            default:
                {
                    // ESHOT.C lib->hits per shoot_type. ATPLAYER and COCONUTS both
                    // aim at the player center (homing) but carry different damage:
                    // LIB_ATPLAY.hits = 1, LIB_COCO.hits = 1, default LIB_NORMAL = 2.
                    int dmg = shootType switch
                    {
                        (int)EshotType.ES_ATPLAYER => HitsAtPlay,
                        (int)EshotType.ES_COCONUTS => HitsCoco,
                        _ => HitsNormal,
                    };
                    // C aims at player CENTER (player_cx, player_cy) via Bresenham
                    // (ESHOT.C:324-325 + InitMobj/MoveSobj). The caller passes the
                    // player's centre coords. Bresenham mirrors C exactly so
                    // ATPLAYER bullet positions stay in lockstep frame-by-frame.
                    b = BulletLogic.AimedAt(BulletKind.Enemy, sx, sy,
                        x2: playerX, y2: playerY,
                        initSpeed: 1, maxSpeed: 6, damage: dmg);
                }
                break;
        }
        b.ShotType = (EnemyShotType)shootType;
        return b;
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

    private List<BulletLogic>? MaybeFireAll(int playerX, int playerY)
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
        if (_shootAgain == ShootAgainStart)
        {
            _shootAgain = ShootAgainNorm;
            _shootCount = Meta.ShootCnt > 0 ? Meta.ShootCnt : 1;
            _shootFlag  = Meta.ShotSpace;
            return null;
        }
        if (_shootAgain != ShootAgainNorm)
        {
            // inter-burst delay (shootagain > 0), count down.
            _shootAgain--;
            if (_shootAgain == 0) _shootAgain = ShootAgainStart;
            return null;
        }

        // NORM_SHOOT: count down shootflag; fire when it goes negative.
        _shootFlag--;
        if (_shootFlag >= 0) return null;
        _shootFlag = Meta.ShotSpace;

        // Fire one bullet per gun (mirrors ENEMY.C:993 — for loop=0..numguns-1
        // ESHOT_Shoot(sprite, loop)). For helicopters numguns=2 this drops
        // two missiles from the wing mounts in a single tick.
        var fired = new List<BulletLogic>(Meta.NumGuns);
        for (int gunIdx = 0; gunIdx < Meta.NumGuns; gunIdx++)
        {
            int bx = X + (gunIdx < Meta.ShootX.Length ? Meta.ShootX[gunIdx] : 0);
            int by = Y + (gunIdx < Meta.ShootY.Length ? Meta.ShootY[gunIdx] : 0);
            int shootType = (gunIdx < Meta.ShootType.Length) ? Meta.ShootType[gunIdx] : 0;
            fired.Add(MakeBullet(shootType, bx, by, playerX, playerY));
        }
        _shootCount--;
        if (_shootCount < 1)
            _shootAgain = Meta.ShootFrame;
        return fired;
    }
}
