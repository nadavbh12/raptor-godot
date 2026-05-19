namespace Raptor.View;

public static class WorldClipper
{
    public const int MapLeft = 16;
    public const int MapRight = 320 - 16;

    public readonly record struct HorizontalClip(int DestX, int SourceX, int Width);

    public static bool TryClipHorizontal(int sourceX, int width, out HorizontalClip clip)
    {
        int left = System.Math.Max(sourceX, MapLeft);
        int right = System.Math.Min(sourceX + width, MapRight);
        if (right <= left)
        {
            clip = default;
            return false;
        }

        clip = new HorizontalClip(left, left - sourceX, right - left);
        return true;
    }
}
