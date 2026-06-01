using System.Collections.Generic;

namespace Raptor.View;

/// OBJECTS.C:663-672 — one SMSHIELD_PIC per owned super-shield, x = MAP_LEFT+2,
/// stepping +13, on row y=1.
internal static class HudSuperShieldIndicator
{
    public readonly record struct Position(int X, int Y);

    public static IEnumerable<Position> Build(int count)
    {
        const int MapLeft = 16; // SOURCE/MAP.H
        int x = MapLeft + 2;
        for (int i = 0; i < count; i++)
        {
            yield return new Position(x, 1);
            x += 13;
        }
    }
}
