using Raptor.Sim.Shots;
using Xunit;

namespace RaptorTests;

public class MegaBombFlashTests
{
    [Fact]
    public void Signal_then_consume_returns_true_once_then_false()
    {
        var f = new MegaBombFlash();
        Assert.False(f.Consume());
        f.Signal();
        Assert.True(f.Consume());
        Assert.False(f.Consume());
    }
}
