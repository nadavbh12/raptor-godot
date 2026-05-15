namespace Raptor.View;

internal static class HudPalette
{
    public readonly record struct Rgb(byte R, byte G, byte B);

    public static Rgb Color(int paletteIndex) => paletteIndex switch
    {
        66 => new Rgb(223, 113, 60),
        67 => new Rgb(207, 97, 52),
        68 => new Rgb(190, 85, 44),
        69 => new Rgb(174, 69, 36),
        70 => new Rgb(162, 56, 28),
        71 => new Rgb(146, 44, 20),
        72 => new Rgb(130, 36, 16),
        73 => new Rgb(113, 24, 12),
        74 => new Rgb(101, 16, 8),
        _ => new Rgb(0, 0, 0),
    };
}
