using System.Collections.Generic;
using System.Linq;
using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

// Task 3.5: StoreLogic now reads ownership from the live Inventory (GetAmt/IsEquip)
// instead of the old hardcoded StarterInventory dictionary. These tests pin
// OwnedCount and the OBJS_CanSell-mirroring MakeSellItems behaviour.
public class StoreLogicTests
{
    // Helper: build a StoreLogic over a given inventory and navigate the SELL
    // list to the slot for `target`, returning the OwnedCount reported there.
    private static int OwnedCountFor(Inventory inv, ObjType target)
    {
        var store = new StoreLogic(inv);
        // OwnedCount reads CurrentObject in the current mode. Walk BUY list
        // (every catalog buyable appears there) to land on `target`.
        // First NextItem dismisses the greeting; CurItem starts at 0.
        var list = store.CurrentList;
        Assert.Contains(target, list);
        // Dismiss greeting, then step to the target index.
        int targetIndex = list.ToList().IndexOf(target);
        store.NextItem();            // dismiss greeting, CurItem stays 0
        for (int i = 0; i < targetIndex; i++) store.NextItem();
        Assert.Equal(target, store.CurrentObject);
        return store.OwnedCount;
    }

    [Fact]
    public void OwnedCount_reflects_injected_inventory_energy()
    {
        var inv = new Inventory();
        inv.Load(ObjType.Energy, 75, inuse: true);

        Assert.Equal(75, OwnedCountFor(inv, ObjType.Energy));
    }

    [Fact]
    public void OwnedCount_is_zero_for_unowned_item()
    {
        var inv = new Inventory();
        // Nothing owned at all → any item reports 0.
        Assert.Equal(0, OwnedCountFor(inv, ObjType.Energy));
    }

    [Fact]
    public void OwnedCount_reports_count_for_owned_but_unequipped_item()
    {
        var inv = new Inventory();
        // Owned slot but not equipped (inuse=false). Pins the load-bearing
        // GetAmt deviation (Inventory.cs): GetAmt returns Num regardless of
        // InUse so save/load round-trips report purchased-but-unequipped
        // quantities. OwnedCount flows straight through GetAmt.
        inv.Load(ObjType.Energy, 50, inuse: false);

        Assert.Equal(50, OwnedCountFor(inv, ObjType.Energy));
    }

    [Fact]
    public void SellList_includes_owned_equipped_weapon_with_enough_count()
    {
        var inv = new Inventory();
        // MiniGun: owned + equipped, num=1 >= start_cnt=1 → sellable.
        inv.Load(ObjType.MiniGun, 1, inuse: true);

        var store = new StoreLogic(inv);
        Assert.Contains(ObjType.MiniGun, store.SellItems);
    }

    [Fact]
    public void SellList_excludes_unequipped_item()
    {
        var inv = new Inventory();
        // Owned (slot exists, num>=start_cnt) but NOT equipped (inuse=false)
        // → OBJS_CanSell: p_objs[type]==NULL → not sellable.
        inv.Load(ObjType.MiniGun, 1, inuse: false);

        var store = new StoreLogic(inv);
        Assert.DoesNotContain(ObjType.MiniGun, store.SellItems);
    }

    [Fact]
    public void SellList_excludes_unowned_item()
    {
        var inv = new Inventory();
        var store = new StoreLogic(inv);
        Assert.DoesNotContain(ObjType.MiniGun, store.SellItems);
    }

    [Fact]
    public void SellList_empty_inventory_is_empty()
    {
        var inv = new Inventory();
        var store = new StoreLogic(inv);
        Assert.Empty(store.SellItems);
    }

    [Fact]
    public void SellList_excludes_energy_at_starter_count()
    {
        var inv = new Inventory();
        // Energy onlyflag, start_cnt=25. num==25 → cannot sell below the
        // starter energy (OBJS_CanSell: onlyflag && type==Energy && num<=start_cnt).
        inv.Load(ObjType.Energy, 25, inuse: true);

        var store = new StoreLogic(inv);
        Assert.DoesNotContain(ObjType.Energy, store.SellItems);
    }

    [Fact]
    public void SellList_includes_energy_above_starter_count()
    {
        var inv = new Inventory();
        // num=50 > start_cnt=25 → sellable.
        inv.Load(ObjType.Energy, 50, inuse: true);

        var store = new StoreLogic(inv);
        Assert.Contains(ObjType.Energy, store.SellItems);
    }

    [Fact]
    public void SellList_includes_detect_when_owned()
    {
        var inv = new Inventory();
        // Detect=17 is beyond the old LastBuyableType=17 inclusive bound, but
        // the sell loop must reach it. Detect: onlyflag, start_cnt=1, num=1
        // > start_cnt? No — num<=start_cnt. onlyflag guard only applies to
        // Energy, so Detect with num>=start_cnt(1) is sellable.
        inv.Load(ObjType.Detect, 1, inuse: true);

        var store = new StoreLogic(inv);
        Assert.Contains(ObjType.Detect, store.SellItems);
    }

    [Fact]
    public void SellList_sorted_cost_ascending_with_type_tiebreak()
    {
        var inv = new Inventory();
        inv.Load(ObjType.Energy, 50, inuse: true);   // effective cost 400*25 = 10000
        inv.Load(ObjType.Detect, 1, inuse: true);     // effective cost 10000*1 = 10000
        inv.Load(ObjType.MiniGun, 1, inuse: true);    // cost 250650

        var store = new StoreLogic(inv);
        var sell = store.SellItems.ToList();
        // Energy(16) and Detect(17) tie at 10000 → Energy first (lower type id);
        // MiniGun(250650) last.
        Assert.Equal(ObjType.Energy, sell[0]);
        Assert.Equal(ObjType.Detect, sell[1]);
        Assert.Equal(ObjType.MiniGun, sell[2]);
    }
}
