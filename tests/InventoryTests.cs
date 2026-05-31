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
