using System.Collections.Generic;
using Raptor.Sim.Bullet;
using Raptor.Sim.Shots;
using Xunit;

namespace Raptor.Tests;

public class PlayerShooterTests
{
    // ── ShotLib table ────────────────────────────────────────────────────────

    [Fact]
    public void ShotLib_FORWARD_GUNS_matches_C()
    {
        // SOURCE/SHOTS.C:137-165 sets these exact values.
        var lib = ShotLib.Get(WeaponType.ForwardGuns);
        Assert.Equal(1, lib.Hits);
        Assert.Equal(8, lib.Speed);
        Assert.Equal(16, lib.MaxSpeed);
        Assert.Equal(4, lib.NumFrames);
        Assert.Equal(2, lib.ShootRate);
        Assert.False(lib.DelayFlag);
        Assert.True(lib.MoveFlag);
        Assert.False(lib.UsePlot);
        Assert.Equal(HitType.All, lib.Ht);
        Assert.Equal(BeamType.Shoot, lib.Beam);
    }

    [Fact]
    public void ShotLib_PLASMA_GUNS_matches_C()
    {
        // SHOTS.C:167-196: hits=2, speed=4, maxspeed=8, shoot_rate=10, ht=S_AIR.
        var lib = ShotLib.Get(WeaponType.PlasmaGuns);
        Assert.Equal(2, lib.Hits);
        Assert.Equal(4, lib.Speed);
        Assert.Equal(8, lib.MaxSpeed);
        Assert.Equal(10, lib.ShootRate);
        Assert.Equal(HitType.Air, lib.Ht);
    }

    [Fact]
    public void ShotLib_table_covers_all_weapons_up_to_DEATH_RAY()
    {
        // LAST_WEAPON = S_DEATH_RAY = 14 → 15 entries total.
        Assert.Equal(15, ShotLib.Count);
        // Each entry's Type matches its position.
        for (int i = 0; i < ShotLib.Count; i++)
            Assert.Equal((WeaponType)i, ShotLib.Table[i].Type);
    }

    // ── Gun offsets ──────────────────────────────────────────────────────────

    [Fact]
    public void GunOffsets_match_RAP_C()
    {
        // RAP.C:68-70 o_gun1/2/3 — exact values.
        Assert.Equal(new[] { 1, 3, 5, 6, 5, 3, 1, 0 }, GunOffsets.OGun1);
        Assert.Equal(new[] { 1, 3, 6, 9, 6, 3, 2, 0 }, GunOffsets.OGun2);
        Assert.Equal(new[] { 2, 6, 8, 11, 8, 6, 2, 0 }, GunOffsets.OGun3);
    }

    // ── PlayerShooter behaviour ──────────────────────────────────────────────

    [Fact]
    public void Shoot_FORWARD_GUNS_spawns_two_bullets_at_neutral_gun_offsets()
    {
        // playerpic=3 (neutral, playerbasepic) → o_gun1[3] = 6.
        // C SHOTS_PlayerShoot sets cur->x = player_cx + 6 and cur->x = player_cx - 6 - 1.
        // The bullet's raw pre-think position is the spawn point; SHOTS_Think
        // (= our Tick) snapshots display = move - hl on the spawn iter.
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        bool fired = ps.Shoot(WeaponType.ForwardGuns, playerCx: 160, playerCy: 176,
                              playerPic: 3, sink);
        Assert.True(fired);
        Assert.Equal(2, sink.Count);
        // Mx/My report the C `shot->move.x/y` — the raw spawn until ticked.
        Assert.Equal(166, sink[0].Mx);  // player_cx + o_gun1[3]
        Assert.Equal(176, sink[0].My);
        Assert.Equal(153, sink[1].Mx);  // player_cx - o_gun1[3] - 1
        Assert.Equal(176, sink[1].My);
        // After one Tick (=one SHOTS_Think iter) the bullet enters displayed
        // state: shot->x = move.x - hlx, shot->y = move.y - hly. hlx=hly=4.
        sink[0].Tick();
        Assert.Equal(162, sink[0].X);
        Assert.Equal(172, sink[0].Y);
        sink[1].Tick();
        Assert.Equal(149, sink[1].X);
        Assert.Equal(172, sink[1].Y);
    }

    [Fact]
    public void Shoot_honors_cooldown_between_fires()
    {
        // SHOTS.C:637-639: `if (lib->cur_shoot) return FALSE;` then set to shoot_rate.
        // FORWARD_GUNS shoot_rate=2 → fire, [cd=2], TickCooldowns ×2, fire again.
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        Assert.True (ps.Shoot(WeaponType.ForwardGuns, 160, 176, 3, sink));   // fired
        Assert.Equal(2, ps.GetCooldown(WeaponType.ForwardGuns));
        Assert.False(ps.Shoot(WeaponType.ForwardGuns, 160, 176, 3, sink));   // cd=2 → blocked
        ps.TickCooldowns();
        Assert.Equal(1, ps.GetCooldown(WeaponType.ForwardGuns));
        Assert.False(ps.Shoot(WeaponType.ForwardGuns, 160, 176, 3, sink));   // cd=1 → blocked
        ps.TickCooldowns();
        Assert.Equal(0, ps.GetCooldown(WeaponType.ForwardGuns));
        Assert.True (ps.Shoot(WeaponType.ForwardGuns, 160, 176, 3, sink));   // cd=0 → fired
    }

    [Fact]
    public void ApplyButton1_only_fires_forward_guns_by_default()
    {
        // Default inventory: no plasma, no micro, no special. Only FORWARD_GUNS fires.
        var ps = new PlayerShooter();
        var bullets = ps.ApplyButton1(playerCx: 160, playerCy: 176, playerPic: 3);
        Assert.Equal(2, bullets.Count);
    }

    [Fact]
    public void ApplyButton1_fires_plasma_when_owned()
    {
        var ps = new PlayerShooter { HasPlasmaGuns = true };
        var bullets = ps.ApplyButton1(160, 176, 3);
        // 2 forward + 1 plasma = 3 bullets.
        Assert.Equal(3, bullets.Count);
    }

    [Fact]
    public void ApplyButton1_fires_micro_missile_when_owned()
    {
        var ps = new PlayerShooter { HasMicroMissile = true };
        var bullets = ps.ApplyButton1(160, 176, 3);
        // 2 forward + 2 micro = 4 bullets.
        Assert.Equal(4, bullets.Count);
    }

    [Fact]
    public void ApplyButton1_fires_full_cascade_when_everything_owned()
    {
        // FORWARD_GUNS + PLASMA_GUNS + MICRO_MISSLE + SpecialWeapon=PulseCannon
        // = 2 + 1 + 2 + 1 = 6.
        var ps = new PlayerShooter
        {
            HasPlasmaGuns   = true,
            HasMicroMissile = true,
            SpecialWeapon   = WeaponType.PulseCannon,
        };
        var bullets = ps.ApplyButton1(160, 176, 3);
        Assert.Equal(6, bullets.Count);
    }

    [Fact]
    public void Reset_clears_all_cooldowns()
    {
        var ps = new PlayerShooter();
        ps.Shoot(WeaponType.ForwardGuns, 160, 176, 3, new List<BulletLogic>());
        Assert.True(ps.GetCooldown(WeaponType.ForwardGuns) > 0);
        ps.Reset();
        Assert.Equal(0, ps.GetCooldown(WeaponType.ForwardGuns));
    }

    [Fact]
    public void Shoot_PLASMA_GUNS_spawns_one_bullet_at_center()
    {
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        ps.Shoot(WeaponType.PlasmaGuns, 160, 176, 3, sink);
        Assert.Single(sink);
        // Raw spawn at center.
        Assert.Equal(160, sink[0].Mx);
        Assert.Equal(176, sink[0].My);
        // After one Tick, display = move - hl. hlx=hly=4.
        sink[0].Tick();
        Assert.Equal(156, sink[0].X);
        Assert.Equal(172, sink[0].Y);
    }

    [Fact]
    public void Beam_weapons_dont_yet_spawn_but_cooldown_unchanged()
    {
        // Turret / ForwardLaser / DeathRay aren't yet spawning a bullet, but
        // their cooldown gate must NOT prevent retries each frame — Shoot()
        // returns false without setting cur_shoot for beams (no bullet means
        // C wouldn't have fired either).
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        Assert.False(ps.Shoot(WeaponType.Turret, 160, 176, 3, sink));
        Assert.Empty(sink);
        // The cooldown is still SET (mirrors C: lib->cur_shoot = lib->shoot_rate
        // BEFORE the switch) — repeat-firing matches C's pacing even for stubs.
        Assert.True(ps.GetCooldown(WeaponType.Turret) > 0);
    }

    // ── HitType tagging ──────────────────────────────────────────────────────

    [Fact]
    public void Forward_guns_bullets_carry_HitType_All()
    {
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        ps.Shoot(WeaponType.ForwardGuns, 160, 176, 3, sink);
        Assert.All(sink, b => Assert.Equal(HitType.All, b.HitType));
    }

    [Fact]
    public void Plasma_bullets_carry_HitType_Air()
    {
        // SHOTS.C lib->ht for PLASMA_GUNS = S_AIR — must NOT hit ground enemies.
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        ps.Shoot(WeaponType.PlasmaGuns, 160, 176, 3, sink);
        Assert.All(sink, b => Assert.Equal(HitType.Air, b.HitType));
    }

    [Fact]
    public void GrdMissile_bullets_carry_HitType_Ground()
    {
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        ps.Shoot(WeaponType.GrdMissile, 160, 176, 3, sink);
        Assert.All(sink, b => Assert.Equal(HitType.Ground, b.HitType));
    }

    [Fact]
    public void MicroMissile_bullets_carry_HitType_GrAll()
    {
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        ps.Shoot(WeaponType.MicroMissile, 160, 176, 3, sink);
        Assert.All(sink, b => Assert.Equal(HitType.GrAll, b.HitType));
    }

    [Fact]
    public void Bomb_bullets_carry_HitType_GTile()
    {
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        ps.Shoot(WeaponType.Bomb, 160, 176, 3, sink);
        Assert.All(sink, b => Assert.Equal(HitType.GTile, b.HitType));
    }

    // ── BulletLogic.PlayerStraight movement parity ───────────────────────────

    [Fact]
    public void PlayerStraight_display_lags_move_each_tick()
    {
        // SHOTS.C:1052-1267 SHOTS_Think for FORWARD_GUNS at player_cx=160, player_cy=176,
        // hly=4, speed=8, maxspeed=16:
        //   Iter 0 (in same tick as PlayerShoot): y = 176-4 = 172.
        //              speed→9. move.y = 176-9 = 167.
        //   Iter 1: y = 167-4 = 163. speed→10. move.y = 167-10 = 157.
        //   Iter 2: y = 157-4 = 153. speed→11. move.y = 157-11 = 146.
        //   Iter 3: y = 146-4 = 142. speed→12. move.y = 146-12 = 134.
        // Each Tick() executes one SHOTS_Think iteration.
        var b = BulletLogic.PlayerStraight(spawnX: 160, spawnY: 176,
            initSpeed: 8, maxSpeed: 16, hlx: 4, hly: 4, damage: 1);
        b.Tick(); Assert.Equal(156, b.X); Assert.Equal(172, b.Y);
        b.Tick(); Assert.Equal(163, b.Y);
        b.Tick(); Assert.Equal(153, b.Y);
        b.Tick(); Assert.Equal(142, b.Y);
    }

    // ── Bonus → inventory wiring (GrantWeapon dispatch) ──────────────────────

    [Fact]
    public void GrantWeapon_FORWARD_GUNS_is_noop_returns_false()
    {
        var ps = new PlayerShooter();
        // FORWARD_GUNS always owned; picking it up is invisible (lib->forever).
        Assert.False(ps.GrantWeapon(0 /* S_FORWARD_GUNS */));
        Assert.False(ps.HasPlasmaGuns);
        Assert.False(ps.HasMicroMissile);
        Assert.Null(ps.SpecialWeapon);
    }

    [Fact]
    public void GrantWeapon_PLASMA_sets_HasPlasmaGuns()
    {
        var ps = new PlayerShooter();
        Assert.True(ps.GrantWeapon(1 /* S_PLASMA_GUNS */));
        Assert.True(ps.HasPlasmaGuns);
    }

    [Fact]
    public void GrantWeapon_MICRO_MISSLE_sets_HasMicroMissile()
    {
        var ps = new PlayerShooter();
        Assert.True(ps.GrantWeapon(2 /* S_MICRO_MISSLE */));
        Assert.True(ps.HasMicroMissile);
    }

    [Fact]
    public void GrantWeapon_specials_replace_SpecialWeapon()
    {
        var ps = new PlayerShooter();
        Assert.True(ps.GrantWeapon(3 /* S_DUMB_MISSLE */));
        Assert.Equal(WeaponType.DumbMissile, ps.SpecialWeapon);
        // Picking up a different special weapon replaces it (C plr.sweapon).
        Assert.True(ps.GrantWeapon(11 /* S_MEGA_BOMB */));
        Assert.Equal(WeaponType.MegaBomb, ps.SpecialWeapon);
    }

    [Fact]
    public void GrantWeapon_non_weapon_types_return_false()
    {
        // S_SUPER_SHIELD (15), S_ENERGY (16), S_DETECT (17), S_ITEMBUY1..6 (18..23)
        // are not weapons; GrantWeapon must return false so the caller handles them.
        var ps = new PlayerShooter();
        Assert.False(ps.GrantWeapon(15));
        Assert.False(ps.GrantWeapon(16));
        Assert.False(ps.GrantWeapon(17));
        Assert.False(ps.GrantWeapon(20));
        Assert.False(ps.HasPlasmaGuns);
        Assert.False(ps.HasMicroMissile);
        Assert.Null(ps.SpecialWeapon);
    }

    [Fact]
    public void PlayerStraight_dies_when_move_passes_top_edge()
    {
        // SHOTS.C:1262-1265 — `if (move.y < 0) move.done = TRUE, doneflag = TRUE`.
        // For a bullet spawned near y=0, the first Tick advances move below zero.
        var b = BulletLogic.PlayerStraight(spawnX: 160, spawnY: 4,
            initSpeed: 8, maxSpeed: 16, hlx: 4, hly: 4, damage: 1);
        // Tick: snapshot Y=0; speed→9; move.y = 4-9 = -5 → dies.
        b.Tick();
        Assert.False(b.Alive);
    }
}
