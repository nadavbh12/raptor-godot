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

    public readonly record struct Box(int X, int Y, int W, int H, int PaletteIndex);

    /// Mirrors OBJECTS.C:644-647 (boss-health/scanner bar). MAP_BOTTOM=182 →
    /// frame at +9 (191), fill at +10 (192). Width = raw damage value (no clamp).
    public static IEnumerable<Box> BuildDamage(int damage)
    {
        if (damage <= 0) yield break;
        yield return new Box(109, 191, 102, 8, 74);
        yield return new Box(110, 192, damage, 6, 68);
    }
}
