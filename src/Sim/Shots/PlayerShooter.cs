using System;
using System.Collections.Generic;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;

namespace Raptor.Sim.Shots;

/// <summary>
/// Player weapon system. Mirrors SOURCE/SHOTS.C SHOTS_PlayerShoot dispatch,
/// the SHOTS_Think cooldown decrement (SHOTS.C:1035-1040), and the RAP.C:1000
/// BUT_1 cascade. Holds per-weapon cooldown timers and the player's owned-
/// weapon inventory.
///
/// Phase order in WaveController:
///   PhaseInput → TickCooldowns + ApplyButton1(fireHeld)
///   PhaseMovement (already ticks each bullet via BulletLogic.Tick)
/// </summary>
public sealed class PlayerShooter
{
    private readonly int[] _curShoot = new int[ShotLib.Count];

    /// <summary>Standing inventory. ForwardGuns is always owned (lib->forever=TRUE).</summary>
    public bool HasPlasmaGuns   { get; set; } = false;
    public bool HasMicroMissile { get; set; } = false;

    /// <summary>
    /// The active special weapon (DUMB_MISSLE / MINI_GUN / TURRET / etc.).
    /// Null = no special equipped. Mirrors C plr.sweapon (RAP.C:1006).
    /// </summary>
    public WeaponType? SpecialWeapon { get; set; } = null;

    /// <summary>Resets cooldowns. Mirrors SHOTS_Init clearing shot_lib.cur_shoot.</summary>
    public void Reset()
    {
        for (int i = 0; i < _curShoot.Length; i++) _curShoot[i] = 0;
    }

    /// <summary>
    /// Apply the weapon-grant side of a bonus pickup. Mirrors C OBJS_Add for
    /// weapon-type OBJ_TYPE values (SOURCE/OBJECTS.C). Returns true iff this
    /// type was a weapon and the inventory changed. Non-weapon bonus types
    /// (S_ENERGY, S_SUPER_SHIELD, S_ITEMBUY*) are out of scope here — those
    /// affect player shield/score and the caller handles them.
    /// </summary>
    public bool GrantWeapon(int objType)
    {
        switch (objType)
        {
            case 0:   // S_FORWARD_GUNS — always owned; no-op.
                return false;
            case 1:   // S_PLASMA_GUNS
                HasPlasmaGuns = true;
                return true;
            case 2:   // S_MICRO_MISSLE
                HasMicroMissile = true;
                return true;
            case >= 3 and <= 14:   // specials S_DUMB_MISSLE..S_DEATH_RAY
                SpecialWeapon = (WeaponType)objType;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Per-tick cooldown decrement (SHOTS.C:1035-1040).</summary>
    public void TickCooldowns()
    {
        for (int i = 0; i < _curShoot.Length; i++)
            if (_curShoot[i] > 0) _curShoot[i]--;
    }

    /// <summary>Cooldown remaining for a given weapon (test introspection).</summary>
    public int GetCooldown(WeaponType w) => _curShoot[(int)w];

    /// <summary>
    /// RAP.C:1000-1008 BUT_1 cascade. Returns the list of newly-spawned bullets
    /// from all weapons fired this tick (FORWARD_GUNS unconditional;
    /// PLASMA_GUNS/MICRO_MISSLE if owned; SpecialWeapon if equipped).
    /// </summary>
    public List<BulletLogic> ApplyButton1(int playerCx, int playerCy, int playerPic,
                                          IReadOnlyList<EnemyLogic>? enemies = null,
                                          Random? rng = null)
    {
        var bullets = new List<BulletLogic>(4);
        Shoot(WeaponType.ForwardGuns, playerCx, playerCy, playerPic, bullets, enemies, rng);
        if (HasPlasmaGuns)
            Shoot(WeaponType.PlasmaGuns, playerCx, playerCy, playerPic, bullets, enemies, rng);
        if (HasMicroMissile)
            Shoot(WeaponType.MicroMissile, playerCx, playerCy, playerPic, bullets, enemies, rng);
        if (SpecialWeapon is WeaponType sw)
            Shoot(sw, playerCx, playerCy, playerPic, bullets, enemies, rng);
        return bullets;
    }

    /// <summary>
    /// Single-weapon shoot. Mirrors SHOTS_PlayerShoot's per-type switch
    /// (SHOTS.C:644-1015). Honors the cur_shoot cooldown gate before spawning.
    /// Returns true iff a bullet was actually spawned. Sink-pushed bullets
    /// have their HitType set from the weapon's ShotLib entry (used by
    /// WaveController collision to filter air/ground enemies per SHOTS.C ht).
    /// </summary>
    public bool Shoot(WeaponType type, int playerCx, int playerCy, int playerPic,
                      List<BulletLogic> sink,
                      IReadOnlyList<EnemyLogic>? enemies = null,
                      Random? rng = null)
    {
        int idx = (int)type;
        if (_curShoot[idx] > 0) return false;   // cooldown active
        var lib = ShotLib.Get(type);
        _curShoot[idx] = lib.ShootRate;
        int sinkStart = sink.Count;

        // Clamp playerPic to the gun-offset table range. C's o_gun arrays are
        // declared as [8]; playerpic ranges 0..6 with neutral=3. We allow the
        // 8th slot (init=4 → playerpic+g_flash=11 out of range) by clamping.
        int pic = playerPic;
        if (pic < 0) pic = 0; if (pic > 7) pic = 7;

        switch (type)
        {
            case WeaponType.ForwardGuns:
                // SHOTS.C:650-684. Two bullets: gun1 right and gun1 left (minus 1).
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx + GunOffsets.OGun1[pic], spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx - GunOffsets.OGun1[pic] - 1, spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                break;

            case WeaponType.PlasmaGuns:
                // SHOTS.C:686-701. One bullet centered.
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx, spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                break;

            case WeaponType.MicroMissile:
                // SHOTS.C:703-733. Two bullets at gun3 ± offset.
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx + GunOffsets.OGun3[pic], spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx - GunOffsets.OGun3[pic], spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                break;

            case WeaponType.MissilePods:
                // SHOTS.C:818-848. Two bullets at gun2 ± offset, with smoke.
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx + GunOffsets.OGun2[pic], spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits, smoke: lib.Smoke));
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx - GunOffsets.OGun2[pic], spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits, smoke: lib.Smoke));
                break;

            case WeaponType.AirMissile:
                // SHOTS.C:850-878. Two bullets at gun2 ± offset, S_AIR hit.
            case WeaponType.GrdMissile:
                // SHOTS.C:880-908. Two bullets at gun2 ± offset, S_GROUND hit.
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx + GunOffsets.OGun2[pic], spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits, smoke: lib.Smoke));
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx - GunOffsets.OGun2[pic], spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits, smoke: lib.Smoke));
                break;

            case WeaponType.Bomb:
                // SHOTS.C:910-923. One bullet center.
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx, spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                break;

            case WeaponType.EnergyGrab:
                // SHOTS.C:925-938. One bullet at center-4.
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx - 4, spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                break;

            case WeaponType.PulseCannon:
                // SHOTS.C:956-969. One bullet center.
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx, spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                break;

            case WeaponType.DumbMissile:
                // SHOTS.C:735-766. Two Bresenham bullets, randomized scatter.
                // C's `cur->move.x2 = cur->x + random(16) + 10` etc; we mirror
                // that with the provided RNG. Falls back to deterministic
                // offsets if no RNG was supplied (tests).
                //
                // SHOTS.C:739 sets cur->delayflag = lib->delayflag (TRUE for
                // DUMB_MISSLE). On move.done the SHOTS.C:1220 delayflag branch
                // re-targets the bullet to (move.x + random(32)-16, 0) — i.e.
                // it transitions to flying straight UP after the initial
                // outward scatter. WaveController.PhaseMovement dispatches
                // that transition via BulletLogic.ReachedTarget.
                {
                    int r1x = (rng?.Next(16) ?? 8) + 10;
                    int r2x = (rng?.Next(16) ?? 8) + 10;
                    var b1 = BulletLogic.AimedAt(BulletKind.Player,
                        x: playerCx, y: playerCy,
                        x2: playerCx + r1x, y2: playerCy + 5,
                        initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed, damage: lib.Hits);
                    var b2 = BulletLogic.AimedAt(BulletKind.Player,
                        x: playerCx, y: playerCy,
                        x2: playerCx - r2x, y2: playerCy + 5,
                        initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed, damage: lib.Hits);
                    b1.PlayerWeapon = WeaponType.DumbMissile; b1.Delayed = true;
                    b2.PlayerWeapon = WeaponType.DumbMissile; b2.Delayed = true;
                    sink.Add(b1); sink.Add(b2);
                }
                break;

            case WeaponType.MiniGun:
                // SHOTS.C:768-790. One Bresenham bullet toward a random enemy.
                // If no enemy is on-screen, C returns FALSE (no shot fired) —
                // we mirror that by un-doing the cooldown and returning false.
                {
                    var target = PickRandomEnemy(enemies, rng);
                    if (target == null)
                    {
                        _curShoot[idx] = 0;
                        return false;
                    }
                    int aimX = target.X + (rng?.Next(2 * target.HalfW) ?? target.HalfW) - 1;
                    int aimY = target.Y + target.HalfH + (rng?.Next(2 * target.HalfH) ?? target.HalfH) - 1;
                    var mg = BulletLogic.AimedAt(BulletKind.Player,
                        x: playerCx, y: playerCy, x2: aimX, y2: aimY,
                        initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed, damage: lib.Hits);
                    mg.PlayerWeapon = WeaponType.MiniGun;
                    sink.Add(mg);
                }
                break;

            case WeaponType.MegaBomb:
                // SHOTS.C:940-954. Bresenham to (160, 75). Tagged with
                // PlayerWeapon=MegaBomb so WaveController.HandleShotDone
                // can fire the detonation effect (SHOTS.C:1232-1241:
                // ESHOT_Clear + damage all enemies + remove shot).
                {
                    var mb = BulletLogic.AimedAt(BulletKind.Player,
                        x: playerCx, y: playerCy, x2: 160, y2: 75,
                        initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed, damage: lib.Hits);
                    mb.PlayerWeapon = WeaponType.MegaBomb;
                    sink.Add(mb);
                }
                break;

            case WeaponType.Turret:
                // SHOTS.C:792-816. Pick a random AIR enemy; if none, no shot.
                // C damages the enemy immediately (enemy->hits -= lib->hits)
                // and creates a 1-tick S_LINE bullet for the visual.
                {
                    var target = PickRandomAirEnemy(enemies, rng);
                    if (target == null)
                    {
                        _curShoot[idx] = 0;
                        return false;
                    }
                    target.TakeDamage(lib.Hits);
                    int aimX = target.X + (rng?.Next(2 * target.HalfW) ?? target.HalfW) - 1;
                    int aimY = target.Y + (rng?.Next(2 * target.HalfH) ?? target.HalfH) - 1;
                    sink.Add(BulletLogic.LineBeam(aimX, aimY, damage: lib.Hits));
                }
                break;

            case WeaponType.ForwardLaser:
                // SHOTS.C:971-999. Two S_BEAM bullets at gun3 ± offset.
                // lib->fplrx = fplry = TRUE — beams track player_cx/cy each
                // iter; pass spawn-time playerCx/Cy as the start so each iter
                // the beam re-renders at (move - hlx) + (playerCx_now - startx).
                sink.Add(BulletLogic.VerticalBeam(
                    x: playerCx + GunOffsets.OGun3[pic], y: playerCy,
                    life: lib.NumFrames, damage: lib.Hits,
                    startPlayerX: playerCx, startPlayerY: playerCy));
                sink.Add(BulletLogic.VerticalBeam(
                    x: playerCx - GunOffsets.OGun3[pic], y: playerCy,
                    life: lib.NumFrames, damage: lib.Hits,
                    startPlayerX: playerCx, startPlayerY: playerCy));
                break;

            case WeaponType.DeathRay:
                // SHOTS.C:1001-1014. Single S_BEAM bullet, center above player.
                // Same fplr-tracking semantics as FORWARD_LASER.
                sink.Add(BulletLogic.VerticalBeam(
                    x: playerCx, y: playerCy - 24,
                    life: lib.NumFrames, damage: lib.Hits,
                    startPlayerX: playerCx, startPlayerY: playerCy));
                break;

            default:
                return false;
        }
        // Tag every bullet we just pushed with the weapon's hit type so
        // WaveController collision can filter air/ground enemies.
        TagHitType(sink, sinkStart, lib.Ht);
        return sink.Count > sinkStart;
    }

    /// <summary>
    /// Helper to tag all bullets the just-dispatched Shoot pushed with the
    /// weapon's HitType. Called by callers after `Shoot()` returns true if they
    /// kept the previous sink size. The public Shoot() handles tagging inline,
    /// but the BUT_1 cascade aggregates several Shoot() calls into one sink;
    /// each call retags only the bullets it pushed.
    /// </summary>
    private static void TagHitType(List<BulletLogic> sink, int from, HitType ht)
    {
        for (int i = from; i < sink.Count; i++) sink[i].HitType = ht;
    }

    private static EnemyLogic? PickRandomEnemy(IReadOnlyList<EnemyLogic>? enemies, Random? rng)
    {
        if (enemies == null || enemies.Count == 0) return null;
        int start = rng?.Next(enemies.Count) ?? 0;
        for (int i = 0; i < enemies.Count; i++)
        {
            var e = enemies[(start + i) % enemies.Count];
            if (e.Alive) return e;
        }
        return null;
    }

    private static EnemyLogic? PickRandomAirEnemy(IReadOnlyList<EnemyLogic>? enemies, Random? rng)
    {
        if (enemies == null || enemies.Count == 0) return null;
        int start = rng?.Next(enemies.Count) ?? 0;
        for (int i = 0; i < enemies.Count; i++)
        {
            var e = enemies[(start + i) % enemies.Count];
            if (e.Alive && !e.IsGround) return e;
        }
        return null;
    }
}
