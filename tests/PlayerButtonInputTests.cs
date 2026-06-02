using System;
using System.Collections.Generic;
using Raptor.Sim;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Raptor.Sim.Player;
using Raptor.Sim.Shots;
using Raptor.Test;
using Xunit;

namespace Raptor.Tests;

// Covers the cross-tick edge latches PlayerButtonInput owns after the E7
// extraction. The B2 (special-cycle) latch is the cleanest probe: a held
// button must cycle the special weapon only on the rising edge, and
// ResetDemoLatches() (called from StartDemoPlayback) must clear it.
public class PlayerButtonInputTests
{
    // Two owned specials (DumbMissile=3, PulseCannon=12; both SpecialW) so
    // CycleSpecial toggles between exactly two, independent of GetNext order.
    private static (PlayerButtonInput bin, PlayerShooter shooter, PlayerLogic player) MakeRig()
    {
        var inv = new Inventory();
        inv.Add(ObjType.DumbMissile);   // first special → EquippedSpecial = DumbMissile
        inv.Add(ObjType.PulseCannon);
        return (new PlayerButtonInput(), new PlayerShooter(inv), new PlayerLogic());
    }

    private static void TickDemoB2(PlayerButtonInput bin, PlayerShooter shooter, PlayerLogic player, int b2)
    {
        bin.ApplyDemo(
            new DemoReplay.Frame(0, b2, 0, 0, 0, 0, 0),
            shooter, player,
            new List<EnemyLogic>(), new List<EnemyLogic>(), new List<BulletLogic>(),
            new Random(1));
    }

    [Fact]
    public void Demo_b2_cycles_special_only_on_rising_edge()
    {
        var (bin, shooter, player) = MakeRig();
        var initial = shooter.SpecialWeapon;

        TickDemoB2(bin, shooter, player, 1);          // rising edge → cycle
        var afterEdge = shooter.SpecialWeapon;
        Assert.NotEqual(initial, afterEdge);

        TickDemoB2(bin, shooter, player, 1);          // still held → no cycle
        Assert.Equal(afterEdge, shooter.SpecialWeapon);

        TickDemoB2(bin, shooter, player, 0);          // release
        TickDemoB2(bin, shooter, player, 1);          // rising edge again → cycle back
        Assert.Equal(initial, shooter.SpecialWeapon);
    }

    [Fact]
    public void ResetDemoLatches_allows_a_held_button_to_re_fire()
    {
        var (bin, shooter, player) = MakeRig();

        TickDemoB2(bin, shooter, player, 1);          // edge → cycle, latch now set
        var afterEdge = shooter.SpecialWeapon;

        bin.ResetDemoLatches();                        // mirrors StartDemoPlayback reset
        TickDemoB2(bin, shooter, player, 1);          // latch cleared → treated as fresh edge
        Assert.NotEqual(afterEdge, shooter.SpecialWeapon);
    }
}
