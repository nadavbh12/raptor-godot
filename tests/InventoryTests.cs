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
