namespace Raptor.View;

internal static class GroundExplosionDrift
{
    /// ANIMS.C:418 — GROUND anims gain +1px Y per frame while scroll_flag is set.
    /// Cumulative drift over the anim's life == its age in frames when scrolling.
    public static int YOffset(int age, bool scrolling) => scrolling ? age : 0;
}
