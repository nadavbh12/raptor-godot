using Raptor.Sim.Shots;

namespace Raptor.View;

internal static class PlayerBulletSprite
{
    public static (string Family, int Frame) FrameFor(WeaponType weapon, int frameCounter)
    {
        var lib = ShotLib.Get(weapon);
        string family = FamilyFor(weapon);
        int frames = lib.NumFrames > 0 ? lib.NumFrames : 1;
        int frame = (lib.StartFrame + frameCounter) % frames;
        return (family, frame);
    }

    private static string FamilyFor(WeaponType weapon) => weapon switch
    {
        WeaponType.ForwardGuns  => "NMSHOT_BLK",
        WeaponType.PlasmaGuns   => "PLASMA_BLK",
        WeaponType.MicroMissile => "MICROM_BLK",
        WeaponType.DumbMissile  => "MISDUM_BLK",
        WeaponType.MiniGun      => "NMSHOT_BLK",
        WeaponType.MissilePods  => "MISRAT_BLK",
        WeaponType.AirMissile   => "MISRAT_BLK",
        WeaponType.GrdMissile   => "MISGRD_BLK",
        WeaponType.Bomb         => "BLDGBOMB_PIC",
        WeaponType.EnergyGrab   => "POWDIS_BLK",
        WeaponType.MegaBomb     => "MEGABM_BLK",
        WeaponType.PulseCannon  => "SHOKWV_BLK",
        WeaponType.ForwardLaser => "FRNTLAS_BLK",
        WeaponType.DeathRay     => "DETHRY_BLK",
        _ => "NMSHOT_BLK",
    };
}
