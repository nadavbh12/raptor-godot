using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class HudSuperShieldIndicatorTests
{
    [Fact]
    public void Icons_at_map_left_plus_2_spaced_13_on_row_1()
    {
        var ps = HudSuperShieldIndicator.Build(3).ToArray();
        Assert.Equal(3, ps.Length);
        Assert.Equal(new HudSuperShieldIndicator.Position(18, 1), ps[0]);
        Assert.Equal(new HudSuperShieldIndicator.Position(31, 1), ps[1]);
        Assert.Equal(new HudSuperShieldIndicator.Position(44, 1), ps[2]);
    }

    [Fact]
    public void Zero_count_builds_nothing() => Assert.Empty(HudSuperShieldIndicator.Build(0));

    // Property "Icon-count exactness": count == input; icon i at x = 18 + 13*i, y = 1.
    [Property(MaxTest = 50)]
    public Property Icon_count_equals_input_and_x_is_left2_plus_13i()
        => Prop.ForAll(Gen.Choose(0, 30).ToArbitrary(), n =>
        {
            var ps = HudSuperShieldIndicator.Build(n).ToArray();
            if (ps.Length != n) return false;
            for (int i = 0; i < n; i++)
                if (ps[i].X != 18 + 13 * i || ps[i].Y != 1) return false;
            return true;
        });
}
