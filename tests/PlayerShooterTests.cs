using System;
using System.Collections.Generic;
using Raptor.Sim;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Raptor.Sim.Shots;
using Xunit;

namespace Raptor.Tests;

public class PlayerShooterTests
{
    // ── ShotLib table ────────────────────────────────────────────────────────

    [Fact]
    public void ForwardGuns_does_not_consume_a_sound_pitch_random_draw()
    {
        // FX.C SND_Patch gates its random(40) pitch draw behind fx_volume>=1,
        // numsnds<=2 (a wall-clock Mix_Playing count), item!=EMPTY and rpflag —
        // all off in the deterministic parity golden, so C draws ZERO sound RNG
        // for weapon fire. ForwardGuns must consume only its two bullet curframe
        // draws (forward.frame.r/l), not a sound-pitch draw. The spurious draw
        // was the full_demo MiniGun-divergence root cause.
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        var actual = new Random(4242);
        ps.Shoot(ObjType.ForwardGuns, 160, 176, 3, sink, enemies: null, rng: actual);

        int numFrames = ShotLib.Get(ObjType.ForwardGuns).NumFrames;
        var expected = new Random(4242);
        PlayerShooter.NextRandom(expected, numFrames, "expected.frame.r");
        PlayerShooter.NextRandom(expected, numFrames, "expected.frame.l");

        Assert.Equal(PlayerShooter.NextRandom(expected, 1000, "expected.next"),
                     PlayerShooter.NextRandom(actual, 1000, "actual.next"));
    }

    [Fact]
    public void ShotLib_FORWARD_GUNS_matches_C()
    {
        // SOURCE/SHOTS.C:137-165 sets these exact values.
        var lib = ShotLib.Get(ObjType.ForwardGuns);
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
    public void ShotLib_PULSE_CANNON_hlx_matches_16x16_SHOKWV_sprite()
    {
        // SHOKWV_BLK is 16x16 (assets/bullets/SHOKWV_BLK_00.png), so C's
        // slib->hlx = width >> 1 = 8 (SHOTS.C:532-564 init). A stale 32x16
        // assumption (hlx=16) shifts every pulse bullet 8px left of C.
        var lib = ShotLib.Get(ObjType.PulseCannon);
        Assert.Equal(8, lib.Hlx);
        Assert.Equal(8, lib.Hly);
    }

    [Fact]
    public void PulseCannon_bullet_collision_x_sits_at_player_cx_minus_8_like_C()
    {
        // C spawns the shockwave at cur->x = player_cx (SHOTS.C:961) and tests
        // tile/enemy collision with shot->x = move.x - hlx (TILE_IsHit(lib->hits,
        // shot->x, shot->y), SHOTS.C:1193). With the 16x16 SHOKWV sprite hlx=8,
        // so the collision X sits at player_cx - 8. Verified against the C binary
        // replaying bench_20260612_150134: at player_cx 58 the pulse bullet's
        // shot->x = 50, which clears ground tile 1161 (x 31..47); an hlx=16 shift
        // put Godot's bullet at x=42 and destroyed that tile ~10 iters early,
        // the iter-558 wave-5 parity divergence.
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        const int playerCx = 58;
        ps.Shoot(ObjType.PulseCannon, playerCx, 100, 3, sink, enemies: null, rng: new Random(1));
        Assert.Single(sink);
        sink[0].Tick();   // first SHOTS_Think snapshots display X = move.x - hlx
        Assert.Equal(playerCx - 8, sink[0].X);
    }

    [Fact]
    public void ShotLib_PLASMA_GUNS_matches_C()
    {
        // SHOTS.C:167-196: hits=2, speed=4, maxspeed=8, shoot_rate=10, ht=S_AIR.
        var lib = ShotLib.Get(ObjType.PlasmaGuns);
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
            Assert.Equal((ObjType)i, ShotLib.Table[i].Type);
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
        bool fired = ps.Shoot(ObjType.ForwardGuns, playerCx: 160, playerCy: 176,
                              playerPic: 3, sink);
        Assert.True(fired);
        Assert.Equal(2, sink.Count);
        // Mx/My report the C `shot->move.x/y` — the raw spawn until ticked.
        Assert.Equal(166, sink[0].Mx);  // player_cx + o_gun1[3]
        Assert.Equal(176, sink[0].My);
        Assert.Equal(153, sink[1].Mx);  // player_cx - o_gun1[3] - 1
        Assert.Equal(176, sink[1].My);
        Assert.All(sink, b => Assert.Equal(ObjType.ForwardGuns, b.PlayerWeapon));
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
        Assert.True (ps.Shoot(ObjType.ForwardGuns, 160, 176, 3, sink));   // fired
        Assert.Equal(2, ps.GetCooldown(ObjType.ForwardGuns));
        Assert.False(ps.Shoot(ObjType.ForwardGuns, 160, 176, 3, sink));   // cd=2 → blocked
        ps.TickCooldowns();
        Assert.Equal(1, ps.GetCooldown(ObjType.ForwardGuns));
        Assert.False(ps.Shoot(ObjType.ForwardGuns, 160, 176, 3, sink));   // cd=1 → blocked
        ps.TickCooldowns();
        Assert.Equal(0, ps.GetCooldown(ObjType.ForwardGuns));
        Assert.True (ps.Shoot(ObjType.ForwardGuns, 160, 176, 3, sink));   // cd=0 → fired
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
        var inv = new Inventory();
        inv.Add(ObjType.PlasmaGuns);
        var ps = new PlayerShooter(inv);
        var bullets = ps.ApplyButton1(160, 176, 3);
        // 2 forward + 1 plasma = 3 bullets.
        Assert.Equal(3, bullets.Count);
    }

    [Fact]
    public void ApplyButton1_fires_micro_missile_when_owned()
    {
        var inv = new Inventory();
        inv.Add(ObjType.MicroMissile);
        var ps = new PlayerShooter(inv);
        var bullets = ps.ApplyButton1(160, 176, 3);
        // 2 forward + 2 micro = 4 bullets.
        Assert.Equal(4, bullets.Count);
    }

    [Fact]
    public void ApplyButton1_fires_full_cascade_when_everything_owned()
    {
        // FORWARD_GUNS + PLASMA_GUNS + MICRO_MISSLE + SpecialWeapon=PulseCannon
        // = 2 + 1 + 2 + 1 = 6.
        var inv = new Inventory();
        inv.Add(ObjType.PlasmaGuns);
        inv.Add(ObjType.MicroMissile);
        inv.Add(ObjType.PulseCannon);  // SpecialW=true → EquippedSpecial set to PulseCannon
        var ps = new PlayerShooter(inv);
        var bullets = ps.ApplyButton1(160, 176, 3);
        Assert.Equal(6, bullets.Count);
    }

    [Fact]
    public void Reset_clears_all_cooldowns()
    {
        var ps = new PlayerShooter();
        ps.Shoot(ObjType.ForwardGuns, 160, 176, 3, new List<BulletLogic>());
        Assert.True(ps.GetCooldown(ObjType.ForwardGuns) > 0);
        ps.Reset();
        Assert.Equal(0, ps.GetCooldown(ObjType.ForwardGuns));
    }

    [Fact]
    public void Shoot_PLASMA_GUNS_spawns_one_bullet_at_center()
    {
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        ps.Shoot(ObjType.PlasmaGuns, 160, 176, 3, sink);
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
    public void Turret_with_no_air_enemies_does_not_fire_but_keeps_cooldown()
    {
        // SHOTS.C:650-652 arms lib->cur_shoot = shoot_rate BEFORE the enemy
        // lookup. The S_TURRET no-air-enemy branch (SHOTS.C:811-818) only does
        // SHOTS_Remove + SND_Patch(FX_NOSHOOT) + break — it never clears
        // cur_shoot, so the 6-tick gate stays armed exactly like the MiniGun
        // no-target branch below. Resetting it to 0 lets the player re-attempt
        // a turret shot every tick, shifting the shared per-wave RNG stream
        // (each turret fire consumes turret.aim.x/y).
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        Assert.False(ps.Shoot(ObjType.Turret, 160, 176, 3, sink, enemies: null));
        Assert.Empty(sink);
        Assert.Equal(ShotLib.Get(ObjType.Turret).ShootRate, ps.GetCooldown(ObjType.Turret));
    }

    [Fact]
    public void MiniGun_with_no_visible_enemy_keeps_cooldown()
    {
        // SHOTS.C:637-642 sets lib->cur_shoot before ENEMY_GetRandom().
        // SHOTS.C:769-773 removes the temporary shot when no enemy exists,
        // but it does not clear lib->cur_shoot.
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();

        Assert.False(ps.Shoot(ObjType.MiniGun, 160, 176, 3, sink, enemies: null));

        Assert.Empty(sink);
        Assert.Equal(ShotLib.Get(ObjType.MiniGun).ShootRate, ps.GetCooldown(ObjType.MiniGun));
    }

    [Fact]
    public void MiniGun_does_not_target_top_edge_flush_enemy_like_c_onscreen()
    {
        // ENEMY.C populates onscreen[] only when `sprite->y + sprite->height > 0`.
        // A top-edge flush sprite (sum == 0) is not eligible until the next
        // iter. This is the full_demo iter-63 MiniGun early-fire divergence.
        var flushEnemy = StaticEnemy(x: 100, y: -24, width: 32, height: 24);
        var sink = new List<BulletLogic>();
        var ps = new PlayerShooter();

        Assert.False(ps.Shoot(ObjType.MiniGun, 160, 176, 3, sink, new[] { flushEnemy }, new System.Random(1)));
        Assert.Empty(sink);
        Assert.Equal(ShotLib.Get(ObjType.MiniGun).ShootRate, ps.GetCooldown(ObjType.MiniGun));
    }

    [Fact]
    public void MiniGun_can_target_pending_removal_enemy_from_c_onscreen_snapshot()
    {
        // C ENEMY_GetRandom samples onscreen[] from the ENEMY_Think snapshot.
        // A sprite killed later in SHOTS_Think can still be in that snapshot
        // until ENEMY_Think removes it, so pending-removal enemies remain
        // valid MiniGun targets during that window.
        var pending = StaticEnemy(x: 100, y: 20, width: 32, height: 24);
        pending.TakeDamage(99, deferRemovalForDump: true);
        var sink = new List<BulletLogic>();
        var ps = new PlayerShooter();

        Assert.True(ps.Shoot(ObjType.MiniGun, 160, 176, 3, sink, new[] { pending }, new System.Random(1)));
        Assert.Single(sink);
    }

    [Fact]
    public void MiniGun_deterministic_flag_uses_middle_enemy_midpoint_without_rng()
    {
        string? previous = Environment.GetEnvironmentVariable("RAPTOR_DETERMINISTIC_MINIGUN");
        string? previousGlobal = Environment.GetEnvironmentVariable("RAPTOR_DETERMINISTIC_RNG");
        Environment.SetEnvironmentVariable("RAPTOR_DETERMINISTIC_MINIGUN", "1");
        Environment.SetEnvironmentVariable("RAPTOR_DETERMINISTIC_RNG", null);
        try
        {
            var enemies = new[]
            {
                StaticEnemy(x: 40, y: 20, width: 32, height: 24),
                StaticEnemy(x: 100, y: 20, width: 32, height: 24),
                StaticEnemy(x: 180, y: 20, width: 32, height: 24),
            };
            var sink = new List<BulletLogic>();
            var ps = new PlayerShooter();
            var rng = new System.Random(1234);

            Assert.True(ps.Shoot(ObjType.MiniGun, 160, 176, 3, sink, enemies, rng));

            Assert.Equal(new System.Random(1234).Next(), rng.Next());
            Assert.Single(sink);

            var expected = BulletLogic.PlayerAimedAt(
                x: 160, y: 176,
                x2: 100 + 16 - 1,
                y2: 20 + 12 + 12 - 1,
                initSpeed: ShotLib.Get(ObjType.MiniGun).Speed,
                maxSpeed: ShotLib.Get(ObjType.MiniGun).MaxSpeed,
                hlx: ShotLib.Get(ObjType.MiniGun).Hlx,
                hly: ShotLib.Get(ObjType.MiniGun).Hly,
                damage: ShotLib.Get(ObjType.MiniGun).Hits);

            sink[0].Tick();
            expected.Tick();

            Assert.Equal(expected.X, sink[0].X);
            Assert.Equal(expected.Y, sink[0].Y);
            Assert.Equal(expected.Mx, sink[0].Mx);
            Assert.Equal(expected.My, sink[0].My);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAPTOR_DETERMINISTIC_MINIGUN", previous);
            Environment.SetEnvironmentVariable("RAPTOR_DETERMINISTIC_RNG", previousGlobal);
        }
    }

    [Fact]
    public void Deterministic_rng_flag_makes_weapon_rng_return_midpoints_without_advancing_rng()
    {
        string? previous = Environment.GetEnvironmentVariable("RAPTOR_DETERMINISTIC_RNG");
        Environment.SetEnvironmentVariable("RAPTOR_DETERMINISTIC_RNG", "1");
        try
        {
            var enemies = new[]
            {
                StaticEnemy(x: 40, y: 20, width: 32, height: 24),
                StaticEnemy(x: 100, y: 20, width: 32, height: 24),
                StaticEnemy(x: 180, y: 20, width: 32, height: 24),
            };
            var sink = new List<BulletLogic>();
            var ps = new PlayerShooter();
            var rng = new System.Random(1234);

            Assert.True(ps.Shoot(ObjType.MiniGun, 160, 176, 3, sink, enemies, rng));

            Assert.Equal(new System.Random(1234).Next(), rng.Next());
            Assert.Single(sink);

            var expected = BulletLogic.PlayerAimedAt(
                x: 160, y: 176,
                x2: 100 + 16 - 1,
                y2: 20 + 12 + 12 - 1,
                initSpeed: ShotLib.Get(ObjType.MiniGun).Speed,
                maxSpeed: ShotLib.Get(ObjType.MiniGun).MaxSpeed,
                hlx: ShotLib.Get(ObjType.MiniGun).Hlx,
                hly: ShotLib.Get(ObjType.MiniGun).Hly,
                damage: ShotLib.Get(ObjType.MiniGun).Hits);

            sink[0].Tick();
            expected.Tick();

            Assert.Equal(expected.Mx, sink[0].Mx);
            Assert.Equal(expected.My, sink[0].My);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAPTOR_DETERMINISTIC_RNG", previous);
        }
    }

    [Fact]
    public void MiniGun_deterministic_top_edge_pair_chooses_c_midpoint_target()
    {
        // First deterministic full_demo diff: both games have the same two
        // visible helicopters after ENEMY_Think, but C's random(2) midpoint
        // selects onscreen[1]. From player center (156,157), targeting the
        // second enemy at x=16,y=-22 produces move.x=148 after one tick.
        string? previous = Environment.GetEnvironmentVariable("RAPTOR_DETERMINISTIC_RNG");
        Environment.SetEnvironmentVariable("RAPTOR_DETERMINISTIC_RNG", "1");
        try
        {
            var enemies = new[]
            {
                StaticEnemy(x: 80, y: -22, width: 32, height: 24),
                StaticEnemy(x: 16, y: -22, width: 32, height: 24),
            };
            var sink = new List<BulletLogic>();
            var ps = new PlayerShooter();

            Assert.True(ps.Shoot(ObjType.MiniGun, 156, 157, 3, sink, enemies, new System.Random(1234)));
            Assert.Single(sink);

            sink[0].Tick();

            Assert.Equal(148, sink[0].Mx);
            Assert.Equal(148, sink[0].My);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAPTOR_DETERMINISTIC_RNG", previous);
        }
    }

    [Fact]
    public void ForwardLaser_spawns_two_VerticalBeams_with_lib_NumFrames_life()
    {
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        Assert.True(ps.Shoot(ObjType.ForwardLaser, 160, 176, 3, sink));
        Assert.Equal(2, sink.Count);
        Assert.All(sink, b => Assert.True(b.IsBeam));
        Assert.All(sink, b => Assert.True(b.BeamDamages));
    }

    [Fact]
    public void DeathRay_spawns_one_VerticalBeam()
    {
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        Assert.True(ps.Shoot(ObjType.DeathRay, 160, 176, 3, sink));
        Assert.Single(sink);
        Assert.True(sink[0].IsBeam);
        Assert.True(sink[0].BeamDamages);
    }

    [Fact]
    public void Beam_despawns_after_its_life_ticks()
    {
        // FORWARD_LASER lib.NumFrames = 4 → beam ticks 4 times before despawn.
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        ps.Shoot(ObjType.ForwardLaser, 160, 176, 3, sink);
        var beam = sink[0];
        beam.Tick(); Assert.True(beam.Alive);
        beam.Tick(); Assert.True(beam.Alive);
        beam.Tick(); Assert.True(beam.Alive);
        beam.Tick(); Assert.False(beam.Alive);
    }

    [Fact]
    public void LineBeam_lives_one_tick_only()
    {
        // C's TURRET is an instant 1-iter line; we model it as life=1.
        var b = BulletLogic.LineBeam(x: 100, y: 50, damage: 5);
        Assert.True(b.IsBeam);
        Assert.False(b.BeamDamages);   // damage already applied at spawn upstream
        b.Tick();
        Assert.False(b.Alive);
    }

    // ── HitType tagging ──────────────────────────────────────────────────────

    [Fact]
    public void Forward_guns_bullets_carry_HitType_All()
    {
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        ps.Shoot(ObjType.ForwardGuns, 160, 176, 3, sink);
        Assert.All(sink, b => Assert.Equal(HitType.All, b.HitType));
    }

    [Fact]
    public void Plasma_bullets_carry_HitType_Air()
    {
        // SHOTS.C lib->ht for PLASMA_GUNS = S_AIR — must NOT hit ground enemies.
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        ps.Shoot(ObjType.PlasmaGuns, 160, 176, 3, sink);
        Assert.All(sink, b => Assert.Equal(HitType.Air, b.HitType));
    }

    [Fact]
    public void GrdMissile_bullets_carry_HitType_Ground()
    {
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        ps.Shoot(ObjType.GrdMissile, 160, 176, 3, sink);
        Assert.All(sink, b => Assert.Equal(HitType.Ground, b.HitType));
    }

    [Fact]
    public void MicroMissile_bullets_carry_HitType_GrAll()
    {
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        ps.Shoot(ObjType.MicroMissile, 160, 176, 3, sink);
        Assert.All(sink, b => Assert.Equal(HitType.GrAll, b.HitType));
    }

    [Fact]
    public void Bomb_bullets_carry_HitType_GTile()
    {
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        ps.Shoot(ObjType.Bomb, 160, 176, 3, sink);
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
    public void GrantWeapon_specials_keep_existing_SpecialWeapon()
    {
        var ps = new PlayerShooter();
        Assert.True(ps.GrantWeapon(3 /* S_DUMB_MISSLE */));
        Assert.Equal(ObjType.DumbMissile, ps.SpecialWeapon);
        // C OBJS_Add only selects a special if plr.sweapon is EMPTY.
        // S_MEGA_BOMB is not selectable (`specialw=FALSE`), so it does not
        // replace the active special.
        Assert.True(ps.GrantWeapon(11 /* S_MEGA_BOMB */));
        Assert.Equal(1, ps.MegaBombCount);
        Assert.Equal(ObjType.DumbMissile, ps.SpecialWeapon);
    }

    [Fact]
    public void MegaBomb_inventory_counts_grants_and_consumes()
    {
        var ps = new PlayerShooter();

        Assert.False(ps.ConsumeMegaBomb());
        Assert.True(ps.GrantWeapon(11 /* S_MEGA_BOMB */));
        Assert.True(ps.GrantWeapon(11 /* S_MEGA_BOMB */));
        Assert.Equal(2, ps.MegaBombCount);

        Assert.True(ps.ConsumeMegaBomb());
        Assert.Equal(1, ps.MegaBombCount);
        Assert.True(ps.ConsumeMegaBomb());
        Assert.Equal(0, ps.MegaBombCount);
        Assert.False(ps.ConsumeMegaBomb());
    }

    // ── Owned-specials + slot selection (OBJS_MakeSpecial port) ──────────────

    [Fact]
    public void GrantWeapon_special_adds_to_OwnedSpecials_and_sets_active()
    {
        var ps = new PlayerShooter();
        Assert.True(ps.GrantWeapon(3));  // DumbMissile
        Assert.Contains(ObjType.DumbMissile, ps.OwnedSpecials);
        Assert.Equal(ObjType.DumbMissile, ps.SpecialWeapon);

        Assert.True(ps.GrantWeapon(11));  // MegaBomb
        Assert.Equal(1, ps.MegaBombCount);
        Assert.DoesNotContain(ObjType.MegaBomb, ps.OwnedSpecials);
        Assert.Contains(ObjType.DumbMissile, ps.OwnedSpecials);
        Assert.Equal(ObjType.DumbMissile, ps.SpecialWeapon);       // first active sticks
    }

    [Fact]
    public void SelectSpecial_succeeds_only_for_owned_types()
    {
        var ps = new PlayerShooter();
        ps.GrantWeapon(3);   // DumbMissile
        ps.GrantWeapon(4);   // MiniGun
        // Active is MiniGun (last granted). Switching to owned DumbMissile works.
        Assert.True(ps.SelectSpecial(ObjType.DumbMissile));
        Assert.Equal(ObjType.DumbMissile, ps.SpecialWeapon);
        // Switching to a non-owned special fails and leaves SpecialWeapon alone.
        Assert.False(ps.SelectSpecial(ObjType.MegaBomb));
        Assert.Equal(ObjType.DumbMissile, ps.SpecialWeapon);
    }

    [Fact]
    public void CycleSpecial_advances_through_owned_specials_and_wraps()
    {
        var ps = new PlayerShooter();
        ps.GrantWeapon((int)ObjType.MiniGun);
        ps.GrantWeapon((int)ObjType.GrdMissile);
        ps.GrantWeapon((int)ObjType.DeathRay);

        ps.CycleSpecial();
        Assert.Equal(ObjType.GrdMissile, ps.SpecialWeapon);

        ps.CycleSpecial();
        Assert.Equal(ObjType.DeathRay, ps.SpecialWeapon);

        ps.CycleSpecial();
        Assert.Equal(ObjType.MiniGun, ps.SpecialWeapon);
    }

    [Fact]
    public void Demo_game0_shareware_loadout_cycles_only_available_specials()
    {
        var ps = new PlayerShooter();

        DemoLoadout.Apply(ps, game: 0, registered: false);

        Assert.Contains(ObjType.MiniGun, ps.OwnedSpecials);
        Assert.Contains(ObjType.AirMissile, ps.OwnedSpecials);
        Assert.DoesNotContain(ObjType.Turret, ps.OwnedSpecials);
        Assert.DoesNotContain(ObjType.DeathRay, ps.OwnedSpecials);
        Assert.Equal(ObjType.AirMissile, ps.SpecialWeapon);

        ps.CycleSpecial();
        Assert.Equal(ObjType.MiniGun, ps.SpecialWeapon);

        ps.CycleSpecial();
        Assert.Equal(ObjType.AirMissile, ps.SpecialWeapon);
    }

    [Fact]
    public void AirMissile_bullets_keep_logical_weapon_but_dump_c_lib_type_bug()
    {
        // C SHOTS_Init accidentally stores S_MISSLE_PODS in
        // shot_lib[S_AIR_MISSLE].type. Gameplay still uses the AirMissile lib
        // entry (speed/rate/hit type), but parity dumps print lib->type.
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();

        Assert.True(ps.Shoot(ObjType.AirMissile, 100, 120, playerPic: 3, sink));

        Assert.All(sink, b =>
        {
            Assert.Equal(ObjType.AirMissile, b.PlayerWeapon);
            Assert.Equal(ObjType.MissilePods, b.CWeaponTypeForDump);
        });
    }

    [Fact]
    public void Reset_clears_cooldowns_but_not_inventory()
    {
        // Reset() only clears per-weapon cooldowns (SHOTS_Init). The Inventory
        // lifetime is owned by WaveController and persists across waves/resets.
        // Test that cooldowns are cleared and inventory state is untouched.
        var inv = new Inventory();
        inv.Add(ObjType.PlasmaGuns);
        inv.Add(ObjType.MicroMissile);
        var ps = new PlayerShooter(inv);
        ps.GrantWeapon(3);   // DumbMissile → owned in inv
        ps.GrantWeapon(5);   // Turret → owned in inv
        Assert.NotEmpty(ps.OwnedSpecials);
        // Fire to arm a cooldown, then Reset.
        ps.Shoot(ObjType.ForwardGuns, 160, 176, 3, new List<BulletLogic>());
        Assert.True(ps.GetCooldown(ObjType.ForwardGuns) > 0);
        ps.Reset();
        // Cooldowns cleared.
        Assert.Equal(0, ps.GetCooldown(ObjType.ForwardGuns));
        // Inventory state preserved through reset (WaveController owns lifetime).
        Assert.True(ps.HasPlasmaGuns);
        Assert.True(ps.HasMicroMissile);
        Assert.NotEmpty(ps.OwnedSpecials);
        Assert.NotNull(ps.SpecialWeapon);
    }

    [Fact]
    public void PlaythroughDriver_KeyToSpecial_maps_RAP_C_SC_keys()
    {
        // RAP.C:955-996. Cross-check the full SC_1..SC_MINUS table.
        Assert.Equal(ObjType.DumbMissile,  Raptor.Test.PlaythroughDriver.KeyToSpecial("1"));
        Assert.Equal(ObjType.MiniGun,      Raptor.Test.PlaythroughDriver.KeyToSpecial("2"));
        Assert.Equal(ObjType.Turret,       Raptor.Test.PlaythroughDriver.KeyToSpecial("3"));
        Assert.Equal(ObjType.MissilePods,  Raptor.Test.PlaythroughDriver.KeyToSpecial("4"));
        Assert.Equal(ObjType.AirMissile,   Raptor.Test.PlaythroughDriver.KeyToSpecial("5"));
        Assert.Equal(ObjType.GrdMissile,   Raptor.Test.PlaythroughDriver.KeyToSpecial("6"));
        Assert.Equal(ObjType.Bomb,         Raptor.Test.PlaythroughDriver.KeyToSpecial("7"));
        Assert.Equal(ObjType.EnergyGrab,   Raptor.Test.PlaythroughDriver.KeyToSpecial("8"));
        Assert.Equal(ObjType.PulseCannon,  Raptor.Test.PlaythroughDriver.KeyToSpecial("9"));
        Assert.Equal(ObjType.DeathRay,     Raptor.Test.PlaythroughDriver.KeyToSpecial("0"));
        Assert.Equal(ObjType.ForwardLaser, Raptor.Test.PlaythroughDriver.KeyToSpecial("Minus"));
        // Non-numeric / unsupported keys → null.
        Assert.Null(Raptor.Test.PlaythroughDriver.KeyToSpecial("Up"));
        Assert.Null(Raptor.Test.PlaythroughDriver.KeyToSpecial("Return"));
        Assert.Null(Raptor.Test.PlaythroughDriver.KeyToSpecial(""));
    }

    [Fact]
    public void PlaythroughDriver_IsFireKey_accepts_C_setup_A_alias()
    {
        Assert.True(Raptor.Test.PlaythroughDriver.IsFireKey("A"));
        Assert.True(Raptor.Test.PlaythroughDriver.IsFireKey("Fire"));
        Assert.True(Raptor.Test.PlaythroughDriver.IsFireKey("Ctrl"));

        Assert.False(Raptor.Test.PlaythroughDriver.IsFireKey("Up"));
        Assert.False(Raptor.Test.PlaythroughDriver.IsFireKey("Return"));
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


    // ── DUMB_MISSLE delayflag + MEGA_BOMB detonation (shot_done dispatch) ────

    [Fact]
    public void DumbMissile_bullets_are_delayed_and_tagged()
    {
        // SHOTS.C:739 sets cur->delayflag = lib->delayflag = TRUE for DUMB_MISSLE.
        // WaveController.HandleShotDone uses both Delayed and PlayerWeapon.
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        var rng = new System.Random(42);
        Assert.True(ps.Shoot(ObjType.DumbMissile, 160, 176, 3, sink, rng: rng));
        Assert.Equal(2, sink.Count);
        Assert.All(sink, b => Assert.True(b.Delayed));
        Assert.All(sink, b => Assert.Equal(ObjType.DumbMissile, b.PlayerWeapon));
    }

    [Fact]
    public void MegaBomb_bullet_is_tagged_with_PlayerWeapon()
    {
        // MegaBomb has lib->delayflag=FALSE; WaveController dispatches the
        // detonation effect on PlayerWeapon==MegaBomb after reaching target.
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        Assert.True(ps.Shoot(ObjType.MegaBomb, 160, 176, 3, sink));
        Assert.Single(sink);
        Assert.False(sink[0].Delayed);
        Assert.Equal(ObjType.MegaBomb, sink[0].PlayerWeapon);
    }

    [Fact]
    public void Bresenham_bullet_sets_ReachedTarget_on_loop_completion()
    {
        // Aim at (105, 50) from (100, 50) → 5 horizontal steps + the pre-advance.
        // After enough Ticks the bullet must report ReachedTarget=true and
        // STAY alive (default-remove dispatch happens in WaveController).
        var b = BulletLogic.AimedAt(BulletKind.Player, x: 100, y: 50,
            x2: 105, y2: 50, initSpeed: 1, maxSpeed: 1, damage: 1);
        Assert.False(b.ReachedTarget);
        for (int i = 0; i < 10 && !b.ReachedTarget; i++) b.Tick();
        Assert.True(b.ReachedTarget);
        Assert.True(b.Alive);   // BulletLogic does NOT auto-kill on reach
    }

    [Fact]
    public void ReInitBresenhamTarget_retargets_and_clears_done()
    {
        // After reach, ReInitBresenhamTarget continues toward a new target
        // and clears the done flag (mirrors C InitMobj(&move) in shot_done).
        var b = BulletLogic.AimedAt(BulletKind.Player, x: 100, y: 50,
            x2: 100, y2: 55, initSpeed: 1, maxSpeed: 1, damage: 1);
        for (int i = 0; i < 8 && !b.ReachedTarget; i++) b.Tick();
        Assert.True(b.ReachedTarget);
        int myAfterReach = b.My;
        // Re-target upward.
        b.ReInitBresenhamTarget(100, 0);
        Assert.False(b.ReachedTarget);
        b.Tick();
        Assert.True(b.Alive);
        Assert.True(b.My < myAfterReach);   // moving upward toward y=0
    }

    // ── Beam fplrx/fplry player-follow tracking ─────────────────────────────

    [Fact]
    public void VerticalBeam_tracks_player_translation_via_fplr()
    {
        // SHOTS.C:1090-1098 — fplrx/fplry beam offsets by (playerCx - startx).
        // Spawn at (160, 50) with player at (160, 176). When player moves to
        // (170, 180), beam should sit at (170, 54).
        var b = BulletLogic.VerticalBeam(x: 160, y: 50, life: 4, damage: 10,
                                         startPlayerX: 160, startPlayerY: 176);
        Assert.True(b.TracksPlayer);
        b.ApplyFplr(170, 180);
        Assert.Equal(170, b.X);  // 160 + (170 - 160) = 170
        Assert.Equal(54,  b.Y);  // 50  + (180 - 176) = 54
    }

    [Fact]
    public void LineBeam_does_not_track_player()
    {
        // SHOTS.C: TURRET lib->fplrx = fplry = FALSE — the line bullet stays
        // where it was spawned (at the enemy it just hit).
        var b = BulletLogic.LineBeam(x: 100, y: 50, damage: 5);
        Assert.False(b.TracksPlayer);
        // Calling ApplyFplr is safe but a no-op when neither flag is set.
        b.ApplyFplr(999, 999);
        Assert.Equal(100, b.X);
        Assert.Equal(50,  b.Y);
    }

    [Fact]
    public void ForwardLaser_beams_spawn_with_tracking_anchored_to_player_center()
    {
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        ps.Shoot(ObjType.ForwardLaser, playerCx: 160, playerCy: 176, playerPic: 3, sink);
        Assert.Equal(2, sink.Count);
        Assert.All(sink, b => Assert.True(b.TracksPlayer));
        // Player moves +5 right; both beams should shift by 5.
        int b0xBefore = sink[0].X;
        int b1xBefore = sink[1].X;
        sink[0].ApplyFplr(165, 176);
        sink[1].ApplyFplr(165, 176);
        Assert.Equal(b0xBefore + 5, sink[0].X);
        Assert.Equal(b1xBefore + 5, sink[1].X);
    }

    [Fact]
    public void PlayerStraight_reports_reached_when_move_passes_top_edge()
    {
        // SHOTS.C:1262-1265 — `if (move.y < 0) move.done = TRUE, doneflag = TRUE`.
        // That happens at the end of SHOTS_Think, after the display position
        // for the current iter was snapped, so the bullet remains live until
        // the next shot_done pass removes it.
        var b = BulletLogic.PlayerStraight(spawnX: 160, spawnY: 4,
            initSpeed: 8, maxSpeed: 16, hlx: 4, hly: 4, damage: 1);
        // Tick: snapshot Y=0; speed->9; move.y = 4-9 = -5 -> move.done.
        b.Tick();
        Assert.True(b.Alive);
        Assert.True(b.ReachedTarget);
        Assert.True(b.PendingShotDone);
        Assert.False(b.DeferredDoneFlag);
        Assert.True(b.DoneFlagForDump);
    }

    [Fact]
    public void Enemy_hit_doneflag_defers_shot_done_without_setting_reached()
    {
        // SHOTS.C enemy-hit paths set shot->doneflag=TRUE after the earlier
        // doneflag check for that pass has already run. The bullet remains in
        // the current display/dump with move.done still false, then shot_done
        // removes it next pass.
        var b = BulletLogic.PlayerStraight(spawnX: 160, spawnY: 176,
            initSpeed: 8, maxSpeed: 16, hlx: 4, hly: 4, damage: 1);

        b.MarkDoneFlagForNextPass();

        Assert.True(b.Alive);
        Assert.False(b.ReachedTarget);
        Assert.True(b.PendingShotDone);
        Assert.True(b.DeferredDoneFlag);
        Assert.True(b.DoneFlagForDump);
    }

    private static EnemyLogic StaticEnemy(int x, int y, int width, int height)
    {
        return new EnemyLogic(new SpriteMeta
        {
            Hits = 7,
            NumFlight = 0,
            Width = width,
            Height = height,
        }, x, y);
    }
}
