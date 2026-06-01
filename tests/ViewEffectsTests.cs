using System.Linq;
using Raptor.View;
using Xunit;

namespace RaptorTests;

public class ViewEffectsTests
{
    [Fact]
    public void Spawned_effect_is_active_for_its_frame_span_then_pruned()
    {
        var fx = new ViewEffects();
        fx.Spawn("GUNSTR_BLK", totalFrames: 4, x: 100, y: 50, spawnIter: 10, ground: false);

        Assert.Single(fx.Active(10));
        Assert.Equal(0, fx.Active(10).Single().Frame);
        Assert.Equal(3, fx.Active(13).Single().Frame);
        Assert.Empty(fx.Active(14));
    }

    [Fact]
    public void Active_reports_position_and_family_unchanged()
    {
        var fx = new ViewEffects();
        fx.Spawn("SHIPGLOW_BLK", totalFrames: 4, x: 7, y: 9, spawnIter: 0, ground: true);
        var e = fx.Active(1).Single();
        Assert.Equal("SHIPGLOW_BLK", e.Family);
        Assert.Equal(7, e.X);
        Assert.Equal(9, e.Y);
        Assert.True(e.Ground);
        Assert.Equal(1, e.Frame);
    }

    [Fact]
    public void Prune_removes_expired_so_the_list_does_not_grow_unbounded()
    {
        var fx = new ViewEffects();
        fx.Spawn("GUNSTR_BLK", 4, 0, 0, spawnIter: 0, ground: false);
        fx.Prune(currentIter: 100);
        Assert.Equal(0, fx.Count);
    }

    [Fact]
    public void Prune_retains_still_active_entries()
    {
        var fx = new ViewEffects();
        fx.Spawn("GUNSTR_BLK", totalFrames: 4, x: 0, y: 0, spawnIter: 0, ground: false);
        fx.Prune(currentIter: 3); // frame 3 of 4 — still alive
        Assert.Equal(1, fx.Count);
        fx.Prune(currentIter: 4); // now expired
        Assert.Equal(0, fx.Count);
    }

    [Fact]
    public void Clear_empties_the_list()
    {
        var fx = new ViewEffects();
        fx.Spawn("X", 1, 0, 0, 0, false);
        fx.Clear();
        Assert.Equal(0, fx.Count);
    }
}
