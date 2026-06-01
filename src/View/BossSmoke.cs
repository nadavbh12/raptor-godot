namespace Raptor.View;

/// ENEMY.C:1076-1085 — bosses with hits<50 emit A_SMALL_AIR_EXPLO (SMFLAK_BLK)
/// every other game-loop pass (gl_cnt & 2) at a within-bounds offset. C uses
/// random(width)/random(height); under deterministic RNG that is width/2,height/2,
/// computed here with no RNG draw.
internal static class BossSmoke
{
    public static bool ShouldSpawn(int hits, int glCnt) => hits < 50 && (glCnt & 2) != 0;
    public static (int X, int Y) SpawnPoint(int x, int y, int width, int height)
        => (x + width / 2, y + height / 2);
}
