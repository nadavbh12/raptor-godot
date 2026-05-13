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
    public void Turret_with_no_air_enemies_does_not_fire_and_resets_cooldown()
    {
        // SHOTS.C:792-799 — if ENEMY_GetRandomAir returns NULL, no shot.
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        Assert.False(ps.Shoot(WeaponType.Turret, 160, 176, 3, sink, enemies: null));
        Assert.Empty(sink);
        Assert.Equal(0, ps.GetCooldown(WeaponType.Turret));
    }

    [Fact]
    public void ForwardLaser_spawns_two_VerticalBeams_with_lib_NumFrames_life()
    {
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        Assert.True(ps.Shoot(WeaponType.ForwardLaser, 160, 176, 3, sink));
        Assert.Equal(2, sink.Count);
        Assert.All(sink, b => Assert.True(b.IsBeam));
        Assert.All(sink, b => Assert.True(b.BeamDamages));
    }

    [Fact]
    public void DeathRay_spawns_one_VerticalBeam()
    {
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        Assert.True(ps.Shoot(WeaponType.DeathRay, 160, 176, 3, sink));
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
        ps.Shoot(WeaponType.ForwardLaser, 160, 176, 3, sink);
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

    // ── Owned-specials + slot selection (OBJS_MakeSpecial port) ──────────────

    [Fact]
    public void GrantWeapon_special_adds_to_OwnedSpecials_and_sets_active()
    {
        var ps = new PlayerShooter();
        Assert.True(ps.GrantWeapon(3));  // DumbMissile
        Assert.Contains(WeaponType.DumbMissile, ps.OwnedSpecials);
        Assert.Equal(WeaponType.DumbMissile, ps.SpecialWeapon);

        Assert.True(ps.GrantWeapon(11));  // MegaBomb
        Assert.Contains(WeaponType.MegaBomb, ps.OwnedSpecials);
        Assert.Contains(WeaponType.DumbMissile, ps.OwnedSpecials);   // both retained
        Assert.Equal(WeaponType.MegaBomb, ps.SpecialWeapon);          // newest active
    }

    [Fact]
    public void SelectSpecial_succeeds_only_for_owned_types()
    {
        var ps = new PlayerShooter();
        ps.GrantWeapon(3);   // DumbMissile
        ps.GrantWeapon(4);   // MiniGun
        // Active is MiniGun (last granted). Switching to owned DumbMissile works.
        Assert.True(ps.SelectSpecial(WeaponType.DumbMissile));
        Assert.Equal(WeaponType.DumbMissile, ps.SpecialWeapon);
        // Switching to a non-owned special fails and leaves SpecialWeapon alone.
        Assert.False(ps.SelectSpecial(WeaponType.MegaBomb));
        Assert.Equal(WeaponType.DumbMissile, ps.SpecialWeapon);
    }

    [Fact]
    public void Reset_clears_inventory_and_specials()
    {
        var ps = new PlayerShooter { HasPlasmaGuns = true, HasMicroMissile = true };
        ps.GrantWeapon(3);
        ps.GrantWeapon(5);
        Assert.NotEmpty(ps.OwnedSpecials);
        ps.Reset();
        Assert.Empty(ps.OwnedSpecials);
        Assert.Null(ps.SpecialWeapon);
        Assert.False(ps.HasPlasmaGuns);
        Assert.False(ps.HasMicroMissile);
    }

    [Fact]
    public void PlaythroughDriver_KeyToSpecial_maps_RAP_C_SC_keys()
    {
        // RAP.C:955-996. Cross-check the full SC_1..SC_MINUS table.
        Assert.Equal(WeaponType.DumbMissile,  Raptor.Test.PlaythroughDriver.KeyToSpecial("1"));
        Assert.Equal(WeaponType.MiniGun,      Raptor.Test.PlaythroughDriver.KeyToSpecial("2"));
        Assert.Equal(WeaponType.Turret,       Raptor.Test.PlaythroughDriver.KeyToSpecial("3"));
        Assert.Equal(WeaponType.MissilePods,  Raptor.Test.PlaythroughDriver.KeyToSpecial("4"));
        Assert.Equal(WeaponType.AirMissile,   Raptor.Test.PlaythroughDriver.KeyToSpecial("5"));
        Assert.Equal(WeaponType.GrdMissile,   Raptor.Test.PlaythroughDriver.KeyToSpecial("6"));
        Assert.Equal(WeaponType.Bomb,         Raptor.Test.PlaythroughDriver.KeyToSpecial("7"));
        Assert.Equal(WeaponType.EnergyGrab,   Raptor.Test.PlaythroughDriver.KeyToSpecial("8"));
        Assert.Equal(WeaponType.PulseCannon,  Raptor.Test.PlaythroughDriver.KeyToSpecial("9"));
        Assert.Equal(WeaponType.DeathRay,     Raptor.Test.PlaythroughDriver.KeyToSpecial("0"));
        Assert.Equal(WeaponType.ForwardLaser, Raptor.Test.PlaythroughDriver.KeyToSpecial("Minus"));
        // Non-numeric / unsupported keys → null.
        Assert.Null(Raptor.Test.PlaythroughDriver.KeyToSpecial("Up"));
        Assert.Null(Raptor.Test.PlaythroughDriver.KeyToSpecial("Return"));
        Assert.Null(Raptor.Test.PlaythroughDriver.KeyToSpecial(""));
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
        Assert.True(ps.Shoot(WeaponType.DumbMissile, 160, 176, 3, sink, rng: rng));
        Assert.Equal(2, sink.Count);
        Assert.All(sink, b => Assert.True(b.Delayed));
        Assert.All(sink, b => Assert.Equal(WeaponType.DumbMissile, b.PlayerWeapon));
    }

    [Fact]
    public void MegaBomb_bullet_is_tagged_with_PlayerWeapon()
    {
        // MegaBomb has lib->delayflag=FALSE; WaveController dispatches the
        // detonation effect on PlayerWeapon==MegaBomb after reaching target.
        var ps = new PlayerShooter();
        var sink = new List<BulletLogic>();
        Assert.True(ps.Shoot(WeaponType.MegaBomb, 160, 176, 3, sink));
        Assert.Single(sink);
        Assert.False(sink[0].Delayed);
        Assert.Equal(WeaponType.MegaBomb, sink[0].PlayerWeapon);
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
        ps.Shoot(WeaponType.ForwardLaser, playerCx: 160, playerCy: 176, playerPic: 3, sink);
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
