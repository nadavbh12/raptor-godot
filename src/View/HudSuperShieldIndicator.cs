using System.Collections.Generic;

namespace Raptor.View;

/// OBJECTS.C:663-672 — one SMSHIELD_PIC per owned super-shield, x = MAP_LEFT+2,
/// stepping +13, on row y=1.
internal static class HudSuperShieldIndicator
{
    public readonly record struct Position(int X, int Y);

    /// <summary>
    /// C draws one icon per super-shield CHARGE — `OBJS_GetTotal` counts the
    /// discrete S_SUPER_SHIELD objects (OBJECTS.C:665), each a full-shield charge.
    /// This port folds super-shield into a single point buffer (slot Num = shield
    /// points), so derive the charge count = ceil(points / points-per-charge).
    /// Without this the HUD drew one icon per shield POINT (~100), flooding the row.
    /// </summary>
    public static int ChargeCount(int points, int pointsPerCharge)
    {
        if (points <= 0 || pointsPerCharge <= 0) return 0;
        return (points + pointsPerCharge - 1) / pointsPerCharge;
    }

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
