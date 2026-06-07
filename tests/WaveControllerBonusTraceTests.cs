using System.Collections.Generic;
using Raptor.Sim;
using Raptor.Sim.Bonus;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// Bonus pickup TRACE-fidelity parity (RAPTOR_BONUS_DUMP artifact vs C BONUS.C).
/// Covers structural-parity-review findings #10/#11/#12. These affect the bonus
/// debug dump only — bonuses are not in the NDJSON checkpoint schema.
/// </summary>
public class WaveControllerBonusTraceTests
{
    [Fact]
    public void Money_pickup_traces_pickup_money_before_mutation_and_no_bare_pickup()
    {
        // Findings #11a + #12 + #10. BONUS.C:255-257 — C emits the pickup_money
        // trace BEFORE setting dflag/countdown, so the snapshot is d=0 cnt=0 (NOT
        // d=1 cnt=50). C never emits a bare "pickup" event (its set is add /
        // pickup_money / pickup_remove / remove_bottom / state). And the post-pickup
        // state reads cnt=49 (same-pass dflag decrement, BONUS.C:268-271).
        var events = new List<(string ev, bool d, int cnt)>();
        var b = new BonusLogic(objType: 23 /* S_ITEMBUY6, moneyflag */, x: 100, y: 50);

        WaveController.TracePickupAndUpdate(b, isMoney: true,
            (ev, bb) => events.Add((ev, bb.DisplayAsPickedUpMoney, bb.PickedUpMoneyCountdown)));

        Assert.Equal(new[] { "pickup_money" }, events.ConvertAll(e => e.ev));  // no bare "pickup"
        Assert.False(events[0].d);       // pre-mutation snapshot: dflag not yet set
        Assert.Equal(0, events[0].cnt);  // d=0 cnt=0, matching BONUS.C:255
        Assert.True(b.DisplayAsPickedUpMoney);
        Assert.Equal(49, b.PickedUpMoneyCountdown);  // 50 set, then same-pass decrement
        Assert.True(b.Alive);
    }

    [Fact]
    public void Nonmoney_pickup_traces_only_pickup_remove_and_kills()
    {
        // Finding #11a. BONUS.C:260-263 — a non-money pickup emits pickup_remove
        // then BONUS_Remove. No bare "pickup" event.
        var events = new List<string>();
        var b = new BonusLogic(objType: 16 /* S_ENERGY, not moneyflag */, x: 100, y: 50);

        WaveController.TracePickupAndUpdate(b, isMoney: false, (ev, bb) => events.Add(ev));

        Assert.Equal(new[] { "pickup_remove" }, events);
        Assert.False(b.Alive);
    }
}
