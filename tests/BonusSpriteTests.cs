using Raptor.View;
using Xunit;

namespace RaptorTests;

public class BonusSpriteTests
{
    [Theory]
    [InlineData(4, "BONUS04_PIC", 4)]
    [InlineData(16, "BONUS15_PIC", 4)]
    [InlineData(23, "BONUS22_PIC", 4)]
    [InlineData(24, "BONUS22_PIC", 4)]
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

    [Fact]
    public void Picked_up_money_uses_c_dollar_sprite()
    {
        Assert.Equal("N$_PIC", BonusSprite.PickedUpMoneySpriteName);
    }

    [Fact]
    public void Bonus_glow_alpha_is_subdued_for_c_light_table()
    {
        Assert.InRange(BonusSprite.GlowAlpha, 0.55f, 0.65f);
    }

    [Fact]
    public void Ground_vehicle_shadow_alpha_is_darker_than_previous_tuning()
    {
        Assert.Equal(0.45f, DebugRenderer.GroundShadowAlpha);
    }
}
