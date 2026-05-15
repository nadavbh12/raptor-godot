namespace Raptor.Sim.Shots;

internal static class DemoLoadout
{
    public static void Apply(PlayerShooter shooter, int game, bool registered)
    {
        shooter.GrantWeapon((int)WeaponType.MicroMissile);
        switch (game)
        {
            case 1:
                GrantMegaBombs(shooter, 4);
                shooter.GrantWeapon((int)WeaponType.PlasmaGuns);
                if (registered)
                    shooter.GrantWeapon((int)WeaponType.Turret);
                shooter.GrantWeapon((int)WeaponType.GrdMissile);
                shooter.CycleSpecial();
                break;

            case 2:
                GrantMegaBombs(shooter, 4);
                shooter.GrantWeapon((int)WeaponType.PlasmaGuns);
                if (registered)
                    shooter.GrantWeapon((int)WeaponType.ForwardLaser);
                shooter.GrantWeapon((int)WeaponType.GrdMissile);
                shooter.CycleSpecial();
                break;

            default:
                shooter.GrantWeapon((int)WeaponType.MegaBomb);
                shooter.GrantWeapon((int)WeaponType.MiniGun);
                shooter.GrantWeapon((int)WeaponType.AirMissile);
                if (registered)
                {
                    shooter.GrantWeapon((int)WeaponType.Turret);
                    shooter.GrantWeapon((int)WeaponType.DeathRay);
                }
                shooter.CycleSpecial();
                break;
        }
    }

    private static void GrantMegaBombs(PlayerShooter shooter, int count)
    {
        for (int i = 0; i < count; i++)
            shooter.GrantWeapon((int)WeaponType.MegaBomb);
    }
}
