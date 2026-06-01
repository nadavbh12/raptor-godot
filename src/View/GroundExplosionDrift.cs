namespace Raptor.View;

internal static class GroundExplosionDrift
{
    /// ANIMS.C:418 — GROUND anims gain +1px Y per frame while scroll_flag is set.
    /// Cumulative drift over the anim's life == its age in frames when scrolling.
    /// Assumes scrolling has been continuous since spawn; a mid-animation scroll
    /// stop/resume will snap the offset (negligible cosmetic, matches C).
    public static int YOffset(int age, bool scrolling) => scrolling ? age : 0;
}
