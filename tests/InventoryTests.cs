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
        Assert.Equal(25, (int)ObjType.LastObject);
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

    // Add's guard mirrors C `type >= S_LAST_OBJECT`; LastObject (25) is that
    // sentinel and is the true out-of-range boundary.
    [Fact]
    public void Add_out_of_range_type_returns_error_and_creates_no_slot()
    {
        var inv = new Inventory();
        Assert.Equal(BuyStuff.Error, inv.Add(ObjType.LastObject));
        Assert.False(inv.IsEquip(ObjType.LastObject));
        Assert.Equal(0, inv.GetAmt(ObjType.LastObject));
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
