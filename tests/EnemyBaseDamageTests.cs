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
}
