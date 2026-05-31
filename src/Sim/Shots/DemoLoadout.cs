namespace Raptor.Sim.Shots;

internal static class DemoLoadout
{
    public static void Apply(PlayerShooter shooter, int game, bool registered)
    {
        shooter.GrantWeapon((int)ObjType.MicroMissile);
        switch (game)
        {
            case 1:
                GrantMegaBombs(shooter, 4);
                shooter.GrantWeapon((int)ObjType.PlasmaGuns);
                if (registered)
                    shooter.GrantWeapon((int)ObjType.Turret);
                shooter.GrantWeapon((int)ObjType.GrdMissile);
                shooter.CycleSpecial();
                break;

            case 2:
                GrantMegaBombs(shooter, 4);
                shooter.GrantWeapon((int)ObjType.PlasmaGuns);
                if (registered)
                    shooter.GrantWeapon((int)ObjType.ForwardLaser);
                shooter.GrantWeapon((int)ObjType.GrdMissile);
                shooter.CycleSpecial();
                break;

            default:
                shooter.GrantWeapon((int)ObjType.MegaBomb);
                shooter.GrantWeapon((int)ObjType.MiniGun);
                shooter.GrantWeapon((int)ObjType.AirMissile);
                if (registered)
                {
                    shooter.GrantWeapon((int)ObjType.Turret);
                    shooter.GrantWeapon((int)ObjType.DeathRay);
                }
                shooter.CycleSpecial();
                break;
        }
    }

    private static void GrantMegaBombs(PlayerShooter shooter, int count)
    {
        for (int i = 0; i < count; i++)
            shooter.GrantWeapon((int)ObjType.MegaBomb);
    }
}
