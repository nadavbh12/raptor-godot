using System;
using System.Collections.Generic;

namespace Raptor.View;

internal static class HudShieldBar
{
    public const int MaxShield = 100;
    public const int Width = 4;
    public const int BottomY = 199;
    public const int ShieldColorRun = 9;

    // SOURCE/MAP.H. C draws two shield bars each frame (RAP.C:549/557):
    //   right (MAP_RIGHT+4) = regular shield, left (MAP_LEFT-8) = super-shield amount.
    public const int MapLeft  = 16;
    public const int MapRight = 320 - 16;
    public const int ShieldX      = MapRight + 4;   // right bar — regular shield
    public const int SuperShieldX = MapLeft - 8;    // left bar  — super-shield

    public readonly record struct Segment(int X, int Y, int Width, int Height, int PaletteIndex);

    /// <summary>
    /// The two HUD shield bars C draws every frame: the regular shield (right) and the
    /// super-shield amount (left). Mirrors RAP_DisplayShieldLevel(MAP_RIGHT+4, shield)
    /// and RAP_DisplayShieldLevel(MAP_LEFT-8, super) in RAP.C:549/557. Returns each
    /// bar's column x and fill level; the caller passes each to <see cref="Build"/>.
    /// </summary>
    public static IEnumerable<(int X, int Level)> Bars(int shield, int superShield)
    {
        yield return (ShieldX, shield);
        yield return (SuperShieldX, superShield);
    }

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
