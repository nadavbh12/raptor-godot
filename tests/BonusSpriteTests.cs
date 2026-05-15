using Raptor.View;
using Xunit;

namespace RaptorTests;

public class BonusSpriteTests
{
    [Theory]
    [InlineData(4, "BONUS04_PIC", 4)]
    [InlineData(16, "BONUS15_PIC", 4)]
    [InlineData(23, "BONUS22_PIC", 4)]
    public void Bonus_sprite_mapping_matches_c_obj_lib(int objType, string spriteName, int frameCount)
    {
        Assert.Equal(spriteName, BonusSprite.SpriteNameFor(objType, frame: 0));
        Assert.Equal(frameCount, BonusSprite.FrameCountFor(objType));
    }

    [Fact]
    public void Bonus_wobble_offsets_match_c_tables()
    {
        Assert.Equal((-1, -3), BonusSprite.DrawOffset(0));
        Assert.Equal((3, 0), BonusSprite.DrawOffset(5));
        Assert.Equal((-3, 0), BonusSprite.DrawOffset(13));
    }
}
