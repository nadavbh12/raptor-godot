using System.Collections.Generic;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Raptor.Sim;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Raptor.Sim.Player;
using Raptor.Sim.Shots;
using Xunit;

namespace Raptor.Tests;

public class WaveControllerTests
{
    /// <summary>
    /// Subclass WavePhaseScheduler (pure C#, no Godot runtime needed) to record
    /// the order in which phase methods are called by Tick().
    /// </summary>
    private class RecordingScheduler : WavePhaseScheduler
    {
        public readonly List<string> Order = new();
        public override void TickInput()            => Order.Add("Input");
        public override void TickSpawn()            => Order.Add("Spawn");
        public override void TickMovement()         => Order.Add("Movement");
        public override void TickCollisionCollect() => Order.Add("CollisionCollect");
        public override void TickCollisionResolve() => Order.Add("CollisionResolve");
        public override void TickCleanup()          => Order.Add("Cleanup");
        public override void TickHud()              => Order.Add("Hud");
        public override void TickCheckpoint()       => Order.Add("Checkpoint");
    }

    [Fact]
    public void Phases_run_in_documented_order_per_tick()
    {
        var rec = new RecordingScheduler();
        rec.Tick();
        Assert.Equal(new[] {
            "Input", "Spawn", "Movement",
            "CollisionCollect", "CollisionResolve",
            "Cleanup", "Hud", "Checkpoint"
        }, rec.Order);
    }

    [Fact]
    public void Default_seed_is_1024_times_wave_num()
    {
        // Verify deterministic per-wave seed formula: seed = 1024 * waveNum.
        Assert.Equal(2048ul, WaveRng.ComputeSeed(2));
        Assert.Equal(1024ul, WaveRng.ComputeSeed(1));
        Assert.Equal(5120ul, WaveRng.ComputeSeed(5));
    }

    [Fact]
    public void Seed_override_string_is_applied_when_parseable()
    {
        // When a seed override is provided, ComputeSeed returns it instead.
        Assert.Equal(9999ul, WaveRng.ComputeSeed(2, "9999"));
    }

    [Fact]
    public void Empty_seed_override_falls_back_to_default()
    {
        Assert.Equal(2048ul, WaveRng.ComputeSeed(2, ""));
        Assert.Equal(2048ul, WaveRng.ComputeSeed(2, null));
    }

    [Fact]
    public void NewShooterRng_is_deterministic_for_a_given_seed()
    {
        // Same Godot seed → identical shooter RNG stream (replay-stable scatter/picks).
        var a = WaveRng.NewShooterRng(1024);
        var b = WaveRng.NewShooterRng(1024);
        for (int i = 0; i < 8; i++)
            Assert.Equal(a.Next(100), b.Next(100));
    }

    [Fact]
    public void Enemy_shot_sound_consumes_c_random_pitch_slot()
    {
        var actual = new System.Random(1234);
        WaveController.ConsumeEnemyShotSoundRandomForParity(actual, EnemyShotType.AtDown);

        var expected = new System.Random(1234);
        PlayerShooter.NextRandom(expected, 40, "expected.fx_enemyshot");

        Assert.Equal(PlayerShooter.NextRandom(expected, 1000, "expected.next"),
                     PlayerShooter.NextRandom(actual, 1000, "actual.next"));
    }

    [Fact]
    public void Bonus_overlapping_player_is_not_collectible_when_shield_zero()
    {
        // BONUS.C:244 gates pickup on OBJS_GetAmt(S_ENERGY) > 0 — a dead/depleted
        // ship (shield 0 => !Alive) cannot collect an overlapping bonus.
        int plx = 100, ply = 100;
        var bonus = new Raptor.Sim.Bonus.BonusLogic(objType: 16, x: plx + 8, y: ply + 8);
        Assert.True(bonus.CanBePickedUpBy(plx, ply));   // overlap holds

        Assert.False(WaveController.BonusCollectible(playerAlive: false, bonus, plx, ply));
        Assert.True(WaveController.BonusCollectible(playerAlive: true, bonus, plx, ply));
    }

    [Fact]
    public void Shield_recharge_is_suppressed_during_death_and_endwave()
    {
        const int charge = 96;

        // Normal play: heals when think_cnt crosses CHARGE_SHIELD, resets to 0.
        var (tc, heal) = ShieldHudController.ShieldRechargeStep(
            charge, diff: 0, charge, deathActive: false, endWaveActive: false, objUsed: false);
        Assert.Equal(0, tc);
        Assert.True(heal);

        // OBJECTS.C:1365 — during the death-explosion countdown the heal is
        // suppressed (startendwave != EMPTY) so a recharge cannot revive a dead
        // ship. think_cnt still resets, matching OBJS_Think.
        (tc, heal) = ShieldHudController.ShieldRechargeStep(
            charge, diff: 0, charge, deathActive: true, endWaveActive: false, objUsed: false);
        Assert.Equal(0, tc);
        Assert.False(heal);

        // Same suppression during the end-of-wave fly-off.
        (_, heal) = ShieldHudController.ShieldRechargeStep(
            charge, diff: 0, charge, deathActive: false, endWaveActive: true, objUsed: false);
        Assert.False(heal);

        // Below threshold: just increments.
        (tc, heal) = ShieldHudController.ShieldRechargeStep(
            10, diff: 0, charge, deathActive: false, endWaveActive: false, objUsed: false);
        Assert.Equal(11, tc);
        Assert.False(heal);

        // High difficulty (diff >= 3) never heals (existing behavior preserved).
        (_, heal) = ShieldHudController.ShieldRechargeStep(
            charge, diff: 3, charge, deathActive: false, endWaveActive: false, objUsed: false);
        Assert.False(heal);
    }

    [Fact]
    public void Shield_recharge_is_suppressed_while_firing_objuse()
    {
        // C OBJS_Use (firing FORWARD_GUNS while BUT_1 held, RAP.C:1005-1013) sets
        // objuse_flag=TRUE and think_cnt=0; OBJS_Think (OBJECTS.C:1393-1397) then
        // consumes the flag and returns WITHOUT incrementing — so an iter the
        // player fires resets the recharge counter and cannot heal.
        const int charge = 96;

        // On the tick that WOULD cross CHARGE_SHIELD, firing suppresses the heal
        // and resets the counter to 0.
        var (tc, heal) = ShieldHudController.ShieldRechargeStep(
            charge, diff: 1, charge, deathActive: false, endWaveActive: false, objUsed: true);
        Assert.Equal(0, tc);
        Assert.False(heal);

        // Same counter, NOT firing → it crosses and heals (the contrast case).
        (tc, heal) = ShieldHudController.ShieldRechargeStep(
            charge, diff: 1, charge, deathActive: false, endWaveActive: false, objUsed: false);
        Assert.Equal(0, tc);
        Assert.True(heal);

        // Mid-counter firing also resets to 0 (continuous fire keeps it pinned low).
        (tc, heal) = ShieldHudController.ShieldRechargeStep(
            40, diff: 1, charge, deathActive: false, endWaveActive: false, objUsed: true);
        Assert.Equal(0, tc);
        Assert.False(heal);
    }

    [Fact]
    public void SubEnergy_damage_gate_mirrors_OBJS_SubEnergy_preconditions()
    {
        // OBJECTS.C:1227-1235 OBJS_SubEnergy pre-drain gates (godmode omitted —
        // no input path in the port):
        //   startendwave != EMPTY        → return 0 (no damage during end-wave fly-off)
        //   curplr_diff == DIFF_0 && amt>1 → amt >>= 1 (training-mode halving)

        // Normal play (Normal difficulty, mid-wave): damage passes through unchanged.
        Assert.Equal(10, WaveController.GateSubEnergyDamage(10, endWaveActive: false, curPlayerDiff: 2));

        // End-of-wave fly-off (startendwave != EMPTY): all damage suppressed.
        Assert.Equal(0, WaveController.GateSubEnergyDamage(10, endWaveActive: true, curPlayerDiff: 2));

        // DIFF_0 (training) halves amounts > 1 (amt >> 1).
        Assert.Equal(5, WaveController.GateSubEnergyDamage(10, endWaveActive: false, curPlayerDiff: 0));

        // DIFF_0 leaves amt <= 1 untouched (the amt > 1 guard).
        Assert.Equal(1, WaveController.GateSubEnergyDamage(1, endWaveActive: false, curPlayerDiff: 0));

        // End-wave suppression takes precedence over the DIFF_0 halving.
        Assert.Equal(0, WaveController.GateSubEnergyDamage(10, endWaveActive: true, curPlayerDiff: 0));

        // Player-death countdown also arms C's startendwave (RAP.C:575-576), so
        // OBJS_SubEnergy's `startendwave != EMPTY` early-return (OBJECTS.C:1267)
        // suppresses damage during death too — not only end-wave (finding #19).
        Assert.Equal(0, WaveController.GateSubEnergyDamage(10, endWaveActive: false, curPlayerDiff: 2, deathActive: true));
        // Death suppression takes precedence over DIFF_0 halving, same as end-wave.
        Assert.Equal(0, WaveController.GateSubEnergyDamage(10, endWaveActive: false, curPlayerDiff: 0, deathActive: true));
        // deathActive defaults to false: the original 5 calls above are unaffected.
    }

    [Fact]
    public void Coconut_enemy_shot_consumes_monkey_pick_before_random_pitch()
    {
        var actual = new System.Random(1234);
        WaveController.ConsumeEnemyShotSoundRandomForParity(actual, EnemyShotType.Coconuts);

        var expected = new System.Random(1234);
        PlayerShooter.NextRandom(expected, 6, "expected.coconut.pick");
        PlayerShooter.NextRandom(expected, 40, "expected.fx_coconut");

        Assert.Equal(PlayerShooter.NextRandom(expected, 1000, "expected.next"),
                     PlayerShooter.NextRandom(actual, 1000, "actual.next"));
    }

    [Fact]
    public void Exp_energy_enemy_death_drops_itembuy6_money_bonus()
    {
        Assert.Equal(23, EnemyDeathEffects.BonusForExplosionType(8));
        Assert.Null(EnemyDeathEffects.BonusForExplosionType(2));
    }

    [Fact]
    public void Enemy_bonus_spawn_x_includes_c_map_left_offset()
    {
        // C ENEMY.C passes sprite->x into BONUS_Add; BONUS_Add stores x + MAP_LEFT.
        Assert.Equal(160, EnemyDeathEffects.BonusSpawnXFromEnemyX(144));
    }

    [Fact]
    public void Body_crash_right_bottom_edge_uses_full_width_height_minus_one()
    {
        // Finding #27: C body-crash tests player_c{x,y} < sprite->{x2,y2} where
        // x2/y2 = sprite->{x,y} + {width,height} - 1 (ENEMY.C:877-878). Godot
        // used 2*Half{W,H}-1 = {W,H}-2 for ODD dims, dropping the right/bottom
        // interior pixel. Inert on shipped (all-even) assets; Width=Height=25
        // exposes it.
        var enemy = new EnemyLogic(new SpriteMeta
        {
            Hits = 3, NumFlight = 0, FlightType = 1, Width = 25, Height = 25,
        }, spawnX: 100, mapY: 100);
        // correct ex2/ey2 = 100+25-1 = 124; buggy = 100+2*12-1 = 123.
        // player at (123,123): correct → 123<124 true; buggy → 123<123 false.
        Assert.True(WaveController.EnemyBodyCrashContainsPlayer(enemy, 123, 123));
    }

    [Fact]
    public void Pending_removal_enemy_can_still_body_crash_before_c_removes_it()
    {
        var enemy = new EnemyLogic(new SpriteMeta
        {
            IName = "pending-crash",
            Hits = 3,
            Width = 24,
            Height = 24,
            FlightType = 1,
            NumFlight = 0,
        }, spawnX: 148, mapY: 164);
        enemy.TakeDamage(99, deferRemovalForDump: true);

        Assert.True(WaveController.EnemyBodyCrashContainsPlayer(
            enemy,
            PlayerLogic.InitX + 16,
            PlayerLogic.InitY + 16));
    }

    private sealed class CountingRandom : System.Random
    {
        public int Count;
        public override int Next(int maxValue) { Count++; return base.Next(maxValue); }
    }

    [Fact]
    public void Bonus_add_gates_mirror_c_caps()
    {
        // Finding #6. C BONUS_Add (BONUS.C:172-182): reject >= S_LAST_OBJECT(24),
        // reject S_ITEMBUY6(23) when energy_count > MAX_MONEY(9), reject when the
        // 12-slot pool is full — all BEFORE the random(16) draw.
        Assert.True(WaveController.BonusAddPasses(objType: 5, liveBonusCount: 0, energyCount: 0));
        Assert.False(WaveController.BonusAddPasses(objType: 24, liveBonusCount: 0, energyCount: 0));  // >= S_LAST_OBJECT
        Assert.False(WaveController.BonusAddPasses(objType: 23, liveBonusCount: 0, energyCount: 10)); // ITEMBUY6 over MAX_MONEY
        Assert.True(WaveController.BonusAddPasses(objType: 23, liveBonusCount: 0, energyCount: 9));   // ==MAX_MONEY allowed (C: > )
        Assert.False(WaveController.BonusAddPasses(objType: 5, liveBonusCount: 12, energyCount: 0));  // pool full
        Assert.True(WaveController.BonusAddPasses(objType: 5, liveBonusCount: 11, energyCount: 0));   // one slot left
    }

    [Fact]
    public void Body_crash_draws_two_random8_and_jitters_explosion()
    {
        // Finding #7. ENEMY.C:1114-1115 — the body-crash explosion is jittered by
        // x = player_cx + (random(8)-4), y = player_cy + (random(8)-4): two shared
        // random(8) draws, AFTER the player-damage and BEFORE the death award.
        var prev = Raptor.Sim.DeterministicRandom.Override;
        Raptor.Sim.DeterministicRandom.Override = false;
        try
        {
            var rng = new CountingRandom();
            var (x, y) = WaveController.BodyCrashExplosionPos(160, 176, rng);
            Assert.Equal(2, rng.Count);                 // exactly two random(8) draws
            Assert.InRange(x - 160, -4, 3);             // player_cx + (random(8)-4)
            Assert.InRange(y - 176, -4, 3);
        }
        finally { Raptor.Sim.DeterministicRandom.Override = prev; }
    }

    [Fact]
    public void Boss_low_health_smoke_gates_on_bossflag_hits50_and_glcnt2()
    {
        // Finding #18. ENEMY.C:1090-1093 — a boss draws low-health smoke when
        // bossflag && hits < 50 && (gl_cnt & 2). gl_cnt is incremented BEFORE
        // ENEMY_Think (RAP.C:1056) while Godot increments _gameLoopIter AFTER the
        // phase, so at the check gl_cnt == _gameLoopIter + 1 (both sides emit the
        // same iter-aligned checkpoint, so the post-increment values coincide).
        var boss = new EnemyLogic(
            new SpriteMeta { IName = "BOSS", Hits = 40, BossFlag = 1, Width = 64, Height = 48 },
            spawnX: 100, mapY: 0);                                  // Hits = 40 (< 50), not nerfed (diff 2)
        var nonBoss = new EnemyLogic(
            new SpriteMeta { IName = "SHIP", Hits = 40, BossFlag = 0 }, spawnX: 100, mapY: 0);
        var healthyBoss = new EnemyLogic(
            new SpriteMeta { IName = "BOSS", Hits = 100, BossFlag = 1, Width = 64, Height = 48 },
            spawnX: 100, mapY: 0);

        // gl_cnt & 2 is set when (gameLoopIter+1) has bit 1 set:
        Assert.True (WaveController.BossSmokeFires(boss, gameLoopIter: 1));  // gl_cnt=2 -> &2=2
        Assert.True (WaveController.BossSmokeFires(boss, gameLoopIter: 2));  // gl_cnt=3 -> &2=2
        Assert.False(WaveController.BossSmokeFires(boss, gameLoopIter: 3));  // gl_cnt=4 -> &2=0
        Assert.False(WaveController.BossSmokeFires(boss, gameLoopIter: 0));  // gl_cnt=1 -> &2=0
        Assert.False(WaveController.BossSmokeFires(nonBoss, gameLoopIter: 1));      // not a boss
        Assert.False(WaveController.BossSmokeFires(healthyBoss, gameLoopIter: 1));  // hits >= 50
    }

    [Fact]
    public void Boss_smoke_draws_two_random_over_body()
    {
        // ENEMY.C:1095-1096 — x = sprite->x + random(width); y = sprite->y +
        // random(height): two shared random() draws (inert under deterministic RNG).
        var prev = Raptor.Sim.DeterministicRandom.Override;
        Raptor.Sim.DeterministicRandom.Override = false;
        try
        {
            var boss = new EnemyLogic(
                new SpriteMeta { IName = "BOSS", Hits = 40, BossFlag = 1, Width = 64, Height = 48 },
                spawnX: 100, mapY: 0);
            var rng = new CountingRandom();
            var (x, y) = WaveController.BossSmokePos(boss, rng);
            Assert.Equal(2, rng.Count);                  // exactly two draws: random(64), random(48)
            Assert.InRange(x - boss.X, 0, 63);           // sprite->x + random(width)
            Assert.InRange(y - boss.Y, 0, 47);           // sprite->y + random(height)
        }
        finally { Raptor.Sim.DeterministicRandom.Override = prev; }
    }

    [Theory]
    [InlineData(true, false, true, true, false, true)]   // all met -> complete
    [InlineData(true, false, true, true, true, false)]   // enemies remain -> wait
    [InlineData(true, false, true, false, false, false)] // player dead -> no
    [InlineData(true, true, true, true, false, false)]   // demo replay -> no
    [InlineData(false, false, true, true, false, false)] // wave inactive -> no
    [InlineData(true, false, false, true, false, false)] // not end-wave -> no
    public void Mission_completion_waits_for_wave_and_enemies_not_explosions(
        bool waveActive,
        bool demoActive,
        bool endWave,
        bool playerAlive,
        bool enemiesRemaining,
        bool expected)
    {
        // ENEMY.C:391-392 — the end-wave countdown arms on last-enemy-removed,
        // regardless of lingering explosions (which keep rendering during fly-off).
        Assert.Equal(expected, EndWaveSequencer.ShouldCompleteMission(
            waveActive, demoActive, endWave, playerAlive, enemiesRemaining));
    }

    [Theory]
    [InlineData(0, 13)]    // EXP_AIRSMALL1 → EXPLO2_BLK
    [InlineData(1, 12)]    // EXP_AIRMED    → LGFLAK_BLK
    [InlineData(2, 12)]    // EXP_AIRLARGE  → LGFLAK_BLK
    [InlineData(5, 42)]    // EXP_GRDLARGE  → GEXPLO_BLK
    [InlineData(8, 12)]    // EXP_ENERGY    → NRGBANG_BLK
    [InlineData(10, 14)]   // EXP_AIRSMALL2 → SMFLAK_BLK
    [InlineData(100, 4)]   // smoke         → SMOKTRAL_BLK
    [InlineData(101, 9)]   // blue spark    → BSPARK_BLK
    [InlineData(102, 9)]   // orange spark  → OSPARK_BLK
    public void Explosion_culled_at_per_type_frame_count_not_flat_50(int expType, int frames)
    {
        // ANIMS.C:187-218 numframes per type — the sim entity must cull at its
        // own length (not a flat 50), matching where the renderer stops drawing.
        Assert.Equal(frames, WaveController.AnimFramesFor(expType));
    }

    [Fact]
    public void Spark_value_zero_maps_to_blue_matching_TILE_IsHit()
    {
        // Finding #28: TILE.C:511-518 — random(2) case 0 → A_BLUE_SPARK, case 1 →
        // A_ORANGE_SPARK. Godot's branch was inverted (spark != 0 ? Blue : Orange).
        // The single random(2) draw is unchanged; only the value→color flips.
        Assert.Equal(101 /*SparkBlueExpType*/, WaveController.SparkExpTypeFor(0));
        Assert.Equal(102 /*SparkOrangeExpType*/, WaveController.SparkExpTypeFor(1));
    }

    [Fact]
    public void Explosion_animation_age_uses_game_loop_iterations_not_framecount()
    {
        int startIter = WaveController.AnimationStartIterForSpawn(currentGameLoopIter: 42);

        Assert.Equal(43, startIter);
        Assert.Equal(-1, WaveController.AnimationAge(currentGameLoopIter: 42, startIter));
        Assert.Equal(0, WaveController.AnimationAge(currentGameLoopIter: 43, startIter));
        Assert.Equal(3, WaveController.AnimationAge(currentGameLoopIter: 46, startIter));
    }

    [Fact]
    public void Player_death_spawns_wing_explosions_each_countdown_tick()
    {
        var rng = new System.Random(1234);

        var explosions = PlayerDeathSequence.BuildExplosions(
            PlayerLogic.InitX,
            PlayerLogic.InitY,
            PlayerDeathSequence.EndDuration,
            rng);

        Assert.Equal(2, explosions.Count);
        Assert.Contains(explosions, e => e.ExpType == WaveController.ExpAirSmall1);
        Assert.Contains(explosions, e => e.ExpType == WaveController.ExpAirSmall2);
        Assert.All(explosions, e =>
        {
            Assert.InRange(e.X, PlayerLogic.InitX, PlayerLogic.InitX + 31);
            Assert.InRange(e.Y, PlayerLogic.InitY, PlayerLogic.InitY + 31);
        });
    }

    [Fact]
    public void Player_death_explode_tick_spawns_final_center_and_debris_burst()
    {
        var rng = new System.Random(1234);

        var explosions = PlayerDeathSequence.BuildExplosions(
            PlayerLogic.InitX,
            PlayerLogic.InitY,
            PlayerDeathSequence.EndExplode,
            rng);

        Assert.Equal(515, explosions.Count);
        Assert.Contains(explosions, e =>
            e.ExpType == WaveController.ExpAirLarge
            && e.X == PlayerLogic.InitX + 16
            && e.Y == PlayerLogic.InitY + 16);
        Assert.Equal(257, Count(explosions, WaveController.ExpAirLarge));
        int burstMed2 = 0;
        for (int i = 3; i < explosions.Count; i++)
        {
            var e = explosions[i];
            if (e.ExpType == WaveController.ExpAirMed2) burstMed2++;
            Assert.InRange(e.X, PlayerLogic.InitX - 16, PlayerLogic.InitX + 47);
            Assert.InRange(e.Y, PlayerLogic.InitY - 16, PlayerLogic.InitY + 47);
        }
        Assert.Equal(256, burstMed2);
    }

    [Fact]
    public void Player_death_draws_pitch_select_random2_while_exploding_not_at_endexplode()
    {
        // RAP.C:567-573 — while startendwave > END_EXPLODE the ship plays
        // SND_Patch(FX_AIREXPLO) at a random(2)-selected pan (30 vs 225). The
        // SND_Patch itself contributes no draw (finding #25 no-op), but the
        // explicit random(2) pan-select IS a game-logic draw on the shared stream
        // (finding #26). It is NOT drawn on the == END_EXPLODE debris frame (the
        // C `>` vs `==` split — mutually exclusive). Value discarded; only the
        // stream advance matters. Masked under deterministic RNG (no death golden).
        var prev = Raptor.Sim.DeterministicRandom.Override;
        Raptor.Sim.DeterministicRandom.Override = false;
        try
        {
            // countdown > EndExplode: 4 scatter draws + 1 pitch-select = 5.
            var exploding = new CountingRandom();
            PlayerDeathSequence.BuildExplosions(160, 176, PlayerDeathSequence.EndDuration, exploding);
            Assert.Equal(5, exploding.Count);

            // countdown == EndExplode: pitch NOT drawn — 4 scatter + debris only.
            var endExplode = new CountingRandom();
            PlayerDeathSequence.BuildExplosions(160, 176, PlayerDeathSequence.EndExplode, endExplode);
            int debris = (PlayerLogic.SpriteWidth * PlayerLogic.SpriteHeight / 2) * 2;
            Assert.Equal(4 + debris, endExplode.Count);
        }
        finally { Raptor.Sim.DeterministicRandom.Override = prev; }
    }

    private static int Count(IReadOnlyList<PlayerDeathSequence.DeathExplosion> explosions, int expType)
    {
        int count = 0;
        foreach (var e in explosions)
            if (e.ExpType == expType) count++;
        return count;
    }

    [Fact]
    public void ScoreOnAbort_returns_wave_start_score_discarding_earned()
    {
        Assert.Equal(10000u, WaveController.ScoreOnAbort(waveStartScore: 10000u, currentScore: 25000u));
    }

    [Property]
    public Property ScoreOnAbort_always_restores_wave_start_score()
    {
        // Property "Score restore": after abort, Score == wave-start score, regardless
        // of whatever was earned during the wave (currentScore). Domain: WaveController abort path.
        return Prop.ForAll<uint, uint>((waveStart, current) =>
            WaveController.ScoreOnAbort(waveStart, current) == waveStart);
    }

    [Theory]
    [InlineData(null, 1, 1)]
    [InlineData("3", 1, 3)]
    [InlineData("9", 1, 9)]
    [InlineData("0", 1, 1)]
    [InlineData("10", 1, 1)]
    [InlineData("abc", 1, 1)]
    public void StartWave_resolves_override(string? env, int defaultWave, int expected)
        => Assert.Equal(expected, WaveController.ResolveStartWave(env, defaultWave));

    [Fact]
    public void Player_missile_smoke_uses_down_drift_anim_and_tail_offset()
    {
        // SHOTS.C:1078 — a smoking player shot (S_AIR_MISSLE et al, lib->smoke)
        // spawns A_SMALL_SMOKE_DOWN every tick at (shot->x + hlx, shot->y + hly<<1).
        // MISRAT_BLK is 8x16 → hlx=4, hly<<1=16 → the puff sits at the missile tail.
        Assert.Equal((104, 66), WaveController.PlayerMissileSmokePos(100, 50));

        // A_SMALL_SMOKE_DOWN = SSMOKE_BLK+4, 5 frames (ANIMS.C:202). The sim must
        // cull the sentinel at 5 iters, not the 13-frame default that would leave
        // the puff lingering long after C has removed it.
        Assert.Equal(5, WaveController.AnimFramesFor(WaveController.SmokeDownExpType));
    }
}
