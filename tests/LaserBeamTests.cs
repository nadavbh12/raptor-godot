using System.Linq;
using Raptor.View;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// ESHOT.C:560 — the ES_LASER column steps ELASER_BLK every 3px from shot.y
/// down to move.y2 (exclusive).
/// </summary>
public class LaserBeamTests
{
    [Fact]
    public void Column_steps_by_three_from_y_to_y2()
        => Assert.Equal(new[] { 10, 13, 16, 19 }, LaserBeam.ColumnYs(10, 22).ToArray());

    [Fact]
    public void Empty_when_y2_at_or_below_y()
        => Assert.Empty(LaserBeam.ColumnYs(20, 20));
}
