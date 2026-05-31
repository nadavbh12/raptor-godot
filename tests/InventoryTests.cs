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
        inv.Add(ObjType.ItemBuy1);
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
}
