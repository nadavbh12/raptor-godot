using Raptor.Sim;
using Xunit;

namespace RaptorTests;

public class EnemyBaseDamageTests
{
    [Fact]
    public void No_bosses_on_screen_yields_zero()
        => Assert.Equal(0, WaveController.ComputeBaseDamage(System.Array.Empty<(bool boss, int y, int hly, int hits, int maxHits)>()));

    [Fact]
    public void Single_boss_returns_health_percent()
        => Assert.Equal(50, WaveController.ComputeBaseDamage(new[] { (boss: true, y: 10, hly: 12, hits: 50, maxHits: 100) }));

    [Fact]
    public void Offscreen_top_boss_is_excluded()
        => Assert.Equal(0, WaveController.ComputeBaseDamage(new[] { (boss: true, y: -30, hly: 12, hits: 50, maxHits: 100) }));

    [Fact]
    public void Non_boss_is_ignored()
        => Assert.Equal(0, WaveController.ComputeBaseDamage(new[] { (boss: false, y: 10, hly: 12, hits: 50, maxHits: 100) }));

    [Fact]
    public void Two_bosses_average_their_percents()
        => Assert.Equal(75, WaveController.ComputeBaseDamage(new[]
        {
            (boss: true, y: 10, hly: 12, hits: 100, maxHits: 100),
            (boss: true, y: 10, hly: 12, hits: 50,  maxHits: 100),
        }));

    [Fact]
    public void Boss_at_exactly_y_plus_hly_zero_is_included()
        => Assert.Equal(50, WaveController.ComputeBaseDamage(
            new[] { (boss: true, y: -12, hly: 12, hits: 50, maxHits: 100) }));

    [Fact]
    public void Boss_one_pixel_above_boundary_is_excluded()
        => Assert.Equal(0, WaveController.ComputeBaseDamage(
            new[] { (boss: true, y: -13, hly: 12, hits: 50, maxHits: 100) }));

    [Fact]
    public void Boss_at_full_health_is_one_hundred_percent()
        => Assert.Equal(100, WaveController.ComputeBaseDamage(
            new[] { (boss: true, y: 0, hly: 0, hits: 100, maxHits: 100) }));
}
