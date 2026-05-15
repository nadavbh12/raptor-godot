using Raptor.Sim.Shots;
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class HudWeaponIconTests
{
    [Theory]
    [InlineData(WeaponType.DumbMissile, "BONUS03_PIC")]
    [InlineData(WeaponType.MiniGun, "BONUS04_PIC")]
    [InlineData(WeaponType.Bomb, "BONUS21_PIC")]
    [InlineData(WeaponType.ForwardLaser, "BONUS12_PIC")]
    public void Weapon_icons_match_c_obj_lib_items(WeaponType weapon, string expectedSpriteName)
    {
        Assert.Equal(expectedSpriteName, HudWeaponIcon.SpriteNameFor(weapon));
    }
}
