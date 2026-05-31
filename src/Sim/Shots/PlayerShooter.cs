using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Raptor.Sim;
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
    private static readonly Lazy<StreamWriter?> RngTrace = new(OpenRngTrace);
    private static readonly Lazy<StreamWriter?> SpecialTrace = new(OpenSpecialTrace);
    private static bool DeterministicMiniGun =>
        DeterministicRandom.Enabled ||
        Environment.GetEnvironmentVariable("RAPTOR_DETERMINISTIC_MINIGUN") == "1";

    // Injected inventory — owns all standing-weapon state (Task 3.3).
    // WaveController passes its canonical Inventory; tests pass new Inventory()
    // or a pre-seeded one. An empty Inventory produces the default loadout:
    //   IsEquip(PlasmaGuns)=false, IsEquip(MicroMissile)=false,
    //   GetAmt(MegaBomb)=0, EquippedSpecial=null
    // which is identical to the old default field values.
    private readonly Inventory _inv;

    /// <summary>
    /// Construct with an explicit inventory. WaveController passes its canonical
    /// <see cref="Inventory"/> instance so state persists across per-wave resets.
    /// Pass <c>new Inventory()</c> (or omit) for tests that use the default loadout.
    /// </summary>
    public PlayerShooter(Inventory? inv = null)
    {
        _inv = inv ?? new Inventory();
    }

    // ── Standing-inventory accessors (delegated to _inv) ───────────────────

    /// <summary>True iff PlasmaGuns are owned. Delegates to _inv.IsEquip.</summary>
    public bool HasPlasmaGuns   => _inv.IsEquip(ObjType.PlasmaGuns);

    /// <summary>True iff MicroMissile is owned. Delegates to _inv.IsEquip.</summary>
    public bool HasMicroMissile => _inv.IsEquip(ObjType.MicroMissile);

    /// <summary>MegaBomb count. Delegates to _inv.GetAmt.</summary>
    public int MegaBombCount => _inv.GetAmt(ObjType.MegaBomb);

    /// <summary>
    /// The active special weapon — the inventory's EquippedSpecial.
    /// Null = no special equipped. Mirrors C plr.sweapon (RAP.C:1006).
    /// Read-only accessor; set via SelectSpecial / CycleSpecial / GrantWeapon.
    /// </summary>
    public ObjType? SpecialWeapon => _inv.EquippedSpecial;

    /// <summary>
    /// Owned special weapons — types that are equipped (IsEquip) and flagged SpecialW.
    /// Computed from the inventory; preserved for callers that enumerate specials.
    /// </summary>
    public IReadOnlyCollection<ObjType> OwnedSpecials
    {
        get
        {
            var result = new List<ObjType>();
            foreach (var (type, _, inuse) in _inv.Slots())
            {
                if (inuse && ObjLib.Of(type).SpecialW)
                    result.Add(type);
            }
            return result;
        }
    }

    /// <summary>
    /// Set the active special to <paramref name="w"/> iff the player owns it.
    /// Delegates to Inventory.MakeSpecial (mirrors OBJS_MakeSpecial, OBJECTS.C:1315).
    /// Returns true on success.
    /// </summary>
    public bool SelectSpecial(ObjType w) => _inv.MakeSpecial(w);

    /// <summary>Cycle to the next owned special. Delegates to Inventory.GetNext (verified C OBJS_GetNext equivalent).</summary>
    public void CycleSpecial()
    {
        _inv.GetNext();
        TraceSpecial("next", _inv.EquippedSpecial.HasValue ? (int)_inv.EquippedSpecial.Value : -1);
    }

    /// <summary>
    /// Resets per-weapon cooldown timers. Mirrors SHOTS_Init clearing shot_lib.cur_shoot.
    ///
    /// Does NOT clear the Inventory: Inventory lifetime is owned by WaveController
    /// (seeded on pilot-create, loaded on pilot-load, cleared only on pilot-create/load).
    /// Reset() is called per-wave (LoadWave) and must not disturb cross-wave weapon ownership.
    /// Verified call sites: WaveController.LoadWave (line ~570) — per-wave only.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < _curShoot.Length; i++) _curShoot[i] = 0;
        // Inventory intentionally NOT cleared here — WaveController owns that lifetime.
    }

    /// <summary>
    /// Apply the weapon-grant side of a bonus pickup. Delegates to Inventory.Add.
    /// Returns true iff this type was a weapon and the inventory changed.
    /// Non-weapon bonus types (S_ENERGY, S_SUPER_SHIELD, S_ITEMBUY*) are out of
    /// scope here — those affect player shield/score and the caller handles them.
    ///
    /// Behavior preserved vs old GrantWeapon:
    ///   ForwardGuns (0) → Inventory.Add is a no-op for always-owned; return false.
    ///   PlasmaGuns  (1) → Add → InUse=true; HasPlasmaGuns now true.
    ///   MicroMissile(2) → Add → InUse=true; HasMicroMissile now true.
    ///   MegaBomb   (11) → Add (OnlyFlag) → Num increments; MegaBombCount increases.
    ///   Specials  (3-14 except 11) → Add → InUse=true; EquippedSpecial set if null.
    ///   Non-weapons (>=15) → return false (Inventory.Add returns GotIt for money/
    ///     non-reg items but the caller treats those separately; we return false here
    ///     exactly as the old switch default did, so callers handle energy/shield).
    /// </summary>
    public bool GrantWeapon(int objType)
    {
        if (objType < 0 || objType > (int)ObjType.DeathRay)
            return false;

        var type = (ObjType)objType;
        if (type == ObjType.ForwardGuns)
            return false;  // always owned; no-op (lib->forever)

        int countBefore = CountSlots();
        bool equippedBefore = _inv.IsEquip(type);
        int amtBefore = _inv.GetAmt(type);
        ObjType? specialBefore = _inv.EquippedSpecial;

        var result = _inv.Add(type);

        // Trace auto-equip of a new special (mirrors old TraceSpecial("auto"/"add") calls).
        if (ObjLib.Of(type).SpecialW)
        {
            bool wasAutoSet = specialBefore == null && _inv.EquippedSpecial == type;
            if (wasAutoSet) TraceSpecial("auto", objType);
            if (_inv.IsEquip(type)) TraceSpecial("add", objType);
        }

        // Return true iff the inventory actually changed (weapon became owned or count grew).
        return result == BuyStuff.GotIt && (_inv.IsEquip(type) != equippedBefore
            || _inv.GetAmt(type) != amtBefore
            || CountSlots() != countBefore);
    }

    /// <summary>
    /// Consume one mega-bomb from the inventory. Returns false if count was 0,
    /// true iff a bomb was consumed.
    ///
    /// Routes through the C-faithful <see cref="Inventory.Use"/> (Task 5.1, OBJS_Use).
    /// The explicit count-0 guard preserves the tested bool semantics (Use is a
    /// no-op when nothing is owned). Parity-neutral: MegaBomb is SpecialW=false, so
    /// Use's cycle-on-zero branch never fires for it — behaviour is a plain
    /// decrement-and-remove.
    /// </summary>
    public bool ConsumeMegaBomb()
    {
        if (_inv.GetAmt(ObjType.MegaBomb) <= 0) return false;
        _inv.Use(ObjType.MegaBomb);
        return true;
    }

    private int CountSlots()
    {
        int n = 0;
        foreach (var _ in _inv.Slots()) n++;
        return n;
    }

    /// <summary>Per-tick cooldown decrement (SHOTS.C:1035-1040).</summary>
    public void TickCooldowns()
    {
        for (int i = 0; i < _curShoot.Length; i++)
            if (_curShoot[i] > 0) _curShoot[i]--;
    }

    /// <summary>Cooldown remaining for a given weapon (test introspection).</summary>
    public int GetCooldown(ObjType w) => _curShoot[(int)w];

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
        Shoot(ObjType.ForwardGuns, playerCx, playerCy, playerPic, bullets, enemies, rng);
        if (HasPlasmaGuns)
            Shoot(ObjType.PlasmaGuns, playerCx, playerCy, playerPic, bullets, enemies, rng);
        if (HasMicroMissile)
            Shoot(ObjType.MicroMissile, playerCx, playerCy, playerPic, bullets, enemies, rng);
        if (SpecialWeapon is ObjType sw)
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
    public bool Shoot(ObjType type, int playerCx, int playerCy, int playerPic,
                      List<BulletLogic> sink,
                      IReadOnlyList<EnemyLogic>? enemies = null,
                      Random? rng = null)
    {
        int idx = (int)type;
        if (_curShoot[idx] > 0)
        {
            TraceLine(string.Format(CultureInfo.InvariantCulture,
                "cooldown.block weapon={0} cooldown={1}", (int)type, _curShoot[idx]));
            return false;
        }
        TraceSpecial("use", idx);
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
            case ObjType.ForwardGuns:
                // SHOTS.C:650-684. Two bullets: gun1 right and gun1 left (minus 1).
                ConsumeRandomPitchSound(rng, "sound.fx_gun");
                NextRandom(rng, lib.NumFrames, "forward.frame.r");  // cur->curframe = random(lib->numframes)
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx + GunOffsets.OGun1[pic], spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                NextRandom(rng, lib.NumFrames, "forward.frame.l");
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx - GunOffsets.OGun1[pic] - 1, spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                break;

            case ObjType.PlasmaGuns:
                // SHOTS.C:686-701. One bullet centered.
                ConsumeRandomPitchSound(rng, "sound.fx_gun");
                NextRandom(rng, lib.NumFrames, "plasma.frame");
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx, spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                break;

            case ObjType.MicroMissile:
                // SHOTS.C:703-733. Two bullets at gun3 ± offset.
                ConsumeRandomPitchSound(rng, "sound.fx_gun");
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx + GunOffsets.OGun3[pic], spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx - GunOffsets.OGun3[pic], spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                break;

            case ObjType.MissilePods:
                // SHOTS.C:818-848. Two bullets at gun2 ± offset, with smoke.
                ConsumeRandomPitchSound(rng, "sound.fx_gun");
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx + GunOffsets.OGun2[pic], spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits, smoke: lib.Smoke));
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx - GunOffsets.OGun2[pic], spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits, smoke: lib.Smoke));
                break;

            case ObjType.AirMissile:
                // SHOTS.C:850-878. Two bullets at gun2 ± offset, S_AIR hit.
            case ObjType.GrdMissile:
                // SHOTS.C:880-908. Two bullets at gun2 ± offset, S_GROUND hit.
                ConsumeRandomPitchSound(rng, "sound.fx_missle");
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx + GunOffsets.OGun2[pic], spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits, smoke: lib.Smoke));
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx - GunOffsets.OGun2[pic], spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits, smoke: lib.Smoke));
                break;

            case ObjType.Bomb:
                // SHOTS.C:910-923. One bullet center.
                ConsumeRandomPitchSound(rng, "sound.fx_missle");
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx, spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                break;

            case ObjType.EnergyGrab:
                // SHOTS.C:925-938. One bullet at center-4.
                ConsumeRandomPitchSound(rng, "sound.fx_gun");
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx - 4, spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                break;

            case ObjType.PulseCannon:
                // SHOTS.C:956-969. One bullet center.
                ConsumeRandomPitchSound(rng, "sound.fx_pulse");
                sink.Add(BulletLogic.PlayerStraight(
                    spawnX: playerCx, spawnY: playerCy,
                    initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                    hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits));
                break;

            case ObjType.DumbMissile:
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
                    ConsumeRandomPitchSound(rng, "sound.fx_missle");
                    int r1x = NextRandom(rng, 16, "dumb.scatter.r", fallback: 8) + 10;
                    int r2x = NextRandom(rng, 16, "dumb.scatter.l", fallback: 8) + 10;
                    var b1 = BulletLogic.PlayerAimedAt(
                        x: playerCx, y: playerCy,
                        x2: playerCx + r1x, y2: playerCy + 5,
                        initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                        hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits);
                    var b2 = BulletLogic.PlayerAimedAt(
                        x: playerCx, y: playerCy,
                        x2: playerCx - r2x, y2: playerCy + 5,
                        initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                        hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits);
                    b1.PlayerWeapon = ObjType.DumbMissile; b1.Delayed = true;
                    b2.PlayerWeapon = ObjType.DumbMissile; b2.Delayed = true;
                    sink.Add(b1); sink.Add(b2);
                }
                break;

            case ObjType.MiniGun:
                // SHOTS.C:768-790. One Bresenham bullet toward a random enemy.
                // If no enemy is on-screen, C returns FALSE (no shot fired) —
                // we mirror that by un-doing the cooldown and returning false.
                {
                    bool deterministic = DeterministicMiniGun;
                    var target = deterministic
                        ? PickMiddleEnemy(enemies)
                        : PickRandomEnemy(enemies, rng);
                    if (target == null)
                    {
                        TraceMiniGunTarget(enemies, target: null);
                        return false;
                    }
                    int aimX;
                    int aimY;
                    if (deterministic)
                    {
                        aimX = target.X + target.HalfW - 1;
                        aimY = target.Y + target.HalfH + target.HalfH - 1;
                    }
                    else
                    {
                        ConsumeRandomPitchSound(rng, "sound.fx_gun");
                        NextRandom(rng, lib.NumFrames, "mini.frame");
                        aimX = target.X + NextRandom(rng, target.Meta.Width, "mini.aim.x", fallback: target.HalfW) - 1;
                        aimY = target.Y + target.HalfH + NextRandom(rng, target.Meta.Height, "mini.aim.y", fallback: target.HalfH) - 1;
                    }
                    TraceMiniGunTarget(enemies, target);
                    var mg = BulletLogic.PlayerAimedAt(
                        x: playerCx, y: playerCy, x2: aimX, y2: aimY,
                        initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                        hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits);
                    mg.PlayerWeapon = ObjType.MiniGun;
                    sink.Add(mg);
                }
                break;

            case ObjType.MegaBomb:
                // SHOTS.C:940-954. Bresenham to (160, 75). Tagged with
                // PlayerWeapon=MegaBomb so WaveController.HandleShotDone
                // can fire the detonation effect (SHOTS.C:1232-1241:
                // ESHOT_Clear + damage all enemies + remove shot).
                {
                    var mb = BulletLogic.PlayerAimedAt(
                        x: playerCx, y: playerCy, x2: 160, y2: 75,
                        initSpeed: lib.Speed, maxSpeed: lib.MaxSpeed,
                        hlx: lib.Hlx, hly: lib.Hly, damage: lib.Hits);
                    mb.PlayerWeapon = ObjType.MegaBomb;
                    sink.Add(mb);
                }
                break;

            case ObjType.Turret:
                // SHOTS.C:792-816. Pick a random AIR enemy; if none, no shot.
                // C damages the enemy immediately (enemy->hits -= lib->hits)
                // and creates a 1-tick S_LINE bullet for the visual.
                {
                    var target = PickRandomAirEnemy(enemies, rng);
                    if (target == null)
                    {
                        // SHOTS.C:811-818 — no air enemy: SHOTS_Remove + break,
                        // leaving lib->cur_shoot armed at shoot_rate (set at
                        // SHOTS.C:652 before the lookup). Do NOT clear the gate;
                        // mirror the MiniGun no-target branch above.
                        return false;
                    }
                    ConsumeRandomPitchSound(rng, "sound.fx_turret");
                    target.TakeDamage(lib.Hits);
                    int aimX = target.X + NextRandom(rng, target.Meta.Width, "turret.aim.x", fallback: target.HalfW) - 1;
                    int aimY = target.Y + NextRandom(rng, target.Meta.Height, "turret.aim.y", fallback: target.HalfH) - 1;
                    sink.Add(BulletLogic.LineBeam(aimX, aimY, damage: lib.Hits));
                }
                break;

            case ObjType.ForwardLaser:
                // SHOTS.C:971-999. Two S_BEAM bullets at gun3 ± offset.
                // FX_LASER has rpflag=FALSE, so it does not consume RNG.
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

            case ObjType.DeathRay:
                // SHOTS.C:1001-1014. Single S_BEAM bullet, center above player.
                // FX_LASER has rpflag=FALSE, so it does not consume RNG.
                // Same fplr-tracking semantics as FORWARD_LASER.
                sink.Add(BulletLogic.VerticalBeam(
                    x: playerCx, y: playerCy - 24,
                    life: lib.NumFrames, damage: lib.Hits,
                    startPlayerX: playerCx, startPlayerY: playerCy));
                break;

            default:
                return false;
        }
        // Tag every bullet we just pushed with the weapon metadata C stores
        // through shot->lib: hit type for collision and weapon id for dumps /
        // shot_done dispatch.
        TagShotMetadata(sink, sinkStart, type, lib.Ht);
        return sink.Count > sinkStart;
    }

    /// <summary>
    /// Helper to tag all bullets the just-dispatched Shoot pushed with the
    /// weapon's HitType. Called by callers after `Shoot()` returns true if they
    /// kept the previous sink size. The public Shoot() handles tagging inline,
    /// but the BUT_1 cascade aggregates several Shoot() calls into one sink;
    /// each call retags only the bullets it pushed.
    /// </summary>
    private static void TagShotMetadata(List<BulletLogic> sink, int from,
                                        ObjType type, HitType ht)
    {
        for (int i = from; i < sink.Count; i++)
        {
            sink[i].HitType = ht;
            sink[i].PlayerWeapon = type;
            sink[i].CWeaponTypeForDump = CShotLibType(type);
        }
    }

    private static ObjType CShotLibType(ObjType type) =>
        type == ObjType.AirMissile ? ObjType.MissilePods : type;

    private static EnemyLogic? PickRandomEnemy(IReadOnlyList<EnemyLogic>? enemies, Random? rng)
    {
        if (enemies == null || enemies.Count == 0) return null;
        var visible = new List<EnemyLogic>(enemies.Count);
        foreach (var e in enemies)
            if (IsVisible(e)) visible.Add(e);
        if (visible.Count == 0) return null;
        return visible[NextRandom(rng, visible.Count, "mini.target")];
    }

    private static EnemyLogic? PickMiddleEnemy(IReadOnlyList<EnemyLogic>? enemies)
    {
        if (enemies == null || enemies.Count == 0) return null;
        var visible = new List<EnemyLogic>(enemies.Count);
        foreach (var e in enemies)
            if (IsVisible(e)) visible.Add(e);
        if (visible.Count == 0) return null;
        return visible[visible.Count / 2];
    }

    private static EnemyLogic? PickRandomAirEnemy(IReadOnlyList<EnemyLogic>? enemies, Random? rng)
    {
        if (enemies == null || enemies.Count == 0) return null;
        var visible = new List<EnemyLogic>(enemies.Count);
        foreach (var e in enemies)
            if (IsVisible(e) && !e.IsGround) visible.Add(e);
        if (visible.Count == 0) return null;
        return visible[NextRandom(rng, visible.Count, "turret.target")];
    }

    private static bool IsVisible(EnemyLogic e)
    {
        if (!e.Alive && !e.PendingRemovalDump) return false;
        return e.Y + e.Meta.Height > 0 && e.Y < 200
            && e.X + e.Meta.Width > 0 && e.X < 320;
    }

    internal static int NextRandom(Random? rng, int maxValue, string label, int fallback = 0)
    {
        int value = DeterministicRandom.NextOrMidpoint(rng, maxValue, fallback);
        var trace = RngTrace.Value;
        if (trace != null)
        {
            trace.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0} max={1} value={2}", label, maxValue, value));
            trace.Flush();
        }
        return value;
    }

    internal static void TraceLine(string message)
    {
        var trace = RngTrace.Value;
        if (trace == null) return;
        trace.WriteLine(message);
        trace.Flush();
    }

    private static void TraceMiniGunTarget(IReadOnlyList<EnemyLogic>? enemies, EnemyLogic? target)
    {
        var trace = RngTrace.Value;
        if (trace == null) return;

        trace.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "mini.target.snapshot count={0} selected={1}",
            enemies?.Count ?? 0,
            target == null
                ? "none"
                : string.Format(CultureInfo.InvariantCulture, "x={0} y={1} w={2} h={3}", target.X, target.Y, target.Meta.Width, target.Meta.Height)));
        if (enemies != null)
        {
            for (int i = 0; i < enemies.Count; i++)
            {
                var e = enemies[i];
                trace.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "mini.target.snapshot[{0}] x={1} y={2} w={3} h={4} alive={5} pending={6} visible={7}",
                    i, e.X, e.Y, e.Meta.Width, e.Meta.Height, e.Alive, e.PendingRemovalDump, IsVisible(e)));
            }
        }
        trace.Flush();
    }

    private static void ConsumeRandomPitchSound(Random? rng, string label)
    {
        // FX.C SND_Patch gates its random(40) pitch draw behind fx_volume>=1,
        // numsnds<=2 (a wall-clock Mix_Playing count), item!=EMPTY and rpflag.
        // In the deterministic parity golden the audio path is off, so C draws
        // NOTHING here — confirmed empirically: consuming this draw desynced the
        // shared per-wave RNG stream and was the long-standing full_demo MiniGun
        // divergence (removing it makes mission_start/mission_long/full_demo all
        // pass). The no-wall-clock sim rule forbids modeling the audio-gated draw
        // anyway. Kept as a no-op call site so the per-weapon SND_Patch mapping
        // stays documented. (Enemy positional SND_3DPatch DOES draw and is handled
        // separately in WaveController.ConsumeEnemyShotSoundRandomForParity.)
        _ = rng;
        _ = label;
    }

    private static StreamWriter? OpenRngTrace()
    {
        string? path = Environment.GetEnvironmentVariable("RAPTOR_SHOOT_RNG_TRACE");
        if (string.IsNullOrWhiteSpace(path)) return null;
        return new StreamWriter(path) { AutoFlush = true };
    }

    private static StreamWriter? OpenSpecialTrace()
    {
        string? path = Environment.GetEnvironmentVariable("RAPTOR_SPECIAL_TRACE");
        if (string.IsNullOrWhiteSpace(path)) return null;
        return new StreamWriter(path) { AutoFlush = true };
    }

    private static void TraceSpecial(string eventName, int type)
    {
        var trace = SpecialTrace.Value;
        if (trace == null) return;
        trace.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "event={0} type={1}", eventName, type));
        trace.Flush();
    }
}
