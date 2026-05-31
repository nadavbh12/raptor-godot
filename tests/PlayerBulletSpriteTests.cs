using Raptor.Sim;
using Raptor.Sim.Shots;
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class PlayerBulletSpriteTests
{
    [Theory]
    [InlineData(ObjType.ForwardGuns, "NMSHOT_BLK", 0)]
    [InlineData(ObjType.MicroMissile, "MICROM_BLK", 0)]
    [InlineData(ObjType.DumbMissile, "MISDUM_BLK", 1)]
    [InlineData(ObjType.AirMissile, "MISRAT_BLK", 0)]
    [InlineData(ObjType.GrdMissile, "MISGRD_BLK", 0)]
    [InlineData(ObjType.MegaBomb, "MEGABM_BLK", 0)]
    public void Family_and_start_frame_match_shot_lib(ObjType weapon, string family, int firstFrame)
    {
        Assert.Equal((family, firstFrame), PlayerBulletSprite.FrameFor(weapon, frameCounter: 0));
    }

    [Fact]
    public void Animation_wraps_with_weapon_frame_count()
    {
        Assert.Equal(("MICROM_BLK", 1), PlayerBulletSprite.FrameFor(ObjType.MicroMissile, frameCounter: 1));
        Assert.Equal(("MICROM_BLK", 0), PlayerBulletSprite.FrameFor(ObjType.MicroMissile, frameCounter: 2));
    }
}
