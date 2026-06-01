using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class BossSmokeTests
{
    [Fact]
    public void Spawns_only_when_boss_low_and_gl_cnt_bit1_set()
    {
        Assert.True(BossSmoke.ShouldSpawn(hits: 49, glCnt: 2));
        Assert.False(BossSmoke.ShouldSpawn(hits: 49, glCnt: 1)); // 1 & 2 == 0
        Assert.False(BossSmoke.ShouldSpawn(hits: 50, glCnt: 2)); // not low enough
    }

    [Fact]
    public void Offset_is_half_width_half_height()
        => Assert.Equal((100 + 16, 40 + 12), BossSmoke.SpawnPoint(x: 100, y: 40, width: 32, height: 24));

    // Property "Deterministic positions": offset is exactly (width/2, height/2), no RNG.
    [Property(MaxTest = 50)]
    public Property Spawn_point_is_exactly_half_bounds()
    {
        var gen =
            from x in Gen.Choose(0, 320)
            from y in Gen.Choose(0, 200)
            from w in Gen.Choose(1, 64)
            from h in Gen.Choose(1, 64)
            select (x, y, w, h);
        return Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var (x, y, w, h) = t;
            var (sx, sy) = BossSmoke.SpawnPoint(x, y, w, h);
            return sx == x + w / 2 && sy == y + h / 2;
        });
    }
}
