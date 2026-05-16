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
        var wave = new WaveController();
        wave.PlayerLogic.Reset();
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
        ((List<EnemyLogic>)wave.GetEnemies()).Add(enemy);

        wave.PhaseMovement();
        wave.PhaseCollisionCollect();

        Assert.Equal(PlayerLogic.InitShield - 6, wave.PlayerLogic.Shield);
    }

}
