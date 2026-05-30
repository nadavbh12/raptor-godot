using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// ES_LASER sim parity (ESHOT.C:385-393 spawn, 453-470 think). The laser is a
/// short-lived beam that tracks the FIRING enemy's gun each tick and damages the
/// player by lib->hits=12 when horizontally aligned and above the player —
/// distinct from the AABB-collision path used by all other enemy bullets.
///
/// NOTE: ES_LASER is fired only by secret-gated enemies (E_SECRET_*) deep in
/// wave 8, so no parity scenario exercises it; these unit tests pin the sim
/// logic directly against the C source. The vertical-column RENDERING
/// (ESHOT.C:558+) is a View concern, NOT modeled here, and needs visual review.
/// </summary>
public class EnemyLaserTests
{
    private static EnemyLogic EnemyAt(int x, int y) =>
        new EnemyLogic(
            new SpriteMeta { Hits = 3, NumFlight = 1, FlightType = 1,
                             FlightX = new[] { x }, FlightY = new[] { y }, NumGuns = 0 },
            spawnX: x, mapY: y);

    [Fact]
    public void Laser_tracks_firing_enemy_gun_each_tick()
    {
        // ESHOT.C:456-457 — x = en.x + shootx[gun] - 4, y = en.y + shooty[gun].
        var e = EnemyAt(100, 40);
        var laser = BulletLogic.EnemyLaser(e, gunShootX: 6, gunShootY: 8);

        // Player far away (no damage) so we just observe the tracked position.
        laser.LaserTick(playerCx: 0, playerCy: 0);

        Assert.Equal(e.X + 6 - 4, laser.X);
        Assert.Equal(e.Y + 8, laser.Y);
    }

    [Fact]
    public void Laser_damages_player_12_when_aligned_and_above()
    {
        // ESHOT.C:462-465 — |x - player_cx| < PLAYERWIDTH/2 (16) && y < player_cy
        //                    => OBJS_SubEnergy(lib->hits = 12).
        var e = EnemyAt(100, 40);
        var laser = BulletLogic.EnemyLaser(e, gunShootX: 4, gunShootY: 8);
        int beamX = e.X + 4 - 4;
        int beamY = e.Y + 8;

        int dmg = laser.LaserTick(playerCx: beamX, playerCy: beamY + 50);

        Assert.Equal(12, dmg);
        Assert.True(laser.Alive);
    }

    [Fact]
    public void Laser_no_damage_when_horizontally_misaligned()
    {
        var e = EnemyAt(100, 40);
        var laser = BulletLogic.EnemyLaser(e, gunShootX: 4, gunShootY: 8);
        int beamX = e.X + 4 - 4;
        int beamY = e.Y + 8;

        // dx = 16 == PLAYERWIDTH/2 → NOT < 16, so no hit (strict).
        int dmg = laser.LaserTick(playerCx: beamX + 16, playerCy: beamY + 50);

        Assert.Equal(0, dmg);
    }

    [Fact]
    public void Laser_no_damage_when_player_not_below_beam()
    {
        // ESHOT.C:462 also requires shot->y < player_cy (player below the beam).
        var e = EnemyAt(100, 40);
        var laser = BulletLogic.EnemyLaser(e, gunShootX: 4, gunShootY: 8);
        int beamX = e.X + 4 - 4;
        int beamY = e.Y + 8;

        int dmg = laser.LaserTick(playerCx: beamX, playerCy: beamY); // y == player_cy → not <

        Assert.Equal(0, dmg);
    }

    [Fact]
    public void Laser_expires_after_num_frames()
    {
        // ESHOT.C:449/454/469 — curframe++ each pass; tracks while curframe <
        // num_frames (4) → 3 damaging passes, then doneflag on the 4th.
        var e = EnemyAt(100, 40);
        var laser = BulletLogic.EnemyLaser(e, gunShootX: 4, gunShootY: 8);
        int beamX = e.X + 4 - 4;
        int beamY = e.Y + 8;

        // Passes 1,2,3: aligned → 12 each, still alive.
        for (int i = 0; i < 3; i++)
        {
            int dmg = laser.LaserTick(playerCx: beamX, playerCy: beamY + 50);
            Assert.Equal(12, dmg);
            Assert.True(laser.Alive);
        }

        // Pass 4: curframe reaches num_frames → expire, no damage.
        int last = laser.LaserTick(playerCx: beamX, playerCy: beamY + 50);
        Assert.Equal(0, last);
        Assert.False(laser.Alive);
    }

    [Fact]
    public void Laser_carries_laser_shot_type_for_view()
    {
        var e = EnemyAt(100, 40);
        var laser = BulletLogic.EnemyLaser(e, gunShootX: 4, gunShootY: 8);
        Assert.Equal(EnemyShotType.Laser, laser.ShotType);
        Assert.True(laser.IsEnemyLaser);
        Assert.Equal(12, laser.Damage);
    }
}
