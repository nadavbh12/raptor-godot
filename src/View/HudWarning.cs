namespace Raptor.View;

internal static class HudWarning
{
    public const int ShieldLowThreshold = 10;
    public const int MapBottom = 200 - 18;
    public const int SystemDamageY = MapBottom - 9;
    public const int SystemDamageDurationFrames = 48;

    public static bool ShieldLowVisible(int shield, int frame)
        => shield <= ShieldLowThreshold && ((frame / 8) & 1) == 0;

    public static bool SystemDamageVisible(int shield, int untilFrame, int frame)
        => frame <= untilFrame && ShieldLowVisible(shield, frame);

    public static int CenterX(int spriteWidth) => (320 - spriteWidth) >> 1;
}
