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
        Assert.Equal(2048ul, WavePhaseScheduler.ComputeSeed(2));
        Assert.Equal(1024ul, WavePhaseScheduler.ComputeSeed(1));
        Assert.Equal(5120ul, WavePhaseScheduler.ComputeSeed(5));
    }

    [Fact]
    public void Seed_override_string_is_applied_when_parseable()
    {
        // When a seed override is provided, ComputeSeed returns it instead.
        Assert.Equal(9999ul, WavePhaseScheduler.ComputeSeed(2, "9999"));
    }

    [Fact]
    public void Empty_seed_override_falls_back_to_default()
    {
        Assert.Equal(2048ul, WavePhaseScheduler.ComputeSeed(2, ""));
        Assert.Equal(2048ul, WavePhaseScheduler.ComputeSeed(2, null));
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
    [InlineData(true, false, true, true, false, false, true)]
    [InlineData(true, false, true, true, false, true, false)]
    [InlineData(true, false, true, true, true, false, false)]
    [InlineData(true, false, true, false, false, false, false)]
    [InlineData(true, true, true, true, false, false, false)]
    [InlineData(false, false, true, true, false, false, false)]
    [InlineData(true, false, false, true, false, false, false)]
    public void Mission_completion_waits_for_wave_enemies_and_explosions(
        bool waveActive,
        bool demoActive,
        bool endWave,
        bool playerAlive,
        bool enemiesRemaining,
        bool explosionsRemaining,
        bool expected)
    {
        Assert.Equal(expected, WaveController.ShouldCompleteMission(
            waveActive, demoActive, endWave, playerAlive, enemiesRemaining, explosionsRemaining));
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

}
