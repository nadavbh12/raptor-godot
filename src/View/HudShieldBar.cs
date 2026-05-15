using System;
using System.Collections.Generic;

namespace Raptor.View;

internal static class HudShieldBar
{
    public const int MaxShield = 100;
    public const int Width = 4;
    public const int BottomY = 199;
    public const int ShieldColorRun = 9;

    public readonly record struct Segment(int X, int Y, int Width, int Height, int PaletteIndex);

    public static IEnumerable<Segment> Build(int x, int level)
    {
        int clampedLevel = Math.Clamp(level, 0, MaxShield);
        uint addx = (ShieldColorRun << 16) / MaxShield;
        uint curs = 0;

        for (int loop = 0; loop < MaxShield; loop++)
        {
            int paletteIndex = loop < clampedLevel ? 74 - (int)(curs >> 16) : 0;
            yield return new Segment(x, BottomY - loop * 2, Width, 1, paletteIndex);
            curs += addx;
        }
    }
}
