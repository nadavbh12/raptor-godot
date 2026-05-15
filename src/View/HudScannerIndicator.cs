using System.Collections.Generic;

namespace Raptor.View;

internal static class HudScannerIndicator
{
    public readonly record struct Line(int X, int Y, int Height, int PaletteIndex);

    public sealed class State
    {
        public int CurrentDpos { get; private set; }

        public void AfterRenderFrame()
        {
        }

        public void AfterSimTick()
        {
            CurrentDpos = (CurrentDpos + 1) % 50;
        }
    }

    public static IEnumerable<Line> BuildIdle(int dpos)
    {
        int wrapped = ((dpos % 50) + 50) % 50;
        yield return new Line(110 + wrapped, 190, 3, 68);
        yield return new Line(110 + 99 - wrapped, 190, 3, 68);
    }
}
