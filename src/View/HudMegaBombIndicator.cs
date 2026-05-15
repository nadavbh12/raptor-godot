using System.Collections.Generic;

namespace Raptor.View;

internal static class HudMegaBombIndicator
{
    public readonly record struct Position(int X, int Y);

    public static IEnumerable<Position> Build(int count)
    {
        const int MapLeft = 16;  // SOURCE/MAP.H
        int x = MapLeft + 2;
        for (int i = 0; i < count; i++)
        {
            yield return new Position(x, 199 - 13);
            x += 13;
        }
    }
}
