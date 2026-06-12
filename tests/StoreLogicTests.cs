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
    // Helper: a StoreLogic over `inv` with a local mutable score (default 10000)
    // exposed via get/set lambdas — mirrors the new live-score ctor without a
    // WaveController. Returns the store plus a closure to read the live score.
    private static StoreLogic NewStore(Inventory inv, uint startScore = 10000)
    {
        uint[] score = { startScore };   // boxed in an array so the lambdas share it
        return new StoreLogic(inv, () => score[0], v => score[0] = v);
    }

    [Fact]
    public void Store_carries_the_pilots_callsign_and_portrait()
    {
        // STORE_Enter shows plr.callsign + id_pics[plr.id_pic] (STORE.C:257/261).
        // Previously these were hardcoded ("T1" / portrait 0), so every pilot looked
        // the same in the supply room.
        var store = new StoreLogic(new Inventory(), () => 1000u, _ => { },
            callsign: "MAV", idPic: 2);
        Assert.Equal("MAV", store.Callsign);
        Assert.Equal(2, store.IdPic);
    }

    // Helper: build a StoreLogic over a given inventory and navigate the SELL
    // list to the slot for `target`, returning the OwnedCount reported there.
    private static int OwnedCountFor(Inventory inv, ObjType target)
    {
        var store = NewStore(inv);
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

        var store = NewStore(inv);
        Assert.Contains(ObjType.MiniGun, store.SellItems);
    }

    [Fact]
    public void SellList_excludes_unequipped_item()
    {
        var inv = new Inventory();
        // Owned (slot exists, num>=start_cnt) but NOT equipped (inuse=false)
        // → OBJS_CanSell: p_objs[type]==NULL → not sellable.
        inv.Load(ObjType.MiniGun, 1, inuse: false);

        var store = NewStore(inv);
        Assert.DoesNotContain(ObjType.MiniGun, store.SellItems);
    }

    [Fact]
    public void SellList_excludes_unowned_item()
    {
        var inv = new Inventory();
        var store = NewStore(inv);
        Assert.DoesNotContain(ObjType.MiniGun, store.SellItems);
    }

    [Fact]
    public void SellList_empty_inventory_is_empty()
    {
        var inv = new Inventory();
        var store = NewStore(inv);
        Assert.Empty(store.SellItems);
    }

    [Fact]
    public void SellList_excludes_energy_at_starter_count()
    {
        var inv = new Inventory();
        // Energy onlyflag, start_cnt=25. num==25 → cannot sell below the
        // starter energy (OBJS_CanSell: onlyflag && type==Energy && num<=start_cnt).
        inv.Load(ObjType.Energy, 25, inuse: true);

        var store = NewStore(inv);
        Assert.DoesNotContain(ObjType.Energy, store.SellItems);
    }

    [Fact]
    public void SellList_includes_energy_above_starter_count()
    {
        var inv = new Inventory();
        // num=50 > start_cnt=25 → sellable.
        inv.Load(ObjType.Energy, 50, inuse: true);

        var store = NewStore(inv);
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

        var store = NewStore(inv);
        Assert.Contains(ObjType.Detect, store.SellItems);
    }

    [Fact]
    public void SellList_sorted_cost_ascending_with_type_tiebreak()
    {
        var inv = new Inventory();
        inv.Load(ObjType.Energy, 50, inuse: true);   // effective cost 400*25 = 10000
        inv.Load(ObjType.Detect, 1, inuse: true);     // effective cost 10000*1 = 10000
        inv.Load(ObjType.MiniGun, 1, inuse: true);    // cost 250650

        var store = NewStore(inv);
        var sell = store.SellItems.ToList();
        // Energy(16) and Detect(17) tie at 10000 → Energy first (lower type id);
        // MiniGun(250650) last.
        Assert.Equal(ObjType.Energy, sell[0]);
        Assert.Equal(ObjType.Detect, sell[1]);
        Assert.Equal(ObjType.MiniGun, sell[2]);
    }
}

// Task 5.2: StoreLogic.Buy/Sell transact against the live player score and
// recompute + reposition the active list onto the same object (STORE.C STOR_BUYIT).
public class StoreLogicTransactTests
{
    private static (StoreLogic store, uint[] score) NewStore(Inventory inv, uint startScore)
    {
        uint[] score = { startScore };
        return (new StoreLogic(inv, () => score[0], v => score[0] = v), score);
    }

    // Switch to Sell mode regardless of the greeting state: the first ToggleMode
    // only dismisses the greeting (StoreLogic semantics), so toggle twice if so.
    private static void EnterSellMode(StoreLogic store)
    {
        if (store.ShowingGreeting) store.NextItem();   // dismiss greeting first
        store.ToggleMode();                            // Buy → Sell, CurItem=0
        Assert.Equal(StoreLogic.Mode.Sell, store.CurrentMode);
    }

    // Navigate the current list onto `target`. Dismisses the greeting if showing,
    // then steps from CurItem=0 to the target's index.
    private static void NavigateTo(StoreLogic store, ObjType target)
    {
        if (store.ShowingGreeting) store.NextItem();   // dismiss greeting (CurItem stays 0)
        var list = store.CurrentList.ToList();
        int idx = list.IndexOf(target);
        Assert.True(idx >= 0, $"{target} not in current list");
        for (int i = 0; i < idx; i++) store.NextItem();
        Assert.Equal(target, store.CurrentObject);
    }

    [Fact]
    public void Money_reflects_live_score()
    {
        var inv = new Inventory();
        var (store, _) = NewStore(inv, 42_000);
        Assert.Equal(42_000, store.Money);
    }

    [Fact]
    public void Buy_current_item_deducts_score_and_updates_owned_count()
    {
        var inv = new Inventory();
        var (store, score) = NewStore(inv, 1_000_000);
        NavigateTo(store, ObjType.MiniGun);   // cost 250650

        var rval = store.Buy();
        Assert.Equal(BuyStuff.GotIt, rval);
        Assert.Equal(1_000_000u - 250_650u, score[0]);     // live score deducted
        Assert.Equal(749_350, store.Money);                // same value, via the accessor
        // After buy, MiniGun is owned; OwnedCount on the current item reflects it.
        Assert.Equal(ObjType.MiniGun, store.CurrentObject); // repositioned onto same item
        Assert.Equal(1, store.OwnedCount);
    }

    [Fact]
    public void Buy_insufficient_funds_leaves_score_and_inventory_untouched()
    {
        var inv = new Inventory();
        var (store, score) = NewStore(inv, 100);            // can't afford anything
        NavigateTo(store, ObjType.MiniGun);

        var rval = store.Buy();
        Assert.Equal(BuyStuff.NoMoney, rval);
        Assert.Equal(100u, score[0]);
        Assert.False(inv.IsEquip(ObjType.MiniGun));
    }

    [Fact]
    public void Buy_repositions_cursor_onto_same_item_after_recompute()
    {
        var inv = new Inventory();
        var (store, _) = NewStore(inv, 5_000_000);
        // ForwardGuns is excluded from BUY; cheapest buyables lead. Buying does not
        // remove an item from the BUY list (you can re-buy), so the cursor must
        // still point at the same object afterwards.
        NavigateTo(store, ObjType.PlasmaGuns);
        store.Buy();
        Assert.Equal(ObjType.PlasmaGuns, store.CurrentObject);
    }

    [Fact]
    public void Sell_current_item_adds_resale_to_score()
    {
        var inv = new Inventory();
        inv.Load(ObjType.MiniGun, 1, inuse: true);          // owned + equipped
        var (store, score) = NewStore(inv, 0);
        EnterSellMode(store);
        NavigateTo(store, ObjType.MiniGun);

        int left = store.Sell();
        Assert.Equal(0, left);                               // non-onlyflag → whole slot gone
        Assert.Equal(125_325u, score[0]);                    // 250650 >> 1
        Assert.False(inv.IsEquip(ObjType.MiniGun));
    }

    [Fact]
    public void Sell_sold_out_item_clamps_cursor_to_valid_object()
    {
        var inv = new Inventory();
        // Two sellable weapons; sell one → it leaves the SELL list, cursor clamps.
        inv.Load(ObjType.MiniGun, 1, inuse: true);
        inv.Load(ObjType.DeathRay, 1, inuse: true);
        var (store, _) = NewStore(inv, 0);
        EnterSellMode(store);
        NavigateTo(store, ObjType.MiniGun);

        store.Sell();
        // MiniGun is gone from the SELL list now; cursor must point at a still-valid
        // object (DeathRay) or null only if the list is empty (it isn't here).
        Assert.NotNull(store.CurrentObject);
        Assert.Equal(ObjType.DeathRay, store.CurrentObject);
        Assert.DoesNotContain(ObjType.MiniGun, store.SellItems);
    }

    [Fact]
    public void Sell_energy_decrements_and_keeps_item_in_list_repositioned()
    {
        var inv = new Inventory();
        inv.Load(ObjType.Energy, 75, inuse: true);           // onlyflag+forever
        var (store, score) = NewStore(inv, 0);
        EnterSellMode(store);
        NavigateTo(store, ObjType.Energy);

        int left = store.Sell();
        Assert.Equal(50, left);                              // 75 - 25
        Assert.Equal(5_000u, score[0]);                      // (400*25)>>1
        // 50 > start_cnt(25) → still sellable, still in list, cursor on Energy.
        Assert.Equal(ObjType.Energy, store.CurrentObject);
        Assert.Contains(ObjType.Energy, store.SellItems);
    }

    // FIX 1: ToggleMode must recompute the now-active list (STORE.C:548-555), so a
    // weapon bought in Buy mode appears in SellItems after switching to Sell mode.
    // Without the recompute, SellItems stays the (empty) construction-time snapshot.
    [Fact]
    public void Buy_then_toggle_to_sell_shows_bought_item_in_sell_list()
    {
        var inv = new Inventory();                           // starts empty → SellItems empty
        var (store, _) = NewStore(inv, 5_000_000);
        NavigateTo(store, ObjType.MiniGun);
        Assert.Equal(BuyStuff.GotIt, store.Buy());           // now own MiniGun
        Assert.DoesNotContain(ObjType.MiniGun, store.SellItems);  // stale snapshot pre-toggle

        EnterSellMode(store);                                // ToggleMode → recompute SellItems
        Assert.Contains(ObjType.MiniGun, store.SellItems);   // freshly bought item now sellable
    }

    // FIX 4: first Buy()/Sell() press while the greeting is up only dismisses it —
    // no transaction (matches the sibling NextItem/PrevItem/ToggleMode guards).
    [Fact]
    public void Buy_while_greeting_only_dismisses_greeting_no_transaction()
    {
        var inv = new Inventory();
        var (store, score) = NewStore(inv, 1_000_000);
        Assert.True(store.ShowingGreeting);

        var rval = store.Buy();                              // first press: dismiss only
        Assert.Equal(BuyStuff.Error, rval);
        Assert.False(store.ShowingGreeting);
        Assert.Equal(1_000_000u, score[0]);                  // score untouched
        // No item bought (CurItem=0 → whatever the first buyable is) — confirm by
        // checking the live inventory has nothing equipped from a transaction.
        Assert.False(inv.IsEquip(store.CurrentObject!.Value));
    }

    [Fact]
    public void Sell_while_greeting_only_dismisses_greeting_no_transaction()
    {
        var inv = new Inventory();
        inv.Load(ObjType.MiniGun, 1, inuse: true);           // owned + sellable
        var (store, score) = NewStore(inv, 0);
        Assert.True(store.ShowingGreeting);

        int left = store.Sell();                             // first press: dismiss only
        Assert.Equal(0, left);
        Assert.False(store.ShowingGreeting);
        Assert.Equal(0u, score[0]);                          // no resale credited
        Assert.True(inv.IsEquip(ObjType.MiniGun));           // still owned (not sold)
    }
}
