using Raptor.View;
using Xunit;

namespace Raptor.Tests;

public class WorldClipperTests
{
    [Theory]
    [InlineData(12, 8, 16, 4, 4)]
    [InlineData(300, 16, 300, 0, 4)]
    public void World_sprite_clip_stays_inside_side_gutters(
        int sourceX,
        int width,
        int expectedDestX,
        int expectedSrcX,
        int expectedWidth)
    {
        Assert.True(WorldClipper.TryClipHorizontal(sourceX, width, out var clip));
        Assert.Equal(expectedDestX, clip.DestX);
        Assert.Equal(expectedSrcX, clip.SourceX);
        Assert.Equal(expectedWidth, clip.Width);
    }

    [Theory]
    [InlineData(-20, 8)]
    [InlineData(304, 8)]
    public void World_sprite_clip_rejects_fully_guttered_sprites(int sourceX, int width)
    {
        Assert.False(WorldClipper.TryClipHorizontal(sourceX, width, out _));
    }
}
