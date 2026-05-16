namespace Raptor.View;

internal static class BonusSprite
{
    public const string PickedUpMoneySpriteName = "N$_PIC";
    public const float GlowAlpha = 0.6f;

    public static string SpriteNameFor(int objType, int frame) => objType switch
    {
        0 => "BONUS00_PIC",
        1 => $"BONUS01_PIC",
        2 => $"BONUS02_PIC",
        3 => "BONUS03_PIC",
        4 => "BONUS04_PIC",
        5 => "BONUS05_PIC",
        6 => "BONUS06_PIC",
        7 => "BONUS07_PIC",
        8 => "BONUS08_PIC",
        9 => "BONUS21_PIC",
        10 => "BONUS09_PIC",
        11 => "BONUS10_PIC",
        12 => "BONUS11_PIC",
        13 => "BONUS12_PIC",
        14 => "BONUS13_PIC",
        15 => "BONUS14_PIC",
        16 => "BONUS15_PIC",
        17 => "BONUS16_PIC",
        18 => "BONUS16_PIC",
        19 => "BONUS17_PIC",
        20 => "BONUS18_PIC",
        21 => "BONUS19_PIC",
        22 => "BONUS20_PIC",
        23 => "BONUS22_PIC",
        24 => "BONUS22_PIC",
        _ => "BONUS00_PIC",
    };

    public static int FrameCountFor(int objType) => objType switch
    {
        1 or 2 or 12 => 2,
        4 or 5 or 10 or 13 or 14 or 16 or 23 or 24 => 4,
        _ => 1,
    };

    public static (int X, int Y) DrawOffset(int pos)
    {
        int wrapped = ((pos % 16) + 16) % 16;
        int[] xpos = { -1, 0, 1, 2, 3, 3, 3, 2, 1, 0, -1, -2, -3, -3, -3, -2 };
        int[] ypos = { -3, -3, -3, -2, -1, 0, 1, 2, 3, 3, 3, 2, 1, 0, -1, -2 };
        return (xpos[wrapped], ypos[wrapped]);
    }
}
