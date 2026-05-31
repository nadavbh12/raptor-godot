using Raptor.Sim;
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class HudWeaponIconTests
{
    [Theory]
    [InlineData(ObjType.DumbMissile, "BONUS03_PIC")]
    [InlineData(ObjType.MiniGun, "BONUS04_PIC")]
    [InlineData(ObjType.Bomb, "BONUS21_PIC")]
    [InlineData(ObjType.ForwardLaser, "BONUS12_PIC")]
    [InlineData(ObjType.SuperShield, "")]
    public void Weapon_icons_match_c_obj_lib_items(ObjType weapon, string expectedSpriteName)
    {
        Assert.Equal(expectedSpriteName, HudWeaponIcon.SpriteNameFor(weapon));
    }
}
