using System.Collections.Generic;
using Raptor.Sim;
using Raptor.Sim.Bullet;
using Raptor.Sim.Shots;
using Xunit;

namespace RaptorTests;

public class PlayerShooterMuzzleTests
{
    [Fact]
    public void ForwardGuns_records_two_muzzle_positions_at_gun1_offsets()
    {
        var inv = new Inventory();
        var shooter = new PlayerShooter(inv);
        var sink = new List<BulletLogic>();
        shooter.ClearMuzzles();

        int pic = 3; // neutral
        shooter.Shoot(ObjType.ForwardGuns, playerCx: 160, playerCy: 180, playerPic: pic, sink: sink);

        Assert.Equal(2, shooter.Muzzles.Count);
        Assert.Equal((160 + GunOffsets.OGun1[pic], 180), (shooter.Muzzles[0].X, shooter.Muzzles[0].Y));
        Assert.Equal((160 - GunOffsets.OGun1[pic] - 1, 180), (shooter.Muzzles[1].X, shooter.Muzzles[1].Y));
    }

    [Fact]
    public void MissilePods_records_two_muzzle_positions_at_gun2_offsets_with_y_offset_one()
    {
        var shooter = new PlayerShooter(new Inventory());
        var sink = new List<BulletLogic>();
        shooter.ClearMuzzles();

        int pic = 3; // neutral
        shooter.Shoot(ObjType.MissilePods, playerCx: 160, playerCy: 180, playerPic: pic, sink: sink);

        // C uses gun2 offsets with y-offset 1 (SHOTS.C:850/866).
        Assert.Equal(2, shooter.Muzzles.Count);
        Assert.Equal((160 + GunOffsets.OGun2[pic], 181), (shooter.Muzzles[0].X, shooter.Muzzles[0].Y));
        Assert.Equal((160 - GunOffsets.OGun2[pic], 181), (shooter.Muzzles[1].X, shooter.Muzzles[1].Y));
    }

    [Fact]
    public void NonFlashWeapon_fires_a_bullet_but_records_zero_muzzles()
    {
        // PlasmaGuns is a non-flash weapon in C: it adds a bullet but spawns no
        // A_PLAYER_SHOOT (GUNSTR_BLK) muzzle flash.
        var shooter = new PlayerShooter(new Inventory());
        var sink = new List<BulletLogic>();
        shooter.ClearMuzzles();

        shooter.Shoot(ObjType.PlasmaGuns, 160, 180, 3, sink);

        Assert.True(sink.Count > 0);          // the shot did fire
        Assert.Empty(shooter.Muzzles);        // but no muzzle flash recorded
    }

    [Fact]
    public void ClearMuzzles_resets_the_list()
    {
        var shooter = new PlayerShooter(new Inventory());
        var sink = new List<BulletLogic>();
        shooter.Shoot(ObjType.ForwardGuns, 160, 180, 3, sink);
        shooter.ClearMuzzles();
        Assert.Empty(shooter.Muzzles);
    }
}
