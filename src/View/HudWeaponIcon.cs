using Raptor.Sim;

namespace Raptor.View;

internal static class HudWeaponIcon
{
    public static string SpriteNameFor(ObjType weapon) => weapon switch
    {
        ObjType.ForwardGuns  => "BONUS00_PIC",
        ObjType.PlasmaGuns   => "BONUS01_PIC",
        ObjType.MicroMissile => "BONUS02_PIC",
        ObjType.DumbMissile  => "BONUS03_PIC",
        ObjType.MiniGun      => "BONUS04_PIC",
        ObjType.Turret       => "BONUS05_PIC",
        ObjType.MissilePods  => "BONUS06_PIC",
        ObjType.AirMissile   => "BONUS07_PIC",
        ObjType.GrdMissile   => "BONUS08_PIC",
        ObjType.Bomb         => "BONUS21_PIC",
        ObjType.EnergyGrab   => "BONUS09_PIC",
        ObjType.MegaBomb     => "BONUS10_PIC",
        ObjType.PulseCannon  => "BONUS11_PIC",
        ObjType.ForwardLaser => "BONUS12_PIC",
        ObjType.DeathRay     => "BONUS13_PIC",
        _ => string.Empty,
    };
}
