using System.Collections.Generic;
using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

public class ObjTypeTests
{
    [Fact]
    public void ObjType_values_match_C_OBJ_TYPE()
    {
        Assert.Equal(0, (int)ObjType.ForwardGuns);
        Assert.Equal(11, (int)ObjType.MegaBomb);
        Assert.Equal(15, (int)ObjType.SuperShield);
        Assert.Equal(16, (int)ObjType.Energy);
        Assert.Equal(17, (int)ObjType.Detect);
        Assert.Equal(23, (int)ObjType.ItemBuy6);
        Assert.Equal(24, (int)ObjType.LastObject);
    }
}

public class ObjLibTests
{
    [Theory]
    [InlineData(ObjType.ForwardGuns, true, false, false, 12000, 1, 1)]
    [InlineData(ObjType.DumbMissile, true, false, true, 145200, 1, 1)]
    [InlineData(ObjType.MegaBomb, false, true, false, 32250, 1, 5)]
    [InlineData(ObjType.Energy, true, true, false, 400, 25, 100)]
    [InlineData(ObjType.SuperShield, false, false, false, 78500, 100, 100)]
    public void Flags_match_C_obj_lib(ObjType t, bool forever, bool only, bool special, int cost, int start, int max)
    {
        var e = ObjLib.Of(t);
        Assert.Equal(forever, e.Forever);
        Assert.Equal(only, e.OnlyFlag);
        Assert.Equal(special, e.SpecialW);
        Assert.Equal(cost, e.Cost);
        Assert.Equal(start, e.StartCnt);
        Assert.Equal(max, e.MaxCnt);
    }
}

public class InventoryCoreTests
{
    [Fact]
    public void Add_new_weapon_auto_equips_and_is_owned()
    {
        var inv = new Inventory();
        Assert.Equal(BuyStuff.GotIt, inv.Add(ObjType.MiniGun));
        Assert.True(inv.IsEquip(ObjType.MiniGun));
        Assert.Equal(1, inv.GetAmt(ObjType.MiniGun));
    }

    [Fact]
    public void Add_onlyflag_stacks_capped_at_max()
    {
        var inv = new Inventory();
        for (int i = 0; i < 10; i++) inv.Add(ObjType.MegaBomb);   // max_cnt=5
        Assert.Equal(5, inv.GetAmt(ObjType.MegaBomb));
    }

    [Fact]
    public void Add_moneyflag_returns_gotit_without_slot()
    {
        var inv = new Inventory();
        Assert.Equal(BuyStuff.GotIt, inv.Add(ObjType.ItemBuy1));
        Assert.False(inv.IsEquip(ObjType.ItemBuy1));   // money bonus: no slot
    }

    [Fact]
    public void Clear_empties_all()
    {
        var inv = new Inventory();
        inv.Add(ObjType.MiniGun);
        inv.Clear();
        Assert.False(inv.IsEquip(ObjType.MiniGun));
    }

    [Fact]
    public void GetTotal_equals_count_under_one_slot_model()
    {
        var inv = new Inventory();
        for (int i = 0; i < 4; i++) inv.Add(ObjType.MegaBomb);
        Assert.Equal(4, inv.GetAmt(ObjType.MegaBomb));
        Assert.Equal(4, inv.GetTotal(ObjType.MegaBomb));   // NOT 1
    }

    // NOTE: In Task 1.3's public surface, Add always auto-equips, so there is no
    // way to construct an owned-but-unequipped slot (no Unequip/Del/Load yet).
    // We therefore lock the two reachable Equip outcomes:
    //   (a) Equip on an unowned type returns false (mirrors OBJS_Equip: no match).
    //   (b) Equip on an already-equipped owned slot returns false (C: p_objs!=NUL).
    [Fact]
    public void Equip_unowned_type_returns_false()
    {
        var inv = new Inventory();
        Assert.False(inv.Equip(ObjType.MiniGun));
        Assert.False(inv.IsEquip(ObjType.MiniGun));
    }

    [Fact]
    public void Equip_already_equipped_slot_returns_false_and_stays_equipped()
    {
        var inv = new Inventory();
        inv.Add(ObjType.MiniGun);                       // owned + auto-equipped
        Assert.False(inv.Equip(ObjType.MiniGun));       // already in use → no-op
        Assert.True(inv.IsEquip(ObjType.MiniGun));
    }

    // Add's guard mirrors C `type >= S_LAST_OBJECT`; LastObject (24) is that
    // sentinel and is the true out-of-range boundary. Add(24) must return Error
    // without throwing — the guard short-circuits before ObjLib.Of(24) is reached.
    [Fact]
    public void Add_out_of_range_type_returns_error_and_creates_no_slot()
    {
        var inv = new Inventory();
        Assert.Equal(BuyStuff.Error, inv.Add(ObjType.LastObject));      // type 24
        Assert.False(inv.IsEquip(ObjType.LastObject));
        Assert.Equal(0, inv.GetAmt(ObjType.LastObject));
    }
}

public class InventorySeedTests
{
    [Fact]
    public void New_pilot_seed_matches_C()
    {
        var inv = new Inventory();
        inv.SeedNewPilot();
        Assert.True(inv.IsEquip(ObjType.ForwardGuns));
        Assert.Equal(75, inv.GetAmt(ObjType.Energy));
        Assert.Null(inv.EquippedSpecial);   // GetNext with no specials → none
    }

    [Fact]
    public void New_pilot_seed_full_slot_set()
    {
        // Exactly ForwardGuns + Energy owned after seed; nothing else.
        var inv = new Inventory();
        inv.SeedNewPilot();

        // Owned
        Assert.True(inv.IsEquip(ObjType.ForwardGuns));
        Assert.Equal(1, inv.GetAmt(ObjType.ForwardGuns));
        Assert.True(inv.IsEquip(ObjType.Energy));
        Assert.Equal(75, inv.GetAmt(ObjType.Energy));

        // Not owned
        Assert.False(inv.IsEquip(ObjType.PlasmaGuns));
        Assert.False(inv.IsEquip(ObjType.DumbMissile));
        Assert.False(inv.IsEquip(ObjType.MegaBomb));
        Assert.False(inv.IsEquip(ObjType.SuperShield));
        Assert.False(inv.IsEquip(ObjType.Detect));
        Assert.False(inv.IsEquip(ObjType.MiniGun));
        Assert.Equal(0, inv.GetAmt(ObjType.PlasmaGuns));
        Assert.Equal(0, inv.GetAmt(ObjType.MegaBomb));
    }
}

public class InventoryCopyFromTests
{
    [Fact]
    public void CopyFrom_replaces_all_state()
    {
        // Build source inventory A with some items + an equipped special.
        var a = new Inventory();
        a.Load(ObjType.ForwardGuns, 1, true);
        a.Load(ObjType.MiniGun,     2, true);
        a.Load(ObjType.MegaBomb,    3, false);
        a.Load(ObjType.Energy,     75, false);
        a.EquippedSpecial = ObjType.MiniGun;

        // Build target inventory B with different junk.
        var b = new Inventory();
        b.Load(ObjType.PlasmaGuns, 9, true);
        b.Load(ObjType.DeathRay,   7, true);
        b.EquippedSpecial = ObjType.DeathRay;

        // CopyFrom replaces B's state with A's state.
        b.CopyFrom(a);

        // B now matches A exactly.
        Assert.Equal(1,  b.GetAmt(ObjType.ForwardGuns));
        Assert.True(b.IsEquip(ObjType.ForwardGuns));
        Assert.Equal(2,  b.GetAmt(ObjType.MiniGun));
        Assert.True(b.IsEquip(ObjType.MiniGun));
        Assert.Equal(3,  b.GetAmt(ObjType.MegaBomb));
        Assert.False(b.IsEquip(ObjType.MegaBomb));
        Assert.Equal(75, b.GetAmt(ObjType.Energy));
        Assert.False(b.IsEquip(ObjType.Energy));
        Assert.Equal(ObjType.MiniGun, b.EquippedSpecial);

        // B's prior junk is gone.
        Assert.Equal(0, b.GetAmt(ObjType.PlasmaGuns));
        Assert.False(b.IsEquip(ObjType.PlasmaGuns));
        Assert.Equal(0, b.GetAmt(ObjType.DeathRay));
        Assert.False(b.IsEquip(ObjType.DeathRay));
    }

    [Fact]
    public void CopyFrom_preserves_caller_reference_identity()
    {
        // After CopyFrom, the target IS STILL THE SAME OBJECT (no replacement).
        var a = new Inventory();
        a.Load(ObjType.MiniGun, 1, true);
        a.EquippedSpecial = ObjType.MiniGun;

        var b = new Inventory();
        var bRef = b;  // capture reference before CopyFrom

        b.CopyFrom(a);

        Assert.Same(bRef, b);                  // same instance
        Assert.True(b.IsEquip(ObjType.MiniGun));
    }

    [Fact]
    public void CopyFrom_does_not_alias_source()
    {
        // CopyFrom is a deep copy: mutating the source after the copy must NOT
        // affect the target (no shared ObjSlot references).
        var a = new Inventory();
        a.Load(ObjType.MiniGun, 1, true);

        var b = new Inventory();
        b.CopyFrom(a);

        // Mutate A after the copy.
        a.Add(ObjType.MegaBomb);          // new slot in A
        a.EquippedSpecial = ObjType.MiniGun;

        // B is unaffected by A's later mutations.
        Assert.Equal(0, b.GetAmt(ObjType.MegaBomb));
        Assert.False(b.IsEquip(ObjType.MegaBomb));
        Assert.Null(b.EquippedSpecial);   // B kept A's pre-copy EquippedSpecial (null)
    }

    [Fact]
    public void CopyFrom_null_throws()
    {
        var b = new Inventory();
        Assert.Throws<System.ArgumentNullException>(() => b.CopyFrom(null!));
    }
}

public class InventoryEnergyTests
{
    // OBJS_SubEnergy — super-shield drains first, no spill-back to energy.
    [Fact]
    public void SubEnergy_drains_super_shield_first_no_spill_back()
    {
        var inv = new Inventory();
        inv.Load(ObjType.Energy, 100, inuse: true);
        inv.Load(ObjType.SuperShield, 20, inuse: true);
        inv.SubEnergy(15);
        Assert.Equal(5, inv.GetAmt(ObjType.SuperShield));   // 20-15
        Assert.Equal(100, inv.GetAmt(ObjType.Energy));      // untouched
    }

    // OBJS_SubEnergy — super-shield goes negative → deleted, energy untouched.
    [Fact]
    public void SubEnergy_deletes_super_shield_when_negative_no_spill_back()
    {
        var inv = new Inventory();
        inv.Load(ObjType.Energy, 100, inuse: true);
        inv.Load(ObjType.SuperShield, 10, inuse: true);
        inv.SubEnergy(15);   // 10-15 = -5 → delete super-shield; energy UNTOUCHED
        Assert.False(inv.IsEquip(ObjType.SuperShield));
        Assert.Equal(0, inv.GetAmt(ObjType.SuperShield));
        Assert.Equal(100, inv.GetAmt(ObjType.Energy));
    }

    // OBJS_SubEnergy — no super-shield → drain energy, clamp at 0.
    [Fact]
    public void SubEnergy_clamps_energy_at_zero_when_no_super_shield()
    {
        var inv = new Inventory();
        inv.Load(ObjType.Energy, 10, inuse: true);
        inv.SubEnergy(15);
        Assert.Equal(0, inv.GetAmt(ObjType.Energy));
    }

    // OBJS_AddEnergy — full amt to energy when below max (NOT fill-then-spill).
    [Fact]
    public void AddEnergy_adds_full_amt_when_below_max_no_spill()
    {
        var inv = new Inventory();
        inv.Load(ObjType.Energy, 95, inuse: true);
        inv.AddEnergy(10);
        Assert.Equal(100, inv.GetAmt(ObjType.Energy));            // 95+10=105 clamp 100
        Assert.Equal(0, inv.GetAmt(ObjType.SuperShield));         // NO super-shield created
        Assert.False(inv.IsEquip(ObjType.SuperShield));
    }

    // OBJS_AddEnergy — dead (num==0) player is not revived.
    [Fact]
    public void AddEnergy_does_not_recharge_dead_player()
    {
        var inv = new Inventory();
        inv.Load(ObjType.Energy, 0, inuse: true);
        inv.AddEnergy(50);
        Assert.Equal(0, inv.GetAmt(ObjType.Energy));
    }

    // OBJS_AddEnergy — spill quarter to existing super-shield only when energy at max.
    [Fact]
    public void AddEnergy_spills_quarter_to_super_shield_when_energy_at_max()
    {
        var inv = new Inventory();
        inv.Load(ObjType.Energy, 100, inuse: true);
        inv.Load(ObjType.SuperShield, 10, inuse: true);
        inv.AddEnergy(8);   // energy at max → super-shield += 8>>2 = 2
        Assert.Equal(100, inv.GetAmt(ObjType.Energy));
        Assert.Equal(12, inv.GetAmt(ObjType.SuperShield));
    }

    // OBJS_AddEnergy — energy at max but no super-shield → no-op, does not create one.
    [Fact]
    public void AddEnergy_at_max_with_no_super_shield_is_noop()
    {
        var inv = new Inventory();
        inv.Load(ObjType.Energy, 100, inuse: true);
        inv.AddEnergy(8);
        Assert.False(inv.IsEquip(ObjType.SuperShield));
    }

    // No energy slot owned → AddEnergy is a no-op (mirrors `if (!cur) return 0`).
    [Fact]
    public void AddEnergy_with_no_energy_slot_is_noop()
    {
        var inv = new Inventory();
        inv.AddEnergy(50);
        Assert.False(inv.IsEquip(ObjType.Energy));
        Assert.Equal(0, inv.GetAmt(ObjType.Energy));
    }

    // No super-shield AND no energy → SubEnergy is a no-op.
    [Fact]
    public void SubEnergy_with_no_slots_is_noop()
    {
        var inv = new Inventory();
        inv.SubEnergy(50);
        Assert.Equal(0, inv.GetAmt(ObjType.Energy));
        Assert.Equal(0, inv.GetAmt(ObjType.SuperShield));
    }

    // Property: arbitrary Add/SubEnergy sequences keep energy in [0,100].
    [Theory]
    [InlineData(new[] { 10, -20, 50, -5, 30, -100, 60, 9999, -1 })]
    [InlineData(new[] { 25, 25, 25, 25, 25, -1, -1, -1 })]
    [InlineData(new[] { -50, 100, -100, 100, -100, 1, 2, 3 })]
    [InlineData(new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 })]
    [InlineData(new[] { -1, -1, -1, 100, 100, -1, 100 })]
    [InlineData(new[] { 7, -3, 11, -200, 42, -1, 99, -99, 100, 100 })]
    public void Energy_stays_in_bounds_over_sequences(int[] amounts)
    {
        var inv = new Inventory();
        inv.Load(ObjType.Energy, 50, inuse: true);
        foreach (int amt in amounts)
        {
            if (amt >= 0) inv.AddEnergy(amt);
            else inv.SubEnergy(-amt);
            int e = inv.GetAmt(ObjType.Energy);
            Assert.InRange(e, 0, 100);
        }
    }
}

public class InventoryGetNextTests
{
    [Fact]
    public void GetNext_cycles_owned_specials_and_wraps()
    {
        var inv = new Inventory();
        inv.Add(ObjType.DumbMissile);
        inv.Add(ObjType.DeathRay);   // both specialw
        inv.EquippedSpecial = ObjType.DumbMissile;
        inv.GetNext();
        Assert.Equal(ObjType.DeathRay, inv.EquippedSpecial);
        inv.GetNext();               // wrap back
        Assert.Equal(ObjType.DumbMissile, inv.EquippedSpecial);
    }

    [Fact]
    public void GetNext_sets_empty_when_no_specials_owned()
    {
        var inv = new Inventory();
        inv.Add(ObjType.ForwardGuns);   // not specialw
        inv.GetNext();
        Assert.Null(inv.EquippedSpecial);
    }

    [Fact]
    public void GetNext_keeps_only_owned_special_when_it_is_the_sole_option()
    {
        // When only one special is owned and it is already equipped,
        // GetNext must wrap around and re-select it (not go to null).
        var inv = new Inventory();
        inv.Add(ObjType.MiniGun);       // specialw, index 4
        inv.EquippedSpecial = ObjType.MiniGun;
        inv.GetNext();
        Assert.Equal(ObjType.MiniGun, inv.EquippedSpecial);
    }
}

// ── Inventory.GetCost / GetResale — OBJS_GetCost / OBJS_GetResale (Task 5.2) ──
public class InventoryCostResaleTests
{
    [Fact]
    public void GetCost_non_onlyflag_is_raw_cost()
    {
        var inv = new Inventory();
        // MiniGun: onlyflag=false → cost as-is.
        Assert.Equal(250650, inv.GetCost(ObjType.MiniGun));
    }

    [Fact]
    public void GetCost_onlyflag_scales_by_start_cnt()
    {
        var inv = new Inventory();
        // Energy: onlyflag, cost=400, start_cnt=25 → 10000.
        Assert.Equal(10000, inv.GetCost(ObjType.Energy));
        // MegaBomb: onlyflag, cost=32250, start_cnt=1 → 32250.
        Assert.Equal(32250, inv.GetCost(ObjType.MegaBomb));
    }

    [Fact]
    public void GetResale_zero_when_not_owned()
    {
        var inv = new Inventory();
        Assert.Equal(0, inv.GetResale(ObjType.MiniGun));   // !IsEquip → 0
    }

    [Fact]
    public void GetResale_is_half_cost_when_owned()
    {
        var inv = new Inventory();
        inv.Load(ObjType.MiniGun, 1, inuse: true);
        // 250650 >> 1 = 125325.
        Assert.Equal(125325, inv.GetResale(ObjType.MiniGun));
    }

    [Fact]
    public void GetResale_onlyflag_uses_scaled_cost_then_halves()
    {
        var inv = new Inventory();
        inv.Load(ObjType.Energy, 50, inuse: true);
        // (400*25) >> 1 = 5000.
        Assert.Equal(5000, inv.GetResale(ObjType.Energy));
    }
}

// ── Inventory.Buy / Sell — OBJS_Buy / OBJS_Sell (Task 5.2) ────────────────────
public class InventoryBuySellTests
{
    // --- Buy ----------------------------------------------------------------

    [Fact]
    public void Buy_deducts_exactly_cost_and_adds_item()
    {
        var inv = new Inventory();
        uint score = 300000;
        var rval = inv.Buy(ObjType.MiniGun, ref score);   // cost 250650
        Assert.Equal(BuyStuff.GotIt, rval);
        Assert.Equal(300000u - 250650u, score);           // exact deduction
        Assert.True(inv.IsEquip(ObjType.MiniGun));
        Assert.Equal(1, inv.GetAmt(ObjType.MiniGun));
    }

    [Fact]
    public void Buy_insufficient_funds_no_change()
    {
        var inv = new Inventory();
        uint score = 100;                                  // < MiniGun cost
        var rval = inv.Buy(ObjType.MiniGun, ref score);
        Assert.Equal(BuyStuff.NoMoney, rval);
        Assert.Equal(100u, score);                         // score untouched
        Assert.False(inv.IsEquip(ObjType.MiniGun));        // inventory untouched
        Assert.Equal(0, inv.GetAmt(ObjType.MiniGun));
    }

    [Fact]
    public void Buy_exactly_affordable_succeeds_to_zero()
    {
        var inv = new Inventory();
        uint score = 250650;                               // exactly MiniGun cost
        var rval = inv.Buy(ObjType.MiniGun, ref score);
        Assert.Equal(BuyStuff.GotIt, rval);
        Assert.Equal(0u, score);
    }

    [Fact]
    public void Buy_super_shield_cap_blocks_at_five_with_no_deduction()
    {
        var inv = new Inventory();
        // GetTotal(SuperShield) == Num under the one-slot model; 5 hits the cap.
        inv.Load(ObjType.SuperShield, 5, inuse: true);
        uint score = 1_000_000;
        var rval = inv.Buy(ObjType.SuperShield, ref score);
        Assert.Equal(BuyStuff.ShipFull, rval);
        Assert.Equal(1_000_000u, score);                   // no deduction
        Assert.Equal(5, inv.GetAmt(ObjType.SuperShield));  // unchanged
    }

    [Fact]
    public void Buy_super_shield_under_cap_succeeds()
    {
        var inv = new Inventory();
        uint score = 1_000_000;
        var rval = inv.Buy(ObjType.SuperShield, ref score);   // cost 78500
        Assert.Equal(BuyStuff.GotIt, rval);
        Assert.Equal(1_000_000u - 78_500u, score);
        Assert.Equal(100, inv.GetAmt(ObjType.SuperShield));   // start_cnt=100
    }

    // --- Sell ---------------------------------------------------------------

    [Fact]
    public void Sell_unowned_returns_zero_no_score_change()
    {
        var inv = new Inventory();
        uint score = 5000;
        int left = inv.Sell(ObjType.MiniGun, ref score);
        Assert.Equal(0, left);
        Assert.Equal(5000u, score);                        // !IsEquip → no resale
    }

    [Fact]
    public void Sell_non_onlyflag_weapon_removes_slot_and_refunds_resale()
    {
        var inv = new Inventory();
        inv.Load(ObjType.MiniGun, 1, inuse: true);
        uint score = 0;
        int left = inv.Sell(ObjType.MiniGun, ref score);
        Assert.Equal(0, left);                             // GetTotal after Del == 0
        Assert.Equal(125325u, score);                      // 250650 >> 1
        Assert.False(inv.IsEquip(ObjType.MiniGun));        // whole slot gone
        Assert.Equal(0, inv.GetAmt(ObjType.MiniGun));
    }

    [Fact]
    public void Sell_non_onlyflag_cycles_equipped_special()
    {
        // MiniGun + DumbMissile both specialw; selling the equipped one cycles
        // EquippedSpecial to the other owned special (OBJS_Del → GetNext).
        var inv = new Inventory();
        inv.Add(ObjType.MiniGun);
        inv.Add(ObjType.DumbMissile);
        inv.EquippedSpecial = ObjType.MiniGun;
        uint score = 0;
        inv.Sell(ObjType.MiniGun, ref score);
        Assert.Equal(ObjType.DumbMissile, inv.EquippedSpecial);
        Assert.False(inv.IsEquip(ObjType.MiniGun));
    }

    [Fact]
    public void Sell_energy_onlyflag_forever_decrements_by_start_cnt_keeps_slot()
    {
        var inv = new Inventory();
        inv.Load(ObjType.Energy, 75, inuse: true);          // onlyflag + forever
        uint score = 0;
        int left = inv.Sell(ObjType.Energy, ref score);
        Assert.Equal(50, left);                             // 75 - start_cnt(25)
        Assert.Equal(50, inv.GetAmt(ObjType.Energy));
        Assert.True(inv.IsEquip(ObjType.Energy));           // forever → never removed
        Assert.Equal(5000u, score);                         // (400*25)>>1
    }

    [Fact]
    public void Sell_megabomb_onlyflag_non_forever_decrements_then_removes_at_zero()
    {
        var inv = new Inventory();
        inv.Load(ObjType.MegaBomb, 2, inuse: true);         // onlyflag, !forever, start_cnt=1
        uint score = 0;
        int left = inv.Sell(ObjType.MegaBomb, ref score);
        Assert.Equal(1, left);                              // 2 - 1
        Assert.Equal(1, inv.GetAmt(ObjType.MegaBomb));
        Assert.True(inv.IsEquip(ObjType.MegaBomb));

        int left2 = inv.Sell(ObjType.MegaBomb, ref score);
        Assert.Equal(0, left2);                             // 1 - 1 = 0 → slot removed
        Assert.False(inv.IsEquip(ObjType.MegaBomb));
    }

    [Fact]
    public void Sell_detect_removes_slot_returns_zero_and_refunds()
    {
        var inv = new Inventory();
        inv.Load(ObjType.Detect, 1, inuse: true);
        uint score = 0;
        int left = inv.Sell(ObjType.Detect, ref score);
        Assert.Equal(0, left);
        Assert.False(inv.IsEquip(ObjType.Detect));
        Assert.Equal(5000u, score);                         // (10000)>>1
    }

    // Money-conservation property: over arbitrary buy-then-sell sequences the
    // score never goes negative (it is uint, so any underflow would wrap huge)
    // and a buy-then-immediate-sell round-trips to half-cost loss (resale = cost>>1).
    [Theory]
    [InlineData(ObjType.MiniGun)]
    [InlineData(ObjType.PlasmaGuns)]
    [InlineData(ObjType.SuperShield)]
    [InlineData(ObjType.MegaBomb)]
    public void Buy_then_sell_round_trips_to_half_cost_loss(ObjType type)
    {
        var inv = new Inventory();
        uint score = 5_000_000;
        uint start = score;

        var bought = inv.Buy(type, ref score);
        Assert.Equal(BuyStuff.GotIt, bought);
        int cost = inv.GetCost(type);
        Assert.Equal(start - (uint)cost, score);

        // Resale is read from the post-buy (owned) state: cost >> 1.
        int resale = inv.GetResale(type);
        inv.Sell(type, ref score);

        // Net loss == cost - resale == cost - (cost>>1).
        Assert.Equal(start - (uint)cost + (uint)resale, score);
        Assert.True(score <= start);            // never gained money on a round-trip
    }

    [Fact]
    public void Repeated_buy_sell_sequence_keeps_score_sane()
    {
        var inv = new Inventory();
        uint score = 2_000_000;
        var ops = new (bool buy, ObjType t)[]
        {
            (true,  ObjType.MiniGun),
            (true,  ObjType.MegaBomb),
            (false, ObjType.MiniGun),
            (true,  ObjType.PlasmaGuns),
            (false, ObjType.MegaBomb),
            (false, ObjType.PlasmaGuns),
            (true,  ObjType.MiniGun),
        };
        foreach (var (buy, t) in ops)
        {
            if (buy) inv.Buy(t, ref score);
            else inv.Sell(t, ref score);
            // uint score that never underflows stays within the seeded ceiling+resales;
            // the meaningful invariant is it never wraps to an absurd value.
            Assert.True(score < 5_000_000u, $"score wrapped/ballooned to {score}");
        }
    }
}

// ── Inventory.Use — OBJS_Use port (Task 5.1) ─────────────────────────────────
public class InventoryUseTests
{
    [Fact]
    public void Use_decrements_non_forever_weapon()
    {
        // MegaBomb is non-Forever (Forever=false): Use decrements its count.
        var inv = new Inventory();
        inv.Load(ObjType.MegaBomb, num: 2, inuse: true);
        inv.Use(ObjType.MegaBomb);
        Assert.Equal(1, inv.GetAmt(ObjType.MegaBomb));
        Assert.True(inv.IsEquip(ObjType.MegaBomb));
    }

    [Fact]
    public void Use_removes_slot_at_zero()
    {
        // At Num=1, Use drops to 0 → slot removed (OBJS_Remove + p_objs[type]=NUL).
        var inv = new Inventory();
        inv.Load(ObjType.MegaBomb, num: 1, inuse: true);
        inv.Use(ObjType.MegaBomb);
        Assert.False(inv.IsEquip(ObjType.MegaBomb));
        Assert.Equal(0, inv.GetAmt(ObjType.MegaBomb));
    }

    [Fact]
    public void Use_does_not_decrement_forever_weapon()
    {
        // DumbMissile has Forever=true: Use never consumes it (lib->forever guard).
        var inv = new Inventory();
        inv.Load(ObjType.DumbMissile, num: 1, inuse: true);
        inv.Use(ObjType.DumbMissile);
        Assert.True(inv.IsEquip(ObjType.DumbMissile));
        Assert.Equal(1, inv.GetAmt(ObjType.DumbMissile));
    }

    [Fact]
    public void Use_on_unowned_type_is_noop()
    {
        // Empty inventory: Use mirrors C's `if (!cur) return` — no throw, no change.
        var inv = new Inventory();
        inv.Use(ObjType.MegaBomb);
        Assert.Equal(0, inv.GetAmt(ObjType.MegaBomb));
        Assert.False(inv.IsEquip(ObjType.MegaBomb));
    }

    // CYCLE-ON-ZERO branch (`EquippedSpecial == type → GetNext()`):
    // UNREACHABLE with shipped flags via the legitimate path. EquippedSpecial is only
    // ever set to a SpecialW type (Add auto-equips only SpecialW; MakeSpecial requires
    // SpecialW; GetNext only selects SpecialW). MegaBomb — the ONLY non-Forever weapon —
    // has SpecialW=false, so a non-Forever slot reaching 0 can never also be the equipped
    // special. The branch is therefore dead under shipped data. It is nonetheless verified
    // C-faithful by inspection of the OBJS_Use port, and GetNext's own cycling behaviour
    // is covered by InventoryGetNextTests above. We deliberately do NOT force an
    // unreachable scenario by hand-corrupting EquippedSpecial — honesty over a hollow test.
}

// ---------------------------------------------------------------------------
// OBJS_LoseObj — OBJECTS.C:1311-1342 (Task 5.3)
// DETERMINISTIC — no RNG. With a special equipped, Del it. Otherwise walk
// type 23..0 and Del the FIRST owned slot whose loseit flag is set.
// ---------------------------------------------------------------------------
public class InventoryLoseObjTests
{
    [Fact]
    public void LoseObj_with_equipped_special_dels_it_and_cycles()
    {
        // Own two specials, equip one; LoseObj removes the equipped one and cycles.
        var inv = new Inventory();
        inv.Add(ObjType.DumbMissile);   // 3, special, auto-equips (EquippedSpecial = DumbMissile)
        inv.Add(ObjType.MiniGun);       // 4, special
        inv.EquippedSpecial = ObjType.MiniGun;

        Assert.True(inv.LoseObj());

        Assert.False(inv.IsEquip(ObjType.MiniGun));      // equipped special removed
        Assert.True(inv.IsEquip(ObjType.DumbMissile));   // the other special survives
        Assert.Equal(ObjType.DumbMissile, inv.EquippedSpecial);  // cycled via GetNext
    }

    [Fact]
    public void LoseObj_no_special_dels_highest_index_loseit()
    {
        // No equipped special; own PlasmaGuns(1, loseit), MicroMissile(2, loseit),
        // and GrdMissile(8, loseit). Walk 23..0 → the HIGHEST-index loseit owned
        // (GrdMissile=8) is the one removed.
        var inv = new Inventory();
        inv.Load(ObjType.PlasmaGuns,   num: 1, inuse: true);   // 1
        inv.Load(ObjType.MicroMissile, num: 1, inuse: true);   // 2
        inv.Load(ObjType.GrdMissile,   num: 1, inuse: true);   // 8
        inv.EquippedSpecial = null;

        Assert.True(inv.LoseObj());

        Assert.False(inv.IsEquip(ObjType.GrdMissile));   // highest-index loseit removed
        Assert.True(inv.IsEquip(ObjType.MicroMissile));  // lower-index survive
        Assert.True(inv.IsEquip(ObjType.PlasmaGuns));
    }

    [Fact]
    public void LoseObj_never_removes_non_loseit()
    {
        // Own only non-loseit types (ForwardGuns=0, SuperShield=15, Energy=16,
        // Detect=17), no equipped special. LoseObj finds nothing to lose: returns
        // true (C's rval init = TRUE) and removes NOTHING.
        var inv = new Inventory();
        inv.Load(ObjType.ForwardGuns, num: 1,   inuse: true);   // 0  loseit=F
        inv.Load(ObjType.SuperShield, num: 100, inuse: true);   // 15 loseit=F
        inv.Load(ObjType.Energy,      num: 75,  inuse: true);   // 16 loseit=F
        inv.Load(ObjType.Detect,      num: 1,   inuse: true);   // 17 loseit=F
        inv.EquippedSpecial = null;

        Assert.True(inv.LoseObj());   // mirror C: rval stays TRUE even when nothing lost

        Assert.True(inv.IsEquip(ObjType.ForwardGuns));
        Assert.True(inv.IsEquip(ObjType.SuperShield));
        Assert.True(inv.IsEquip(ObjType.Energy));
        Assert.True(inv.IsEquip(ObjType.Detect));
    }

    [Fact]
    public void LoseObj_skips_unowned_loseit_types()
    {
        // Sanity: a loseit type that is NOT owned must not be "removed" and must not
        // short-circuit the walk. Own only MicroMissile(2, loseit); higher-index
        // loseit types (e.g. GrdMissile) are unowned. LoseObj must remove MicroMissile.
        var inv = new Inventory();
        inv.Load(ObjType.MicroMissile, num: 1, inuse: true);   // 2 loseit, owned
        inv.EquippedSpecial = null;

        Assert.True(inv.LoseObj());

        Assert.False(inv.IsEquip(ObjType.MicroMissile));
    }
}
