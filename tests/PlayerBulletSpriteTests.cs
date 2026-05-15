using Raptor.Sim.Shots;
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class PlayerBulletSpriteTests
{
    [Theory]
    [InlineData(WeaponType.ForwardGuns, "NMSHOT_BLK", 0)]
    [InlineData(WeaponType.MicroMissile, "MICROM_BLK", 0)]
    [InlineData(WeaponType.DumbMissile, "MISDUM_BLK", 1)]
    [InlineData(WeaponType.AirMissile, "MISRAT_BLK", 0)]
    [InlineData(WeaponType.GrdMissile, "MISGRD_BLK", 0)]
    [InlineData(WeaponType.MegaBomb, "MEGABM_BLK", 0)]
    public void Family_and_start_frame_match_shot_lib(WeaponType weapon, string family, int firstFrame)
    {
        Assert.Equal((family, firstFrame), PlayerBulletSprite.FrameFor(weapon, frameCounter: 0));
    }

    [Fact]
    public void Animation_wraps_with_weapon_frame_count()
    {
        Assert.Equal(("MICROM_BLK", 1), PlayerBulletSprite.FrameFor(WeaponType.MicroMissile, frameCounter: 1));
        Assert.Equal(("MICROM_BLK", 0), PlayerBulletSprite.FrameFor(WeaponType.MicroMissile, frameCounter: 2));
    }
}
