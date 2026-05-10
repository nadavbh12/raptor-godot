using System.Collections.Generic;
using Raptor.Sim;
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
}
