using Raptor.Sim.Shots;

namespace Raptor.View;

internal static class HudWeaponIcon
{
    public static string SpriteNameFor(WeaponType weapon) => weapon switch
    {
        WeaponType.ForwardGuns  => "BONUS00_PIC",
        WeaponType.PlasmaGuns   => "BONUS01_PIC",
        WeaponType.MicroMissile => "BONUS02_PIC",
        WeaponType.DumbMissile  => "BONUS03_PIC",
        WeaponType.MiniGun      => "BONUS04_PIC",
        WeaponType.Turret       => "BONUS05_PIC",
        WeaponType.MissilePods  => "BONUS06_PIC",
        WeaponType.AirMissile   => "BONUS07_PIC",
        WeaponType.GrdMissile   => "BONUS08_PIC",
        WeaponType.Bomb         => "BONUS21_PIC",
        WeaponType.EnergyGrab   => "BONUS09_PIC",
        WeaponType.MegaBomb     => "BONUS10_PIC",
        WeaponType.PulseCannon  => "BONUS11_PIC",
        WeaponType.ForwardLaser => "BONUS12_PIC",
        WeaponType.DeathRay     => "BONUS13_PIC",
        _ => string.Empty,
    };
}
