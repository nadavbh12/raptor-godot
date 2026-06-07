using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// Finding #20 — SuperShield is a multi-node inventory item (C onlyflag=FALSE,
/// OBJECTS.C:489), so each buy appends a fresh OBJ node (num=100) to
/// first_objs..last_objs rather than folding into one slot. These tests pin the
/// C-faithful multi-node behavior: node-counting GetTotal, the 5-node Buy cap,
/// SubEnergy draining the equipped node then promoting the next (OBJS_Del →
/// OBJS_Equip), Sell(Detect) un-equipping WITHOUT removing the node, and the
/// obj_hash folding each node separately.
///
/// obj_hash goldens are C-algorithm-derived (tools/obj_hash_oracle.py, a port of
/// C compute_obj_hash verified to reproduce the two committed goldens exactly:
/// empty → cbf29ce484222325, fresh pilot → 4445527f98b766af). No live C recording
/// buys SuperShields yet, so this scenario IS the TDD oracle for the refactor.
/// </summary>
public class InventorySuperShieldTests
{
    private static Inventory FreshPilot()
    {
        var inv = new Inventory();
        inv.SeedNewPilot();   // [ForwardGuns(0,1), Energy(16,75)]
        return inv;
    }

    [Fact]
    public void Buy_two_super_shields_creates_two_full_nodes()
    {
        var inv = FreshPilot();
        uint score = 1_000_000;
        Assert.Equal(BuyStuff.GotIt, inv.Buy(ObjType.SuperShield, ref score));
        Assert.Equal(BuyStuff.GotIt, inv.Buy(ObjType.SuperShield, ref score));
        // GetTotal counts NODES (OBJS_GetTotal), not points.
        Assert.Equal(2, inv.GetTotal(ObjType.SuperShield));
        // The equipped node reports a full 100 (GetAmt = p_objs[type]->num).
        Assert.Equal(100, inv.GetAmt(ObjType.SuperShield));
        Assert.True(inv.IsEquip(ObjType.SuperShield));
        // Two buys deducted twice (cost 78_500 each).
        Assert.Equal(1_000_000u - 2 * 78_500u, score);
    }

    [Fact]
    public void Two_super_shield_nodes_each_fold_into_obj_hash()
    {
        var inv = FreshPilot();
        inv.Add(ObjType.SuperShield);
        inv.Add(ObjType.SuperShield);
        // tools/obj_hash_oracle.py fresh_plus_2_supershields:
        //   [(0,1),(16,75),(15,100),(15,100)] → ff7977dc6ae4ef27
        Assert.Equal(0xff7977dc6ae4ef27UL, inv.ComputeObjHash());
    }

    [Fact]
    public void Buy_super_shield_cap_blocks_at_five_nodes()
    {
        var inv = FreshPilot();
        uint score = 10_000_000;
        for (int i = 0; i < 5; i++)
            Assert.Equal(BuyStuff.GotIt, inv.Buy(ObjType.SuperShield, ref score));
        Assert.Equal(5, inv.GetTotal(ObjType.SuperShield));
        // 6th hits the cap (GetTotal(SuperShield) >= 5 → ShipFull), no deduction.
        uint scoreBefore = score;
        Assert.Equal(BuyStuff.ShipFull, inv.Buy(ObjType.SuperShield, ref score));
        Assert.Equal(scoreBefore, score);
        Assert.Equal(5, inv.GetTotal(ObjType.SuperShield));
    }

    [Fact]
    public void SubEnergy_drains_equipped_shield_then_promotes_next_node()
    {
        var inv = FreshPilot();
        inv.Add(ObjType.SuperShield);   // node A (equipped), num 100
        inv.Add(ObjType.SuperShield);   // node B (un-equipped), num 100
        Assert.Equal(2, inv.GetTotal(ObjType.SuperShield));

        // Drain the equipped node below 0 → OBJS_Del removes it, OBJS_Equip
        // promotes node B. The supply is NOT wiped out.
        inv.SubEnergy(101);

        Assert.True(inv.IsEquip(ObjType.SuperShield));         // promoted node B
        Assert.Equal(1, inv.GetTotal(ObjType.SuperShield));    // one node left
        Assert.Equal(100, inv.GetAmt(ObjType.SuperShield));    // full again (node B)
    }

    [Fact]
    public void Sell_detect_unequips_but_keeps_node_in_obj_hash()
    {
        var inv = FreshPilot();
        inv.Add(ObjType.Detect);   // [(0,1),(16,75),(17,1)]
        Assert.True(inv.IsEquip(ObjType.Detect));
        // fresh_plus_detect oracle: 536a4148e2f5f021
        Assert.Equal(0x536a4148e2f5f021UL, inv.ComputeObjHash());

        uint score = 0;
        inv.Sell(ObjType.Detect, ref score);

        // C OBJS_Sell(S_DETECT): p_objs[type]=NUL (un-equip) but NO OBJS_Remove —
        // the node stays in the list and STILL folds [17,1] into obj_hash.
        Assert.False(inv.IsEquip(ObjType.Detect));
        Assert.Equal(0x536a4148e2f5f021UL, inv.ComputeObjHash());
        Assert.Equal((uint)(10_000 >> 1), score);   // resale = cost(10000) >> 1
    }

    // --- Regression guards: the committed obj_hash goldens must stay exact ----

    [Fact]
    public void Empty_and_fresh_pilot_obj_hash_goldens_unchanged()
    {
        Assert.Equal(0xcbf29ce484222325UL, new Inventory().ComputeObjHash());
        Assert.Equal(0x4445527f98b766afUL, FreshPilot().ComputeObjHash());
    }
}
