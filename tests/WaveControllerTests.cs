using System.Collections.Generic;
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
        var (tc, heal) = WaveController.ShieldRechargeStep(
            charge, diff: 0, charge, deathActive: false, endWaveActive: false);
        Assert.Equal(0, tc);
        Assert.True(heal);

        // OBJECTS.C:1365 — during the death-explosion countdown the heal is
        // suppressed (startendwave != EMPTY) so a recharge cannot revive a dead
        // ship. think_cnt still resets, matching OBJS_Think.
        (tc, heal) = WaveController.ShieldRechargeStep(
            charge, diff: 0, charge, deathActive: true, endWaveActive: false);
        Assert.Equal(0, tc);
        Assert.False(heal);

        // Same suppression during the end-of-wave fly-off.
        (_, heal) = WaveController.ShieldRechargeStep(
            charge, diff: 0, charge, deathActive: false, endWaveActive: true);
        Assert.False(heal);

        // Below threshold: just increments.
        (tc, heal) = WaveController.ShieldRechargeStep(
            10, diff: 0, charge, deathActive: false, endWaveActive: false);
        Assert.Equal(11, tc);
        Assert.False(heal);

        // High difficulty (diff >= 3) never heals (existing behavior preserved).
        (_, heal) = WaveController.ShieldRechargeStep(
            charge, diff: 3, charge, deathActive: false, endWaveActive: false);
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
        Assert.Equal(23, WaveController.BonusForExplosionType(8));
        Assert.Null(WaveController.BonusForExplosionType(2));
    }

    [Fact]
    public void Enemy_bonus_spawn_x_includes_c_map_left_offset()
    {
        // C ENEMY.C passes sprite->x into BONUS_Add; BONUS_Add stores x + MAP_LEFT.
        Assert.Equal(160, WaveController.BonusSpawnXFromEnemyX(144));
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
        Assert.Equal(expected, WaveController.ShouldCompleteMission(
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

        var explosions = WaveController.BuildPlayerDeathExplosions(
            PlayerLogic.InitX,
            PlayerLogic.InitY,
            WaveController.EndDuration,
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

        var explosions = WaveController.BuildPlayerDeathExplosions(
            PlayerLogic.InitX,
            PlayerLogic.InitY,
            WaveController.EndExplode,
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

    private static int Count(IReadOnlyList<WaveController.DeathExplosion> explosions, int expType)
    {
        int count = 0;
        foreach (var e in explosions)
            if (e.ExpType == expType) count++;
        return count;
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

}
