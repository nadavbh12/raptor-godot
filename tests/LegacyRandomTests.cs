using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

public class LegacyRandomTests
{
    [Fact]
    public void Next_matches_macos_libc_rand_sequence()
    {
        var rng = new LegacyRandom(3072);

        Assert.Equal(51631104, rng.Next());
        Assert.Equal(180571540, rng.Next());
        Assert.Equal(471479569, rng.Next());
        Assert.Equal(2089942400, rng.Next());
    }

    [Fact]
    public void Next_maxValue_matches_C_random_macro()
    {
        var rng = new LegacyRandom(3072);

        Assert.Equal(0, rng.Next(4));
        Assert.Equal(4, rng.Next(16));
        Assert.Equal(1, rng.Next(8));
    }
}
