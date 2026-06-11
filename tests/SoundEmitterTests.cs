using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

public class SoundEmitterTests
{
    [Fact]
    public void StopAll_invokes_the_installed_sink()
    {
        int calls = 0;
        var prev = SoundEmitter.StopAllSink;
        try
        {
            SoundEmitter.StopAllSink = () => calls++;
            SoundEmitter.StopAll();
            Assert.Equal(1, calls);
        }
        finally { SoundEmitter.StopAllSink = prev; }
    }

    [Fact]
    public void StopAll_is_a_noop_when_no_sink_installed()
    {
        var prev = SoundEmitter.StopAllSink;
        try
        {
            SoundEmitter.StopAllSink = null;   // headless / before any View
            SoundEmitter.StopAll();            // must not throw
        }
        finally { SoundEmitter.StopAllSink = prev; }
    }
}
